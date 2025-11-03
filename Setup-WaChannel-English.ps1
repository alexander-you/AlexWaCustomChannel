# Setup-WaChannel-English.ps1
# Interactive setup script for WA Channel

Write-Host @"
=========================================================
        WA Channel - Interactive Setup              
                                                         
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

# Function to install NuGet Package if not installed
function Install-NugetPackage {
    param(
        [string]$PackageName,
        [string]$Version,
        [string]$ProjectPath
    )
    Write-Host "Checking if $PackageName is installed in $ProjectPath..." -ForegroundColor Cyan
    $packagesConfigPath = Join-Path $ProjectPath "packages.config"
    $isInstalled = $false
    if (Test-Path $packagesConfigPath) {
        [xml]$packagesConfig = Get-Content $packagesConfigPath
        $isInstalled = $packagesConfig.packages.package | Where-Object { $_.id -eq $PackageName -and $_.version -eq $Version }
    }

    if (-not $isInstalled) {
        Write-Host "$PackageName not found or version mismatch. Installing..." -ForegroundColor Yellow
        nuget install $PackageName -Version $Version -OutputDirectory "$ProjectPath\packages" -Source "https://api.nuget.org/v3/index.json" -Verbosity quiet
    }
    else {
        Write-Host "$PackageName is already installed." -ForegroundColor Green
    }
}

# Function to replace text in a file
function Replace-TextInFile {
    param(
        [string]$FilePath,
        [string]$OldText,
        [string]$NewText
    )
    (Get-Content $FilePath) -replace [regex]::Escape($OldText), [regex]::Escape($NewText) | Set-Content $FilePath
}

# Step 1: Input parameters
$resourceGroupName = Get-UserInput -Prompt "Enter a resource group name" -Default "wa-rg"
$location = Get-UserInput -Prompt "Enter location" -Default "eastus"
$storageAccountName = Get-UserInput -Prompt "Enter a storage account name" -Default "wastorage"
$functionAppName = Get-UserInput -Prompt "Enter a function app name" -Default "wa-func-$(Get-Random -Maximum 9999)"
$pluginName = Get-UserInput -Prompt "Enter plugin file name" -Default "Wa_PostExecute_WaOutboundApi.cs"
$functionName = Get-UserInput -Prompt "Enter function name" -Default "SendWaTemplate"
$keyVaultUrl = Get-UserInput -Prompt "Enter Key Vault URL (optional)" -Default ""
$acsConnectionString = Get-UserInput -Prompt "Enter Azure Communication Services (ACS) connection string" -Default ""

# Step 2: Create resource group
Write-Host "Creating resource group $resourceGroupName..." -ForegroundColor Cyan
az group create --name $resourceGroupName --location $location

# Step 3: Create storage account
Write-Host "Creating storage account $storageAccountName..." -ForegroundColor Cyan
az storage account create --name $storageAccountName --location $location --resource-group $resourceGroupName --sku Standard_LRS

# Step 4: Create function app
Write-Host "Creating function app $functionAppName..." -ForegroundColor Cyan
az functionapp create --resource-group $resourceGroupName --consumption-plan-location $location --runtime dotnet --functions-version 4 --name $functionAppName --storage-account $storageAccountName

# Step 5: Zip deployment of the function
Write-Host "Deploying the function via ZIP..." -ForegroundColor Cyan
$zipPath = Join-Path $PSScriptRoot "AzureFunction\deploy.zip"
az functionapp deployment source config-zip --src $zipPath --name $functionAppName --resource-group $resourceGroupName

# Step 6: Set app settings
Write-Host "Setting application settings..." -ForegroundColor Cyan
az functionapp config appsettings set --name $functionAppName --resource-group $resourceGroupName --settings "WA_KEYVAULT_URL=$keyVaultUrl" "WA_ACS_CONNECTION_STRING=$acsConnectionString"

# Step 7: Get function key
Write-Host "Retrieving function key..." -ForegroundColor Cyan
$functionKey = az functionapp function keys list --function-name $functionName --name $functionAppName --resource-group $resourceGroupName --query "default" -o tsv

# Step 8: Update local configuration file
Write-Host "Updating local configuration file wa_config.json..." -ForegroundColor Cyan
$configFilePath = Join-Path $PSScriptRoot "wa_config.json"
$config = @{
    "functionUrl" = "https://$functionAppName.azurewebsites.net/api/$functionName?code=$functionKey";
    "storageAccountName" = $storageAccountName;
    "resourceGroupName" = $resourceGroupName;
    "functionAppName" = $functionAppName;
    "pluginName" = $pluginName;
    "functionName" = $functionName;
    "keyVaultUrl" = $keyVaultUrl;
    "acsConnectionString" = $acsConnectionString;
    "dataverseUrl" = "";
}

$config | ConvertTo-Json -Depth 10 | Set-Content $configFilePath

# Step 9: Build plugin (assumes dotnet sdk installed)
Write-Host "Building plugin..." -ForegroundColor Cyan
$pluginDir = Join-Path $PSScriptRoot "Plugin"
$solutionPath = Join-Path $PSScriptRoot "Solution\WaCustomChannel.sln"

# Ensure required NuGet packages are installed
Install-NugetPackage -PackageName "Microsoft.CrmSdk.CoreAssemblies" -Version "9.1.0.122" -ProjectPath $pluginDir

# Build the plugin solution
Write-Host "Running dotnet build on $solutionPath..." -ForegroundColor Cyan
dotnet build $solutionPath --configuration Release

# Step 10: Register plugin and import solution into Dataverse
Write-Host "Please log in to Power Platform admin center to register the plugin and import solution." -ForegroundColor Yellow
Write-Host "1. Go to https://make.powerapps.com and select your environment" -ForegroundColor Yellow
Write-Host "2. Navigate to Solutions and import the solution file from the Solution folder" -ForegroundColor Yellow
Write-Host "3. Register the plugin assembly from the Plugin/bin/Release folder using Plug-in Registration tool" -ForegroundColor Yellow
Write-Host "4. Configure the custom channel instance in Customer Insights – Journeys with the following details:" -ForegroundColor Yellow
Write-Host "   - Configuration file: wa_config.json" -ForegroundColor Yellow
Write-Host "   - Plugin file: $pluginName" -ForegroundColor Yellow
Write-Host "   - Function URL: https://$functionAppName.azurewebsites.net/api/$functionName?code=$functionKey" -ForegroundColor Yellow

Write-Host "\nSetup completed successfully! Please follow the above steps in the Power Platform admin center to finish the integration." -ForegroundColor Green
