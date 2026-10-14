using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WinScpGlass.Models;
using WinScpGlass.Services;

namespace WinScpGlass.ViewModels;

/// <summary>Start screen: saved sites on the left, connection form on the right.</summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _connectCancel;

    public ObservableCollection<SiteProfile> Sites { get; }
    public ICollectionView SitesView { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private SiteProfile? _selectedSite;

    // ---- form
    [ObservableProperty] private string? _editingId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserLabel), nameof(PasswordLabel), nameof(HostPlaceholder), nameof(PortPlaceholder),
        nameof(IsSsh), nameof(IsFtp), nameof(IsWebDav), nameof(IsTls), nameof(UserPlaceholder))]
    private ProtocolKind _protocol = ProtocolKind.Sftp;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordLabel))]
    private string _privateKeyPath = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortPlaceholder), nameof(IsTls))]
    private FtpEncryption _ftpEncryption = FtpEncryption.None;
    [ObservableProperty] private bool _ftpActiveMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortPlaceholder), nameof(IsTls))]
    private bool _useTls = true;
    [ObservableProperty] private bool _acceptAnyTlsCertificate;
    [ObservableProperty] private string _remoteDirectory = "";
    [ObservableProperty] private string _localDirectory = "";
    [ObservableProperty] private bool _savePassword = true;
    [ObservableProperty] private bool _showAdvanced;

    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectingText = "";

    public bool HasSites => Sites.Count > 0;
    public bool IsSavedSite => EditingId != null;
    public string FormTitle => EditingId == null ? "New connection" : (string.IsNullOrWhiteSpace(Name) ? Host : Name);

    public bool IsSsh => Protocol is ProtocolKind.Sftp or ProtocolKind.Scp;
    public bool IsFtp => Protocol == ProtocolKind.Ftp;
    public bool IsWebDav => Protocol == ProtocolKind.WebDav;
    public bool IsTls => Protocol == ProtocolKind.S3
        || (Protocol == ProtocolKind.WebDav && UseTls)
        || (Protocol == ProtocolKind.Ftp && FtpEncryption != FtpEncryption.None);

    public string UserLabel => Protocol == ProtocolKind.S3 ? "Access key ID" : "Username";
    public string UserPlaceholder => Protocol == ProtocolKind.S3 ? "AKIA…" : "root";
    public string PasswordLabel => Protocol == ProtocolKind.S3 ? "Secret access key"
        : IsSsh && !string.IsNullOrWhiteSpace(PrivateKeyPath) ? "Key passphrase" : "Password";
    public string HostPlaceholder => Protocol == ProtocolKind.S3 ? "s3.amazonaws.com" : "example.com or 192.168.1.10";
    public string PortPlaceholder => SiteProfile.DefaultPort(Protocol, FtpEncryption, UseTls).ToString();

    public ConnectViewModel(MainViewModel main)
    {
        _main = main;
        Sites = new ObservableCollection<SiteProfile>(
            main.Settings.Sites.OrderByDescending(s => s.LastUsed ?? DateTime.MinValue).ThenBy(s => s.DisplayName));
        Sites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSites));
        SitesView = CollectionViewSource.GetDefaultView(Sites);
        SitesView.Filter = o => o is SiteProfile s && (string.IsNullOrWhiteSpace(SearchText)
            || s.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || s.Subtitle.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }

    partial void OnSearchTextChanged(string value) => SitesView.Refresh();

    partial void OnSelectedSiteChanged(SiteProfile? value)
    {
        if (value != null) LoadForm(value);
    }

    partial void OnEditingIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsSavedSite));
        OnPropertyChanged(nameof(FormTitle));
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(FormTitle));
    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(FormTitle));

    private void LoadForm(SiteProfile site)
    {
        EditingId = site.Id;
        Name = site.Name;
        Protocol = site.Protocol;
        Host = site.Host;
        PortText = site.Port > 0 ? site.Port.ToString() : "";
        User = site.User;
        Password = AppSettings.Unprotect(site.EncryptedPassword) ?? "";
        SavePassword = site.EncryptedPassword != null || string.IsNullOrEmpty(Password);
        PrivateKeyPath = site.PrivateKeyPath ?? "";
        FtpEncryption = site.FtpEncryption;
        FtpActiveMode = site.FtpActiveMode;
        UseTls = site.UseTls;
        AcceptAnyTlsCertificate = site.AcceptAnyTlsCertificate;
        RemoteDirectory = site.RemoteDirectory ?? "";
        LocalDirectory = site.LocalDirectory ?? "";
    }

    /// <summary>Accepts what people type for ssh: "user@host", "host:port", "sftp://user@host:port/".</summary>
    private void NormalizeHostField()
    {
        var text = Host.Trim();
        int scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        text = text.TrimEnd('/');
        int slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];

        int at = text.LastIndexOf('@');
        if (at > 0)
        {
            if (string.IsNullOrWhiteSpace(User)) User = text[..at];
            text = text[(at + 1)..];
        }

        // host:port, but leave bare IPv6 addresses alone
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port) && port is > 0 and < 65536)
        {
            PortText = port.ToString();
            text = text[..colon];
        }
        Host = text;
    }

    private SiteProfile BuildProfile()
    {
        NormalizeHostField();
        var existing = Sites.FirstOrDefault(s => s.Id == EditingId);
        return new SiteProfile
        {
            Id = EditingId ?? Guid.NewGuid().ToString("N"),
            Name = Name.Trim(),
            Protocol = Protocol,
            Host = string.IsNullOrWhiteSpace(Host) && Protocol == ProtocolKind.S3 ? "s3.amazonaws.com" : Host.Trim(),
            Port = int.TryParse(PortText, out int port) && port is > 0 and < 65536 ? port : 0,
            User = User.Trim(),
            EncryptedPassword = SavePassword ? AppSettings.Protect(Password) : null,
            PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath.Trim(),
            FtpEncryption = FtpEncryption,
            FtpActiveMode = FtpActiveMode,
            UseTls = UseTls,
            AcceptAnyTlsCertificate = AcceptAnyTlsCertificate,
            RemoteDirectory = string.IsNullOrWhiteSpace(RemoteDirectory) ? null : RemoteDirectory.Trim(),
            LocalDirectory = string.IsNullOrWhiteSpace(LocalDirectory) ? null : LocalDirectory.Trim(),
            LastUsed = existing?.LastUsed,
        };
    }

    [RelayCommand]
    private void NewSite()
    {
        SelectedSite = null;
        EditingId = null;
        Name = Host = PortText = User = Password = PrivateKeyPath = RemoteDirectory = LocalDirectory = "";
        Protocol = ProtocolKind.Sftp;
        FtpEncryption = FtpEncryption.None;
        FtpActiveMode = false;
        UseTls = true;
        AcceptAnyTlsCertificate = false;
        SavePassword = true;
    }

    [RelayCommand]
    private void SaveSite()
    {
        if (string.IsNullOrWhiteSpace(Host) && Protocol != ProtocolKind.S3)
        {
            _main.Notify("Enter a host name before saving.", Tone.Warning);
            return;
        }
        var profile = BuildProfile();
        Upsert(profile);
        _main.Notify($"Saved “{profile.DisplayName}”", Tone.Success);
    }

    private void Upsert(SiteProfile profile)
    {
        var settings = _main.Settings;
        int index = settings.Sites.FindIndex(s => s.Id == profile.Id);
        if (index >= 0) settings.Sites[index] = profile; else settings.Sites.Add(profile);

        int viewIndex = Sites.IndexOf(Sites.FirstOrDefault(s => s.Id == profile.Id)!);
        if (viewIndex >= 0) Sites[viewIndex] = profile; else Sites.Insert(0, profile);

        _main.SaveSettings();
        EditingId = profile.Id;

        // Selecting reloads the form from the stored copy; keep a password that was typed but not saved.
        string typedPassword = Password;
        bool savePassword = SavePassword;
        SelectedSite = profile;
        Password = typedPassword;
        SavePassword = savePassword;
    }

    [RelayCommand]
    private async Task DeleteSiteAsync()
    {
        var site = Sites.FirstOrDefault(s => s.Id == EditingId);
        if (site == null) return;
        if (!await Dialogs.ConfirmAsync("Delete site?", $"“{site.DisplayName}” will be removed from your saved sites. Files on the server are not affected.",
                "Delete", danger: true, glyph: ""))
        {
            return;
        }
        Sites.Remove(site);
        _main.Settings.Sites.RemoveAll(s => s.Id == site.Id);
        _main.SaveSettings();
        NewSite();
    }

    [RelayCommand]
    private void BrowsePrivateKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a private key",
            Filter = "Private keys (*.ppk;*.pem;*.key;id_*)|*.ppk;*.pem;*.key;id_*|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true) PrivateKeyPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseLocalDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the local start folder" };
        if (dialog.ShowDialog() == true) LocalDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private Task ConnectSiteAsync(SiteProfile? site)
    {
        if (site == null) return Task.CompletedTask;
        SelectedSite = site;
        LoadForm(site);
        return ConnectAsync();
    }

    [RelayCommand]
    private void CancelConnect()
    {
        _connectCancel?.Cancel();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting) return;

        var site = BuildProfile();
        if (string.IsNullOrWhiteSpace(site.Host))
        {
            await Dialogs.ErrorAsync("Host name needed", "Enter the address of the server you want to connect to.");
            return;
        }

        string? password = Password;
        bool usesKey = site.IsSsh && !string.IsNullOrWhiteSpace(site.PrivateKeyPath);
        if (string.IsNullOrEmpty(password) && !usesKey)
        {
            password = await Dialogs.PromptAsync(
                site.Protocol == ProtocolKind.S3 ? "Secret access key" : "Password",
                $"Enter the {(site.Protocol == ProtocolKind.S3 ? "secret key" : "password")} for {site.Subtitle}.",
                "Connect", isPassword: true, glyph: "");
            if (password == null) return;
        }

        using var cancel = new CancellationTokenSource();
        _connectCancel = cancel;
        IsConnecting = true;
        try
        {
            string? hostKey = null;
            if (site.IsSsh)
            {
                _main.Settings.KnownHosts.TryGetValue(site.HostKeyId, out hostKey);
                if (hostKey == null)
                {
                    ConnectingText = "Verifying server identity…";
                    string fingerprint = await RemoteConnection.ScanHostKeyAsync(RemoteConnection.BuildOptions(site, null, null), cancel.Token);
                    if (!await TrustHostKeyAsync(site, fingerprint)) return;
                    hostKey = fingerprint;
                }
            }

            ConnectingText = $"Connecting to {site.Host}…";
            var options = RemoteConnection.BuildOptions(site, password, hostKey);
            var connection = await RemoteConnection.OpenAsync(site, options, cancel.Token);

            if (Sites.FirstOrDefault(s => s.Id == site.Id) is { } saved)
            {
                saved.LastUsed = DateTime.Now;
                var stored = _main.Settings.Sites.FirstOrDefault(s => s.Id == site.Id);
                if (stored != null) stored.LastUsed = saved.LastUsed;
                _main.SaveSettings();
            }

            var session = new SessionViewModel(_main, connection);
            _main.ShowSession(session);
            await session.InitializeAsync();
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // User pressed Cancel.
        }
        catch (Exception ex)
        {
            await ShowConnectErrorAsync(site, ex);
        }
        finally
        {
            _connectCancel = null;
            IsConnecting = false;
        }
    }

    private async Task<bool> TrustHostKeyAsync(SiteProfile site, string fingerprint)
    {
        var choice = await Dialogs.ShowAsync(new DialogRequest
        {
            Title = "Trust this server?",
            Message = $"This is the first time you are connecting to {site.Host}. Check that this fingerprint matches the one from your server administrator.",
            Detail = fingerprint,
            Glyph = "",
            Tone = Tone.Security,
            Buttons = new[]
            {
                new DialogButton("cancel", "Cancel", IsCancel: true),
                new DialogButton("trust", "Trust & connect", IsPrimary: true),
            }
        });
        if (choice != "trust") return false;
        _main.Settings.KnownHosts[site.HostKeyId] = fingerprint;
        _main.SaveSettings();
        return true;
    }

    private async Task ShowConnectErrorAsync(SiteProfile site, Exception ex)
    {
        string message = ex.Message;
        bool hostKeyProblem = site.IsSsh && _main.Settings.KnownHosts.ContainsKey(site.HostKeyId)
            && message.Contains("host key", StringComparison.OrdinalIgnoreCase);

        if (hostKeyProblem)
        {
            var choice = await Dialogs.ShowAsync(new DialogRequest
            {
                Title = "Server identity changed",
                Message = "The server presented a different host key than the one you trusted before. This can mean the server was reinstalled — or that someone is intercepting the connection. Only forget the saved key if you are sure the change is legitimate.",
                Detail = message,
                Glyph = "",
                Tone = Tone.Warning,
                Buttons = new[]
                {
                    new DialogButton("forget", "Forget saved key", IsDanger: true),
                    new DialogButton("ok", "Close", IsPrimary: true, IsCancel: true),
                }
            });
            if (choice == "forget")
            {
                _main.Settings.KnownHosts.Remove(site.HostKeyId);
                _main.SaveSettings();
                _main.Notify("Saved host key removed. Connect again to review the new key.", Tone.Info);
            }
            return;
        }

        if (message.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.ErrorAsync("Connection refused",
                $"{site.Host} is reachable, but nothing accepted a {site.ProtocolLabel} connection on port {site.EffectivePort}. " +
                "Check that the protocol and port match the server — a server you reach with “ssh” in a terminal needs SFTP (or SCP) on port 22, unless you pass -p to ssh.",
                message);
            return;
        }

        await Dialogs.ErrorAsync("Couldn't connect", $"WinSCP could not connect to {site.Host}.", message);
    }
}
