using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Azure;
using Azure.Communication.Messages;
using Azure.Communication.Messages.Models.Channels;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AlexWaCustomChannel.Functions
{
    public class SendAlexWaTemplate
    {
        private readonly ILogger _log;
        private const string KV_URL_ENV = "ALEX_KEYVAULT_URL";
        private const string ACS_CONN_ENV = "ALEX_ACS_CONNECTION_STRING";
        private const string ACS_CONN_KV = "alex-acs-connection-string";

        public SendAlexWaTemplate(ILoggerFactory loggerFactory)
        {
            _log = loggerFactory.CreateLogger<SendAlexWaTemplate>();
        }

        [Function("SendAlexWaTemplate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _log.LogInformation("SendAlexWaTemplate – request received.");

            try
            {
                // 1) Read JSON body
                string bodyJson = await new StreamReader(req.Body).ReadToEndAsync();
                _log.LogInformation($"Request body: {bodyJson}");

                var waReq = JsonSerializer.Deserialize<WaRequest>(bodyJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (waReq?.template is null)
                    return await CreateErrorResponse(req, "template section missing", HttpStatusCode.BadRequest);

                // 2) Get ACS connection string
                string? connStr = await GetAcsConnAsync();
                if (string.IsNullOrWhiteSpace(connStr))
                    return await CreateErrorResponse(req, "ACS connection string missing", HttpStatusCode.InternalServerError);

                var client = new NotificationMessagesClient(connStr);

                // 3) Build MessageTemplate
                var tpl = waReq.template;
                var msgTemplate = BuildTemplate(tpl.name, tpl.language ?? "he", tpl.values);

                // 4) Log what we're sending
                _log.LogInformation($"Sending template '{tpl.name}' with {tpl.values?.Count ?? 0} parameters");

                // 5) Prepare and send notification
                var channelGuid = Guid.Parse(waReq.ChannelRegistrationId);
                var recipients = new List<string> { waReq.To };
                var content = new TemplateNotificationContent(channelGuid, recipients, msgTemplate);

                var sendResult = await client.SendAsync(content);

                // 6) Success response
                var responseObj = new WaResponse
                {
                    Success      = true,
                    MessageId    = sendResult.Value.Receipts[0].MessageId,
                    Status       = sendResult.Value.Receipts[0].To,
                    Recipient    = waReq.To,
                    TemplateName = tpl.name,
                    Timestamp    = DateTime.UtcNow
                };

                var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string jsonString = JsonSerializer.Serialize(responseObj, jsonOptions);

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(jsonString, Encoding.UTF8);
                return res;
            }
            catch (RequestFailedException ex)
            {
                _log.LogError(ex, "ACS request failed");
                return await CreateErrorResponse(req, $"ACS Error: {ex.Message}", HttpStatusCode.BadGateway);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled exception");
                return await CreateErrorResponse(req, ex.Message, HttpStatusCode.InternalServerError);
            }
        }

   //     private MessageTemplate BuildTemplate(
    //string templateName,
    //string templateLanguage,
    //List<Value> parameters)

            /*
        //פונקציה חדשה אולי תעשה באלגן התחלה  
        {
            var template = new MessageTemplate(templateName, templateLanguage);

            if (parameters == null || parameters.Count == 0)
            {
                _log.LogInformation("No parameters provided");
                return template;
            }

            _log.LogInformation($"Building template: {templateName} with {parameters.Count} parameters");

            // בדיקה האם זו תבנית עם כפתור
            bool hasUrlButton = templateName.Contains("link") ||
                               templateName.Contains("survey");

            // אם זו תבנית עם כפתור URL ויש לנו 2 פרמטרים
            if (hasUrlButton && parameters.Count >= 2)
            {
                // יצירת bindings
                var bindings = new WhatsAppMessageTemplateBindings();

                // Body binding
                var bodyBinding = new WhatsAppMessageTemplateBindingsComponent();
                bodyBinding.RefValue = "1";
                bindings.Body.Add(bodyBinding);

                // Button binding - נסה dynamic כדי לעקוף בעיות טיפוסים
                dynamic buttonBinding = new WhatsAppMessageTemplateBindingsButton();
                buttonBinding.SubType = "url";
                buttonBinding.RefValue = "2";
                bindings.Buttons.Add(buttonBinding);

                template.Bindings = bindings;

                // הוספת הערכים
                template.Values.Add(new MessageTemplateText("1", parameters[0].text));

                var quickAction = new MessageTemplateQuickAction("2");
                quickAction.Text = parameters[1].text;
                template.Values.Add(quickAction);

                _log.LogInformation($"Built URL button template with 2 parameters");
            }
            else
            {
                // תבנית רגילה - כמו שהיה קודם
                var bindings = new WhatsAppMessageTemplateBindings();
                bindings.Body.Add(new WhatsAppMessageTemplateBindingsComponent("1"));
                template.Bindings = bindings;

                foreach (var p in parameters.Where(v => v.kind == "text" && !string.IsNullOrEmpty(v.text)))
                {
                    string index = p.name;
                    template.Values.Add(new MessageTemplateText(index, p.text));
                }
            }

            return template;
        }

        //פונקציה חדשה אולי תעשה באלגן סיום  

            */
            
            
        //פונקציה קודמת שעבדה - זו מרכיבה את התנבנית 
        // Build template with support for text parameters and media headers
        private MessageTemplate BuildTemplate(
            string templateName,
            string templateLanguage,
            List<Value> parameters)
        {
            var template = new MessageTemplate(templateName, templateLanguage);

            // Only add bindings if we have parameters
            if (parameters != null && parameters.Count > 0)
            {
                var bindings = new WhatsAppMessageTemplateBindings();
                var hasBindings = false;

                // 1. Process body text parameters
                foreach (var p in parameters.Where(v =>
                    v.kind == "text" &&
                    !string.IsNullOrEmpty(v.text) &&
                    v.name != "headerMedia")) // Skip headerMedia
                {
                    // The plugin sends index as "1", "2" etc.
                    string index = p.name;

                    _log.LogInformation($"Adding body parameter {index}: {p.text}");

                    bindings.Body.Add(new WhatsAppMessageTemplateBindingsComponent(index));
                    template.Values.Add(new MessageTemplateText(index, p.text));
                    hasBindings = true;
                }

                // 2. Process media header if exists
                var mediaParam = parameters.FirstOrDefault(v =>
                 (v.name == "headerMedia" || v.name == "documentfile" || v.name == "location") &&
                 (!string.IsNullOrEmpty(v.url) || v.kind == "location"));

                if (mediaParam != null)
                {
                    // לוג מה נמצא
                    _log.LogInformation($"Processing media header: {mediaParam.kind} - {mediaParam.url}");

                    // Media headers need special handling
                    bindings.Header.Add(new WhatsAppMessageTemplateBindingsComponent("header"));
                    hasBindings = true;

                    // 🔧 טפל ב-location קודם, לפני יצירת URI
                    if (mediaParam.kind?.ToLower() == "location")
                    {
                        var location = new MessageTemplateLocation("header");
                        location.LocationName = mediaParam.text;
                        location.Address = mediaParam.address ?? "";

                        if (double.TryParse(mediaParam.latitude, out double lat) &&
                            double.TryParse(mediaParam.longitude, out double lng))
                        {
                            location.Position = new Azure.Core.GeoJson.GeoPosition(lng, lat);
                        }

                        template.Values.Add(location);
                        _log.LogInformation("Added location to template header");
                    }
                    else
                    {
                        // רק עבור media (לא location) ניצור URI

                        try
                        {
                            var mediaUri = new Uri(mediaParam.url);

                            switch (mediaParam.kind?.ToLower())
                            {
                                case "video":
                                    template.Values.Add(new MessageTemplateVideo("header", mediaUri));
                                    _log.LogInformation("Added video to template header");
                                    break;

                                case "document":
                                    _log.LogInformation($"Document filename from payload: {mediaParam.text}");
                                    var documentTemplate = new MessageTemplateDocument("header", mediaUri);
                                    template.Values.Add(documentTemplate);
                                    _log.LogInformation("Added document to template header");
                                    break;

                                case "location":  // 🔧 הזז לפני default!
                                    var location = new MessageTemplateLocation("header");
                                    location.LocationName = mediaParam.text;
                                    location.Address = mediaParam.address ?? ""; // 🔧 מהpayload

                                    // 🔧 קורדינטות מהpayload
                                    if (double.TryParse(mediaParam.latitude, out double lat) &&
                                        double.TryParse(mediaParam.longitude, out double lng))
                                    {
                                        location.Position = new Azure.Core.GeoJson.GeoPosition(lng, lat);
                                    }

                                    template.Values.Add(location);
                                    _log.LogInformation("Added location to template header");
                                    break;

                                case "image":
                                default:
                                    template.Values.Add(new MessageTemplateImage("header", mediaUri));
                                    _log.LogInformation("Added image to template header");
                                    break;


                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError($"Error processing media URL: {ex.Message}");
                            throw;
                        }
                    }
                }
                // Only set bindings if we actually added any
                if (hasBindings)
                {
                    template.Bindings = bindings;
                    _log.LogInformation($"Template has {bindings.Body.Count} body bindings and {bindings.Header.Count} header bindings");
                }
                else
                {
                    _log.LogInformation("No bindings added - template will be sent without parameters");
                }
            }
            else
            {
                _log.LogInformation("No parameters provided - sending template without bindings");
            }

            return template;
        }
      

        // Get ACS connection string
        private async Task<string?> GetAcsConnAsync()
        {
            string? kvUrl = Environment.GetEnvironmentVariable(KV_URL_ENV);
            if (!string.IsNullOrWhiteSpace(kvUrl))
            {
                try
                {
                    var kv = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());
                    var secret = await kv.GetSecretAsync(ACS_CONN_KV);
                    string? val = secret.Value.Value;
                    if (!string.IsNullOrWhiteSpace(val))
                        return val;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Failed to get secret from Key Vault: {ex.Message}");
                }
            }
            return Environment.GetEnvironmentVariable(ACS_CONN_ENV);
        }

        // Error response helper
        private static async Task<HttpResponseData> CreateErrorResponse(
            HttpRequestData req,
            string error,
            HttpStatusCode code)
        {
            var errObj = new WaResponse
            {
                Success   = false,
                Error     = error,
                Timestamp = DateTime.UtcNow
            };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonString = JsonSerializer.Serialize(errObj, jsonOptions);

            var res = req.CreateResponse(code);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(jsonString, Encoding.UTF8);
            return res;
        }
    }

    // DTOs
    public class WaRequest
    {
        public string ChannelRegistrationId { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public Template template { get; set; } = new Template();
    }

    public class Template
    {
        public string name { get; set; } = string.Empty;
        public string language { get; set; } = "he";
        public List<Value> values { get; set; } = new();
    }

    public class Value
    {
        public string name { get; set; } = string.Empty;
        public string kind { get; set; } = "text";
        public string text { get; set; } = string.Empty;
        public string url { get; set; } = string.Empty;
        public string address { get; set; } = string.Empty;      
        public string latitude { get; set; } = string.Empty;     
        public string longitude { get; set; } = string.Empty;    
    }

    public class WaResponse
    {
        public bool Success { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public string TemplateName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? Error { get; set; }
    }
}


/*
// -------------------------------------------
//  AlexWaCustomChannel.Functions.SendAlexWaTemplate
//  Simplified version that works with current ACS SDK
//  Supports: Text templates, Media headers (image/video/document)
//  Limited support: Location, Interactive buttons
// -------------------------------------------

// Minimal Azure Function that works with your current plugin
// This version expects the exact format your plugin sends

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Azure;
using Azure.Communication.Messages;
using Azure.Communication.Messages.Models.Channels;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AlexWaCustomChannel.Functions
{
    public class SendAlexWaTemplate
    {
        private readonly ILogger _log;
        private const string KV_URL_ENV = "ALEX_KEYVAULT_URL";
        private const string ACS_CONN_ENV = "ALEX_ACS_CONNECTION_STRING";
        private const string ACS_CONN_KV = "alex-acs-connection-string";

        public SendAlexWaTemplate(ILoggerFactory loggerFactory)
        {
            _log = loggerFactory.CreateLogger<SendAlexWaTemplate>();
        }

        [Function("SendAlexWaTemplate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _log.LogInformation("SendAlexWaTemplate – request received.");

            try
            {
                // 1) Read JSON body
                string bodyJson = await new StreamReader(req.Body).ReadToEndAsync();
                _log.LogInformation($"Request body: {bodyJson}");

                var waReq = JsonSerializer.Deserialize<WaRequest>(bodyJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (waReq?.template is null)
                    return await CreateErrorResponse(req, "template section missing", HttpStatusCode.BadRequest);

                // 2) Get ACS connection string
                string? connStr = await GetAcsConnAsync();
                if (string.IsNullOrWhiteSpace(connStr))
                    return await CreateErrorResponse(req, "ACS connection string missing", HttpStatusCode.InternalServerError);

                var client = new NotificationMessagesClient(connStr);

                // 3) Build MessageTemplate - SIMPLE VERSION
                var tpl = waReq.template;
                var msgTemplate = BuildTemplate(tpl.name, tpl.language ?? "he", tpl.values);

                // 4) Log what we're sending
                _log.LogInformation($"Sending template '{tpl.name}' with {tpl.values?.Count ?? 0} parameters");

                // 5) Prepare and send notification
                var channelGuid = Guid.Parse(waReq.ChannelRegistrationId);
                var recipients = new List<string> { waReq.To };
                var content = new TemplateNotificationContent(channelGuid, recipients, msgTemplate);

                var sendResult = await client.SendAsync(content);

                // 6) Success response
                var responseObj = new WaResponse
                {
                    Success      = true,
                    MessageId    = sendResult.Value.Receipts[0].MessageId,
                    Status       = sendResult.Value.Receipts[0].To,
                    Recipient    = waReq.To,
                    TemplateName = tpl.name,
                    Timestamp    = DateTime.UtcNow
                };

                var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string jsonString = JsonSerializer.Serialize(responseObj, jsonOptions);

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(jsonString, Encoding.UTF8);
                return res;
            }
            catch (RequestFailedException ex)
            {
                _log.LogError(ex, "ACS request failed");
                return await CreateErrorResponse(req, $"ACS Error: {ex.Message}", HttpStatusCode.BadGateway);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled exception");
                return await CreateErrorResponse(req, ex.Message, HttpStatusCode.InternalServerError);
            }
        }

        // Simple template builder that works with current plugin format
        private MessageTemplate BuildTemplate(
            string templateName,
            string templateLanguage,
            List<Value> parameters)
        {
            var template = new MessageTemplate(templateName, templateLanguage);

            // Only add bindings if we have parameters
            if (parameters != null && parameters.Count > 0)
            {
                var bindings = new WhatsAppMessageTemplateBindings();
                var hasBindings = false;

                foreach (var p in parameters.Where(v => v.kind == "text" && !string.IsNullOrEmpty(v.text)))
                {
                    // The plugin sends index as "1", "2" etc.
                    string index = p.name;

                    _log.LogInformation($"Adding parameter {index}: {p.text}");

                    bindings.Body.Add(new WhatsAppMessageTemplateBindingsComponent(index));
                    template.Values.Add(new MessageTemplateText(index, p.text));
                    hasBindings = true;
                }

                // Only set bindings if we actually added any
                if (hasBindings)
                {
                    template.Bindings = bindings;
                    _log.LogInformation($"Template has {bindings.Body.Count} body bindings");
                }
                else
                {
                    _log.LogInformation("No bindings added - template will be sent without parameters");
                }
            }
            else
            {
                _log.LogInformation("No parameters provided - sending template without bindings");
            }

            return template;
        }

        // Get ACS connection string
        private async Task<string?> GetAcsConnAsync()
        {
            string? kvUrl = Environment.GetEnvironmentVariable(KV_URL_ENV);
            if (!string.IsNullOrWhiteSpace(kvUrl))
            {
                try
                {
                    var kv = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());
                    var secret = await kv.GetSecretAsync(ACS_CONN_KV);
                    string? val = secret.Value.Value;
                    if (!string.IsNullOrWhiteSpace(val))
                        return val;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Failed to get secret from Key Vault: {ex.Message}");
                }
            }
            return Environment.GetEnvironmentVariable(ACS_CONN_ENV);
        }

        // Error response helper
        private static async Task<HttpResponseData> CreateErrorResponse(
            HttpRequestData req,
            string error,
            HttpStatusCode code)
        {
            var errObj = new WaResponse
            {
                Success   = false,
                Error     = error,
                Timestamp = DateTime.UtcNow
            };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonString = JsonSerializer.Serialize(errObj, jsonOptions);

            var res = req.CreateResponse(code);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(jsonString, Encoding.UTF8);
            return res;
        }
    }

    // DTOs - exactly matching what the plugin sends
    public class WaRequest
    {
        public string ChannelRegistrationId { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public Template template { get; set; } = new Template();
    }

    public class Template
    {
        public string name { get; set; } = string.Empty;
        public string language { get; set; } = "he";
        public List<Value> values { get; set; } = new();
    }

    public class Value
    {
        public string name { get; set; } = string.Empty;
        public string kind { get; set; } = "text";
        public string text { get; set; } = string.Empty;
    }

    public class WaResponse
    {
        public bool Success { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public string TemplateName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? Error { get; set; }
    }
}

*/


/*
// -------------------------------------------
//  AlexWaCustomChannel.Functions.SendAlexWaTemplate
//  .NET 8 Isolated Azure Function
//  Azure.Communication.Messages 1.1.0 (GA)
// -------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Azure;
using Azure.Communication.Messages;
using Azure.Communication.Messages.Models.Channels;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AlexWaCustomChannel.Functions
{
    public class SendAlexWaTemplate
    {
        private readonly ILogger _log;
        private const string KV_URL_ENV = "ALEX_KEYVAULT_URL";
        private const string ACS_CONN_ENV = "ALEX_ACS_CONNECTION_STRING";
        private const string ACS_CONN_KV = "alex-acs-connection-string";

        public SendAlexWaTemplate(ILoggerFactory loggerFactory)
        {
            _log = loggerFactory.CreateLogger<SendAlexWaTemplate>();
        }

        [Function("SendAlexWaTemplate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _log.LogInformation("SendAlexWaTemplate – request received.");

            // 1) Read JSON body
            string bodyJson = await new StreamReader(req.Body).ReadToEndAsync();
            var waReq = JsonSerializer.Deserialize<WaRequest>(bodyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (waReq?.template is null)
                return await CreateErrorResponse(req, "template section missing", HttpStatusCode.BadRequest);

            // 2) Get ACS connection string
            string? connStr = await GetAcsConnAsync();
            if (string.IsNullOrWhiteSpace(connStr))
                return await CreateErrorResponse(req, "ACS connection string missing", HttpStatusCode.InternalServerError);

            var client = new NotificationMessagesClient(connStr);

            try
            {
                // 3) Build MessageTemplate with WhatsApp bindings
                var tpl = waReq.template;
                var msgTemplate = BuildTemplate(tpl.name, tpl.language ?? "he", tpl.values);

                // 4) Prepare and send notification
                var channelGuid = Guid.Parse(waReq.ChannelRegistrationId);
                var recipients = new List<string> { waReq.To };
                var content = new TemplateNotificationContent(channelGuid, recipients, msgTemplate);
                var sendResult = await client.SendAsync(content);

                // 5) Success response including 'status'
                var responseObj = new WaResponse
                {
                    Success      = true,
                    MessageId    = sendResult.Value.Receipts[0].MessageId,
                    Status       = sendResult.Value.Receipts[0].To,    // ← NEW: include status
                    Recipient    = waReq.To,
                    TemplateName = tpl.name,
                    Timestamp    = DateTime.UtcNow
                };

                // Serialize to camelCase JSON
                var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string jsonString = JsonSerializer.Serialize(responseObj, jsonOptions);

                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(jsonString, Encoding.UTF8);
                return res;
            }
            catch (RequestFailedException ex)
            {
                _log.LogError(ex, "ACS request failed");
                return await CreateErrorResponse(req, ex.Message, HttpStatusCode.BadGateway);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled exception");
                return await CreateErrorResponse(req, ex.Message, HttpStatusCode.InternalServerError);
            }
        }

        // Builds the MessageTemplate and its WhatsApp bindings
        private static MessageTemplate BuildTemplate(
            string templateName,
            string templateLanguage,
            List<Value> parameters)
        {
            var template = new MessageTemplate(templateName, templateLanguage);

            if (parameters?.Count > 0)
            {
                var bindings = new WhatsAppMessageTemplateBindings();
                foreach (var p in parameters)
                {
                    string index = p.name.StartsWith("param", StringComparison.OrdinalIgnoreCase)
                        ? p.name.Substring(5)
                        : p.name;
                    bindings.Body.Add(new WhatsAppMessageTemplateBindingsComponent(index));
                }
                template.Bindings = bindings;

                foreach (var p in parameters)
                {
                    string index = p.name.StartsWith("param", StringComparison.OrdinalIgnoreCase)
                        ? p.name.Substring(5)
                        : p.name;
                    template.Values.Add(new MessageTemplateText(index, p.text));
                }
            }

            return template;
        }

        // Retrieves the ACS connection string from Key Vault or environment
        private async Task<string?> GetAcsConnAsync()
        {
            string? kvUrl = Environment.GetEnvironmentVariable(KV_URL_ENV);
            if (!string.IsNullOrWhiteSpace(kvUrl))
            {
                var kv = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());
                var secret = await kv.GetSecretAsync(ACS_CONN_KV);
                string? val = secret.Value.Value;
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
            return Environment.GetEnvironmentVariable(ACS_CONN_ENV);
        }

        // Error response helper (camelCase JSON)
        private static async Task<HttpResponseData> CreateErrorResponse(
            HttpRequestData req,
            string error,
            HttpStatusCode code)
        {
            var errObj = new WaResponse
            {
                Success   = false,
                Error     = error,
                Timestamp = DateTime.UtcNow
            };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string jsonString = JsonSerializer.Serialize(errObj, jsonOptions);

            var res = req.CreateResponse(code);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(jsonString, Encoding.UTF8);
            return res;
        }
    }

    // DTOs
    public class WaRequest
    {
        public string ChannelRegistrationId { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public Template template { get; set; } = new Template();
    }

    public class Template
    {
        public string name { get; set; } = string.Empty;
        public string language { get; set; } = "he";
        public List<Value> values { get; set; } = new();
    }

    public class Value
    {
        public string name { get; set; } = string.Empty;
        public string text { get; set; } = string.Empty;
    }

    public class WaResponse
    {
        public bool Success { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;  // ← NEW
        public string Recipient { get; set; } = string.Empty;
        public string TemplateName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? Error { get; set; }
    }
}
*/