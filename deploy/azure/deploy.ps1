[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $Location,
    [Parameter(Mandatory)] [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })] [string] $ParametersFile,
    [securestring] $DatabaseAdministratorPassword,
    [securestring] $OperatorKeySecret,
    [securestring] $SourceSecret,
    [securestring] $DestinationSecret
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

foreach ($scriptOwnedName in @('databaseAdministratorPassword', 'operatorKeySecret', 'sourceSecretValue', 'destinationSecretValue', 'runtimeReplicaCount')) {
    if ($null -ne $parameters.PSObject.Properties[$scriptOwnedName]) {
        throw "Remove '$scriptOwnedName' from the nonsecret Bicep parameter file; deploy.ps1 owns it."
    }
}

$allowedParameterNames = @(
    'namePrefix', 'location', 'registryName', 'registryResourceGroupName',
    'serviceBusNamespaceName', 'serviceBusResourceGroupName', 'databaseProvider',
    'adminImage', 'ingestionImage', 'workerImage', 'databaseAdministratorLogin',
    'adminAllowedCidrs', 'ingestionExternal', 'mappingTenantSlug', 'sourceReference',
    'destinationReference', 'mappingRevision'
)
$providedParameterNames = @($parameters.PSObject.Properties.Name)
$missingParameterNames = @($allowedParameterNames | Where-Object { $_ -notin $providedParameterNames })
$unexpectedParameterNames = @($providedParameterNames | Where-Object { $_ -notin $allowedParameterNames })
if ($missingParameterNames.Count -gt 0) { throw "Missing nonsecret parameters: $($missingParameterNames -join ', ')." }
if ($unexpectedParameterNames.Count -gt 0) { throw "Unexpected or script-owned parameters: $($unexpectedParameterNames -join ', ')." }

$namePrefix = [string](Get-ParameterValue $parameters 'namePrefix')
$parameterLocation = [string](Get-ParameterValue $parameters 'location')
$registryName = [string](Get-ParameterValue $parameters 'registryName')
$registryResourceGroup = [string](Get-ParameterValue $parameters 'registryResourceGroupName')
$databaseProvider = [string](Get-ParameterValue $parameters 'databaseProvider')
$databaseAdministratorLogin = [string](Get-ParameterValue $parameters 'databaseAdministratorLogin')
$adminAllowedCidrs = @(Get-ParameterValue $parameters 'adminAllowedCidrs')
$serviceBusNamespace = [string](Get-ParameterValue $parameters 'serviceBusNamespaceName')
$serviceBusResourceGroup = [string](Get-ParameterValue $parameters 'serviceBusResourceGroupName')
$tenantSlug = [string](Get-ParameterValue $parameters 'mappingTenantSlug')
$sourceReference = [string](Get-ParameterValue $parameters 'sourceReference')
$destinationReference = [string](Get-ParameterValue $parameters 'destinationReference')
$mappingRevision = [string](Get-ParameterValue $parameters 'mappingRevision')

if ($namePrefix -cnotmatch '^[a-z0-9]{3,16}$') { throw 'namePrefix must be 3-16 lowercase letters or digits.' }
if ($parameterLocation -ne $Location) { throw "Parameter location '$parameterLocation' must match -Location '$Location'." }
if ($registryName -cnotmatch '^[a-z0-9]{5,50}$') { throw 'registryName must be 5-50 lowercase letters or digits.' }
foreach ($name in @($ResourceGroup, $registryResourceGroup)) {
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 90 -or $name -match '[<>%&\\?/]' -or $name.EndsWith('.')) {
        throw "Resource-group name '$name' is invalid."
    }
}
if ($databaseProvider -notin @('sqlserver', 'postgres')) { throw "databaseProvider must be 'sqlserver' or 'postgres'." }
if ($databaseAdministratorLogin -cnotmatch '^[A-Za-z][A-Za-z0-9_]{0,62}$') { throw 'databaseAdministratorLogin is invalid.' }
if ($adminAllowedCidrs.Count -eq 0) { throw 'At least one Admin CIDR is required.' }
if ($adminAllowedCidrs -contains '0.0.0.0/0' -or $adminAllowedCidrs -contains '::/0') { throw 'An allow-all Admin CIDR is forbidden.' }
if ([string]::IsNullOrWhiteSpace($serviceBusNamespace) -ne [string]::IsNullOrWhiteSpace($serviceBusResourceGroup)) {
    throw 'Supply both Service Bus namespace and resource-group names, or leave both empty.'
}
if (-not [string]::IsNullOrWhiteSpace($serviceBusNamespace) -and $serviceBusNamespace -cnotmatch '^[a-z][a-z0-9-]{4,48}[a-z0-9]$') {
    throw 'serviceBusNamespaceName must be a 6-50 character lowercase Azure Service Bus namespace name.'
}
if (-not [string]::IsNullOrWhiteSpace($serviceBusResourceGroup) -and
    ($serviceBusResourceGroup.Length -gt 90 -or $serviceBusResourceGroup -match '[<>%&\\?/]' -or $serviceBusResourceGroup.EndsWith('.'))) {
    throw "Service Bus resource-group name '$serviceBusResourceGroup' is invalid."
}
if ($tenantSlug -cnotmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$') { throw 'mappingTenantSlug is invalid.' }
if ($sourceReference -cnotmatch '^[a-z0-9][a-z0-9_]{0,62}$') { throw 'sourceReference is invalid.' }
if ($destinationReference -cnotmatch '^[a-z0-9][a-z0-9_]{0,62}$') { throw 'destinationReference is invalid.' }
if ($mappingRevision -cnotmatch '^[a-z0-9-]+$' -or "destination-$mappingRevision".Length -gt 20) {
    throw 'mappingRevision must keep generated Container Apps secret names at 20 characters or fewer.'
}
foreach ($secretName in @("source-$tenantSlug-$($sourceReference.Replace('_', '-'))", "destination-$tenantSlug-$($destinationReference.Replace('_', '-'))")) {
    if ($secretName.Length -gt 127) { throw "Generated Key Vault secret name '$secretName' exceeds 127 characters." }
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
