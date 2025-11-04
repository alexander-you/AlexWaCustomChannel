using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlexWaCustomChannel.Plugins
{
    /// <summary>Plugin that forwards WhatsApp messages to Azure Function with full template and media support</summary>
    public class alex_PostExecute_WaOutboundApi : IPlugin
    {
        private readonly string UnsecureConfig;
        private readonly string SecureConfig;
        private const string ENV_VAR_SCHEMA = "alex_alex_wa_func_url";
        private static string _cachedFuncUrl;

        public alex_PostExecute_WaOutboundApi(string unsecureConfig)
            : this(unsecureConfig, null) { }

        public alex_PostExecute_WaOutboundApi(string unsecureConfig, string secureConfig)
        {
            UnsecureConfig = unsecureConfig?.Trim();
            SecureConfig   = secureConfig?.Trim();
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            trace.Trace($"⏺ UnsecureConfig = '{UnsecureConfig}'");
            trace.Trace($"⏺ SecureConfig   = '{SecureConfig}'");

            try
            {
                if (ctx.MessageName != "alex_waoutboundapi")
                    return;

                if (!ctx.InputParameters.Contains("payload"))
                    throw new InvalidPluginExecutionException("Missing parameter: payload");

                // ---------- 1. קריאת ה-payload המקורי ----------
                var originalJson = (string)ctx.InputParameters["payload"];
                trace.Trace($"Payload received: {originalJson}");
                var original = JObject.Parse(originalJson);

                // שמירת ערכים חשובים מה-payload המקורי
                var channelDefinitionId = (string)original["ChannelDefinitionId"];
                var requestId = (string)original["RequestId"];
                var fromAddress = (string)original["From"];

                // ---------- 2. חילוץ פרטי תבנית ומילוי ערכים ----------
                var msg = original["Message"] as JObject ?? new JObject();
                var templateNm = (string)msg["templename"] ?? "";
                var templateType = (string)msg["templateType"] ?? "text";
                var language = (string)msg["language"] ?? "he";

                if (string.IsNullOrWhiteSpace(templateNm))
                    throw new InvalidPluginExecutionException("templename missing in Message.");

                trace.Trace($"Template: {templateNm}, Type: {templateType}");

                // הדפס את msdynmkt_messageparts אם קיים
                var messagePartsJson = (string)msg["msdynmkt_messageparts"];
                if (!string.IsNullOrWhiteSpace(messagePartsJson))
                {
                    trace.Trace($"Message parts JSON: {messagePartsJson}");
                }

                // ---------- 3. קריאת ChannelRegistrationId מה-Channel Instance ----------
                var channelRegistrationId = GetChannelRegistrationId(serviceProvider, channelDefinitionId, fromAddress, trace);

                if (string.IsNullOrWhiteSpace(channelRegistrationId))
                    throw new InvalidPluginExecutionException($"Channel Registration ID not found for channel instance: {fromAddress}");

                trace.Trace($"Channel Registration ID: {channelRegistrationId}");
                trace.Trace($"Building payload for template type: {templateType}");

                // ---------- 4. בניית מערך values כולל תמיכה במדיה ----------
                var valuesArr = BuildEnhancedValues(msg, templateType, serviceProvider, trace);

                trace.Trace($"Values array has {valuesArr.Count} items");
                if (valuesArr.Count > 0)
                {
                    trace.Trace($"Values array content: {valuesArr.ToString(Formatting.None)}");
                }

                // ---------- 5. הרכבת payload חדש ----------
                var outbound = new JObject
                {
                    ["ChannelDefinitionId"] = channelDefinitionId,
                    ["ChannelRegistrationId"] = channelRegistrationId,
                    ["RequestId"]           = requestId,
                    ["OrganizationId"]      = original["OrganizationId"],
                    ["From"]                = original["From"],
                    ["To"]                  = original["To"],
                    ["template"] = new JObject
                    {
                        ["name"]     = templateNm,
                        ["language"] = language,
                        ["type"]     = templateType,
                        ["values"]   = valuesArr
                    }
                };

                var newJson = outbound.ToString(Formatting.None);
                trace.Trace($"Outbound payload: {newJson}");

                // ---------- 6. אחזור כתובת הפונקציה ----------
                var funcUrl = FirstNonEmpty(UnsecureConfig, SecureConfig)
                              ?? GetUrlFromEnvVar(serviceProvider, trace);

                if (string.IsNullOrWhiteSpace(funcUrl))
                    throw new InvalidPluginExecutionException("Azure Function URL not configured.");

                trace.Trace($"Function URL to call: {funcUrl}");

                // ---------- 7. קריאה לפונקציה ----------
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var httpResp = http.PostAsync(
                                   funcUrl,
                                   new StringContent(newJson, Encoding.UTF8, "application/json"))
                                   .Result;

                    var respBody = httpResp.Content.ReadAsStringAsync().Result;
                    trace.Trace($"Function status: {httpResp.StatusCode}");
                    trace.Trace($"Function body  : {respBody}");

                    // Parse the response from Azure Function
                    var funcResponse = JObject.Parse(respBody);

                    // ---------- 8. בניית תגובה בפורמט הנכון ל-D365 ----------
                    var d365Response = new JObject
                    {
                        ["ChannelDefinitionId"] = channelDefinitionId,
                        ["RequestId"] = requestId,
                        ["Status"] = funcResponse.Value<bool>("success") ? "Sent" : "NotSent",
                        ["MessageId"] = funcResponse.Value<string>("messageId") ?? ""
                    };

                    // אם יש שגיאה, הוסף אותה
                    if (!funcResponse.Value<bool>("success"))
                    {
                        d365Response["ErrorMessage"] = funcResponse.Value<string>("error") ?? "Unknown error";
                    }

                    // החזר את התגובה בפורמט הנכון
                    ctx.OutputParameters["response"] = d365Response.ToString(Formatting.None);

                    trace.Trace($"D365 Response: {d365Response.ToString(Formatting.None)}");

                    // שמור את המיפוי בטבלה אם הצלחנו לשלוח
                    if (funcResponse.Value<bool>("success") && !string.IsNullOrEmpty(funcResponse.Value<string>("messageId")))
                    {
                        try
                        {
                            var tracking = new Entity("alex_whatsapp_message_tracking");
                            tracking["alex_message_id"] = funcResponse.Value<string>("messageId");
                            tracking["alex_request_id"] = requestId;
                            tracking["alex_channel_definition_id"] = channelDefinitionId;
                            tracking["alex_from_number"] = fromAddress;
                            tracking["alex_to_number"] = (string)original["To"];
                            tracking["alex_template_name"] = templateNm;
                            tracking["alex_send_timestamp"] = DateTime.UtcNow;

                            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                            var svc = factory.CreateOrganizationService(null);
                            svc.Create(tracking);

                            trace.Trace($"Saved message tracking: MessageId={funcResponse.Value<string>("messageId")}, RequestId={requestId}");
                        }
                        catch (Exception ex)
                        {
                            trace.Trace($"Failed to save message tracking: {ex.Message}");
                            // לא זורקים את השגיאה - לא רוצים שזה יפיל את השליחה
                        }
                    }

                    if (!httpResp.IsSuccessStatusCode)
                        throw new InvalidPluginExecutionException(
                            $"Function error: {httpResp.StatusCode} - {respBody}");

                    trace.Trace("alex_PostExecute_WaOutboundApi: DONE");
                }
            }
            catch (Exception ex)
            {
                trace.Trace($"Error: {ex}");

                // גם בשגיאה, החזר תגובה בפורמט הנכון
                var errorResponse = new JObject
                {
                    ["ChannelDefinitionId"] = ctx.InputParameters.Contains("payload")
                        ? (string)JObject.Parse((string)ctx.InputParameters["payload"])["ChannelDefinitionId"]
                        : "",
                    ["RequestId"] = ctx.InputParameters.Contains("payload")
                        ? (string)JObject.Parse((string)ctx.InputParameters["payload"])["RequestId"]
                        : "",
                    ["Status"] = "NotSent",
                    ["ErrorMessage"] = ex.Message
                };

                ctx.OutputParameters["response"] = errorResponse.ToString(Formatting.None);

                throw new InvalidPluginExecutionException("Error sending WhatsApp message.", ex);
            }
        }

        // ---------- Enhanced Values Builder with Media Support ----------
        private JArray BuildEnhancedValues(JObject msg, string templateType, IServiceProvider serviceProvider, ITracingService trace)
        {
            var valuesArr = new JArray();

            // 1. Body parameters (text) - תמיד קיימים
            foreach (var prop in msg.Properties())
            {
                if (prop.Name.StartsWith("param", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace((string)prop.Value))
                {
                    var index = prop.Name.Substring(5);
                    valuesArr.Add(new JObject
                    {
                        ["name"] = index,
                        ["kind"] = "text",
                        ["text"] = (string)prop.Value
                    });
                }
            }

            // 2. בדוק אם יש headerMedia
            var headerMediaId = (string)msg["headerMedia"];
            if (!string.IsNullOrWhiteSpace(headerMediaId))
            {
                trace.Trace($"Found headerMedia GUID: {headerMediaId}");

                // קרא את ה-URL מהקובץ
                var (mediaUrl, fileName) = GetMediaUrlFromFile(serviceProvider, headerMediaId, trace);

                if (!string.IsNullOrWhiteSpace(mediaUrl))
                {
                    // נסה לזהות את סוג המדיה
                    var mediaType = DetermineMediaType(mediaUrl, serviceProvider, headerMediaId, trace);

                    var mediaValue = new JObject
                    {
                        ["name"] = "headerMedia",
                        ["kind"] = mediaType, // "image", "video", "document"
                        ["text"] = "",
                        ["url"] = mediaUrl
                    };

                    valuesArr.Add(mediaValue);

                    trace.Trace($"Added media to values array: type={mediaType}, url={mediaUrl}");
                    trace.Trace($"Media value JSON: {mediaValue.ToString(Formatting.None)}");
                }
                else
                {
                    trace.Trace("WARNING: Media URL is empty or null");
                }
            }
            else
            {
                trace.Trace("No headerMedia found in message");

                // 3. בדוק אם יש documentfile
                var documentFileId = (string)msg["documentfile"];
                if (!string.IsNullOrWhiteSpace(documentFileId))
                {
                    trace.Trace($"Found documentfile GUID: {documentFileId}");

                    // קרא את ה-URL מהקובץ
                    var (documentUrl, fileName) = GetMediaUrlFromFile(serviceProvider, documentFileId, trace);

                    if (!string.IsNullOrWhiteSpace(documentUrl))
                    {
                        // נסה לזהות את סוג המדיה
                        var mediaType = DetermineMediaType(documentUrl, serviceProvider, documentFileId, trace);

                        var documentValue = new JObject
                        {
                            ["name"] = "documentfile",
                            ["kind"] = mediaType, // "image", "video", "document"
                            ["text"] = fileName,  // הוסף את השם
                            ["url"] = documentUrl
                        };

                        valuesArr.Add(documentValue);

                        trace.Trace($"Added document to values array: type={mediaType}, url={documentUrl}");
                        trace.Trace($"Document value JSON: {documentValue.ToString(Formatting.None)}");
                    }
                    else
                    {
                        trace.Trace("WARNING: Document URL is empty or null");
                    }
                }
                else
                {
                    trace.Trace("No documentfile found in message");
                }

                // 4. בדוק אם יש נתוני מיקום
                var locationName = (string)msg["locationName"];
                var locationAddress = (string)msg["locationAddress"];
                var latitude = (string)msg["latitude"];
                var longitude = (string)msg["longitude"];

                if (!string.IsNullOrWhiteSpace(locationName) ||
                    (!string.IsNullOrWhiteSpace(latitude) && !string.IsNullOrWhiteSpace(longitude)))
                {
                    trace.Trace("Found location data in message");

                    var locationValue = new JObject
                    {
                        ["name"] = "location",
                        ["kind"] = "location",
                        ["text"] = locationName ?? "מיקום",
                        ["address"] = locationAddress ?? "",
                        ["latitude"] = latitude ?? "32.0853",
                        ["longitude"] = longitude ?? "34.7818"
                    };

                    valuesArr.Add(locationValue);
                    trace.Trace($"Added location to values array: {locationName} at {latitude},{longitude}");
                }
                else
                {
                    trace.Trace("No location data found in message");
                }

                // תיקון: אם יש headerMedia אבל אין body parameters, הוסף parameter ריק
                if (valuesArr.Any(v => (string)v["name"] == "headerMedia") &&
                    !valuesArr.Any(v => (string)v["kind"] == "text"))
                {
                    trace.Trace("Adding default body parameter for media template");
                    valuesArr.Add(new JObject
                    {
                        ["name"] = "1",
                        ["kind"] = "text",
                        ["text"] = " "
                    });
                }
            }
            // תיקון: אם יש location אבל אין body parameters, הוסף parameter ריק
            if (valuesArr.Any(v => (string)v["kind"] == "location") &&
                !valuesArr.Any(v => (string)v["kind"] == "text"))
            {
                trace.Trace("Adding default body parameter for location template");
                valuesArr.Add(new JObject
                {
                    ["name"] = "1",
                    ["kind"] = "text",
                    ["text"] = " "
                });
            }

            return valuesArr;
        }

        // ---------- Get Media URL from File ----------
        private (string url, string fileName) GetMediaUrlFromFile(IServiceProvider serviceProvider, string fileId, ITracingService trace)
        {
            if (string.IsNullOrWhiteSpace(fileId) || !Guid.TryParse(fileId, out Guid fileGuid))
                return (null, null);

            trace.Trace($"Getting media URL for file: {fileId}");

            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            try
            {
                // קרא את השדות כולל content type
                var file = svc.Retrieve("msdyncrm_file", fileGuid,
                    new ColumnSet("msdyncrm_blobcdnuri", "msdyncrm_name", "msdyncrm_contenttype"));

                var cdnUrl = file.GetAttributeValue<string>("msdyncrm_blobcdnuri");
                var fileName = file.GetAttributeValue<string>("msdyncrm_name");
                var contentType = file.GetAttributeValue<string>("msdyncrm_contenttype");

                trace.Trace($"Found file: {fileName}, ContentType: {contentType}, URL: {cdnUrl}");

                return (cdnUrl, fileName);
            }
            catch (Exception ex)
            {
                trace.Trace($"Error getting file URL: {ex.Message}");
                return (null, null);
            }
        }

        // ---------- Determine Media Type ----------
        private string DetermineMediaType(string url, IServiceProvider serviceProvider, string fileId, ITracingService trace)
        {
            // נסה קודם לקבל את ה-content type מהקובץ
            if (!string.IsNullOrWhiteSpace(fileId) && Guid.TryParse(fileId, out Guid fileGuid))
            {
                try
                {
                    var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                    var svc = factory.CreateOrganizationService(null);

                    var file = svc.Retrieve("msdyncrm_file", fileGuid, new ColumnSet("msdyncrm_contenttype"));
                    var contentType = file.GetAttributeValue<string>("msdyncrm_contenttype")?.ToLower() ?? "";

                    trace.Trace($"File content type: {contentType}");

                    // זיהוי לפי content type
                    if (contentType.StartsWith("image/"))
                        return "image";
                    if (contentType.StartsWith("video/"))
                        return "video";
                    if (contentType.StartsWith("application/pdf") ||
                        contentType.StartsWith("application/msword") ||
                        contentType.StartsWith("application/vnd."))
                        return "document";
                }
                catch (Exception ex)
                {
                    trace.Trace($"Error getting content type: {ex.Message}");
                }
            }

            // אם לא הצלחנו, נסה לזהות לפי סיומת או URL
            var lower = url?.ToLower() ?? "";

            // תמונות
            if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") ||
                lower.EndsWith(".png") || lower.EndsWith(".gif") ||
                lower.EndsWith(".webp") || lower.Contains("/image"))
                return "image";

            // וידאו
            if (lower.EndsWith(".mp4") || lower.EndsWith(".avi") ||
                lower.EndsWith(".mov") || lower.EndsWith(".mkv") ||
                lower.EndsWith(".webm") || lower.Contains("/video"))
                return "video";

            // מסמכים
            if (lower.EndsWith(".pdf") || lower.EndsWith(".doc") ||
                lower.EndsWith(".docx") || lower.EndsWith(".xls") ||
                lower.EndsWith(".xlsx") || lower.EndsWith(".ppt") ||
                lower.EndsWith(".pptx") || lower.Contains("/document"))
                return "document";

            // ברירת מחדל - נניח שזו תמונה
            trace.Trace($"Could not determine media type for URL: {url}, defaulting to image");
            return "image";
        }

        // ----------  Helpers ----------
        private static string FirstNonEmpty(params string[] vals) =>
            vals?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static string GetChannelRegistrationId(IServiceProvider sp, string channelDefinitionId, string fromAddress, ITracingService trace)
        {
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            // Step 1: חיפוש Channel Instance לפי Channel Definition ID ו-Contact Point (מספר טלפון)
            var query = new QueryExpression("msdyn_channelinstance")
            {
                ColumnSet = new ColumnSet("msdyn_extendedentityid", "msdyn_name", "msdyn_contactpoint"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("msdyn_channeldefinitionid", ConditionOperator.Equal, channelDefinitionId),
                        new ConditionExpression("msdyn_contactpoint", ConditionOperator.Equal, fromAddress),
                        new ConditionExpression("statecode", ConditionOperator.Equal, 0) // Active
                    }
                }
            };

            var results = svc.RetrieveMultiple(query);

            if (results.Entities.Count == 0)
            {
                trace.Trace($"No active channel instance found for definition {channelDefinitionId} and contact point {fromAddress}");
                return null;
            }

            var channelInstance = results.Entities.First();
            trace.Trace($"Found channel instance: {channelInstance.GetAttributeValue<string>("msdyn_name")}");

            // Step 2: קבל את ה-EntityReference ל-alex_wachannelinstance
            var waChannelInstanceRef = channelInstance.GetAttributeValue<EntityReference>("msdyn_extendedentityid");

            if (waChannelInstanceRef == null)
            {
                trace.Trace("msdyn_extendedentityid is null");
                return null;
            }

            trace.Trace($"WA Channel Instance ID: {waChannelInstanceRef.Id}");

            // Step 3: קרא את הרשומה מ-alex_wachannelinstance
            try
            {
                var waChannelInstance = svc.Retrieve("alex_wachannelinstance",
                    waChannelInstanceRef.Id,
                    new ColumnSet("alex_channelregistrationid", "alex_name", "alex_wabanumber"));

                // קח את ה-Registration ID
                var registrationId = waChannelInstance.GetAttributeValue<string>("alex_channelregistrationid");

                trace.Trace($"Found ACS Registration ID: {registrationId}");
                return registrationId;
            }
            catch (Exception ex)
            {
                trace.Trace($"Error retrieving alex_wachannelinstance: {ex.Message}");
                return null;
            }
        }

        private static string GetUrlFromEnvVar(IServiceProvider sp, ITracingService trace)
        {
            if (!string.IsNullOrWhiteSpace(_cachedFuncUrl))
                return _cachedFuncUrl;

            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            var qe = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue"),
                Criteria  = { Conditions =
                {
                    new ConditionExpression("schemaname", ConditionOperator.Equal, ENV_VAR_SCHEMA)
                }}
            };
            var link = qe.AddLink("environmentvariablevalue", "environmentvariabledefinitionid",
                                  "environmentvariabledefinitionid", JoinOperator.LeftOuter);
            link.Columns     = new ColumnSet("value");
            link.EntityAlias = "val";

            var def = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
            var currentVal = (def?["val.value"] as AliasedValue)?.Value as string;
            var defaultVal = def?.GetAttributeValue<string>("defaultvalue");
            _cachedFuncUrl = FirstNonEmpty(currentVal, defaultVal)?.Trim();

            trace.Trace($"EnvVar URL resolved to: {_cachedFuncUrl ?? "<null>"}");
            return _cachedFuncUrl;
        }
    }
}





