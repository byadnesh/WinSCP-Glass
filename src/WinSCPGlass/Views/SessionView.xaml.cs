using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using WinScpGlass.ViewModels;

namespace WinScpGlass.Views;

public partial class SessionView : UserControl
{
    private SessionViewModel? _vm;

    public SessionView()
    {
        InitializeComponent();
        Loaded += (_, _) => RemoteView.FocusList();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Tab jumps between the two panels, like a classic commander.</summary>
    public void FocusOther(FilePanelView current) => (current == LocalView ? RemoteView : LocalView).FocusList();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.TerminalFocusRequested -= FocusTerminal;
        }
        _vm = e.NewValue as SessionViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.TerminalFocusRequested += FocusTerminal;
        UpdateTerminalRow();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsTerminalVisible)) UpdateTerminalRow();
    }

    private void UpdateTerminalRow()
    {
        bool visible = _vm is { HasTerminal: true, IsTerminalVisible: true };
        TerminalRow.MinHeight = visible ? 150 : 0;
        TerminalRow.Height = visible ? new GridLength(_vm!.TerminalHeight) : new GridLength(0);
        if (!visible && TerminalView.IsKeyboardFocusWithin) RemoteView.FocusList();
    }

    private void FocusTerminal() =>
        Dispatcher.BeginInvoke(() => TerminalView.Focus(), DispatcherPriority.Input);

    private void TerminalSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_vm != null) _vm.TerminalHeight = TerminalRow.ActualHeight;
    }

    private void Session_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+` shows/hides the terminal, like in code editors.
        if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Control && _vm is { HasTerminal: true })
        {
            _vm.ToggleTerminalCommand.Execute(null);
            if (_vm.IsTerminalVisible) FocusTerminal();
            e.Handled = true;
        }
    }
}
