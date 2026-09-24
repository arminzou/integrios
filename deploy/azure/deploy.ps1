[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $Location,
    [Parameter(Mandatory)] [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })] [string] $ParametersFile,
    [securestring] $DatabaseAdministratorPassword,
    [securestring] $OperatorKeySecret,
    [securestring] $SourceSecret,
    [securestring] $DestinationSecret,
    [securestring] $AdminOidcClientSecret
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot 'main.bicep'
$resolvedParametersFile = (Resolve-Path -LiteralPath $ParametersFile).Path

function Invoke-AzureCli {
    & az @args
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: az $($args -join ' ')" }
}

function ConvertFrom-SecureValue([securestring] $Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Get-ParameterValue([object] $Parameters, [string] $Name) {
    $property = $Parameters.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "The parameter file must define '$Name'." }
    $property.Value.value
}

function Invoke-Deployment([int] $RuntimeReplicaCount) {
    $deploymentParameterFile = Join-Path ([IO.Path]::GetTempPath()) "integrios-azure-$([guid]::NewGuid().ToString('N')).json"
    try {
        $deploymentParameters = $compiled.parametersJson | ConvertFrom-Json -AsHashtable
        $deploymentParameters.parameters.databaseAdministratorPassword = @{ value = ConvertFrom-SecureValue $DatabaseAdministratorPassword }
        $deploymentParameters.parameters.operatorKeySecret = @{ value = ConvertFrom-SecureValue $OperatorKeySecret }
        $deploymentParameters.parameters.sourceSecretValue = @{ value = ConvertFrom-SecureValue $SourceSecret }
        $deploymentParameters.parameters.destinationSecretValue = @{ value = ConvertFrom-SecureValue $DestinationSecret }
        if ($adminOidcEnabled) {
            $deploymentParameters.parameters.adminOidcClientSecret = @{ value = ConvertFrom-SecureValue $AdminOidcClientSecret }
        }
        $deploymentParameters.parameters.runtimeReplicaCount = @{ value = $RuntimeReplicaCount }
        $json = $deploymentParameters | ConvertTo-Json -Depth 8
        $stream = [IO.FileStream]::new($deploymentParameterFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
            try { $writer.Write($json) } finally { $writer.Dispose() }
        }
        finally { $stream.Dispose() }
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode($deploymentParameterFile, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }

        Invoke-AzureCli deployment group create `
            --name main `
            --resource-group $ResourceGroup `
            --template-file $template `
            --parameters "@$deploymentParameterFile" `
            --only-show-errors `
            --output none
    }
    finally {
        Remove-Variable json, deploymentParameters -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $deploymentParameterFile) {
            Remove-Item -LiteralPath $deploymentParameterFile -Force
        }
    }
}

function Get-DeploymentOutputs {
    $json = & az deployment group show `
        --resource-group $ResourceGroup `
        --name main `
        --query properties.outputs `
        --output json `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Could not read deployment outputs.' }
    $json | ConvertFrom-Json
}

function Invoke-Job([string] $JobName) {
    $execution = & az containerapp job start `
        --resource-group $ResourceGroup `
        --name $JobName `
        --query name `
        --output tsv `
        --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($execution)) {
        throw "Could not start Container Apps Job $JobName."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(12)
    do {
        $status = & az containerapp job execution list `
            --resource-group $ResourceGroup `
            --name $JobName `
            --query "[?name=='$execution'].properties.status | [0]" `
            --output tsv `
            --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw "Could not read execution status for $JobName." }
        if ($status -eq 'Succeeded') { return }
        if ($status -in @('Failed', 'Stopped', 'Degraded')) {
            throw "Container Apps Job $JobName execution $execution ended with status $status."
        }
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Container Apps Job $JobName execution $execution did not finish within 12 minutes."
}

function Wait-HealthyRevision([string] $AppName) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
    do {
        $health = & az containerapp revision list `
            --resource-group $ResourceGroup `
            --name $AppName `
            --query '[?properties.active].properties.healthState | [0]' `
            --output tsv `
            --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw "Could not read revision health for $AppName." }
        if ($health -eq 'Healthy') { return }
        if ($health -eq 'Unhealthy') { throw "The active revision for $AppName is unhealthy." }
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The active revision for $AppName did not become healthy within 10 minutes."
}

$compiled = & az bicep build-params --file $resolvedParametersFile --stdout --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($compiled.parametersJson)) {
    throw 'The Bicep parameter file could not be compiled.'
}
$parameters = ($compiled.parametersJson | ConvertFrom-Json).parameters

foreach ($scriptOwnedName in @('databaseAdministratorPassword', 'operatorKeySecret', 'sourceSecretValue', 'destinationSecretValue', 'adminOidcClientSecret', 'runtimeReplicaCount')) {
    if ($null -ne $parameters.PSObject.Properties[$scriptOwnedName]) {
        throw "Remove '$scriptOwnedName' from the nonsecret Bicep parameter file; deploy.ps1 owns it."
    }
}

