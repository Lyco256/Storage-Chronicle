using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.Desktop;

/// <summary>Displays Agent continuity state and exposes explicit pending-gap decisions.</summary>
public sealed class AgentHealthPanel : UserControl
{
    private readonly AgentPipeProjectionClient client;
    private readonly TextBlock status = new();
    private readonly ListBox pending = new() { [AutomationProperties.NameProperty] = "Pending reconciliation requests" };

    /// <summary>Creates a health surface backed by the local Agent pipe.</summary>
    public AgentHealthPanel(AgentPipeProjectionClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        var refresh = new Button { Content = "Refresh health", [AutomationProperties.NameProperty] = "Refresh Agent health" };
        refresh.Click += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        var review = new Button { Content = "Review selected gap", [AutomationProperties.NameProperty] = "Review selected reconciliation" };
        review.Click += async (_, _) => await ReviewAsync().ConfigureAwait(true);
        Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(8),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { status, refresh, review } },
                pending
            }
        };
        AttachedToVisualTree += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var health = await client.GetHealthAsync().ConfigureAwait(true);
            status.Text = $"Agent: {health.State}  Volumes: {health.Volumes.Count}  Pending: {health.PendingReconciliations?.Count ?? 0}";
            pending.ItemsSource = health.PendingReconciliations ?? Array.Empty<PendingReconciliationRequest>();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            status.Text = $"Agent unavailable: {exception.Message}";
            pending.ItemsSource = Array.Empty<PendingReconciliationRequest>();
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
}
