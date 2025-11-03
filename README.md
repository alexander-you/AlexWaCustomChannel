# WaCustomChannel  
A custom WhatsApp channel integration for Dynamics 365 Customer Insights – Journeys using Azure Communication Services. This project enables marketing journeys to send WhatsApp template messages via Azure Communication Services.  

## Project structure  
- `AzureFunction` – .NET 8 Azure Function that receives HTTP triggers from CIJ and relays them to Azure Communication Services to send a WhatsApp template message.  
- `Setup-WaChannel-English.ps1` – PowerShell script to register the custom channel in Dynamics 365.  
- `wa_config.json` – configuration file for the channel and template mappings.  
- `Plugin` and `Solution` – Dataverse plugin assembly and solution files that define the custom channel entity and configuration needed inside CIJ.  

## Architecture  
1. Customer Insights – Journeys triggers a custom channel action during a customer journey.  
2. The custom channel plugin calls the Azure Function endpoint with details of the recipient and template parameters.  
3. The Azure Function uses the Azure Communication Services Messages client to send a WhatsApp template message to the recipient.  
4. The function returns a status to CIJ so that the journey can continue.  

This integration decouples the marketing platform from ACS and allows you to control message content and sending logic.  

## Prerequisites  
- An Azure subscription with an Azure Communication Services resource enabled for WhatsApp messaging.  
- A verified WhatsApp business number configured in ACS and approved templates.  
- A Dynamics 365 Customer Insights – Journeys environment with administrator rights.  
- .NET 8 SDK if you plan to build or modify the function locally.  
- PowerShell for running the setup script.  

## Setup  
1. Clone or download this repository.  
2. Deploy the Azure Function using the provided deploy.zip or via the included PowerShell script.  
3. Run `Setup-WaChannel-English.ps1` to create the required Azure resources and register the custom channel in Dynamics. The script will prompt you for resource names and will generate `wa_config.json` with your function URL and other settings.  
4. Import the Dataverse solution found under `Solution` into your CIJ environment.  
5. Register the plugin assembly from the `Plugin/bin/Release` folder using the Plugin Registration Tool.  
6. Configure the custom channel in CIJ using the generated `wa_config.json` and plugin file name.  

## Usage  
After setup, you can configure customer journeys in CIJ to use the new WhatsApp custom channel. When triggered, the channel will call the Azure Function to send WhatsApp template messages through ACS.  

## Contributing  
Contributions are welcome! Please fork this repository and open a pull request with your improvements.  

## License  
Specify a license here if desired.
