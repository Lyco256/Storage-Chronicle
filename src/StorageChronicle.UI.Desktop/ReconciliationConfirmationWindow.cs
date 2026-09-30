using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Globalization;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.Desktop;

/// <summary>Shows the fixed user confirmation required before a continuity-gap reconciliation.</summary>
public sealed class ReconciliationConfirmationWindow : Window
{
    /// <summary>Creates the confirmation dialog for one pending gap.</summary>
    public ReconciliationConfirmationWindow(PendingReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Title = "変更履歴の欠落を確認";
        Width = 640;
        Height = 420;
        CanResize = false;
        SetValue(AutomationProperties.NameProperty, "Reconciliation confirmation");

        var prompt = new ReconciliationPrompt(
            request.VolumeId?.Value ?? "(unknown)",
            string.IsNullOrWhiteSpace(request.FileSystem) ? "Unknown" : request.FileSystem,
            request.Reason,
            request.GapStartUtc,
            request.DiscoveredUtc);

        var execute = new Button
        {
            Name = "ExecuteButton",
            Content = "実行する",
            [AutomationProperties.NameProperty] = "実行する"
        };
        execute.Click += (_, _) => Close(true);
        var decline = new Button
        {
            Name = "DeclineButton",
            Content = "実行しない",
            [AutomationProperties.NameProperty] = "実行しない"
        };
        decline.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Name = "TitleText",
                    Text = "このドライブの変更履歴に欠落があります",
                    FontSize = 22,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock { Text = $"ドライブ: {prompt.Volume}" },
                new TextBlock { Text = $"ファイルシステム: {prompt.FileSystem}" },
                new TextBlock { Text = $"原因: {prompt.Reason}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"未確認期間: {FormatGapStart(prompt.GapStart)} ～ {prompt.DiscoveredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" },
                new TextBlock { Text = $"検出時刻: {prompt.DiscoveredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" },
                new TextBlock
                {
                    Text = "全数照合を実行すると、現在の状態との差分を確認できます。正確な変更時刻と操作プロセスは復元できません。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { execute, decline }
                }
            }
        };
    }

    /// <summary>Shows the modal confirmation and returns true for 「実行する」.</summary>
    public Task<bool?> ShowDialogAsync(Window owner) => ShowDialog<bool?>(owner);

    private static string FormatGapStart(DateTimeOffset? value) => value is { } start
        ? start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
        : "最後に連続性が保証された境界";
}
