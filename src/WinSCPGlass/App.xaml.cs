using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinScpGlass.Controls;
using WinScpGlass.Models;
using WinScpGlass.ViewModels;

namespace WinScpGlass;

public partial class App : Application
{
    public static MainViewModel Shell { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var settings = AppSettings.Load();
        ApplyTheme(settings.DarkTheme);

        Shell = new MainViewModel(settings);
        Dialogs.Host = Shell;

        var window = new MainWindow { DataContext = Shell };
        MainWindow = window;
        window.Show();
#if DEBUG
        Debugging.SnapshotRunner.RunIfRequested(window);
#endif
    }

    public static void ApplyTheme(bool dark)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
        if (Current.MainWindow != null) WindowGlass.SetDark(Current.MainWindow, dark);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogError(e.Exception);
        if (MainWindow is not { IsLoaded: true })
        {
            // Failed before the UI exists: there is nothing to keep running.
            MessageBox.Show(e.Exception.Message, "WinSCP Glass could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        ShowUnexpectedError(e.Exception);
    }

    private static bool _showingUnexpectedError;

    // An exception inside layout repeats on every pass; show one sheet, not a queue of them.
    private static async void ShowUnexpectedError(Exception exception)
    {
        if (_showingUnexpectedError) return;
        _showingUnexpectedError = true;
        try
        {
            await Dialogs.ErrorAsync("Unexpected error",
                $"Something went wrong. Details were saved to {Path.Combine(AppSettings.Folder, "error.log")}.",
                exception.Message);
        }
        finally
        {
            _showingUnexpectedError = false;
        }
    }

    private static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"[{DateTime.Now:u}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging is best effort.
        }
    }
}
