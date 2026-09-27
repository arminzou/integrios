# Container-free contract checks. Run: pwsh -NoProfile -File deploy/azure/tests/setup-helpers.Tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../setup-helpers.ps1')

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

# No real Azure call is possible: the fake answers revision commands and rejects everything else.
function az {
    if (($args[0..1] -join ' ') -eq 'containerapp revision') {
        $global:LASTEXITCODE = $script:revisionExit
        return $script:revisionOutput
    }
    throw "Unexpected Azure command in test: $($args -join ' ')"
}
$ResourceGroup = 'test'

# A revision already in the requested state is success; any other failure still throws.
$script:revisionExit = 1
$script:revisionOutput = 'ERROR: (RevisionAlreadyInRequestedState) Revision app--1 is already deactivated!.'
Set-RevisionState deactivate 'app' 'app--1'
$script:revisionOutput = 'ERROR: (AuthorizationFailed) denied'
$rejected = $false
try { Set-RevisionState deactivate 'app' 'app--1' } catch { $rejected = $true }
Assert $rejected 'Other revision errors must still fail.'

# The deployment command never takes an Operator password: the first Operator is created
# interactively after deployment.
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../deploy.ps1'), [ref]$tokens, [ref]$errors)
$parameters = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
Assert (@($parameters | Where-Object { $_ -match 'Operator' -and $_ -ne 'OperatorKeySecret' }).Count -eq 0) "deploy.ps1 must not accept Operator credentials: $($parameters -join ', ')"

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
Write-Host 'PASS: revision state, no Operator credentials in deploy.ps1, and protected parameter-file permissions.'
