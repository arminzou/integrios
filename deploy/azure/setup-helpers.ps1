# Helpers used by deploy.ps1 for setup: revision state, protected request files, and first-Operator
# provisioning. No password is passed to az in an argument, printed, or retained
# in the normal job template. Source this file only; it performs no deployment on its own.
# Azure can report a revision as active and then reject its deactivation because it is already
# inactive (and the reverse). Either way the revision is in the requested state.
function Set-RevisionState([ValidateSet('activate', 'deactivate')] [string] $Action, [string] $App, [string] $Revision) {
    $output = & az containerapp revision $Action --resource-group $ResourceGroup --name $App --revision $Revision --output none --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0 -and "$output" -notmatch 'RevisionAlreadyInRequestedState') {
        throw "Could not $Action revision $Revision of ${App}: $output"
    }
}

function Write-ProtectedJson([string] $Path, [object] $Value) {
    # Restrict access before writing any bytes, including on Windows where temp inherits ACLs.
    if ($IsWindows) {
        # Create with the DACL atomically; a write-only handle cannot change its own DACL afterwards.
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
        $stream = [IO.FileSystemAclExtensions]::Create([IO.FileInfo]::new($Path), [IO.FileMode]::CreateNew,
            [Security.AccessControl.FileSystemRights]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $acl)
    }
    else {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    }
    try {
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 1024, $true)
        try { $writer.Write(($Value | ConvertTo-Json -Depth 50)) }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Set-SetupJob([string] $Url, [object] $Body) {
    $path = Join-Path ([IO.Path]::GetTempPath()) "integrios-setup-$([guid]::NewGuid().ToString('N')).json"
    try {
        Write-ProtectedJson $path $Body
        # Suppress service errors as well: a rejected request may echo sensitive request fields.
        $null = & az rest --method put --url $Url --body "@$path" --output none --only-show-errors 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Could not update the setup job. Sensitive request details were suppressed.' }
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
        do {
            $state = & az rest --method get --url $Url --query properties.provisioningState --output tsv --only-show-errors 2>$null
            if ($LASTEXITCODE -ne 0) { throw 'Could not confirm setup job reconciliation.' }
            if ($state -eq 'Succeeded') { return }
            if ($state -in @('Failed', 'Canceled')) { throw 'Setup job reconciliation failed.' }
            Start-Sleep -Seconds 3
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        throw 'Setup job reconciliation did not complete within three minutes.'
    }
    finally {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
}

function Get-FirstOperatorInitialized([string] $JobName, [string] $Execution, [string] $WorkspaceId) {
    # A completed execution has no live replica to stream from, so read its console output from
    # Log Analytics. Scope to this execution only, never a previous run or infrastructure state.
    $customerId = & az monitor log-analytics workspace show --ids $WorkspaceId --query customerId --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($customerId)) { throw 'Could not read the Log Analytics workspace.' }
    $query = "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(1h) | where ContainerJobName_s == '$JobName' " +
        "| where ContainerGroupName_s startswith '$Execution-' | where Log_s has 'initialized' | project Log_s"
    # Ingestion commonly lags a few minutes; allow up to 15.
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
    while ($true) {
        $rows = & az monitor log-analytics query --workspace $customerId.Trim() --analytics-query $query --output json --only-show-errors
        if ($LASTEXITCODE -eq 0) {
            foreach ($row in @($rows | ConvertFrom-Json)) {
                if ([string]$row.Log_s -match '\{"initialized":(true|false)\}') { return $Matches[1] -eq 'true' }
            }
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
        Start-Sleep -Seconds 20
    }
    throw 'The setup status execution completed but its initialized-state result did not reach Log Analytics within 15 minutes. Runtime remains stopped; retry deployment.'
}

function Invoke-FirstOperatorSetup([string] $JobName, [string] $WorkspaceId) {
    $job = & az containerapp job show --resource-group $ResourceGroup --name $JobName --output json --only-show-errors | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0 -or -not $job.id) { throw 'Could not read the setup job.' }
    $url = "https://management.azure.com$($job.id)?api-version=2025-07-01"
    $baseline = @{
        location = $job.location
        identity = @{ type = 'UserAssigned'; userAssignedIdentities = @{} }
        properties = @{
            environmentId = $job.properties.environmentId
            configuration = $job.properties.configuration
            template = $job.properties.template
        }
    }
    foreach ($id in $job.identity.userAssignedIdentities.Keys) { $baseline.identity.userAssignedIdentities[$id] = @{} }
    # Always restore the credential-free, machine-key bootstrap, including after an interrupted
    # previous attempt. The initial Bicep reconciliation has already supplied this baseline.
    $working = ($baseline | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable)
    $working.properties.template.containers[0].args = @('operator-user', 'bootstrap-status')
    $working.properties.configuration.replicaRetryLimit = 0
    try {
        Set-SetupJob $url $working
        $execution = Invoke-Job -JobName $JobName -ReturnExecution
        if (Get-FirstOperatorInitialized $JobName $execution $WorkspaceId) {
            Write-Host 'First Operator already initialized; preserving all accounts and credentials.'
            return
        }

        if (-not $InteractiveSetup -and ($null -eq $InitialOperatorPassword -or
                [string]::IsNullOrWhiteSpace($InitialOperatorDisplayName) -or [string]::IsNullOrWhiteSpace($InitialOperatorEmail))) {
            throw 'Fresh password-enabled deployment requires -InitialOperatorDisplayName, -InitialOperatorEmail and -InitialOperatorPassword (SecureString), or -InteractiveSetup for masked prompts.'
        }
        if ([string]::IsNullOrWhiteSpace($InitialOperatorDisplayName)) { $InitialOperatorDisplayName = Read-Host 'First Operator display name' }
        if ([string]::IsNullOrWhiteSpace($InitialOperatorEmail)) { $InitialOperatorEmail = Read-Host 'First Operator sign-in email' }
        if ($null -eq $InitialOperatorPassword) {
            $InitialOperatorPassword = Read-Host 'First Operator password' -AsSecureString
            $confirmation = Read-Host 'Confirm first Operator password' -AsSecureString
            try {
                if ((ConvertFrom-SecureValue $InitialOperatorPassword) -cne (ConvertFrom-SecureValue $confirmation)) {
                    throw 'Passwords do not match.'
                }
            }
            finally { $confirmation.Dispose() }
        }
        if ($InitialOperatorPassword.Length -eq 0) { throw 'First Operator password must not be empty.' }
        $working.properties.configuration.secrets = @($working.properties.configuration.secrets) + @{
            name = 'first-operator-password'; value = ConvertFrom-SecureValue $InitialOperatorPassword
        }
        $working.properties.template.volumes = @(@{
            name = 'first-operator'; storageType = 'Secret'
            secrets = @(@{ secretRef = 'first-operator-password'; path = 'password' })
        })
        $working.properties.template.containers[0].volumeMounts = @(@{ volumeName = 'first-operator'; mountPath = '/run/first-operator' })
        $working.properties.template.containers[0].args = @('operator-user', 'bootstrap', '--display-name', $InitialOperatorDisplayName,
            '--email', $InitialOperatorEmail, '--password-file', '/run/first-operator/password')
        Set-SetupJob $url $working
        Invoke-Job -JobName $JobName
    }
    finally {
        $working = $null
        try { Set-SetupJob $url $baseline }
        catch { throw 'Setup job cleanup failed. Runtime remains stopped. Rerun deploy.ps1 to reconcile the credential-free job and safely retry; existing accounts are preserved.' }
    }
}
