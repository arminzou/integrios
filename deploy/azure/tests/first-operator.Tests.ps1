# Container-free contract checks. Run: pwsh -NoProfile -File deploy/azure/tests/first-operator.Tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../first-operator.ps1')
$realStatusReader = ${function:Get-FirstOperatorInitialized}

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function ConvertFrom-SecureValue([securestring] $Value) {
    [System.Net.NetworkCredential]::new('', $Value).Password
}

# No real Azure call is possible: the fake rejects every command it does not understand.
function az {
    $global:LASTEXITCODE = 0
    if (($args[0..2] -join ' ') -eq 'containerapp job show') {
        return $script:jobJson
    }
    if (($args[0..2] -join ' ') -eq 'monitor log-analytics workspace') { return 'workspace-guid' }
    if (($args[0..2] -join ' ') -eq 'monitor log-analytics query') {
        $script:statusQuery = $args[($args.IndexOf('--analytics-query') + 1)]
        return $script:statusRows
    }
    if (($args[0..1] -join ' ') -eq 'containerapp revision') {
        $global:LASTEXITCODE = $script:revisionExit
        return $script:revisionOutput
    }
    throw "Unexpected Azure command in test: $($args -join ' ')"
}
function Invoke-Job([string] $JobName, [switch] $ReturnExecution) {
    $script:runs++
    if ($script:failProvision -and -not $ReturnExecution) { throw 'Simulated provisioning failure' }
    if ($ReturnExecution) { return 'execution-under-test' }
}
function Get-FirstOperatorInitialized([string] $JobName, [string] $Execution) {
    Assert ($Execution -eq 'execution-under-test') 'Status must use the current execution.'
    $script:initialized
}
function Set-SetupJob([string] $Url, [object] $Body) {
    $script:requests.Add(($Body | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable))
    if ($script:failCleanup -and $Body.properties.template.containers[0].args[0] -eq 'bootstrap') {
        throw 'Simulated cleanup failure'
    }
}
$script:jobJson = @{
    id = '/subscriptions/test/resourceGroups/test/providers/Microsoft.App/jobs/bootstrap'
    location = 'centralus'
    identity = @{ userAssignedIdentities = @{ '/identities/bootstrap' = @{ clientId = 'test' } } }
    properties = @{
        environmentId = '/environments/test'
        configuration = @{ secrets = @(@{ name = 'operator-key'; keyVaultUrl = 'https://example.invalid/key' }) }
        template = @{ containers = @(@{ name = 'bootstrap'; args = @('bootstrap') }) }
    }
} | ConvertTo-Json -Depth 20
$ResourceGroup = 'test'
$InteractiveSetup = $false
$InitialOperatorDisplayName = 'First Operator'
$InitialOperatorEmail = 'operator@example.invalid'
$InitialOperatorPassword = ConvertTo-SecureString 'only-a-test-password' -AsPlainText -Force

foreach ($scenario in @('fresh', 'existing', 'missing-input', 'provision-failure', 'cleanup-failure')) {
    $script:requests = [Collections.Generic.List[object]]::new()
    $script:runs = 0
    $script:initialized = $scenario -eq 'existing'
    $script:failProvision = $scenario -eq 'provision-failure'
    $script:failCleanup = $scenario -eq 'cleanup-failure'
    $savedPassword = $InitialOperatorPassword
    if ($scenario -in @('existing', 'missing-input')) { $InitialOperatorPassword = $null }
    $errorMessage = $null
    try { Invoke-FirstOperatorSetup 'bootstrap' '/workspaces/test' }
    catch { $errorMessage = $_.Exception.Message }
    finally { $InitialOperatorPassword = $savedPassword }
    $restored = $script:requests[-1]
    Assert ($restored.properties.template.containers[0].args[0] -eq 'bootstrap') "$scenario must restore Bootstrap."
    Assert (@($restored.properties.configuration.secrets).Count -eq 1) "$scenario must remove the first password secret."
    Assert (-not $restored.properties.template.ContainsKey('volumes')) "$scenario must remove the secret mount."
    if ($scenario -eq 'fresh') {
        Assert ($null -eq $errorMessage -and $script:runs -eq 2) 'Fresh setup must check state then provision.'
        $creation = $script:requests[1]
        Assert ($creation.properties.template.volumes[0].storageType -eq 'Secret') 'Password must be mounted as a secret.'
        Assert ($creation.properties.template.containers[0].args -notcontains 'only-a-test-password') 'Password must not enter command args.'
    }
    elseif ($scenario -eq 'existing') { Assert ($null -eq $errorMessage -and $script:runs -eq 1) 'Existing account must not need input.' }
    else { Assert (-not [string]::IsNullOrWhiteSpace($errorMessage)) "$scenario must fail clearly." }
}

$file = Join-Path ([IO.Path]::GetTempPath()) "integrios-protected-test-$([guid]::NewGuid().ToString('N')).json"
try {
    Write-ProtectedJson $file @{ value = 'nonsecret-test-value' }
    Assert (((Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).value) -eq 'nonsecret-test-value') 'Protected JSON must round-trip.'
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $file
        Assert $acl.AreAccessRulesProtected 'Windows file must disable inherited ACLs.'
        Assert (@($acl.Access).Count -eq 1) 'Windows file must grant only the current identity.'
    }
    else {
        Assert ([IO.File]::GetUnixFileMode($file) -eq ([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)) 'Unix file must be mode 600.'
    }
}
finally { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
# The status reader must find a completed execution's result in Log Analytics, scoped to it.
$script:statusRows = '[{"Log_s":"{\"initialized\":false}"}]'
Assert ((& $realStatusReader 'bootstrap' 'bootstrap-abc123' '/workspaces/test') -eq $false) 'Status reader must parse Log Analytics output.'
Assert ($script:statusQuery -match "startswith 'bootstrap-abc123-'") 'Status reader must scope to the execution.'
$script:statusRows = '[{"Log_s":"{\"initialized\":true}"}]'
Assert ((& $realStatusReader 'bootstrap' 'bootstrap-abc123' '/workspaces/test') -eq $true) 'Status reader must report initialized.'

$script:revisionExit = 1
$script:revisionOutput = 'ERROR: (RevisionAlreadyInRequestedState) Revision app--1 is already deactivated!.'
Set-RevisionState deactivate 'app' 'app--1'
$script:revisionOutput = 'ERROR: (AuthorizationFailed) denied'
$rejected = $false
try { Set-RevisionState deactivate 'app' 'app--1' } catch { $rejected = $true }
Assert $rejected 'Other revision errors must still fail.'

Write-Host 'PASS: first-Operator setup contracts and protected secret-file permissions.'
