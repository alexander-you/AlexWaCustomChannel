using System;
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
        public void Execute(IServiceProvider serviceProvider)
        {
            // Obtain execution context
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            
            // Obtain tracing service for debugging
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            
            try
            {
                tracingService.Trace("alex_PostExecute_WaOutboundApi: Starting execution");
                
                // Validate we're in the correct context
                if (context.MessageName != "alexWaOutboundApi")
                {
                    tracingService.Trace("Incorrect message name: " + context.MessageName);
                    return;
                }
                
                // Get payload from input parameters
                if (!context.InputParameters.Contains("payload"))
                {
                    throw new InvalidPluginExecutionException("Missing required parameter: payload");
                }
                
                var jsonPayload = (string)context.InputParameters["payload"];
                tracingService.Trace($"Payload received: {jsonPayload}");
                
                // Get Azure Function URL from environment variable
                var functionUrl = Environment.GetEnvironmentVariable("ALEX_WA_FUNC_URL");
                if (string.IsNullOrEmpty(functionUrl))
                {
                    // Fallback to secure configuration
                    functionUrl = GetSecureConfig(serviceProvider, "ALEX_WA_FUNC_URL");
                    
                    if (string.IsNullOrEmpty(functionUrl))
                    {
                        throw new InvalidPluginExecutionException("Azure Function URL not configured. Please set ALEX_WA_FUNC_URL.");
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
                    
                    // Set response in output parameters
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
                
                context.OutputParameters["response"] = errorResponse;
                
                throw new InvalidPluginExecutionException($"Error sending WhatsApp message: {ex.Message}", ex);
            }
        }
        
        private string GetSecureConfig(IServiceProvider serviceProvider, string key)
        {
            // Implementation for retrieving secure configuration
            // This would typically come from plugin secure configuration
            return null;
        }
    }
}