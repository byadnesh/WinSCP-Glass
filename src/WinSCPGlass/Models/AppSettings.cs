using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinScpGlass.Models;

public sealed class AppSettings
{
    public bool DarkTheme { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }
    public bool ShowTerminal { get; set; } = true;
    public double TerminalHeight { get; set; } = 280;
    public string? LastLocalDirectory { get; set; }
    public List<SiteProfile> Sites { get; set; } = new();
    /// <summary>"host:port" → trusted SSH host key fingerprint (trust on first use).</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();

    [JsonIgnore]
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("WINSCP_GLASS_SETTINGS_DIR") is { Length: > 0 } custom
            ? custom // portable / test setups
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP Glass");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file must not prevent startup; keep a copy for the user.
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, FilePath, true);
    }

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return null;
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
