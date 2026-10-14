using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinScpGlass.Models;
using WinScpGlass.Services;

namespace WinScpGlass.ViewModels;

/// <summary>This computer. An empty path means the drive list ("This PC").</summary>
public sealed class LocalPanelViewModel : FilePanelViewModel
{
    private readonly AppSettings _settings;

    public LocalPanelViewModel(SessionViewModel session, AppSettings settings) : base(session)
    {
        _settings = settings;
    }

    public override string Title => "This PC";
    public override string Subtitle => "Local files";
    public override string Glyph => "";
    public override string DetailHeader => "Type";
    public override bool IsRemote => false;
    public override bool CanModifyHere => CurrentPath.Length > 0;

    protected override Task<List<FileEntry>> LoadAsync(string path) => Task.Run(() =>
    {
        var result = new List<FileEntry>();
        if (path.Length == 0)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string letter = drive.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? (drive.DriveType == DriveType.Network ? "Network drive" : "Local disk")
                    : drive.VolumeLabel;
                result.Add(new FileEntry
                {
                    Name = $"{label} ({letter})",
                    FullPath = drive.RootDirectory.FullName,
                    IsDirectory = true,
                    IsDrive = true,
                    Size = drive.TotalFreeSpace,
                    SizeOverride = $"{Format.Bytes(drive.TotalFreeSpace)} free",
                    Detail = drive.DriveType == DriveType.Network ? "Network drive" : drive.DriveFormat,
                });
            }
            return result;
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new DirectoryNotFoundException($"“{path}” does not exist.");

        foreach (var info in directory.EnumerateFileSystemInfos())
        {
            if (!_settings.ShowHiddenFiles && ShellIcons.IsHiddenOrSystem(info)) continue;
            bool isDir = info is DirectoryInfo;
            result.Add(new FileEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = isDir,
                Size = info is FileInfo f ? f.Length : 0,
                Modified = info.LastWriteTime,
            });
        }
        return result;
    });

    protected override void Decorate(FileEntry entry)
    {
        if (entry.IsDrive)
        {
            entry.Icon = ShellIcons.ForDrive(entry.FullPath);
            return;
        }
        base.Decorate(entry);
        entry.Detail = entry.IsDirectory ? "File folder" : ShellIcons.TypeName(Path.GetExtension(entry.Name));
    }

    protected override string? ParentOf(string path)
    {
        if (path.Length == 0) return null;
        var root = Path.GetPathRoot(path);
        if (string.Equals(root?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "";
        return Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
    }

    protected override IEnumerable<Crumb> CrumbsFor(string path)
    {
        yield return new Crumb("This PC", "", true);
        if (path.Length == 0) yield break;

        var root = Path.GetPathRoot(path) ?? path;
        yield return new Crumb(root.TrimEnd('\\'), root, false);
        var current = root;
        foreach (var part in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            yield return new Crumb(part, current, false);
        }
    }

    public override string Combine(string directory, string name) => Path.Combine(directory, name);

    public override Task CreateFolderAsync(string name) => Task.Run(() =>
    {
        var target = Path.Combine(CurrentPath, name);
        if (Path.Exists(target)) throw new IOException($"“{name}” already exists.");
        Directory.CreateDirectory(target);
    });

    public override Task RenameAsync(FileEntry entry, string newName) => Task.Run(() =>
    {
        var target = Path.Combine(Path.GetDirectoryName(entry.FullPath.TrimEnd('\\'))!, newName);
        if (entry.IsDirectory) Directory.Move(entry.FullPath, target);
        else File.Move(entry.FullPath, target);
    });

    public override Task DeleteAsync(IReadOnlyList<FileEntry> entries) => Task.Run(() =>
        RecycleBin.Send(entries.Where(e => !e.IsDrive).Select(e => e.FullPath)));

    protected override Task OpenFileAsync(FileEntry entry)
    {
        Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

/// <summary>Deletes by moving to the Recycle Bin, like Explorer does.</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    private const uint FO_DELETE = 0x3;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOERRORUI = 0x400;

    public static void Send(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', list) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int result = SHFileOperation(ref operation);
        if (result != 0) throw new IOException($"Windows could not delete the selection (error 0x{result:X}).");
    }
}
