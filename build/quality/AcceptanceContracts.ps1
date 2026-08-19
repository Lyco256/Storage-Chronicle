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
