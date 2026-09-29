using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia;
using StorageChronicle.Settings;
using StorageChronicle.UI.Settings;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.Desktop;

/// <summary>Modal editor for Machine and current-user settings, backed only by Agent IPC.</summary>
public sealed class SettingsDialogWindow : Window
{
    private readonly SettingsDialogViewModel viewModel;
    private readonly TextBox monitoringPaths = Field("One absolute path per line");
    private readonly TextBox excludedPaths = Field("One absolute path per line");
    private readonly TextBox logStoragePath = Field("Absolute history directory");
    private readonly TextBox mediaMirrors = Field("One media-id=absolute-path pair per line");
    private readonly TextBox noiseFilter = Field("Standard or Aggressive");
    private readonly TextBox flushInterval = Field("1–60 seconds");
    private readonly TextBox activityTimeout = Field("0.5–60 seconds");
    private readonly TextBox paneTimeout = Field("0.5–60 seconds");
    private readonly TextBox eventStackPageSize = Field("50–5000 rows");
    private readonly TextBox eventStackSort = Field("NewestFirst or OldestFirst");
    private readonly TextBox eventStackMode = Field("Source, Normalized, or Grouped");
    private readonly TextBox diffFormat = Field("Unified or SideBySide");
    private readonly TextBox diffZoom = Field("50–300 percent");
    private readonly TextBox diffColumns = Field("BeforeAfter or AfterBefore");
    private readonly TextBox savedFilters = Field("One saved filter per line");
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    /// <summary>Creates the modal settings editor using the shared Agent pipe client.</summary>
    public SettingsDialogWindow(AgentPipeProjectionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        viewModel = new SettingsDialogViewModel(client);
        Title = "Storage Chronicle Settings";
        Width = 760;
        Height = 820;
        MinWidth = 620;
        MinHeight = 600;

        var fields = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        AddSection(fields, "Machine settings");
        AddField(fields, "Monitored paths", monitoringPaths);
        AddField(fields, "Excluded paths", excludedPaths);
        AddField(fields, "History storage path", logStoragePath);
        AddField(fields, "External-media mirrors", mediaMirrors);
        AddField(fields, "Noise filter", noiseFilter);
        AddField(fields, "Flush interval", flushInterval);
        AddSection(fields, "User settings");
        AddField(fields, "Activity group timeout", activityTimeout);
        AddField(fields, "Pane timeout", paneTimeout);
        AddField(fields, "Event Stack page size", eventStackPageSize);
        AddField(fields, "Event Stack sort", eventStackSort);
        AddField(fields, "Event Stack initial mode", eventStackMode);
        AddField(fields, "Diff View format", diffFormat);
        AddField(fields, "Diff View zoom", diffZoom);
        AddField(fields, "Diff View column order", diffColumns);
        AddField(fields, "Saved filters", savedFilters);
        fields.Children.Add(status);

        var apply = new Button { Content = "Apply", [AutomationProperties.NameProperty] = "Apply settings" };
        apply.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);
        var cancel = new Button { Content = "Close", [AutomationProperties.NameProperty] = "Close settings" };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { apply, cancel } };
        var layout = new DockPanel { Children =
        {
            new Border { [DockPanel.DockProperty] = Dock.Bottom, Padding = new Thickness(16, 8), Child = buttons },
            new ScrollViewer { Content = fields }
        }};
        Content = layout;
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        try
        {
            await viewModel.LoadAsync().ConfigureAwait(true);
            PopulateFields();
            status.Text = viewModel.StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            status.Text = $"Settings could not be loaded from the Agent: {exception.Message}";
        }
    }

    private async Task ApplyAsync()
    {
        try
        {
            ReadFields();
            await viewModel.ApplyCommand.ExecuteAsync(null).ConfigureAwait(true);
            status.Text = viewModel.ErrorMessage ?? viewModel.StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
        {
            status.Text = $"Settings were not applied: {exception.Message}";
        }
    }

    private void PopulateFields()
    {
        var machine = viewModel.MachineDraft;
        var user = viewModel.UserDraft;
        monitoringPaths.Text = string.Join(Environment.NewLine, machine.MonitoringPaths);
        excludedPaths.Text = string.Join(Environment.NewLine, machine.ExcludedPaths);
        logStoragePath.Text = machine.LogStoragePath;
        mediaMirrors.Text = string.Join(Environment.NewLine, machine.MediaMirrors.Select(pair => $"{pair.Key}={pair.Value}"));
        noiseFilter.Text = machine.NoiseFilter.ToString();
        flushInterval.Text = machine.FlushIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        activityTimeout.Text = user.ActivityGroupTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        paneTimeout.Text = user.PaneTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        eventStackPageSize.Text = user.EventStackPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        eventStackSort.Text = user.EventStackSort.ToString();
        eventStackMode.Text = user.InitialEventStackMode.ToString();
        diffFormat.Text = user.DiffFormat.ToString();
        diffZoom.Text = user.DiffZoomPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        diffColumns.Text = user.DiffColumns.ToString();
        savedFilters.Text = string.Join(Environment.NewLine, user.SavedFilters);
    }

    private void ReadFields()
    {
        var machine = viewModel.MachineDraft;
        var user = viewModel.UserDraft;
        viewModel.MachineDraft = machine with
        {
            MonitoringPaths = Lines(monitoringPaths.Text),
            ExcludedPaths = Lines(excludedPaths.Text),
            LogStoragePath = logStoragePath.Text?.Trim() ?? string.Empty,
            MediaMirrors = ParseMirrors(mediaMirrors.Text),
            NoiseFilter = ParseEnum<NoiseFilterProfile>(noiseFilter.Text),
            FlushIntervalSeconds = ParseInt(flushInterval.Text, "Flush interval")
        };
        viewModel.UserDraft = user with
        {
            ActivityGroupTimeoutSeconds = ParseDouble(activityTimeout.Text, "Activity group timeout"),
            PaneTimeoutSeconds = ParseDouble(paneTimeout.Text, "Pane timeout"),
            EventStackPageSize = ParseInt(eventStackPageSize.Text, "Event Stack page size"),
            EventStackSort = ParseEnum<EventStackSortOrder>(eventStackSort.Text),
            InitialEventStackMode = ParseEnum<EventStackInitialMode>(eventStackMode.Text),
            DiffFormat = ParseEnum<DiffDisplayFormat>(diffFormat.Text),
            DiffZoomPercent = ParseInt(diffZoom.Text, "Diff View zoom"),
            DiffColumns = ParseEnum<DiffColumnOrder>(diffColumns.Text),
            SavedFilters = Lines(savedFilters.Text)
        };
    }

    private static string[] Lines(string? value) => (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyDictionary<string, string> ParseMirrors(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(value))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1) throw new FormatException("Each mirror must use media-id=absolute-path format.");
            if (!result.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim())) throw new FormatException("Mirror media identifiers must be unique.");
        }
        return result;
    }

    private static TEnum ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value?.Trim(), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException($"'{value}' is not a supported {typeof(TEnum).Name} value.");

    private static int ParseInt(string? value, string field) => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : throw new FormatException($"{field} must be an integer.");

    private static double ParseDouble(string? value, string field) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : throw new FormatException($"{field} must be a number.");

    private static TextBox Field(string placeholder) => new()
    {
        AcceptsReturn = true,
        MinHeight = 32,
        PlaceholderText = placeholder
    };

    private static void AddSection(Panel panel, string title) => panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new Thickness(0, 8, 0, 0) });

    private static void AddField(Panel panel, string name, Control control)
    {
        control[AutomationProperties.NameProperty] = name;
        panel.Children.Add(new TextBlock { Text = name });
        panel.Children.Add(control);
    }
}
