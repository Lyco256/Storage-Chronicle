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
