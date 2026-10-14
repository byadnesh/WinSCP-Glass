using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WinScpGlass.Controls;
using WinScpGlass.ViewModels;

namespace WinScpGlass;

public partial class MainWindow : Window
{
    private readonly BlurEffect _backdropBlur = new() { Radius = 0, KernelType = KernelType.Gaussian };
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowGlass.Apply(this, App.Shell.IsDark);
        StateChanged += (_, _) => UpdateMaximizedState();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveDialog)) UpdateDialog(_viewModel!.ActiveDialog);
    }

    // ---------------------------------------------------------------- dialog sheet

    private void UpdateDialog(DialogRequest? request)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (request != null)
        {
            SheetContent.DataContext = request;
            DialogLayer.Visibility = Visibility.Visible;
            AppLayer.Effect = _backdropBlur;
            AppLayer.IsHitTestVisible = false;

            _backdropBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(14, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            Sheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = spring });
            SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = spring });

            Dispatcher.BeginInvoke(() => SheetContent.FocusInitial(), System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (_viewModel?.ActiveDialog != null) return; // another dialog opened meanwhile
            DialogLayer.Visibility = Visibility.Collapsed;
            AppLayer.Effect = null;
        };
        AppLayer.IsHitTestVisible = true;
        _backdropBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Sheet.BeginAnimation(OpacityProperty, fade);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var dialog = _viewModel?.ActiveDialog;
        if (dialog == null) return;
        if (e.Key == Key.Escape)
        {
            dialog.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button)
        {
            dialog.Accept();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- caption

    private void UpdateMaximizedState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A maximized chrome-less window overhangs the screen by the resize frame.
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = maximized
            ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4)
            : new Thickness(0);
        UiProps.SetIcon(MaxButton, maximized ? "" : "");
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _viewModel?.Session?.Dispose();
    }
}
