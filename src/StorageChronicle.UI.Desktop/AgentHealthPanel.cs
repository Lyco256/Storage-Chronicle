using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Threading;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.Desktop;

/// <summary>Displays Agent continuity state and exposes explicit pending-gap decisions.</summary>
public sealed class AgentHealthPanel : UserControl
{
    private readonly AgentPipeProjectionClient client;
    private readonly TextBlock status = new();
    private readonly ListBox pending = new() { [AutomationProperties.NameProperty] = "Pending reconciliation requests" };
    private readonly ListBox pendingMedia = new() { [AutomationProperties.NameProperty] = "Pending media-mirror approvals" };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool mediaApprovalOpen;

    /// <summary>Creates a health surface backed by the local Agent pipe.</summary>
    public AgentHealthPanel(AgentPipeProjectionClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        var refresh = new Button { Content = "Refresh health", [AutomationProperties.NameProperty] = "Refresh Agent health" };
        refresh.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        var review = new Button { Content = "Review selected gap", [AutomationProperties.NameProperty] = "Review selected reconciliation" };
        review.Click += async (_, _) => await ReviewAsync().ConfigureAwait(true);
        var reviewMedia = new Button { Content = "Review media consent", [AutomationProperties.NameProperty] = "Review selected media-mirror consent" };
        reviewMedia.Click += async (_, _) => await ReviewMediaAsync().ConfigureAwait(true);
        refreshTimer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(8),
            Children =
            {
                new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { status, refresh, review } },
                    new TextBlock { Text = "Media-mirror consent requests" },
                    pendingMedia,
                    reviewMedia,
                    pending
                } }
            }
        };
        AttachedToVisualTree += async (_, _) => { refreshTimer.Start(); await RefreshAsync().ConfigureAwait(true); };
        DetachedFromVisualTree += (_, _) => refreshTimer.Stop();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var health = await client.GetHealthAsync().ConfigureAwait(true);
            status.Text = $"Agent: {health.State}  Volumes: {health.Volumes.Count}  Pending: {health.PendingReconciliations?.Count ?? 0}";
            pending.ItemsSource = health.PendingReconciliations ?? Array.Empty<PendingReconciliationRequest>();
            pendingMedia.ItemsSource = health.PendingMediaMirrorApprovals ?? Array.Empty<PendingMediaMirrorApproval>();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            status.Text = $"Agent unavailable: {exception.Message}";
            pending.ItemsSource = Array.Empty<PendingReconciliationRequest>();
            pendingMedia.ItemsSource = Array.Empty<PendingMediaMirrorApproval>();
        }
    }

    private async Task ReviewAsync()
    {
        if (pending.SelectedItem is not PendingReconciliationRequest request) return;
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
            {
                status.Text = "Reconciliation unavailable: the confirmation owner is not available.";
                return;
            }

            var confirmation = new ReconciliationConfirmationWindow(request);
            var decision = await confirmation.ShowDialogAsync(owner).ConfigureAwait(true);
            if (decision is not bool execute) return;
            var health = await client.DecideReconciliationAsync(request.RequestId, execute).ConfigureAwait(true);
            status.Text = $"Agent: {health.State}  Volumes: {health.Volumes.Count}  Pending: {health.PendingReconciliations?.Count ?? 0}";
            pending.ItemsSource = health.PendingReconciliations ?? Array.Empty<PendingReconciliationRequest>();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            status.Text = $"Reconciliation unavailable: {exception.Message}";
        }
    }

    private async Task ReviewMediaAsync()
    {
        if (mediaApprovalOpen || pendingMedia.SelectedItem is not PendingMediaMirrorApproval request) return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            status.Text = "Media consent unavailable: the confirmation owner is not available.";
            return;
        }

        mediaApprovalOpen = true;
        try
        {
            var confirmation = new MediaMirrorApprovalWindow(request);
            var decision = await confirmation.ShowDialogAsync(owner).ConfigureAwait(true);
            if (decision is not bool approve) return;
            var health = await client.DecideMediaMirrorApprovalAsync(request.RequestId, approve).ConfigureAwait(true);
            status.Text = $"Agent: {health.State}  Volumes: {health.Volumes.Count}  Pending: {health.PendingReconciliations?.Count ?? 0}";
            pendingMedia.ItemsSource = health.PendingMediaMirrorApprovals ?? Array.Empty<PendingMediaMirrorApproval>();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            status.Text = $"Media consent unavailable: {exception.Message}";
        }
        finally
        {
            mediaApprovalOpen = false;
        }
    }
}
