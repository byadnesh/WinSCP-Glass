using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace WinScpGlass.Services;

/// <summary>Login details for the interactive shell, taken from the file session that is already open.</summary>
public sealed record SshTerminalOptions(
    string Host, int Port, string User, string? Password,
    string? PrivateKeyPath, string? PrivateKeyPassphrase,
    // WinSCP-format fingerprint the user already trusted, e.g. "ssh-ed25519 255 base64".
    string? TrustedHostKey);

/// <summary>
/// An interactive SSH shell (with a pseudo-terminal) for the built-in terminal.
/// Output arrives on a background thread through <see cref="Output"/>.
/// </summary>
public sealed class SshTerminal : IDisposable
{
    private readonly SshTerminalOptions _options;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly object _decodeLock = new();
    private SshClient? _client;
    private ShellStream? _shell;
    private bool _closedRaised;

    public event Action<string>? Output;
    /// <summary>Raised once when the shell ends; the argument is an error message, or null for a normal exit.</summary>
    public event Action<string?>? Closed;

    public bool IsConnected => _client?.IsConnected == true && _shell != null;

    public SshTerminal(SshTerminalOptions options)
    {
        _options = options;
    }

    public async Task ConnectAsync(uint columns, uint rows, CancellationToken cancel)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(_options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_options.PrivateKeyPath)
                : new PrivateKeyFile(_options.PrivateKeyPath, _options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(_options.User, key));
        }
        if (!string.IsNullOrEmpty(_options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(_options.User, _options.Password));
            // Many Linux servers only offer keyboard-interactive; answer its password prompt too.
            var interactive = new KeyboardInteractiveAuthenticationMethod(_options.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (!prompt.IsEchoed) prompt.Response = _options.Password;
                }
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(_options.User));

        var info = new ConnectionInfo(_options.Host, _options.Port > 0 ? _options.Port : 22, _options.User, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        RestrictHostKeyAlgorithms(info);

        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.HostKeyReceived += (_, e) => e.CanTrust = IsTrusted(e);
        client.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
        _client = client;

        await client.ConnectAsync(cancel);

        var shell = client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 64 * 1024);
        shell.DataReceived += (_, e) => Decode(e.Data);
        shell.Closed += (_, _) => RaiseClosed(null);
        shell.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
        _shell = shell;
    }

    /// <summary>
    /// Only accept the key type the user already trusted, so the server presents the same key
    /// whose fingerprint WinSCP showed (servers usually hold several host keys).
    /// </summary>
    private void RestrictHostKeyAlgorithms(ConnectionInfo info)
    {
        var type = _options.TrustedHostKey?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(type)) return;

        bool Matches(string algorithm) =>
            algorithm == type
            || (type == "ssh-rsa" && algorithm.StartsWith("rsa-sha2-", StringComparison.Ordinal))
            || algorithm.StartsWith(type + "-cert", StringComparison.Ordinal);

        var keep = info.HostKeyAlgorithms.Keys.Where(Matches).ToList();
        if (keep.Count == 0) return;
        foreach (var algorithm in info.HostKeyAlgorithms.Keys.ToList())
        {
            if (!keep.Contains(algorithm)) info.HostKeyAlgorithms.Remove(algorithm);
        }
    }

    private bool IsTrusted(HostKeyEventArgs e)
    {
        // WinSCP stores "type bits base64sha256"; SSH.NET reports the same unpadded base64.
        var trusted = _options.TrustedHostKey?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return trusted != null && string.Equals(trusted.TrimEnd('='), e.FingerPrintSHA256, StringComparison.Ordinal);
    }

    private void Decode(byte[] data)
    {
        string text;
        lock (_decodeLock)
        {
            // The decoder keeps partial UTF-8 sequences split across packets.
            var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
            int count = _decoder.GetChars(data, 0, data.Length, chars, 0);
            text = new string(chars, 0, count);
        }
        if (text.Length > 0) Output?.Invoke(text);
    }

    private void RaiseClosed(string? error)
    {
        if (_closedRaised) return;
        _closedRaised = true;
        Closed?.Invoke(error);
    }

    public void Send(string text)
    {
        var shell = _shell;
        if (shell == null || !IsConnected) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            shell.Write(bytes, 0, bytes.Length);
            shell.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            RaiseClosed(ex.Message);
        }
    }

    public void Resize(uint columns, uint rows)
    {
        try
        {
            _shell?.ChangeWindowSize(columns, rows, 0, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException)
        {
            // The shell is closing; the next connect uses the new size.
        }
    }

    public void Dispose()
    {
        _closedRaised = true;
        try { _shell?.Dispose(); } catch (Exception) { /* already closed */ }
        try { _client?.Dispose(); } catch (Exception) { /* already closed */ }
        _shell = null;
        _client = null;
    }
}
