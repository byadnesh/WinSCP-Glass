using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinScpGlass.Services;
using WinScpGlass.Terminal;

namespace WinScpGlass.ViewModels;

public enum TerminalState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>The built-in SSH terminal: an interactive shell on the same server as the file session.</summary>
public sealed partial class TerminalViewModel : ObservableObject, IDisposable
{
    private readonly Func<SshTerminalOptions> _options;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly StringBuilder _pending = new();
    private bool _flushQueued;
    private SshTerminal? _ssh;
    private CancellationTokenSource? _connectCancel;
    private bool _disposed;

    public TerminalEmulator Emulator { get; } = new(100, 28);
    public string HostLabel { get; }

    /// <summary>The screen changed and needs repainting.</summary>
    public event Action? Updated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsConnected), nameof(CanReconnect))]
    private TerminalState _state;

    [ObservableProperty] private string _title = "";

    public bool IsConnected => State == TerminalState.Connected;
    public bool CanReconnect => State is TerminalState.Disconnected or TerminalState.Failed;

    public string StatusText => State switch
    {
        TerminalState.Connecting => "Connecting…",
        TerminalState.Connected => string.IsNullOrWhiteSpace(Title) ? $"Connected · {HostLabel}" : Title,
        TerminalState.Disconnected => "Session ended",
        TerminalState.Failed => "Not connected",
        _ => "Ready"
    };

    public TerminalViewModel(Func<SshTerminalOptions> options, string hostLabel)
    {
        _options = options;
        HostLabel = hostLabel;
        Emulator.Reply += reply => _ssh?.Send(reply);
        Emulator.TitleChanged += title => Title = title;
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(StatusText));

    public async Task ConnectAsync()
    {
        if (_disposed || State is TerminalState.Connecting or TerminalState.Connected) return;

        _ssh?.Dispose();
        _ssh = null;
        _connectCancel = new CancellationTokenSource();
        State = TerminalState.Connecting;
        Title = "";
        WriteLocal($"\x1b[38;5;111m●\x1b[0m \x1b[2mConnecting to {HostLabel}…\x1b[0m\r\n");

        var ssh = new SshTerminal(_options());
        ssh.Output += OnOutput;
        ssh.Closed += error => _dispatcher.BeginInvoke(() => OnClosed(ssh, error));
        try
        {
            await ssh.ConnectAsync((uint)Emulator.Columns, (uint)Emulator.Rows, _connectCancel.Token);
            if (_disposed)
            {
                ssh.Dispose();
                return;
            }
            _ssh = ssh;
            State = TerminalState.Connected;
        }
        catch (Exception ex)
        {
            ssh.Dispose();
            if (_disposed) return;
            State = TerminalState.Failed;
            string reason = ex.Message.Contains("negotiat", StringComparison.OrdinalIgnoreCase)
                ? "The server's identity did not match the key you trusted for this site."
                : ex.Message;
            WriteLocal($"\x1b[38;5;204m✕ Couldn't open the terminal:\x1b[0m {reason}\r\n\x1b[2mPress Enter to try again.\x1b[0m\r\n");
        }
    }

    private void OnClosed(SshTerminal source, string? error)
    {
        if (_disposed || source != _ssh) return;
        _ssh.Dispose();
        _ssh = null;
        State = error == null ? TerminalState.Disconnected : TerminalState.Failed;
        string message = error == null ? "Session ended." : $"Connection lost: {error}";
        WriteLocal($"\r\n\x1b[2m{message} Press Enter to reconnect.\x1b[0m\r\n");
    }

    private void OnOutput(string text)
    {
        // Output arrives on the SSH thread; batch it so bursts become one repaint.
        lock (_pending)
        {
            _pending.Append(text);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        _dispatcher.BeginInvoke(Flush, DispatcherPriority.Normal);
    }

    private void Flush()
    {
        string text;
        lock (_pending)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushQueued = false;
        }
        if (_disposed) return;
        Emulator.Feed(text);
        Updated?.Invoke();
    }

    private void WriteLocal(string text)
    {
        Emulator.Feed(text);
        Updated?.Invoke();
    }

    /// <summary>Keyboard input from the view. Enter restarts a closed session.</summary>
    public void Send(string text)
    {
        if (CanReconnect)
        {
            if (text == "\r") _ = ConnectAsync();
            return;
        }
        _ssh?.Send(text);
    }

    public void Resize(int columns, int rows)
    {
        Emulator.Resize(columns, rows);
        _ssh?.Resize((uint)columns, (uint)rows);
        Updated?.Invoke();
    }

    public void ChangeDirectory(string path)
    {
        if (!IsConnected) return;
        // Single-quote for the shell: ' becomes '\''
        Send($"cd '{path.Replace("'", "'\\''")}'\r");
    }

    [RelayCommand]
    private Task Reconnect() => ConnectAsync();

    [RelayCommand]
    private void Clear()
    {
        if (Emulator.IsAlternateScreen) return;
        Emulator.Clear();
        Updated?.Invoke();
        // Ctrl+L makes the shell redraw its prompt on the cleared screen.
        if (IsConnected) _ssh?.Send("\x0c");
    }

    public void Dispose()
    {
        _disposed = true;
        _connectCancel?.Cancel();
        _ssh?.Dispose();
        _ssh = null;
    }
}