# Template parameter names, required parameters, and Azure naming and length rules are enforced by
# Bicep and ARM validation before any resource in the deployment changes. The checks below cover
# only what Azure cannot know.
$parameterLocation = [string](Get-ParameterValue $parameters 'location')
$registryName = [string](Get-ParameterValue $parameters 'registryName')
$databaseProvider = [string](Get-ParameterValue $parameters 'databaseProvider')
$adminAllowedCidrs = @(Get-ParameterValue $parameters 'adminAllowedCidrs')
$serviceBusNamespace = [string]$parameters.PSObject.Properties['serviceBusNamespaceName']?.Value.value
$serviceBusResourceGroup = [string]$parameters.PSObject.Properties['serviceBusResourceGroupName']?.Value.value
$adminOidcAuthority = [string]$parameters.PSObject.Properties['adminOidcAuthority']?.Value.value
$adminOidcClientId = [string]$parameters.PSObject.Properties['adminOidcClientId']?.Value.value
$adminOidcEnabled = -not [string]::IsNullOrWhiteSpace($adminOidcAuthority)

if ($parameterLocation -ne $Location) { throw "Parameter location '$parameterLocation' must match -Location '$Location'." }
# Azure accepts an allow-all restriction, and the template silently disables Service Bus access
# when only one of its two names is set, so neither mistake would fail a deployment.
if ($adminAllowedCidrs -contains '0.0.0.0/0' -or $adminAllowedCidrs -contains '::/0') { throw 'An allow-all Admin CIDR is forbidden.' }
if ([string]::IsNullOrWhiteSpace($serviceBusNamespace) -ne [string]::IsNullOrWhiteSpace($serviceBusResourceGroup)) {
    throw 'Supply both Service Bus namespace and resource-group names, or leave both empty.'
}
if ($adminOidcEnabled -eq [string]::IsNullOrWhiteSpace($adminOidcClientId)) {
    throw 'Supply both adminOidcAuthority and adminOidcClientId to enable dashboard sign-in, or leave both empty.'
}
if ($adminOidcEnabled) {
    $authorityUri = $null
    if (-not [Uri]::TryCreate($adminOidcAuthority, [UriKind]::Absolute, [ref] $authorityUri) -or $authorityUri.Scheme -ne 'https') {
        throw 'adminOidcAuthority must be an absolute https URI.'
    }
}

$immutableImagePattern = '^.+\.azurecr\.io/.+@sha256:[a-f0-9]{64}$'
foreach ($parameterName in @('adminImage', 'ingestionImage', 'workerImage')) {
    $image = [string](Get-ParameterValue $parameters $parameterName)
    if ($image -cnotmatch $immutableImagePattern -or $image.EndsWith(('0' * 64), [StringComparison]::Ordinal)) {
        throw "$parameterName must be a real immutable ACR digest reference."
    }
    if (-not $image.StartsWith("$registryName.azurecr.io/", [StringComparison]::OrdinalIgnoreCase)) {
        throw "$parameterName must come from $registryName.azurecr.io."
    }
}

Invoke-AzureCli account show --only-show-errors --output none

if ($null -eq $DatabaseAdministratorPassword) { $DatabaseAdministratorPassword = Read-Host 'Database administrator password' -AsSecureString }
if ($null -eq $OperatorKeySecret) { $OperatorKeySecret = Read-Host 'Initial OperatorKey secret' -AsSecureString }
if ($null -eq $SourceSecret) { $SourceSecret = Read-Host 'Source secret value' -AsSecureString }
if ($null -eq $DestinationSecret) { $DestinationSecret = Read-Host 'Destination secret value' -AsSecureString }
if ($adminOidcEnabled) {
    if ($null -eq $AdminOidcClientSecret) { $AdminOidcClientSecret = Read-Host 'Dashboard OpenID Connect client secret' -AsSecureString }
    if ($AdminOidcClientSecret.Length -eq 0) { throw 'Dashboard sign-in requires the OpenID Connect client secret.' }
}

Invoke-AzureCli group create --name $ResourceGroup --location $Location --only-show-errors --output none

Write-Host 'Scaling runtime to zero and reconciling infrastructure...'
Invoke-Deployment -RuntimeReplicaCount 0
$outputs = Get-DeploymentOutputs

Write-Host "Applying $databaseProvider migrations..."
Invoke-Job -JobName $outputs.jobNames.value.migrate

Write-Host 'Running idempotent Bootstrap...'
Invoke-Job -JobName $outputs.jobNames.value.bootstrap

Write-Host 'Validating configured destination secret references without printing values...'
Invoke-Job -JobName $outputs.jobNames.value.validateSecrets

Write-Host 'Starting the matched runtime set...'
Invoke-Deployment -RuntimeReplicaCount 1
$outputs = Get-DeploymentOutputs

foreach ($app in $outputs.appNames.value.PSObject.Properties.Value) {
    Wait-HealthyRevision -AppName $app
}

Write-Host "Ready: https://$($outputs.adminFqdn.value)"
Write-Host "Ingestion: https://$($outputs.ingestionFqdn.value)"
Write-Host "OpenID Connect redirect URI: $($outputs.adminOidcRedirectUris.value.callback)"
Write-Host "OpenID Connect sign-out redirect URI: $($outputs.adminOidcRedirectUris.value.signedOut)"