/*
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlexWaCustomChannel.Plugins
{
    /// <summary>Plugin that forwards WhatsApp messages to Azure Function in CI-Journeys format</summary>
    public class alex_PostExecute_WaOutboundApi : IPlugin
    {
        private readonly string UnsecureConfig;
        private readonly string SecureConfig;
        private const string ENV_VAR_SCHEMA = "alex_alex_wa_func_url";
        private static string _cachedFuncUrl;

        public alex_PostExecute_WaOutboundApi(string unsecureConfig)
            : this(unsecureConfig, null) { }

        public alex_PostExecute_WaOutboundApi(string unsecureConfig, string secureConfig)
        {
            UnsecureConfig = unsecureConfig?.Trim();
            SecureConfig   = secureConfig?.Trim();
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            trace.Trace($"⏺ UnsecureConfig = '{UnsecureConfig}'");
            trace.Trace($"⏺ SecureConfig   = '{SecureConfig}'");

            try
            {
                if (ctx.MessageName != "alex_waoutboundapi")
                    return;

                if (!ctx.InputParameters.Contains("payload"))
                    throw new InvalidPluginExecutionException("Missing parameter: payload");

                // ---------- 1. קריאת ה-payload המקורי ----------
                var originalJson = (string)ctx.InputParameters["payload"];
                trace.Trace($"Payload received: {originalJson}");
                var original = JObject.Parse(originalJson);

                // שמירת ערכים חשובים מה-payload המקורי
                var channelDefinitionId = (string)original["ChannelDefinitionId"];
                var requestId = (string)original["RequestId"];

                // ---------- 2. חילוץ פרטי תבנית ומילוי ערכים ----------
                var msg = original["Message"] as JObject ?? new JObject();
                var templateNm = (string)msg["templename"] ?? "";
                var language = (string)msg["language"]   ?? "he";

                if (string.IsNullOrWhiteSpace(templateNm))
                    throw new InvalidPluginExecutionException("templename missing in Message.");

                // בנה מערך values חדש
                var valuesArr = new JArray();

                // === 1. אם יש headerMedia, הוסף אותו כערך IMAGE ===
                if (msg.TryGetValue("headerMedia", out JToken mediaGuidToken) &&
                    Guid.TryParse(mediaGuidToken.ToString(), out Guid mediaGuid))
                {
                    var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                    var svc = serviceFactory.CreateOrganizationService(ctx.UserId);

                    try
                    {
                        var file = svc.Retrieve("msdyncrm_file", mediaGuid, new ColumnSet("msdyncrm_blobcdnuri"));
                        var imageUrl = file.GetAttributeValue<string>("msdyncrm_blobcdnuri");

                        if (!string.IsNullOrWhiteSpace(imageUrl))
                        {
                            valuesArr.Add(new JObject
                            {
                                ["name"] = "headerMedia",
                                ["kind"] = "image",
                                ["url"] = imageUrl
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        trace.Trace($"⚠️ Failed to fetch headerMedia: {ex.Message}");
                    }
                }

                // === 2. הוסף param1, param2 וכו' בדיוק כמו קודם ===
                foreach (var p in msg.Properties()
                                     .Where(p => p.Name.StartsWith("param", StringComparison.OrdinalIgnoreCase) &&
                                                 !string.IsNullOrWhiteSpace((string)p.Value)))
                {
                    var index = p.Name.Substring(5); // "1", "2" …
                    valuesArr.Add(new JObject
                    {
                        ["name"] = index,
                        ["kind"] = "text",
                        ["text"] = (string)p.Value
                    });
                }


                /*
                 * הקוד הזה ירד כדי לטפל גם במקרים בהם יש קישור לתמונה
                // בנה מערך values מתוך paramX
                var valuesArr = new JArray(
                    msg.Properties()
                       .Where(p => p.Name.StartsWith("param", StringComparison.OrdinalIgnoreCase) &&
                                   !string.IsNullOrWhiteSpace((string)p.Value))
                       .Select(p =>
                       {
                           // חילוץ המספר אחרי "param"
                           var index = p.Name.Substring(5);   // לדוגמה "1" מתוך "param1"

                           return new JObject
                           {
                               ["name"] = index,              // "1", "2" … כפי שמוגדר ב-WABA
                               ["kind"] = "text",             // השאר "text" אלא אם בתבנית זה quickAction / url
                               ["text"] = (string)p.Value
                           };
                       }));

                */

