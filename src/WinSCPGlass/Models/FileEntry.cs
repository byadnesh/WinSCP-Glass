using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WinScpGlass.Models;

/// <summary>One row in a file panel (local or remote).</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    /// <summary>File type (local) or permissions (remote).</summary>
    public string Detail { get; set; } = "";
    public string? SizeOverride { get; init; }
    // Assigned on the UI thread: shell icon lookup is not safe on worker threads.
    public ImageSource? Icon { get; set; }

    public string SizeText => SizeOverride ?? (IsDirectory ? "" : Format.Bytes(Size));
    public string ModifiedText => Modified is { } m ? Format.Date(m) : "";
}

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }

    public static string Speed(int bytesPerSecond) => bytesPerSecond <= 0 ? "" : $"{Bytes(bytesPerSecond)}/s";

    public static string Date(DateTime value)
    {
        var today = DateTime.Today;
        if (value.Date == today) return $"Today, {value:t}";
        if (value.Date == today.AddDays(-1)) return $"Yesterday, {value:t}";
        return value.Year == today.Year ? value.ToString("d MMM, HH:mm") : value.ToString("d MMM yyyy");
    }
}

/// <summary>Folders first, then natural ("file2" before "file10") ordering on the chosen column.</summary>
public sealed class FileEntryComparer : System.Collections.IComparer
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    private readonly string _key;
    private readonly int _sign;

    public FileEntryComparer(string key, ListSortDirection direction)
    {
        _key = key;
        _sign = direction == ListSortDirection.Ascending ? 1 : -1;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not FileEntry a || y is not FileEntry b) return 0;
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        int result = _key switch
        {
            "Size" => a.Size.CompareTo(b.Size),
            "Modified" => Nullable.Compare(a.Modified, b.Modified),
            "Detail" => string.Compare(a.Detail, b.Detail, StringComparison.CurrentCultureIgnoreCase),
            _ => 0
        };
        if (result == 0) result = StrCmpLogicalW(a.Name, b.Name);
        return result * _sign;
    }
}
