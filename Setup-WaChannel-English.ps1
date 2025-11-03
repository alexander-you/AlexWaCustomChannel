# Setup-AlexWaChannel-English.ps1
# Interactive setup script for Alex WA Channel

Write-Host @"
=========================================================
        Alex WA Channel - Interactive Setup              
                                                         
  This script will guide you step-by-step                
=========================================================
"@ -ForegroundColor Cyan

# Function to wait for user
function Wait-ForUser {
    param([string]$Message = "Press Enter to continue...")
    Write-Host "`n$Message" -ForegroundColor Yellow
    Read-Host
}

# Function to get user input with default
function Get-UserInput {
    param(
        [string]$Prompt,
        [string]$Default
    )
    $input = Read-Host "$Prompt $(if($Default){"[$Default]"})"
    if ([string]::IsNullOrWhiteSpace($input)) { return $Default }
    return $input
}

try {
    # Step 0: Check prerequisites
    Write-Host "`n[CHECK] Checking prerequisites..." -ForegroundColor Green
    
    $missing = @()
    
    # Check Azure CLI
    if (!(Get-Command az -ErrorAction SilentlyContinue)) {
        $missing += "Azure CLI"
    }
    
    # Check .NET
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $missing += ".NET SDK"
    }
    
    # Check Azure Functions Core Tools
    if (!(Get-Command func -ErrorAction SilentlyContinue)) {
        $missing += "Azure Functions Core Tools"
    }
    
    if ($missing.Count -gt 0) {
        Write-Host "[ERROR] Missing tools:" -ForegroundColor Red
        $missing | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
        Write-Host "`nPlease install missing tools and run again." -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "[OK] All required tools are installed!" -ForegroundColor Green
    
    # Step 1: Collect information
    Write-Host "`n[INPUT] Collecting environment details..." -ForegroundColor Green
    
    $resourceGroup = Get-UserInput -Prompt "Resource Group name" -Default "alex-wa-rg"
    $location = Get-UserInput -Prompt "Azure Region" -Default "westeurope"
    
    Write-Host "`nFor Dataverse URL:" -ForegroundColor Yellow
    Write-Host "1. Go to: https://make.powerapps.com" -ForegroundColor Gray
    Write-Host "2. Select your environment" -ForegroundColor Gray
    Write-Host "3. Copy the URL from browser" -ForegroundColor Gray
    $dataverseUrl = Get-UserInput -Prompt "Dataverse URL"
    
    Write-Host "`nFor ACS Connection String:" -ForegroundColor Yellow
    Write-Host "1. Go to Azure Portal" -ForegroundColor Gray
    Write-Host "2. Navigate to Communication Service" -ForegroundColor Gray
    Write-Host "3. Settings -> Keys -> Connection String" -ForegroundColor Gray
    $acsConnection = Get-UserInput -Prompt "ACS Connection String"
    
    # Step 2: Azure login
    Write-Host "`n[LOGIN] Connecting to Azure..." -ForegroundColor Green
    Write-Host "A browser window will open for authentication." -ForegroundColor Yellow
    Wait-ForUser -Message "Press Enter to open browser..."
    
    az login
    
    # Show subscriptions
    Write-Host "`nAvailable Subscriptions:" -ForegroundColor Yellow
    az account list --output table
    
    $subId = Get-UserInput -Prompt "Subscription ID (or Enter for default)"
    if ($subId) {
        az account set --subscription $subId
    }
    
    # Step 3: Create Azure resources
    Write-Host "`n[AZURE] Creating Azure resources..." -ForegroundColor Green
    $createRg = Get-UserInput -Prompt "Create new Resource Group? (Y/N)" -Default "Y"
    
    if ($createRg -eq 'Y') {
        Write-Host "Creating Resource Group: $resourceGroup in $location" -ForegroundColor Yellow
        az group create --name $resourceGroup --location $location
    }
    
    Wait-ForUser
    
    # Storage Account
    Write-Host "`nCreating Storage Account..." -ForegroundColor Yellow
    $storageAccount = "alexwastorage$(Get-Random -Maximum 9999)"
    Write-Host "Name: $storageAccount" -ForegroundColor Gray
    
    az storage account create `
        --name $storageAccount `
        --resource-group $resourceGroup `
        --location $location `
        --sku Standard_LRS
    
    # Function App
    Write-Host "`nCreating Function App..." -ForegroundColor Yellow
    $functionAppName = "alex-wa-func-$(Get-Random -Maximum 9999)"
    Write-Host "Name: $functionAppName" -ForegroundColor Gray
    
    az functionapp create `
        --name $functionAppName `
        --resource-group $resourceGroup `
        --storage-account $storageAccount `
        --consumption-plan-location $location `
        --runtime dotnet-isolated `
        --runtime-version 8 `
        --functions-version 4
    
    # Configure app settings
    Write-Host "`nConfiguring Function App settings..." -ForegroundColor Yellow
    az functionapp config appsettings set `
        --name $functionAppName `
        --resource-group $resourceGroup `
        --settings `
            "ALEX_ACS_CONNECTION_STRING=$acsConnection"
    
    Wait-ForUser
    
    # Step 4: Build code
    Write-Host "`n[BUILD] Building Azure Function..." -ForegroundColor Green
    
    if (!(Test-Path ".\AzureFunction")) {
        Write-Host "[ERROR] AzureFunction folder not found!" -ForegroundColor Red
        Write-Host "Make sure you're in the correct directory with all files." -ForegroundColor Yellow
        exit 1
    }
    
    Push-Location ".\AzureFunction"
    
    Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
    dotnet restore
    
    Write-Host "Building project..." -ForegroundColor Yellow
    dotnet build --configuration Release
    
    Pop-Location
    
    Wait-ForUser -Message "Check build succeeded. Press Enter to continue..."
    
    # Step 5: Deploy
    Write-Host "`n[DEPLOY] Deploying Azure Function..." -ForegroundColor Green
    Write-Host "This may take several minutes..." -ForegroundColor Yellow
    
    Push-Location ".\AzureFunction"
    func azure functionapp publish $functionAppName
    Pop-Location
    
    # Get Function Key
    Write-Host "`nGetting Function Key..." -ForegroundColor Yellow
    $functionKey = az functionapp keys list `
        --name $functionAppName `
        --resource-group $resourceGroup `
        --query "functionKeys.default" -o tsv
    
    $functionUrl = "https://$functionAppName.azurewebsites.net/api/SendAlexWaTemplate?code=$functionKey"
    
    # Step 6: Summary
    Write-Host "`n[SUCCESS] Setup completed successfully!" -ForegroundColor Green
    Write-Host "`nSetup Details:" -ForegroundColor Cyan
    
    $config = @{
        ResourceGroup = $resourceGroup
        FunctionApp = $functionAppName
        FunctionUrl = $functionUrl
        DataverseUrl = $dataverseUrl
        StorageAccount = $storageAccount
        Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    }
    
    $config | Format-Table -AutoSize
    
    # Save config
    $saveConfig = Get-UserInput -Prompt "Save configuration to file? (Y/N)" -Default "Y"
    if ($saveConfig -eq 'Y') {
        $config | ConvertTo-Json | Out-File -FilePath "alex_wa_config.json" -Encoding UTF8
        Write-Host "[OK] Configuration saved to: alex_wa_config.json" -ForegroundColor Green
    }
    
    # Show next steps
    Write-Host "`n[NEXT STEPS]:" -ForegroundColor Cyan
    Write-Host @"
    
1. Build the Plugin:
   - Open Plugin\alex_PostExecute_WaOutboundApi.cs in Visual Studio
   - Build as Class Library (.NET Framework 4.6.2)
   - Sign with Strong Name Key

2. Register the Plugin:
   - Open Plugin Registration Tool
   - Connect to: $dataverseUrl
   - Register the Assembly
   - Set Environment Variable:
     ALEX_WA_FUNC_URL = $functionUrl

3. Import the Solution:
   - Go to make.powerapps.com
   - Solutions -> Import
   - Select the Solution file

4. Configure Channel Instance:
   - Create alex_waChannelInstance record
   - Enter ACS connection details

"@ -ForegroundColor White
    
    # Copy Function URL to clipboard
    $functionUrl | Set-Clipboard
    Write-Host "`n[INFO] Function URL copied to clipboard!" -ForegroundColor Green
    
} catch {
    Write-Host "`n[ERROR]: $_" -ForegroundColor Red
    Write-Host "Stack Trace:" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Gray
}