/*

// ---------- 3. קריאת ChannelRegistrationId מה-Channel Instance ----------
var fromAddress = (string)original["From"];
                var channelRegistrationId = GetChannelRegistrationId(serviceProvider, channelDefinitionId, fromAddress, trace);

                if (string.IsNullOrWhiteSpace(channelRegistrationId))
                    throw new InvalidPluginExecutionException($"Channel Registration ID not found for channel instance: {fromAddress}");

                trace.Trace($"Channel Registration ID: {channelRegistrationId}");

                // ---------- 4. הרכבת payload חדש ----------
                var outbound = new JObject
                {
                    ["ChannelDefinitionId"] = channelDefinitionId,
                    ["ChannelRegistrationId"] = channelRegistrationId,
                    ["RequestId"]           = requestId,
                    ["OrganizationId"]      = original["OrganizationId"],
                    ["From"]                = original["From"],
                    ["To"]                  = original["To"],
                    ["template"] = new JObject
                    {
                        ["name"]     = templateNm,
                        ["language"] = language,
                        ["values"]   = valuesArr
                    }
                };

                var newJson = outbound.ToString(Formatting.None);
                trace.Trace($"Outbound payload: {newJson}");

                // ---------- 5. אחזור כתובת הפונקציה ----------
                var funcUrl = FirstNonEmpty(UnsecureConfig, SecureConfig)
                              ?? GetUrlFromEnvVar(serviceProvider, trace);

                if (string.IsNullOrWhiteSpace(funcUrl))
                    throw new InvalidPluginExecutionException("Azure Function URL not configured.");

                trace.Trace($"Function URL to call: {funcUrl}");

                // ---------- 6. קריאה לפונקציה ----------
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var httpResp = http.PostAsync(
                                   funcUrl,
                                   new StringContent(newJson, Encoding.UTF8, "application/json"))
                                   .Result;

                    var respBody = httpResp.Content.ReadAsStringAsync().Result;
                    trace.Trace($"Function status: {httpResp.StatusCode}");
                    trace.Trace($"Function body  : {respBody}");

                    // Parse the response from Azure Function
                    var funcResponse = JObject.Parse(respBody);

                    // ---------- 7. בניית תגובה בפורמט הנכון ל-D365 ----------
                    var d365Response = new JObject
                    {
                        ["ChannelDefinitionId"] = channelDefinitionId,
                        ["RequestId"] = requestId,
                        ["Status"] = funcResponse.Value<bool>("success") ? "Sent" : "NotSent",
                        ["MessageId"] = funcResponse.Value<string>("messageId") ?? ""
                    };

                    // אם יש שגיאה, הוסף אותה
                    if (!funcResponse.Value<bool>("success"))
                    {
                        d365Response["ErrorMessage"] = funcResponse.Value<string>("error") ?? "Unknown error";
                    }

                    // החזר את התגובה בפורמט הנכון
                    ctx.OutputParameters["response"] = d365Response.ToString(Formatting.None);

                    trace.Trace($"D365 Response: {d365Response.ToString(Formatting.None)}");

                    if (!httpResp.IsSuccessStatusCode)
                        throw new InvalidPluginExecutionException(
                            $"Function error: {httpResp.StatusCode} - {respBody}");

                    trace.Trace("alex_PostExecute_WaOutboundApi: DONE");
                }
            }
            catch (Exception ex)
            {
                trace.Trace($"Error: {ex}");

                // גם בשגיאה, החזר תגובה בפורמט הנכון
                var errorResponse = new JObject
                {
                    ["ChannelDefinitionId"] = ctx.InputParameters.Contains("payload")
                        ? (string)JObject.Parse((string)ctx.InputParameters["payload"])["ChannelDefinitionId"]
                        : "",
                    ["RequestId"] = ctx.InputParameters.Contains("payload")
                        ? (string)JObject.Parse((string)ctx.InputParameters["payload"])["RequestId"]
                        : "",
                    ["Status"] = "NotSent",
                    ["ErrorMessage"] = ex.Message
                };

                ctx.OutputParameters["response"] = errorResponse.ToString(Formatting.None);

                throw new InvalidPluginExecutionException("Error sending WhatsApp message.", ex);
            }
        }

        // ----------  Helpers ----------
        private static string FirstNonEmpty(params string[] vals) =>
            vals?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static string GetChannelRegistrationId(IServiceProvider sp, string channelDefinitionId, string fromAddress, ITracingService trace)
        {
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            // Step 1: חיפוש Channel Instance לפי Channel Definition ID ו-Contact Point (מספר טלפון)
            var query = new QueryExpression("msdyn_channelinstance")
            {
                ColumnSet = new ColumnSet("msdyn_extendedentityid", "msdyn_name", "msdyn_contactpoint"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("msdyn_channeldefinitionid", ConditionOperator.Equal, channelDefinitionId),
                        new ConditionExpression("msdyn_contactpoint", ConditionOperator.Equal, fromAddress),
                        new ConditionExpression("statecode", ConditionOperator.Equal, 0) // Active
                    }
                }
            };

            var results = svc.RetrieveMultiple(query);

            if (results.Entities.Count == 0)
            {
                trace.Trace($"No active channel instance found for definition {channelDefinitionId} and contact point {fromAddress}");
                return null;
            }

            var channelInstance = results.Entities.First();
            trace.Trace($"Found channel instance: {channelInstance.GetAttributeValue<string>("msdyn_name")}");

            // Step 2: קבל את ה-EntityReference ל-alex_wachannelinstance
            var waChannelInstanceRef = channelInstance.GetAttributeValue<EntityReference>("msdyn_extendedentityid");

            if (waChannelInstanceRef == null)
            {
                trace.Trace("msdyn_extendedentityid is null");
                return null;
            }

            trace.Trace($"WA Channel Instance ID: {waChannelInstanceRef.Id}");

            // Step 3: קרא את הרשומה מ-alex_wachannelinstance
            try
            {
                var waChannelInstance = svc.Retrieve("alex_wachannelinstance",
                    waChannelInstanceRef.Id,
                    new ColumnSet("alex_channelregistrationid", "alex_name", "alex_wabanumber"));

                // קח את ה-Registration ID
                var registrationId = waChannelInstance.GetAttributeValue<string>("alex_channelregistrationid");

                trace.Trace($"Found ACS Registration ID: {registrationId}");
                return registrationId;
            }
            catch (Exception ex)
            {
                trace.Trace($"Error retrieving alex_wachannelinstance: {ex.Message}");
                return null;
            }
        }

        private static string GetUrlFromEnvVar(IServiceProvider sp, ITracingService trace)
        {
            if (!string.IsNullOrWhiteSpace(_cachedFuncUrl))
                return _cachedFuncUrl;

            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            var qe = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue"),
                Criteria  = { Conditions =
                {
                    new ConditionExpression("schemaname", ConditionOperator.Equal, ENV_VAR_SCHEMA)
                }}
            };
            var link = qe.AddLink("environmentvariablevalue", "environmentvariabledefinitionid",
                                  "environmentvariabledefinitionid", JoinOperator.LeftOuter);
            link.Columns     = new ColumnSet("value");
            link.EntityAlias = "val";

            var def = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
            var currentVal = (def?["val.value"] as AliasedValue)?.Value as string;
            var defaultVal = def?.GetAttributeValue<string>("defaultvalue");
            _cachedFuncUrl = FirstNonEmpty(currentVal, defaultVal)?.Trim();

            trace.Trace($"EnvVar URL resolved to: {_cachedFuncUrl ?? "<null>"}");
            return _cachedFuncUrl;
        }
    }
}

*/


