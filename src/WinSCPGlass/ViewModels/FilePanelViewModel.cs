using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinScpGlass.Models;
using WinScpGlass.Services;

namespace WinScpGlass.ViewModels;

public sealed record Crumb(string Label, string Path, bool IsRoot);

/// <summary>ObservableCollection that can swap its whole content with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Shared behaviour of the local and remote file panels.</summary>
public abstract partial class FilePanelViewModel : ObservableObject
{
    private readonly List<FileEntry> _all = new();
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private bool _loadedOnce;

    protected SessionViewModel Session { get; }
    public SessionViewModel Owner => Session;

    public BulkObservableCollection<FileEntry> Items { get; } = new();
    public ObservableCollection<Crumb> Crumbs { get; } = new();
    public IReadOnlyList<FileEntry> AllItems => _all;
    public IReadOnlyList<FileEntry> Selected { get; private set; } = Array.Empty<FileEntry>();

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private bool _isEditingPath;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isEmpty;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public abstract string Glyph { get; }
    public abstract string DetailHeader { get; }
    public abstract bool IsRemote { get; }

    /// <summary>Raised after a reload so the view can reapply its sort and selection.</summary>
    public event Action<string?>? Reloaded;

    protected FilePanelViewModel(SessionViewModel session)
    {
        Session = session;
    }

    protected abstract Task<List<FileEntry>> LoadAsync(string path);
    protected abstract string? ParentOf(string path);
    protected abstract IEnumerable<Crumb> CrumbsFor(string path);
    public abstract string Combine(string directory, string name);
    public abstract bool CanModifyHere { get; }
    public abstract Task CreateFolderAsync(string name);
    public abstract Task RenameAsync(FileEntry entry, string newName);
    public abstract Task DeleteAsync(IReadOnlyList<FileEntry> entries);
    protected abstract Task OpenFileAsync(FileEntry entry);

    protected virtual void Decorate(FileEntry entry)
    {
        entry.Icon ??= entry.IsDirectory ? ShellIcons.Folder : ShellIcons.ForExtension(Path.GetExtension(entry.Name));
    }

    public bool CanGoUp => ParentOf(CurrentPath) != null;
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public void Activate() => Session.ActivePanel = this;

    public async Task NavigateAsync(string path, bool record = true, string? selectName = null)
    {
        IsBusy = true;
        try
        {
            var entries = await LoadAsync(path);
            foreach (var entry in entries) Decorate(entry);

            if (record && _loadedOnce && !string.Equals(path, CurrentPath, StringComparison.Ordinal))
            {
                _back.Push(CurrentPath);
                _forward.Clear();
            }
            _loadedOnce = true;
            if (!string.Equals(path, CurrentPath, StringComparison.Ordinal)) FilterText = "";

            CurrentPath = path;
            _all.Clear();
            _all.AddRange(entries);
            ApplyFilter();

            Crumbs.Clear();
            foreach (var crumb in CrumbsFor(path)) Crumbs.Add(crumb);
            IsEditingPath = false;

            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(CanModifyHere));
            Reloaded?.Invoke(selectName);
        }
        catch (ConnectionLostException ex)
        {
            await Session.HandleConnectionLostAsync(ex);
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync("Can't open this folder", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        Items.ReplaceAll(filter.Length == 0
            ? _all
            : _all.Where(e => e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
        IsEmpty = Items.Count == 0;
        UpdateSelection(Array.Empty<FileEntry>());
    }

    public void UpdateSelection(IReadOnlyList<FileEntry> selected)
    {
        Selected = selected;
        int folders = _all.Count(e => e.IsDirectory);
        int files = _all.Count - folders;
        if (selected.Count == 0)
        {
            StatusText = $"{Plural(folders, "folder")}, {Plural(files, "file")}";
        }
        else
        {
            long bytes = selected.Where(e => !e.IsDirectory).Sum(e => e.Size);
            StatusText = $"{selected.Count} of {Items.Count} selected" + (bytes > 0 ? $" · {Format.Bytes(bytes)}" : "");
        }
        Session.NotifySelectionChanged();
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public Task RefreshAsync(string? selectName = null) => NavigateAsync(CurrentPath, record: false, selectName);

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private Task GoUp()
    {
        var parent = ParentOf(CurrentPath);
        if (parent == null) return Task.CompletedTask;
        // Coming back up, keep the folder we left selected.
        string leaving = IsRemote ? RemotePathName(CurrentPath) : LocalPathName(CurrentPath);
        return NavigateAsync(parent, selectName: leaving);
    }

    [RelayCommand]
    private async Task GoBack()
    {
        if (_back.Count == 0) return;
        string target = _back.Pop();
        _forward.Push(CurrentPath);
        await NavigateAsync(target, record: false);
    }

    [RelayCommand]
    private async Task GoForward()
    {
        if (_forward.Count == 0) return;
        string target = _forward.Pop();
        _back.Push(CurrentPath);
        await NavigateAsync(target, record: false);
    }

    [RelayCommand]
    private Task NavigateCrumb(Crumb? crumb) => crumb == null ? Task.CompletedTask : NavigateAsync(crumb.Path);

    [RelayCommand]
    private void BeginEditPath()
    {
        PathText = CurrentPath;
        IsEditingPath = true;
    }

    [RelayCommand]
    private Task CommitPath()
    {
        var target = PathText.Trim();
        IsEditingPath = false;
        if (target.Length == 0 || target == CurrentPath) return Task.CompletedTask;
        return NavigateAsync(target);
    }

    [RelayCommand]
    private void CancelEditPath() => IsEditingPath = false;

    [RelayCommand]
    public Task Open(FileEntry? entry)
    {
        if (entry == null) return Task.CompletedTask;
        return entry.IsDirectory ? NavigateAsync(entry.FullPath) : OpenFileAsync(entry);
    }

    private static string RemotePathName(string path) => path.TrimEnd('/').Split('/').LastOrDefault() ?? "";

    private static string LocalPathName(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
