using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using StorageChronicle.Contracts.Runtime;

namespace StorageChronicle.UI.Desktop;

/// <summary>Displays the exact media-mirror consent scope and the filesystem's ACL-protection limits.</summary>
public sealed class MediaMirrorApprovalWindow : Window
{
    private readonly Button approve;

    /// <summary>Creates a confirmation for one live, Agent-issued media approval request.</summary>
    public MediaMirrorApprovalWindow(PendingMediaMirrorApproval request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Title = "External media history approval";
        Width = 620;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetValue(AutomationProperties.NameProperty, "External media history approval");

        var aclDisclosure = request.AclDisclosure switch
        {
            MediaMirrorAclDisclosure.NtfsAclVerified => "The dedicated directory ACL was checked and verified on NTFS.",
            MediaMirrorAclDisclosure.NtfsAclUnavailable => "The volume is NTFS, but the dedicated directory ACL could not be verified.",
            MediaMirrorAclDisclosure.NotProvidedByFileSystem => $"{request.FileSystem} does not provide NTFS ACL protection; Storage Chronicle will not describe this location as ACL-protected.",
            _ => "The filesystem/ACL protection could not be verified. Approval is disabled."
        };

        var disclosure = new TextBlock
        {
            Text = $"PC: {request.PcIdentity}\nMedia: {request.LogicalMediaId}\nVolume identity: {request.VolumeId}\nMirror root: {request.MediaRoot}\\.StorageChronicle\nRoot identity: {request.DedicatedMediaRootIdentity}\nFilesystem: {request.FileSystem}\n\nIf you approve, Storage Chronicle may read existing owned media history and import it into this PC's history: {request.ExistingHistoryReadAndImportRequested}. It may also append future media-specific history beneath this dedicated root: {request.FutureHistoryAppendRequested}. It will not edit, replace, or delete other media files.\n\n{aclDisclosure}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        approve = new Button { Content = "Approve this PC and media", [AutomationProperties.NameProperty] = "Approve this PC and media" };
        approve.IsEnabled = request.AclDisclosure != MediaMirrorAclDisclosure.Unknown && request.ExistingHistoryReadAndImportRequested && request.FutureHistoryAppendRequested;
        approve.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", [AutomationProperties.NameProperty] = "Cancel media-mirror approval" };
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                disclosure,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { approve, cancel } }
            }
        };
    }

    /// <summary>Shows the approval dialog and returns true only for an explicit approval click.</summary>
    public Task<bool?> ShowDialogAsync(Window owner) => ShowDialog<bool?>(owner);
}