/*
 * גרסה שעבדה מעולה לפני שינוי גדול מאוד
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlexWaCustomChannel.Plugins
{
    /// <summary>Plugin that forwards WhatsApp messages to Azure Function in CI-Journeys format</summary>
    public class alex_PostExecute_WaOutboundApi : IPlugin
    {
        private readonly string UnsecureConfig;
        private readonly string SecureConfig;
        private const string ENV_VAR_SCHEMA = "alex_alex_wa_func_url";
        private static string _cachedFuncUrl;

        public alex_PostExecute_WaOutboundApi(string unsecureConfig)
            : this(unsecureConfig, null) { }

        public alex_PostExecute_WaOutboundApi(string unsecureConfig, string secureConfig)
        {
            UnsecureConfig = unsecureConfig?.Trim();
            SecureConfig   = secureConfig?.Trim();
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            trace.Trace($"⏺ UnsecureConfig = '{UnsecureConfig}'");
            trace.Trace($"⏺ SecureConfig   = '{SecureConfig}'");

            try
            {
                if (ctx.MessageName != "alex_waoutboundapi")
                    return;

                if (!ctx.InputParameters.Contains("payload"))
                    throw new InvalidPluginExecutionException("Missing parameter: payload");

                // ---------- 1. קריאת ה-payload המקורי ----------
                var originalJson = (string)ctx.InputParameters["payload"];
                trace.Trace($"Payload received: {originalJson}");
                var original = JObject.Parse(originalJson);

                // שמירת ערכים חשובים מה-payload המקורי
                var channelDefinitionId = (string)original["ChannelDefinitionId"];
                var requestId = (string)original["RequestId"];

                // ---------- 2. חילוץ פרטי תבנית ומילוי ערכים ----------
                var msg = original["Message"] as JObject ?? new JObject();
                var templateNm = (string)msg["templename"] ?? "";
                var language = (string)msg["language"]   ?? "he";

                if (string.IsNullOrWhiteSpace(templateNm))
                    throw new InvalidPluginExecutionException("templename missing in Message.");

                // בנה מערך values מתוך paramX
                var valuesArr = new JArray(
                    msg.Properties()
                       .Where(p => p.Name.StartsWith("param", StringComparison.OrdinalIgnoreCase) &&
                                   !string.IsNullOrWhiteSpace((string)p.Value))
                       .Select(p =>
                       {
                           // חילוץ המספר אחרי "param"
                           var index = p.Name.Substring(5);   // לדוגמה "1" מתוך "param1"

                           return new JObject
                           {
                               ["name"] = index,              // "1", "2" … כפי שמוגדר ב-WABA
                               ["kind"] = "text",             // השאר "text" אלא אם בתבנית זה quickAction / url
                               ["text"] = (string)p.Value
                           };
                       }));

                // ---------- 3. הרכבת payload חדש ----------
                var outbound = new JObject
                {
                    ["ChannelDefinitionId"] = channelDefinitionId,
                    ["ChannelRegistrationId"] = "4bce1aca-81cc-48fd-b78d-5bc19a9a37a7",
                    ["RequestId"]           = requestId,
                    ["OrganizationId"]      = original["OrganizationId"],
                    ["From"]                = original["From"],
                    ["To"]                  = original["To"],
                    ["template"] = new JObject
                    {
                        ["name"]     = templateNm,
                        ["language"] = language,
                        ["values"]   = valuesArr
                    }
                };

                var newJson = outbound.ToString(Formatting.None);
                trace.Trace($"Outbound payload: {newJson}");

                // ---------- 4. אחזור כתובת הפונקציה ----------
                var funcUrl = FirstNonEmpty(UnsecureConfig, SecureConfig)
                              ?? GetUrlFromEnvVar(serviceProvider, trace);

                if (string.IsNullOrWhiteSpace(funcUrl))
                    throw new InvalidPluginExecutionException("Azure Function URL not configured.");

                trace.Trace($"Function URL to call: {funcUrl}");

                // ---------- 5. קריאה לפונקציה ----------
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var httpResp = http.PostAsync(
                                   funcUrl,
                                   new StringContent(newJson, Encoding.UTF8, "application/json"))
                                   .Result;

                    var respBody = httpResp.Content.ReadAsStringAsync().Result;
                    trace.Trace($"Function status: {httpResp.StatusCode}");
                    trace.Trace($"Function body  : {respBody}");

                    // Parse the response from Azure Function
                    var funcResponse = JObject.Parse(respBody);

                    // ---------- 6. בניית תגובה בפורמט הנכון ל-D365 ----------
                    var d365Response = new JObject
                    {
                        ["ChannelDefinitionId"] = channelDefinitionId,
                        ["RequestId"] = requestId,
                        ["Status"] = funcResponse.Value<bool>("success") ? "Sent" : "NotSent",
                        ["MessageId"] = funcResponse.Value<string>("messageId") ?? ""
                    };

                    // אם יש שגיאה, הוסף אותה
                    if (!funcResponse.Value<bool>("success"))
                    {
                        d365Response["ErrorMessage"] = funcResponse.Value<string>("error") ?? "Unknown error";
                    }

                    // החזר את התגובה בפורמט הנכון
                    ctx.OutputParameters["response"] = d365Response.ToString(Formatting.None);

                    trace.Trace($"D365 Response: {d365Response.ToString(Formatting.None)}");

                    if (!httpResp.IsSuccessStatusCode)
                        throw new InvalidPluginExecutionException(
                            $"Function error: {httpResp.StatusCode} - {respBody}");

                    trace.Trace("alex_PostExecute_WaOutboundApi: DONE");
                }
            }
            catch (Exception ex)
            {
                trace.Trace($"Error: {ex}");

                // גם בשגיאה, החזר תגובה בפורמט הנכון
                var errorResponse = new JObject
                {
                    ["ChannelDefinitionId"] = ctx.InputParameters.Contains("payload")
                        ? (string)JObject.Parse((string)ctx.InputParameters["payload"])["ChannelDefinitionId"]
                        : "",
                    ["RequestId"] = ctx.InputParameters.Contains("payload")
                        ? (string)JObject.Parse((string)ctx.InputParameters["payload"])["RequestId"]
                        : "",
                    ["Status"] = "NotSent",
                    ["ErrorMessage"] = ex.Message
                };

                ctx.OutputParameters["response"] = errorResponse.ToString(Formatting.None);

                throw new InvalidPluginExecutionException("Error sending WhatsApp message.", ex);
            }
        }

        // ----------  Helpers ----------
        private static string FirstNonEmpty(params string[] vals) =>
            vals?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static string GetUrlFromEnvVar(IServiceProvider sp, ITracingService trace)
        {
            if (!string.IsNullOrWhiteSpace(_cachedFuncUrl))
                return _cachedFuncUrl;

            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(null);

            var qe = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue"),
                Criteria  = { Conditions =
                {
                    new ConditionExpression("schemaname", ConditionOperator.Equal, ENV_VAR_SCHEMA)
                }}
            };
            var link = qe.AddLink("environmentvariablevalue", "environmentvariabledefinitionid",
                                  "environmentvariabledefinitionid", JoinOperator.LeftOuter);
            link.Columns     = new ColumnSet("value");
            link.EntityAlias = "val";

            var def = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
            var currentVal = (def?["val.value"] as AliasedValue)?.Value as string;
            var defaultVal = def?.GetAttributeValue<string>("defaultvalue");
            _cachedFuncUrl = FirstNonEmpty(currentVal, defaultVal)?.Trim();

            trace.Trace($"EnvVar URL resolved to: {_cachedFuncUrl ?? "<null>"}");
            return _cachedFuncUrl;
        }
    }
}

*/


