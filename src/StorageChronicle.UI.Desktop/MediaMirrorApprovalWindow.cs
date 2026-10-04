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
            MediaMirrorAclDisclosure.NtfsAclUnavailable => "The volume is NTFS. After approval, if the dedicated product tree does not exist, the Agent will create the new .StorageChronicle tree, this PC's writer directory, and ownership markers, then inspect the authenticated account's exact SID, the parent DELETE_CHILD rights, and the complete history tree ACLs before saving consent. If any required descriptor cannot be checked or a broad principal has write/delete access, consent is rejected; the empty product-owned tree may remain, but no existing history is imported and no event history is appended.",
            MediaMirrorAclDisclosure.NotProvidedByFileSystem => $"{request.FileSystem} does not provide NTFS ACL protection; Storage Chronicle will not describe this location as ACL-protected.",
            _ => "The filesystem/ACL protection could not be verified. Approval is disabled."
        };

        var rootIdentity = string.Equals(request.DedicatedMediaRootIdentity, "pending-new-root", StringComparison.Ordinal)
            ? "Not created yet; approval will create a new dedicated product tree and this PC's writer directory."
            : request.DedicatedMediaRootIdentity;
        var existingHistoryScope = request.ExistingHistoryReadAndImportRequested
            ? "Existing history: this PC is not yet authorized to read/import the existing owned history. Approval permits that import into this PC's history."
            : "Existing history: no owned mirror history exists at this root, so no existing events will be imported.";
        var futureAppendScope = request.FutureHistoryAppendRequested
            ? "Future history: approval permits appending new media-specific history only beneath the dedicated product root. Other media files will not be edited, replaced, moved, or deleted."
            : "Future history: no append permission is requested.";
        var disclosure = new TextBlock
        {
            Text = $"PC: {request.PcIdentity}\nMedia: {request.LogicalMediaId}\nVolume identity: {request.VolumeId}\nMirror root: {request.MediaRoot}\\.StorageChronicle\nRoot identity: {rootIdentity}\nFilesystem: {request.FileSystem}\n\n{existingHistoryScope}\n\n{futureAppendScope}\n\n{aclDisclosure}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        approve = new Button { Content = "Approve this PC and media", [AutomationProperties.NameProperty] = "Approve this PC and media" };
        approve.IsEnabled = request.AclDisclosure != MediaMirrorAclDisclosure.Unknown && request.FutureHistoryAppendRequested;
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
