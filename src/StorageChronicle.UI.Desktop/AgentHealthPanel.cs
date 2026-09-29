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
        var execute = new Button { Content = "Reconcile selected", [AutomationProperties.NameProperty] = "Execute selected reconciliation" };
        execute.Click += async (_, _) => await DecideAsync(execute: true).ConfigureAwait(true);
        var decline = new Button { Content = "Keep gap", [AutomationProperties.NameProperty] = "Decline selected reconciliation" };
        decline.Click += async (_, _) => await DecideAsync(execute: false).ConfigureAwait(true);
        Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(8),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { status, refresh, execute, decline } },
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

    private async Task DecideAsync(bool execute)
    {
        if (pending.SelectedItem is not PendingReconciliationRequest request) return;
        try
        {
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