/* using System;
using System.Net.Http;
using System.Text;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;

namespace AlexWaCustomChannel.Plugins
{
    /// <summary>
    /// Plugin to handle WhatsApp outbound messages via Azure Function
    /// </summary>
    public class alex_PostExecute_WaOutboundApi : IPlugin
    {
        private readonly string UnsecureConfig;
        private readonly string SecureConfig;

        /// <summary>
        /// Default constructor required for plugin registration
        /// </summary>
        public alex_PostExecute_WaOutboundApi()
        {
        }

        /// <summary>
        /// Constructor with configuration parameters
        /// </summary>
        public alex_PostExecute_WaOutboundApi(string unsecureConfig, string secureConfig)
        {
            UnsecureConfig = unsecureConfig;
            SecureConfig = secureConfig;
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            // Obtain execution context
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            // Obtain tracing service for debugging
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            // אחרי שורה 40, הוסף:
            tracingService.Trace($"Input Parameters Count: {context.InputParameters.Count}");
            foreach (var param in context.InputParameters)
            {
                tracingService.Trace($"Parameter: {param.Key} = {param.Value}");
            }

            try
            {
                tracingService.Trace("alex_PostExecute_WaOutboundApi: Starting execution");

                // Validate we're in the correct context
                if (context.MessageName != "alex_waoutboundapi")
                {
                    tracingService.Trace("Incorrect message name: " + context.MessageName);
                    return;
                }

                // Get payload from input parameters - שים לב לקידומת alex_
                if (!context.InputParameters.Contains("payload"))
                {
                    throw new InvalidPluginExecutionException("Missing required parameter: payload");
                }

                var jsonPayload = (string)context.InputParameters["payload"];
                tracingService.Trace($"Payload received: {jsonPayload}");

                // Get Azure Function URL from configuration
                // במקום הקוד הנוכחי, שים:
                var functionUrl = UnsecureConfig;
                tracingService.Trace($"UnsecureConfig value: '{UnsecureConfig}'");

                if (string.IsNullOrEmpty(functionUrl))
                {
                    throw new InvalidPluginExecutionException("Azure Function URL not configured. Please set it in the plugin step Unsecure Configuration.");
                }

                if (string.IsNullOrEmpty(functionUrl))
                {
                    // Try to get from secure configuration
                    functionUrl = SecureConfig;

                    if (string.IsNullOrEmpty(functionUrl))
                    {
                        throw new InvalidPluginExecutionException("Azure Function URL not configured. Please set it in the plugin step Unsecure Configuration.");
                    }
                }

                tracingService.Trace($"Function URL: {functionUrl}");

                // Call Azure Function
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(30);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var response = httpClient.PostAsync(functionUrl, content).Result;

                    var responseContent = response.Content.ReadAsStringAsync().Result;
                    tracingService.Trace($"Function response status: {response.StatusCode}");
                    tracingService.Trace($"Function response content: {responseContent}");

                    // Set response in output parameters - שים לב לקידומת alex_
                    context.OutputParameters["response"] = responseContent;

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidPluginExecutionException($"Azure Function returned error: {response.StatusCode} - {responseContent}");
                    }
                }

                tracingService.Trace("alex_PostExecute_WaOutboundApi: Execution completed successfully");
            }
            catch (Exception ex)
            {
                tracingService.Trace($"Error in alex_PostExecute_WaOutboundApi: {ex.Message}");
                tracingService.Trace($"Stack trace: {ex.StackTrace}");

                // Create error response
                var errorResponse = JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = ex.Message,
                    timestamp = DateTime.UtcNow
                });

                context.OutputParameters["alex_response"] = errorResponse;

                throw new InvalidPluginExecutionException($"Error sending WhatsApp message: {ex.Message}", ex);
            }
        }
    }
}
*/