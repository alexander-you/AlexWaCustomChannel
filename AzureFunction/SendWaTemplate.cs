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

namespace WaCustomChannel.Functions
{
    public class SendWaTemplate
    {
        private readonly ILogger _log;
        private const string KV_URL_ENV = "WA_KEYVAULT_URL";
        private const string ACS_CONN_ENV = "WA_ACS_CONNECTION_STRING";
        private const string ACS_CONN_KV = "wa-acs-connection-string";

        public SendWaTemplate(ILoggerFactory loggerFactory)
        {
            _log = loggerFactory.CreateLogger<SendWaTemplate>();
        }

        [Function("SendWaTemplate")]
        public async Task<HttpResponseData> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _log.LogInformation("SendWaTemplate function triggered.");

            // Read request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            _log.LogInformation($"Request Body: {requestBody}");

            // Parse JSON payload
            JsonDocument payload;
            try
            {
                payload = JsonDocument.Parse(requestBody);
            }
            catch (JsonException ex)
            {
                _log.LogError(ex, "Invalid JSON in request body.");
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Invalid JSON");
                return badResponse;
            }

            // Extract fields (modify according to your payload structure)
            string toPhone = payload.RootElement.GetProperty("to").GetString();
            string templateName = payload.RootElement.GetProperty("templateName").GetString();
            string languageCode = payload.RootElement.GetProperty("languageCode").GetString();
            // Template parameters (assuming array of strings)
            List<string> templateParams = payload.RootElement.GetProperty("templateParams").EnumerateArray().Select(x => x.GetString()).ToList();

            // Resolve ACS connection string
            string acsConnectionString = Environment.GetEnvironmentVariable(ACS_CONN_ENV);
            if (string.IsNullOrEmpty(acsConnectionString))
            {
                string kvUrl = Environment.GetEnvironmentVariable(KV_URL_ENV);
                if (!string.IsNullOrEmpty(kvUrl))
                {
                    var client = new SecretClient(new Uri(kvUrl), new DefaultAzureCredential());
                    try
                    {
                        KeyVaultSecret secret = await client.GetSecretAsync(ACS_CONN_KV);
                        acsConnectionString = secret.Value;
                    }
                    catch (RequestFailedException ex)
                    {
                        _log.LogError(ex, "Failed to retrieve ACS connection string from Key Vault.");
                    }
                }
            }

            if (string.IsNullOrEmpty(acsConnectionString))
            {
                _log.LogError("ACS connection string is not configured.");
                var badResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await badResponse.WriteStringAsync("ACS connection string is not configured.");
                return badResponse;
            }

            // Create ACS client
            var smsClient = new MessagesClient(acsConnectionString);

            // Build message content for WhatsApp
            var content = new WhatsAppMessageContent(templateName, languageCode)
            {
                TemplateParameters = templateParams
            };

            // Create message object
            var message = new WhatsAppMessage(to: toPhone, message: content);

            try
            {
                // Send message
                SendMessageResult result = await smsClient.SendAsync(message);
                _log.LogInformation($"Message sent. MessageId: {result.MessageId}");

                var okResponse = req.CreateResponse(HttpStatusCode.OK);
                await okResponse.WriteStringAsync($"Message sent. MessageId: {result.MessageId}");
                return okResponse;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send message via ACS.");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Failed to send message");
                return errorResponse;
            }
        }
    }
}
