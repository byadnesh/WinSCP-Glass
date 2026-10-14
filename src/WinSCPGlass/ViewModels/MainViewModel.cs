using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinScpGlass.Models;

namespace WinScpGlass.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    public ConnectViewModel Connect { get; }
    public ObservableCollection<Toast> Toasts { get; } = new();

    [ObservableProperty]
    private object _currentView;

    [ObservableProperty]
    private SessionViewModel? _session;

    [ObservableProperty]
    private DialogRequest? _activeDialog;

    [ObservableProperty]
    private bool _isDark;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        _isDark = settings.DarkTheme;
        Connect = new ConnectViewModel(this);
        _currentView = Connect;
    }

    partial void OnIsDarkChanged(bool value)
    {
        App.ApplyTheme(value);
        Settings.DarkTheme = value;
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    public void ShowSession(SessionViewModel session)
    {
        Session = session;
        CurrentView = session;
    }

    public void ShowConnect()
    {
        Session = null;
        CurrentView = Connect;
    }

    public void SaveSettings()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"Couldn't save settings: {ex.Message}", Tone.Danger);
        }
    }

    public async void Notify(string message, Tone tone = Tone.Info)
    {
        string glyph = tone switch
        {
            Tone.Success => "",
            Tone.Danger => "",
            Tone.Warning => "",
            _ => ""
        };
        var toast = new Toast(message, glyph, tone);
        Toasts.Add(toast);
        while (Toasts.Count > 3) Toasts.RemoveAt(0);
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Toasts.Remove(toast);
    }
}
