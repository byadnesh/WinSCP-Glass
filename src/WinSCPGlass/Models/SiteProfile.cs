using System.Text.Json.Serialization;

namespace WinScpGlass.Models;

public enum ProtocolKind { Sftp, Scp, Ftp, WebDav, S3 }

public enum FtpEncryption { None, ExplicitTls, ImplicitTls }

/// <summary>A saved site ("bookmark") as persisted in settings.json.</summary>
public sealed class SiteProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ProtocolKind Protocol { get; set; } = ProtocolKind.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    /// <summary>DPAPI-protected (current Windows user), base64.</summary>
    public string? EncryptedPassword { get; set; }
    public string? PrivateKeyPath { get; set; }
    public FtpEncryption FtpEncryption { get; set; }
    public bool FtpActiveMode { get; set; }
    public bool UseTls { get; set; } = true;
    public bool AcceptAnyTlsCertificate { get; set; }
    public string? RemoteDirectory { get; set; }
    public string? LocalDirectory { get; set; }
    public DateTime? LastUsed { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Host) ? "New site" : Host) : Name;

    [JsonIgnore]
    public string Subtitle => string.IsNullOrWhiteSpace(User) ? Host : $"{User}@{Host}";

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var words = DisplayName.Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
            return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        }
    }

    [JsonIgnore]
    public string ProtocolLabel => LabelFor(Protocol);

    /// <summary>Stable hue per site so every site gets its own coloured orb.</summary>
    [JsonIgnore]
    public int Hue
    {
        get
        {
            int hash = 17;
            foreach (char c in Id) hash = unchecked(hash * 31 + c);
            return Math.Abs(hash % 360);
        }
    }

    public static string LabelFor(ProtocolKind protocol) => protocol switch
    {
        ProtocolKind.Sftp => "SFTP",
        ProtocolKind.Scp => "SCP",
        ProtocolKind.Ftp => "FTP",
        ProtocolKind.WebDav => "WebDAV",
        ProtocolKind.S3 => "S3",
        _ => protocol.ToString()
    };

    public static int DefaultPort(ProtocolKind protocol, FtpEncryption ftp, bool tls) => protocol switch
    {
        ProtocolKind.Sftp or ProtocolKind.Scp => 22,
        ProtocolKind.Ftp => ftp == FtpEncryption.ImplicitTls ? 990 : 21,
        ProtocolKind.WebDav => tls ? 443 : 80,
        ProtocolKind.S3 => 443,
        _ => 0
    };

    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : DefaultPort(Protocol, FtpEncryption, UseTls);

    [JsonIgnore]
    public bool IsSsh => Protocol is ProtocolKind.Sftp or ProtocolKind.Scp;

    [JsonIgnore]
    public string HostKeyId => $"{Host.Trim().ToLowerInvariant()}:{EffectivePort}";

    public SiteProfile Clone() => (SiteProfile)MemberwiseClone();
}
