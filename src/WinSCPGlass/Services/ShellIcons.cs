using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinScpGlass.Services;

/// <summary>Native Windows file-type icons and type names, cached per extension.</summary>
public static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Folder => IconCache.GetOrAdd("<folder>", _ => Load("folder", FILE_ATTRIBUTE_DIRECTORY, true));

    public static ImageSource? ForExtension(string? extension)
    {
        var ext = string.IsNullOrEmpty(extension) ? ".__none" : extension;
        return IconCache.GetOrAdd(ext, e => Load("file" + e, FILE_ATTRIBUTE_NORMAL, true));
    }

    public static ImageSource? ForDrive(string root) =>
        IconCache.GetOrAdd("<drive>" + root, _ => Load(root, 0, false));

    public static string TypeName(string? extension)
    {
        var ext = string.IsNullOrEmpty(extension) ? ".__none" : extension;
        return TypeCache.GetOrAdd(ext, e =>
        {
            var info = new SHFILEINFO();
            SHGetFileInfo("file" + e, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return string.IsNullOrEmpty(info.szTypeName) ? "File" : info.szTypeName;
        });
    }

    private static ImageSource? Load(string path, uint attributes, bool byAttributes)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | (byAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
        SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public static bool IsHiddenOrSystem(FileSystemInfo info) =>
        (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}
