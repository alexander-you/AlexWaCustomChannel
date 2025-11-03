# AlexWaCustomChannel  
A custom WhatsApp channel integration for Dynamics 365 Customer Insights Journeys. This project allows marketing journeys to send WhatsApp template messages via Azure Communication Services.  
## Project structure  
- `AzureFunction` - .NET 8 Azure Function that receives HTTP triggers from CIJ and relays them to Azure Communication Services to send a WhatsApp template message.  
- `Setup-AlexWaChannel-English.ps1` - PowerShell script to register the custom channel in Dynamics 365.  
- `alex_wa_config.json` - configuration file for the channel and template mappings.  
- `Plugin` and `Solution` - Dataverse plugin assembly and solution files that define the custom channel entity and configuration needed inside CIJ.  
## Architecture  
1. Customer Insights Journeys triggers a custom channel action during a customer journey.  
2. The custom channel plugin calls the Azure Function endpoint with details of the recipient and template parameters.  
3. The Azure Function uses the Azure Communication Services Messages client to send a WhatsApp template message to the recipient.  
4. The function returns a status to CIJ so that the journey can continue.  
This integration decouples the marketing platform from ACS and allows you to control message content and sending logic.  
## Prerequisites  
- An Azure subscription with an Azure Communication Services resource enabled for WhatsApp messaging.  
- A verified WhatsApp business number configured in ACS and approved templates.  
- A Dynamics 365 Customer Insights Journeys environment with administrator rights.  
- .NET 8 SDK if you plan to build or modify the function locally.  
- PowerShell for running the setup script.  
## Setup  
1. Clone or download this repository.  
2. Deploy the Azure Function:  
   - Use Visual Studio or the Azure portal to publish the `AzureFunction` project to an Azure Function App.  
   - Configure application settings in the Function App:  
     - `ACS_ConnectionString` - your Azure Communication Services connection string.  
     - `FromPhoneNumber` - your ACS configured WhatsApp number in international format.  
     - `AzureAdTenantId`, `AzureAdClientId`, `AzureAdClientSecret` - if using Azure AD secured endpoint.  
   - Test the function locally using `func start` or `dotnet run`.  
3. Register the custom channel in Customer Insights Journeys:  
   - Update `alex_wa_config.json` with your function endpoint and template names.  
   - Run `Setup-AlexWaChannel-English.ps1` with appropriate parameters (environment URL, application id and secret). This script uploads the solution and plugin, creates the channel configuration record, and sets up authentication.  
4. In Customer Insights Journeys, create or modify a journey and add the custom WhatsApp channel tile. Select the appropriate template name and map dynamic variables.  
## Example HTTP payload  
When CIJ calls the Azure Function it sends a JSON payload similar to:  
```json
{
  "contextId": "journey-instance-id",
  "phone": "+972501234567",
  "templateName": "order_confirmation",
  "parameters": [
    {
      "name": "customerName",
      "value": "Dana"
    },
    {
      "name": "orderNumber",
      "value": "12345"
    }
  ]
}
```  
The function maps these parameters to the ACS WhatsApp template and sends a message.  
## Contributing  
Pull requests and suggestions are welcome. If you discover a bug or have a feature request, please open an issue.  
## License  
Specify your chosen license here (e.g., MIT) to clarify how others may use this project. 
