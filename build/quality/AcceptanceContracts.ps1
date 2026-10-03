Set-StrictMode -Version Latest

function Get-RequiredWindowsPrivilegedCapabilities {
    [OutputType([string[]])]
    param()

    return @(
        'Vhdx',
        'UsnQuery',
        'UsnRead',
        'Mft',
        'Reconciliation',
        'Etw',
        'ReadDirectoryChangesW',
        'BufferGap',
        'Smb',
        'Service',
        'SessionAgent',
        'Clipboard',
        'VolumeGuid',
        'HotAttachDetach',
        'AclDeniedMetadata',
        'NonNtfs'
    )
}

function Get-RequiredInstallerCaseIds {
    [OutputType([string[]])]
    param()

    return @(
        'clean-install',
        'repair',
        'update',
        'rollback',
        'uninstall',
        'failed-install-rollback',
        'history-retention',
        'service',
        'session',
        'non-admin',
        'storage-permission'
    )
}

function Get-RequiredWindows10StageAChecks {
    [OutputType([string[]])]
    param()

    return @(
        'Application',
        'AvaloniaUI',
        'Agent',
        'SessionAgent',
        'Usn',
        'Mft',
        'Etw',
        'ReadDirectoryChangesW',
        'Clipboard',
        'Smb',
        'CloudFilesCapability',
        'Reconciliation',
        'Installer',
        'HistoryRetention',
        'NoDriver'
    )
}

function Write-NewJsonArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value,
        [ValidateRange(1, 100)][int]$Depth = 12
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($parent)) { throw 'The JSON artifact path has no parent directory.' }
    if ([System.IO.File]::Exists($fullPath) -or [System.IO.Directory]::Exists($fullPath)) { throw "The JSON artifact destination already exists: $fullPath" }
    [void][System.IO.Directory]::CreateDirectory($parent)

    $temporaryName = '.' + [System.IO.Path]::GetFileName($fullPath) + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $temporaryPath = Join-Path $parent $temporaryName
    $json = ConvertTo-Json -InputObject $Value -Depth $Depth
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine)
    $stream = [System.IO.FileStream]::new($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    # Same-directory move is atomic and the two-argument overload never replaces an existing artifact.
    [System.IO.File]::Move($temporaryPath, $fullPath)
}
