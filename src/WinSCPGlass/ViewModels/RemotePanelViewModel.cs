using WinScpGlass.Models;
using WinScpGlass.Services;
using WinSCP;

namespace WinScpGlass.ViewModels;

/// <summary>The server side, backed by a WinSCP session.</summary>
public sealed class RemotePanelViewModel : FilePanelViewModel
{
    private readonly RemoteConnection _connection;

    public RemotePanelViewModel(SessionViewModel session, RemoteConnection connection) : base(session)
    {
        _connection = connection;
    }

    public override string Title => _connection.Site.DisplayName;
    public override string Subtitle => $"{_connection.Site.ProtocolLabel} · {_connection.Site.Subtitle}";
    public override string Glyph => "";
    public override string DetailHeader => "Permissions";
    public override bool IsRemote => true;
    public override bool CanModifyHere => true;

    protected override Task<List<FileEntry>> LoadAsync(string path) => _connection.ListAsync(path);

    protected override string? ParentOf(string path) =>
        string.IsNullOrEmpty(path) || path == "/" ? null : RemoteConnection.Parent(path);

    protected override IEnumerable<Crumb> CrumbsFor(string path)
    {
        yield return new Crumb(_connection.Site.Host, "/", true);
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            yield return new Crumb(part, current, false);
        }
    }

    public override string Combine(string directory, string name) => RemotePath.Combine(directory, name);

    public override async Task CreateFolderAsync(string name)
    {
        var target = Combine(CurrentPath, name);
        if (await _connection.ExistsAsync(target)) throw new InvalidOperationException($"“{name}” already exists.");
        await _connection.CreateDirectoryAsync(target);
    }

    public override Task RenameAsync(FileEntry entry, string newName) =>
        _connection.MoveAsync(entry.FullPath, Combine(RemoteConnection.Parent(entry.FullPath), newName));

    public override async Task DeleteAsync(IReadOnlyList<FileEntry> entries)
    {
        foreach (var entry in entries) await _connection.DeleteAsync(entry.FullPath);
    }

    protected override Task OpenFileAsync(FileEntry entry) => Session.OpenRemoteFileAsync(entry);
}
