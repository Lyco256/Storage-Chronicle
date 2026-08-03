using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StorageChronicle.Settings;

namespace StorageChronicle.UI.Settings;

/// <summary>Modal Avalonia dialog for editing Agent-owned and user-owned settings.</summary>
public partial class SettingsDialogWindow : Window
{
    /// <summary>Creates an unbound Window for Avalonia XAML tooling.</summary>
    public SettingsDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>Creates the modal settings Window backed by an Agent gateway.</summary>
    public SettingsDialogWindow(IAgentSettingsGateway gateway)
        : this(new SettingsDialogViewModel(gateway))
    {
    }

    /// <summary>Creates the modal settings Window backed by the supplied ViewModel.</summary>
    public SettingsDialogWindow(SettingsDialogViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.ApplySucceeded += OnApplySucceeded;
        Closed += OnClosed;
        ViewModel.Load();
        // Re-assign after the synchronous Agent load so every binding reads the loaded draft immediately.
        DataContext = null;
        DataContext = ViewModel;
    }

    /// <summary>Gets the ViewModel displayed by this dialog.</summary>
    public SettingsDialogViewModel ViewModel { get; } = null!;

    /// <summary>Shows this dialog as a modal child and returns true after a successful apply.</summary>
    public Task<bool?> ShowDialogAsync(Window owner) => ShowDialog<bool?>(owner);

    private void OnApplySucceeded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                Close(true);
            }
        });
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            ViewModel.CancelApplyCommand.Execute(null);
            return;
        }

        Close(false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.ApplySucceeded -= OnApplySucceeded;
        if (ViewModel.IsBusy)
        {
            ViewModel.CancelApplyCommand.Execute(null);
        }
    }
}
