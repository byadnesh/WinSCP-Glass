using System.IO;
using WinScpGlass.Models;
using WinSCP;

namespace WinScpGlass.Services;

/// <summary>Thrown when the server connection dropped and must be reopened.</summary>
public sealed class ConnectionLostException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Async facade over WinSCP.Session (which is blocking and not re-entrant).
/// Every call runs on a worker thread and calls are serialized.
/// </summary>
public sealed class RemoteConnection : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SessionOptions _options;
    private Session _session;
    private Action<FileTransferProgressEventArgs>? _progress;

    public SiteProfile Site { get; }
    public string HomePath { get; private set; } = "/";

    private RemoteConnection(SiteProfile site, SessionOptions options)
    {
        Site = site;
        _options = options;
        _session = CreateSession();
    }

    private Session CreateSession()
    {
        var session = new Session();
        session.FileTransferProgress += (_, e) => _progress?.Invoke(e);
        return session;
    }

    public static SessionOptions BuildOptions(SiteProfile site, string? password, string? hostKey)
    {
        var options = new SessionOptions
        {
            Protocol = site.Protocol switch
            {
                ProtocolKind.Scp => Protocol.Scp,
                ProtocolKind.Ftp => Protocol.Ftp,
                ProtocolKind.WebDav => Protocol.Webdav,
                ProtocolKind.S3 => Protocol.S3,
                _ => Protocol.Sftp
            },
            HostName = site.Host.Trim(),
            PortNumber = site.Port,
            UserName = string.IsNullOrWhiteSpace(site.User) ? null : site.User.Trim(),
            Timeout = TimeSpan.FromSeconds(20),
        };

        bool hasKey = site.IsSsh && !string.IsNullOrWhiteSpace(site.PrivateKeyPath);
        if (hasKey)
        {
            options.SshPrivateKeyPath = site.PrivateKeyPath;
            if (!string.IsNullOrEmpty(password)) options.PrivateKeyPassphrase = password;
        }
        else if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }

        if (site.IsSsh && hostKey != null)
        {
            options.SshHostKeyFingerprint = hostKey;
        }

        switch (site.Protocol)
        {
            case ProtocolKind.Ftp:
                options.FtpSecure = site.FtpEncryption switch
                {
                    FtpEncryption.ExplicitTls => FtpSecure.Explicit,
                    FtpEncryption.ImplicitTls => FtpSecure.Implicit,
                    _ => FtpSecure.None
                };
                options.FtpMode = site.FtpActiveMode ? FtpMode.Active : FtpMode.Passive;
                break;
            case ProtocolKind.WebDav:
                options.Secure = site.UseTls;
                break;
            case ProtocolKind.S3:
                options.Secure = true;
                break;
        }

        bool usesTls = site.Protocol == ProtocolKind.S3
            || (site.Protocol == ProtocolKind.WebDav && site.UseTls)
            || (site.Protocol == ProtocolKind.Ftp && site.FtpEncryption != FtpEncryption.None);
        if (usesTls && site.AcceptAnyTlsCertificate)
        {
            options.GiveUpSecurityAndAcceptAnyTlsHostCertificate = true;
        }
        return options;
    }

    /// <summary>Asks the server for its SSH host key without logging in.</summary>
    public static Task<string> ScanHostKeyAsync(SessionOptions options, CancellationToken cancel) => Task.Run(() =>
    {
        using var session = new Session();
        using var registration = cancel.Register(session.Abort);
        return session.ScanFingerprint(options, "SHA-256");
    }, cancel);

    public static async Task<RemoteConnection> OpenAsync(SiteProfile site, SessionOptions options, CancellationToken cancel)
    {
        var connection = new RemoteConnection(site, options);
        try
        {
            await Task.Run(() =>
            {
                using var registration = cancel.Register(connection._session.Abort);
                connection._session.Open(options);
                connection.HomePath = connection._session.HomePath ?? "/";
            }, cancel);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Task ReconnectAsync() => RunAsync(() =>
    {
        try { _session.Dispose(); } catch (Exception) { /* already broken */ }
        _session = CreateSession();
        _session.Open(_options);
        return true;
    }, reconnecting: true);

    private async Task<T> RunAsync<T>(Func<T> work, bool reconnecting = false)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return work();
                }
                catch (SessionException ex) when (!reconnecting && !_session.Opened)
                {
                    throw new ConnectionLostException(ex.Message, ex);
                }
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunAsync(Action work) => RunAsync(() => { work(); return true; });

    public Task<List<FileEntry>> ListAsync(string path) => RunAsync(() =>
    {
        var info = _session.ListDirectory(path);
        var result = new List<FileEntry>(info.Files.Count);
        foreach (RemoteFileInfo file in info.Files)
        {
            if (file.IsThisDirectory || file.IsParentDirectory) continue;
            result.Add(new FileEntry
            {
                Name = file.Name,
                FullPath = RemotePath.Combine(path, file.Name),
                IsDirectory = file.IsDirectory,
                Size = file.IsDirectory ? 0 : file.Length,
                Modified = file.LastWriteTime,
                Detail = file.FilePermissions?.Text ?? ""
            });
        }
        return result;
    });

    public Task CreateDirectoryAsync(string path) => RunAsync(() => _session.CreateDirectory(path));

    public Task MoveAsync(string from, string to) => RunAsync(() => _session.MoveFile(from, to));

    public Task DeleteAsync(string path) => RunAsync(() =>
    {
        var mask = RemotePath.Combine(Parent(path), RemotePath.EscapeFileMask(RemotePath.GetFileName(path)));
        _session.RemoveFiles(mask).Check();
    });

    public Task<bool> ExistsAsync(string path) => RunAsync(() => _session.FileExists(path));

    public Task DownloadAsync(string remotePath, string localDirectory, Action<FileTransferProgressEventArgs> progress) => RunAsync(() =>
    {
        _progress = progress;
        try
        {
            var source = RemotePath.Combine(Parent(remotePath), RemotePath.EscapeFileMask(RemotePath.GetFileName(remotePath)));
            var target = localDirectory.EndsWith('\\') ? localDirectory : localDirectory + "\\";
            _session.GetFiles(source, target, false, TransferOptions()).Check();
        }
        finally
        {
            _progress = null;
        }
    });

    public Task UploadAsync(string localPath, string remoteDirectory, Action<FileTransferProgressEventArgs> progress) => RunAsync(() =>
    {
        _progress = progress;
        try
        {
            var trimmed = localPath.TrimEnd('\\');
            var source = Path.Combine(Path.GetDirectoryName(trimmed) ?? "", RemotePath.EscapeFileMask(Path.GetFileName(trimmed)));
            _session.PutFiles(source, RemotePath.AddDirectorySeparator(remoteDirectory), false, TransferOptions()).Check();
        }
        finally
        {
            _progress = null;
        }
    });

    private static TransferOptions TransferOptions() => new()
    {
        TransferMode = TransferMode.Binary,
        OverwriteMode = OverwriteMode.Overwrite,
        PreserveTimestamp = true,
    };

    public static string Parent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var trimmed = path.TrimEnd('/');
        int i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    public void Abort()
    {
        try { _session.Abort(); } catch (Exception) { /* best effort */ }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch (Exception) { /* closing a broken session */ }
    }
}
