using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinScpGlass.Models;
using WinScpGlass.Services;

namespace WinScpGlass.ViewModels;

/// <summary>A connected session: local panel, remote panel and the transfer queue.</summary>
public sealed partial class SessionViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly List<RemoteEditWatch> _edits = new();
    private bool _reconnecting;
    private bool _disposed;

    public RemoteConnection Connection { get; }
    public LocalPanelViewModel Local { get; }
    public RemotePanelViewModel Remote { get; }
    public TransferQueueViewModel Queue { get; }

    [ObservableProperty] private FilePanelViewModel _activePanel;
    [ObservableProperty] private bool _isReconnecting;

    public SiteProfile Site => Connection.Site;
    public string ChipText => $"{Site.Subtitle}";
    public string ProtocolLabel => Site.ProtocolLabel;

    public bool ShowHiddenFiles
    {
        get => _main.Settings.ShowHiddenFiles;
        set
        {
            if (_main.Settings.ShowHiddenFiles == value) return;
            _main.Settings.ShowHiddenFiles = value;
            _main.SaveSettings();
            OnPropertyChanged();
            _ = Local.RefreshAsync();
        }
    }

    public SessionViewModel(MainViewModel main, RemoteConnection connection)
    {
        _main = main;
        Connection = connection;
        Local = new LocalPanelViewModel(this, main.Settings);
        Remote = new RemotePanelViewModel(this, connection);
        Queue = new TransferQueueViewModel(this);
        _activePanel = Remote;
        Remote.IsActive = true;
    }

    public async Task InitializeAsync()
    {
        string local = Site.LocalDirectory ?? _main.Settings.LastLocalDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await Local.NavigateAsync(local);
        await Remote.NavigateAsync(Site.RemoteDirectory ?? Connection.HomePath);
    }

    partial void OnActivePanelChanged(FilePanelViewModel? oldValue, FilePanelViewModel newValue)
    {
        if (oldValue != null) oldValue.IsActive = false;
        newValue.IsActive = true;
        NotifySelectionChanged();
    }

    public FilePanelViewModel Other(FilePanelViewModel panel) => panel == Local ? Remote : Local;

    public void NotifySelectionChanged()
    {
        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
    }

    private bool CanUpload() => Local.Selected.Any(e => !e.IsDrive);
    private bool CanDownload() => Remote.Selected.Count > 0;
    private bool CanRename() => ActivePanel.Selected.Count == 1 && !ActivePanel.Selected[0].IsDrive;
    private bool CanDelete() => ActivePanel.Selected.Count > 0 && ActivePanel.Selected.All(e => !e.IsDrive);
    private bool CanCreateFolder() => ActivePanel.CanModifyHere;

    // ------------------------------------------------------------------ transfers

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private Task Upload() => EnqueueAsync(Local.Selected, Local, Remote.CurrentPath);

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task Download() => EnqueueAsync(Remote.Selected, Remote, Local.CurrentPath);

    /// <summary>Copy the active panel's selection to the other side.</summary>
    [RelayCommand]
    private Task TransferSelection() => ActivePanel == Local ? Upload() : Download();

    /// <summary>Items dragged from one panel onto the other.</summary>
    public Task DropAsync(FilePanelViewModel source, IReadOnlyList<FileEntry> items, FilePanelViewModel target) =>
        source == target ? Task.CompletedTask : EnqueueAsync(items, source, target.CurrentPath);

    /// <summary>Files dragged in from Windows Explorer onto the remote panel.</summary>
    public Task DropExternalAsync(IEnumerable<string> paths)
    {
        var entries = paths.Select(p => new FileEntry
        {
            Name = Path.GetFileName(p.TrimEnd('\\')),
            FullPath = p,
            IsDirectory = Directory.Exists(p),
        }).ToList();
        return EnqueueAsync(entries, Local, Remote.CurrentPath);
    }

    private async Task EnqueueAsync(IReadOnlyList<FileEntry> items, FilePanelViewModel source, string targetDirectory)
    {
        var list = items.Where(e => !e.IsDrive).ToList();
        if (list.Count == 0) return;
        if (source == Remote && targetDirectory.Length == 0)
        {
            _main.Notify("Open a folder on this PC first, then download into it.", Tone.Warning);
            return;
        }

        // Conflicts are only knowable for the folder the target panel is showing.
        var target = Other(source);
        if (string.Equals(target.CurrentPath, targetDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var existing = new HashSet<string>(target.AllItems.Select(e => e.Name),
                target.IsRemote ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            var clashes = list.Where(e => existing.Contains(e.Name)).ToList();
            if (clashes.Count > 0)
            {
                var choice = await Dialogs.ShowAsync(new DialogRequest
                {
                    Title = clashes.Count == 1 ? $"Replace “{clashes[0].Name}”?" : $"Replace {clashes.Count} items?",
                    Message = clashes.Count == 1
                        ? "An item with the same name already exists in the destination."
                        : "Some items already exist in the destination. Replace them, or skip those and copy the rest.",
                    Glyph = "",
                    Tone = Tone.Warning,
                    Buttons = list.Count > clashes.Count
                        ? new[]
                        {
                            new DialogButton("cancel", "Cancel", IsCancel: true),
                            new DialogButton("skip", "Skip existing"),
                            new DialogButton("replace", "Replace", IsPrimary: true),
                        }
                        : new[]
                        {
                            new DialogButton("cancel", "Cancel", IsCancel: true),
                            new DialogButton("replace", "Replace", IsPrimary: true),
                        }
                });
                if (choice == "skip") list = list.Except(clashes).ToList();
                else if (choice != "replace") return;
            }
        }

        var direction = source == Local ? TransferDirection.Upload : TransferDirection.Download;
        foreach (var entry in list)
        {
            Queue.Add(new TransferJob
            {
                Direction = direction,
                Name = entry.Name,
                SourcePath = entry.FullPath,
                TargetDirectory = targetDirectory,
                IsDirectory = entry.IsDirectory,
            });
        }
    }

    private readonly Dictionary<FilePanelViewModel, DispatcherTimer> _refreshTimers = new();

    public void OnTransferCompleted(TransferJob job)
    {
        var panel = job.Direction == TransferDirection.Upload ? (FilePanelViewModel)Remote : Local;
        bool showing = string.Equals(panel.CurrentPath.TrimEnd('/', '\\'), job.TargetDirectory.TrimEnd('/', '\\'),
            panel.IsRemote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        if (!showing) return;

        // Coalesce refreshes when many small files finish back to back.
        if (!_refreshTimers.TryGetValue(panel, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await panel.RefreshAsync();
            };
            _refreshTimers[panel] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    // ------------------------------------------------------------------ file operations

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private async Task NewFolder()
    {
        var panel = ActivePanel;
        var name = await Dialogs.PromptAsync("New folder",
            panel.IsRemote ? $"Create a folder in {panel.CurrentPath} on the server." : $"Create a folder in {panel.CurrentPath}.",
            "Create", "New folder", "Folder name", glyph: "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(panel, () => panel.CreateFolderAsync(name.Trim()), name.Trim());
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task Rename()
    {
        var panel = ActivePanel;
        var entry = panel.Selected[0];
        var name = await Dialogs.PromptAsync("Rename", $"Enter a new name for “{entry.Name}”.", "Rename", entry.Name, glyph: "");
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        await RunAsync(panel, () => panel.RenameAsync(entry, name.Trim()), name.Trim());
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        var panel = ActivePanel;
        var items = panel.Selected.ToList();
        string what = items.Count == 1 ? $"“{items[0].Name}”" : $"{items.Count} items";
        string message = panel.IsRemote
            ? $"{what} will be permanently deleted from the server. This can't be undone."
            : $"{what} will be moved to the Recycle Bin.";
        if (!await Dialogs.ConfirmAsync($"Delete {what}?", message, "Delete", danger: true, glyph: "")) return;
        await RunAsync(panel, () => panel.DeleteAsync(items), null);
        _main.Notify(items.Count == 1 ? $"Deleted {items[0].Name}" : $"Deleted {items.Count} items", Tone.Success);
    }

    [RelayCommand]
    private Task Refresh() => ActivePanel.RefreshAsync();

    private async Task RunAsync(FilePanelViewModel panel, Func<Task> operation, string? selectAfter)
    {
        panel.IsBusy = true;
        try
        {
            await operation();
        }
        catch (ConnectionLostException ex)
        {
            await HandleConnectionLostAsync(ex);
            return;
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync("Something went wrong", ex.Message);
        }
        finally
        {
            panel.IsBusy = false;
        }
        await panel.RefreshAsync(selectAfter);
    }

    // ------------------------------------------------------------------ open / edit remote files

    public async Task OpenRemoteFileAsync(FileEntry entry)
    {
        string folder = Path.Combine(Path.GetTempPath(), "WinSCP Glass", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string remoteDirectory = RemoteConnection.Parent(entry.FullPath);
        string localFile = Path.Combine(folder, entry.Name);

        Queue.Add(new TransferJob
        {
            Direction = TransferDirection.Download,
            Name = entry.Name,
            SourcePath = entry.FullPath,
            TargetDirectory = folder,
            AfterComplete = () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(localFile) { UseShellExecute = true });
                    _edits.Add(new RemoteEditWatch(localFile, remoteDirectory, this));
                    _main.Notify($"Editing {entry.Name} — saving it uploads the changes", Tone.Info);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
                {
                    _main.Notify($"No app is registered to open {entry.Name}", Tone.Warning);
                }
                return Task.CompletedTask;
            }
        });
        await Task.CompletedTask;
    }

    internal void UploadEditedFile(string localFile, string remoteDirectory)
    {
        if (_disposed) return;
        Queue.Add(new TransferJob
        {
            Direction = TransferDirection.Upload,
            Name = Path.GetFileName(localFile),
            SourcePath = localFile,
            TargetDirectory = remoteDirectory,
        });
    }

    // ------------------------------------------------------------------ connection

    public async Task HandleConnectionLostAsync(ConnectionLostException ex)
    {
        if (_reconnecting || _disposed) return;
        _reconnecting = true;
        try
        {
            while (true)
            {
                var choice = await Dialogs.ShowAsync(new DialogRequest
                {
                    Title = "Connection lost",
                    Message = $"The connection to {Site.Host} was interrupted.",
                    Detail = ex.Message,
                    Glyph = "",
                    Tone = Tone.Warning,
                    Buttons = new[]
                    {
                        new DialogButton("close", "Disconnect", IsCancel: true),
                        new DialogButton("retry", "Reconnect", IsPrimary: true),
                    }
                });
                if (choice != "retry")
                {
                    Close();
                    return;
                }
                IsReconnecting = true;
                try
                {
                    await Connection.ReconnectAsync();
                    _main.Notify("Reconnected", Tone.Success);
                    break;
                }
                catch (Exception retryError)
                {
                    ex = new ConnectionLostException(retryError.Message, retryError);
                }
                finally
                {
                    IsReconnecting = false;
                }
            }
        }
        finally
        {
            _reconnecting = false;
        }
        await Remote.RefreshAsync();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        if (Queue.HasActiveJobs && !await Dialogs.ConfirmAsync("Disconnect?",
                "Transfers are still running. Disconnecting will stop them.", "Disconnect", danger: true, glyph: ""))
        {
            return;
        }
        Queue.CancelAllCommand.Execute(null);
        Close();
    }

    private void Close()
    {
        if (Local.CurrentPath.Length > 0) _main.Settings.LastLocalDirectory = Local.CurrentPath;
        _main.SaveSettings();
        Dispose();
        _main.ShowConnect();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var edit in _edits) edit.Dispose();
        _edits.Clear();
        foreach (var timer in _refreshTimers.Values) timer.Stop();
        Connection.Abort();
        Connection.Dispose();
    }
}

/// <summary>Watches a downloaded copy of a remote file and uploads it whenever it is saved.</summary>
internal sealed class RemoteEditWatch : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;

    public RemoteEditWatch(string localFile, string remoteDirectory, SessionViewModel session)
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            session.UploadEditedFile(localFile, remoteDirectory);
        };

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(localFile)!, Path.GetFileName(localFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        var dispatcher = Dispatcher.CurrentDispatcher;
        FileSystemEventHandler restart = (_, _) => dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
        _watcher.Changed += restart;
        // Many editors save by writing a temp file and renaming it over the original.
        _watcher.Renamed += (s, e) => restart(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _watcher.Dispose();
    }
}
