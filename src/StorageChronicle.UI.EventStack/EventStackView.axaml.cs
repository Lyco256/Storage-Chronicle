using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace StorageChronicle.UI.EventStack;

/// <summary>Compiled-binding Avalonia view for the Event Stack.</summary>
public partial class EventStackView : UserControl
{
    /// <summary>Creates a view whose DataContext can be assigned by the shell.</summary>
    public EventStackView()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Creates a view for a supplied UI-only view model.</summary>
    public EventStackView(EventStackViewModel viewModel) : this() => DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not EventStackViewModel viewModel) return;
        var key = args.Key switch
        {
            Key.Up => EventStackKey.Up,
            Key.Down => EventStackKey.Down,
            Key.Enter => EventStackKey.Enter,
            Key.Right => EventStackKey.Right,
            Key.Left => EventStackKey.Left,
            Key.PageUp => EventStackKey.PageUp,
            Key.PageDown => EventStackKey.PageDown,
            Key.Home => EventStackKey.Home,
            Key.End => EventStackKey.End,
            Key.Escape => EventStackKey.Escape,
            _ => (EventStackKey?)null
        };
        if (key is null) return;
        args.Handled = true;
        await viewModel.HandleKeyAsync(key.Value).ConfigureAwait(true);
    }
}
