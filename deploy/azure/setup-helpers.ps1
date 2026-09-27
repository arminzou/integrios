# Helpers used by deploy.ps1: revision state and protected parameter files. Source this file only;
# it performs no deployment on its own.
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
