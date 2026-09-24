[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $Location,
    [Parameter(Mandatory)] [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })] [string] $ParametersFile,
    [securestring] $DatabaseAdministratorPassword,
    [securestring] $OperatorKeySecret,
    [securestring] $AdminOidcClientSecret
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot 'main.bicep'
$releaseImageSource = 'ghcr.io/arminzou/integrios'
$imageParameters = [ordered]@{ adminImage = 'admin'; ingestionImage = 'ingestion'; workerImage = 'worker' }
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
        if ($adminOidcEnabled) {
            $deploymentParameters.parameters.adminOidcClientSecret = @{ value = ConvertFrom-SecureValue $AdminOidcClientSecret }
        }
        $deploymentParameters.parameters.runtimeReplicaCount = @{ value = $RuntimeReplicaCount }
        if ($releaseImages) {
            $deploymentParameters.parameters.Remove('release')
            foreach ($name in $releaseImages.Keys) { $deploymentParameters.parameters[$name] = @{ value = $releaseImages[$name] } }
        }
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

        for ($attempt = 1; $attempt -le 20; $attempt++) {
            $deploymentOutput = & az deployment group create `
                --name main `
                --resource-group $ResourceGroup `
                --template-file $template `
                --parameters "@$deploymentParameterFile" `
                --only-show-errors `
                --output none 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0) { return }

            $errorJson = & az deployment group show `
                --name main `
                --resource-group $ResourceGroup `
                --query properties.error `
                --output json `
                --only-show-errors
            if ($LASTEXITCODE -ne 0) { throw "Azure deployment failed: $deploymentOutput" }

            $details = @(($errorJson | ConvertFrom-Json).details)
            $identitySecretErrors = @($details | Where-Object {
                $_.code -eq 'InvalidParameterValueInContainerTemplate' -and
                $_.message -match 'Unable to get value using Managed identity'
            })
            if ($details.Count -eq 0 -or $identitySecretErrors.Count -ne $details.Count -or $attempt -eq 20) {
                throw "Azure deployment failed: $deploymentOutput"
            }

            Write-Warning "Container Apps cannot read its new Key Vault secret role yet; retrying deployment in 30 seconds ($attempt/20)."
            Start-Sleep -Seconds 30
        }
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

function Get-ImageDigest([string] $Image) {
    $digest = & az acr manifest show-metadata `
        --registry $registryName `
        --name $Image `
        --query digest `
        --output tsv `
        --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $digest
}

# A release names one matched image set. Import it into the Operator's registry when absent, then
# pin each image to the digest it resolves to now, so every replica and job runs the same bytes.
function Resolve-ReleaseImage([string] $Service) {
    $image = "integrios/${Service}:$release"
    $digest = Get-ImageDigest $image
    if ([string]::IsNullOrWhiteSpace($digest)) {
        Write-Host "Importing $releaseImageSource/${Service}:$release into $registryName..."
        Invoke-AzureCli acr import `
            --name $registryName `
            --source "$releaseImageSource/${Service}:$release" `
            --image $image `
            --only-show-errors `
            --output none
        $digest = Get-ImageDigest $image
    }
    if ($digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw "Could not resolve $image in $registryName to a digest." }
    "$registryName.azurecr.io/integrios/$Service@$digest"
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

foreach ($scriptOwnedName in @('databaseAdministratorPassword', 'operatorKeySecret', 'adminOidcClientSecret', 'runtimeReplicaCount')) {
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

# Images come from exactly one source: a release version the command resolves, or three explicit
# digests for an image set the Operator built or imported themselves.
$release = [string]$parameters.PSObject.Properties['release']?.Value.value
$explicitImages = @($imageParameters.Keys | Where-Object { $null -ne $parameters.PSObject.Properties[$_] })
if (-not [string]::IsNullOrWhiteSpace($release)) {
    if ($explicitImages.Count -gt 0) { throw "Set either release or $($imageParameters.Keys -join ', '), not both." }
    if ($release -cnotmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "release '$release' must be a version without the Git tag's v prefix, such as 0.9.0."
    }
}
else {
    if ($explicitImages.Count -ne $imageParameters.Count) {
        throw "Set release, or all of $($imageParameters.Keys -join ', ')."
    }
    $immutableImagePattern = '^.+\.azurecr\.io/.+@sha256:[a-f0-9]{64}$'
    foreach ($parameterName in $imageParameters.Keys) {
        $image = [string](Get-ParameterValue $parameters $parameterName)
        if ($image -cnotmatch $immutableImagePattern -or $image.EndsWith(('0' * 64), [StringComparison]::Ordinal)) {
            throw "$parameterName must be a real immutable ACR digest reference."
        }
        if (-not $image.StartsWith("$registryName.azurecr.io/", [StringComparison]::OrdinalIgnoreCase)) {
            throw "$parameterName must come from $registryName.azurecr.io."
        }
    }
}

Invoke-AzureCli account show --only-show-errors --output none

$releaseImages = $null
if (-not [string]::IsNullOrWhiteSpace($release)) {
    Write-Host "Resolving release $release in $registryName..."
    $releaseImages = [ordered]@{}
    foreach ($name in $imageParameters.Keys) {
        $releaseImages[$name] = Resolve-ReleaseImage $imageParameters[$name]
        Write-Host "  $($releaseImages[$name])"
    }
}

if ($null -eq $DatabaseAdministratorPassword) { $DatabaseAdministratorPassword = Read-Host 'Database administrator password' -AsSecureString }
if ($null -eq $OperatorKeySecret) { $OperatorKeySecret = Read-Host 'Initial OperatorKey secret' -AsSecureString }
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
Write-Host "Source-secret vault: $($outputs.secretVaults.value.source)"
Write-Host "Destination-secret vault: $($outputs.secretVaults.value.destination)"
Write-Host "OpenID Connect redirect URI: $($outputs.adminOidcRedirectUris.value.callback)"
Write-Host "OpenID Connect sign-out redirect URI: $($outputs.adminOidcRedirectUris.value.signedOut)"
