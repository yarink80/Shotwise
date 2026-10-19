using System.Net.Security;
using System.Security.Cryptography;
using FluentFTP;
using FluentFTP.Exceptions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Infrastructure;
using Shotwise.Sharing.Naming;

namespace Shotwise.Sharing.Destinations;

public enum FtpProtocol { Ftp, FtpsExplicit, FtpsImplicit, Sftp }
public enum FtpNameMode { Automatic, Original, Fixed }
public enum FtpProxyKind { None, Http, Socks4, Socks5 }

public sealed class FtpDestinationSettings
{
    public FtpProtocol Protocol { get; set; } = FtpProtocol.FtpsExplicit;
    public string Host { get; set; } = "";
    /// <summary>0 = protocol default (21 / 990 / 22).</summary>
    public int Port { get; set; }
    public string UserName { get; set; } = "";
    public string Directory { get; set; } = "/";
    public bool Passive { get; set; } = true;
    public FtpNameMode NameMode { get; set; } = FtpNameMode.Automatic;
    public string FixedName { get; set; } = "";
    /// <summary>Optional public URL prefix; the link is this + the uploaded file name (e.g. https://example.com/shots/).</summary>
    public string PublicUrlBase { get; set; } = "";
    /// <summary>SFTP only: path to an OpenSSH/PEM private key (passphrase kept in the credential store).</summary>
    public string PrivateKeyPath { get; set; } = "";
    public FtpProxyKind ProxyKind { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUser { get; set; } = "";
    /// <summary>FTPS: accept certificates that fail validation (self-signed servers). Off by default.</summary>
    public bool AcceptInvalidCertificate { get; set; }
    /// <summary>SFTP host key SHA-256 (trust on first use; a changed key is rejected).</summary>
    public string HostFingerprintSha256 { get; set; } = "";
    public Dictionary<string, string> HostFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int EffectivePort => Port > 0 ? Port : Protocol switch { FtpProtocol.Sftp => 22, FtpProtocol.FtpsImplicit => 990, _ => 21 };
}

/// <summary>SHR-05: FTP, FTPS (FluentFTP) and SFTP (SSH.NET).</summary>
public sealed class FtpDestination : ShareDestinationBase
{
    private readonly NamingCounter _counter;
    private IShareUi? _shareUi;

    public override Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct)
    {
        _shareUi = ui;
        return Task.FromResult(true);
    }

    internal async Task<bool> TrustHostAsync(FtpDestinationSettings s, string fingerprint)
    {
        var host = $"{s.Host.ToLowerInvariant()}:{s.EffectivePort}";
        if (s.HostFingerprints.TryGetValue(host, out var known))
            return string.Equals(known, fingerprint, StringComparison.Ordinal);
        if (_shareUi is null) return false;
        var form = new ShareForm { Description = $"Unknown SSH host {host}. Verify SHA-256 fingerprint with the server administrator: SHA256:{fingerprint}", OkText = "Trust host" };
        form.Add(new ShareFormField { Key = "trust", Label = "I verified this fingerprint", Kind = ShareFieldKind.Checkbox, Value = false });
        if (!await _shareUi.ShowFormAsync("Verify SFTP host key", form).ConfigureAwait(false) || !form.Get<bool>("trust")) return false;
        s.HostFingerprints[host] = fingerprint;
        SaveSettings(s);
        return true;
    }

    public FtpDestination(ShareContext ctx) : base(ctx) => _counter = new NamingCounter(ctx.Settings);

    public override string Id => "ftp";
    public override string DisplayName => "FTP / SFTP";
    public override string IconGlyph => "";
    public override DestinationCategory Category => DestinationCategory.Storage;
    public override IReadOnlySet<string> SupportedFormats => FormatKeys.Everything;
    public override bool CanShowOptions => true;
    public override bool RequiresAccount => true;
    public override bool IsConfigured => !string.IsNullOrWhiteSpace(LoadSettings<FtpDestinationSettings>().Host);
    public override IReadOnlyList<PolicyFeature> RequiredPolicies => new[] { PolicyFeature.Ftp };

    internal static string PasswordKey(FtpDestinationSettings s) => CredentialKeys.Make("ftp", $"{s.UserName}@{s.Host}");
    internal static string PassphraseKey(FtpDestinationSettings s) => CredentialKeys.Make("ftp", $"{s.UserName}@{s.Host}:passphrase");

    public override async Task ShowOptionsAsync(IShareUi ui)
    {
        var s = LoadSettings<FtpDestinationSettings>();
        var form = new ShareForm { Description = "Passwords are stored in Windows Credential Manager, never in settings files." };
        form.Add(new ShareFormField
        {
            Key = "protocol", Label = "Protocol", Kind = ShareFieldKind.Choice, Value = s.Protocol.ToString(),
            Choices = new[] { ("Ftp", "FTP (unencrypted)"), ("FtpsExplicit", "FTPS (explicit TLS)"), ("FtpsImplicit", "FTPS (implicit TLS)"), ("Sftp", "SFTP (SSH)") },
        });
        form.Add(new ShareFormField { Key = "host", Label = "Server", Value = s.Host, Required = true });
        form.Add(new ShareFormField { Key = "port", Label = "Port (0 = default)", Kind = ShareFieldKind.Number, Value = (double)s.Port, Minimum = 0, Maximum = 65535 });
        form.Add(new ShareFormField { Key = "user", Label = "User name", Value = s.UserName });
        form.Add(new ShareFormField { Key = "password", Label = "Password (blank = keep)", Kind = ShareFieldKind.Password });
        form.Add(new ShareFormField { Key = "key", Label = "SFTP private key file", Value = s.PrivateKeyPath });
        form.Add(new ShareFormField { Key = "dir", Label = "Remote folder", Value = s.Directory });
        form.Add(new ShareFormField { Key = "passive", Label = "Passive mode", Kind = ShareFieldKind.Checkbox, Value = s.Passive });
        form.Add(new ShareFormField
        {
            Key = "name", Label = "File name", Kind = ShareFieldKind.Choice, Value = s.NameMode.ToString(),
            Choices = new[] { ("Automatic", "Automatic"), ("Original", "Original"), ("Fixed", "Fixed name") },
        });
        form.Add(new ShareFormField { Key = "fixed", Label = "Fixed name", Value = s.FixedName });
        form.Add(new ShareFormField { Key = "url", Label = "Public URL prefix", Value = s.PublicUrlBase, Placeholder = "https://example.com/shots/" });
        form.Add(new ShareFormField
        {
            Key = "proxy", Label = "SFTP proxy", Kind = ShareFieldKind.Choice, Value = s.ProxyKind.ToString(),
            Choices = new[] { ("None", "None"), ("Http", "HTTP"), ("Socks4", "SOCKS4"), ("Socks5", "SOCKS5") },
        });
        form.Add(new ShareFormField { Key = "proxyhost", Label = "Proxy host", Value = s.ProxyHost });
        form.Add(new ShareFormField { Key = "proxyport", Label = "Proxy port", Kind = ShareFieldKind.Number, Value = (double)s.ProxyPort, Minimum = 1, Maximum = 65535 });
        form.Add(new ShareFormField { Key = "badcert", Label = "Accept untrusted FTPS certificates", Kind = ShareFieldKind.Checkbox, Value = s.AcceptInvalidCertificate });
        if (!await ui.ShowFormAsync("FTP / SFTP options", form).ConfigureAwait(false)) return;

        if (Enum.TryParse<FtpProtocol>(form.Get<string>("protocol"), out var pr)) s.Protocol = pr;
        s.Host = (form.Get<string>("host") ?? "").Trim();
        s.Port = (int)(form.Get<double?>("port") ?? 0);
        s.UserName = (form.Get<string>("user") ?? "").Trim();
        s.PrivateKeyPath = (form.Get<string>("key") ?? "").Trim();
        s.Directory = string.IsNullOrWhiteSpace(form.Get<string>("dir")) ? "/" : form.Get<string>("dir")!.Trim();
        s.Passive = form.Get<bool>("passive");
        if (Enum.TryParse<FtpNameMode>(form.Get<string>("name"), out var nm)) s.NameMode = nm;
        s.FixedName = (form.Get<string>("fixed") ?? "").Trim();
        s.PublicUrlBase = (form.Get<string>("url") ?? "").Trim();
        if (Enum.TryParse<FtpProxyKind>(form.Get<string>("proxy"), out var pk)) s.ProxyKind = pk;
        s.ProxyHost = (form.Get<string>("proxyhost") ?? "").Trim();
        s.ProxyPort = (int)(form.Get<double?>("proxyport") ?? 8080);
        s.AcceptInvalidCertificate = form.Get<bool>("badcert");
        var pw = form.Get<string>("password");
        if (!string.IsNullOrEmpty(pw)) Ctx.Credentials.Write(PasswordKey(s), pw);
        SaveSettings(s);
    }

    public override Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct)
    {
        var s = LoadSettings<FtpDestinationSettings>();
        if (string.IsNullOrWhiteSpace(s.Host)) throw new ShotwiseException(ErrorCode.DestinationNotConfigured, "Configure the server in the FTP / SFTP options first.");
        var names = files.Select((f, i) => RemoteName(s, f, i, files.Count)).ToList();
        return s.Protocol == FtpProtocol.Sftp ? UploadSftpAsync(s, files, names, progress, ct) : UploadFtpAsync(s, files, names, progress, ct);
    }

    internal string RemoteName(FtpDestinationSettings s, ShareFile f, int index, int count)
    {
        var ext = Path.GetExtension(f.Path);
        switch (s.NameMode)
        {
            case FtpNameMode.Original: return NamingEngine.Sanitize(Path.GetFileNameWithoutExtension(f.Path)) + ext;
            case FtpNameMode.Fixed when !string.IsNullOrWhiteSpace(s.FixedName):
                return NamingEngine.Sanitize(s.FixedName) + (count > 1 ? $"_{index + 1}" : "") + ext;
            default:
                var share = Ctx.Settings?.Current.Share ?? new ShareSettings();
                var omit = share.DoNotIncludeCaptureSourceInNames || (Ctx.Settings?.Current.Privacy.StripMetadataOnShare ?? false);
                return NamingEngine.BuildName(new NamingContext { Title = f.Title, Width = f.Width, Height = f.Height, Counter = _counter.Next() }, share.Naming, omit) + ext;
        }
    }

    private static string JoinRemote(string dir, string name)
    {
        var d = string.IsNullOrWhiteSpace(dir) ? "/" : dir.Replace('\\', '/');
        return d.TrimEnd('/') + "/" + name;
    }

    internal static string? PublicLink(FtpDestinationSettings s, string name)
        => string.IsNullOrWhiteSpace(s.PublicUrlBase) ? null : s.PublicUrlBase.TrimEnd('/') + "/" + Uri.EscapeDataString(name);

    private async Task<ShareResult> UploadFtpAsync(FtpDestinationSettings s, IReadOnlyList<ShareFile> files, List<string> names, IProgress<double>? progress, CancellationToken ct)
    {
        var password = Ctx.Credentials.Read(PasswordKey(s)) ?? "";
        using var client = new AsyncFtpClient(s.Host, string.IsNullOrEmpty(s.UserName) ? "anonymous" : s.UserName,
            string.IsNullOrEmpty(password) && string.IsNullOrEmpty(s.UserName) ? "anonymous@" : password, s.EffectivePort);
        client.Config.EncryptionMode = s.Protocol switch
        {
            FtpProtocol.FtpsExplicit => FtpEncryptionMode.Explicit,
            FtpProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };
        client.Config.DataConnectionType = s.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        client.Config.ConnectTimeout = 30_000;
        client.ValidateCertificate += (_, e) => e.Accept = e.PolicyErrors == SslPolicyErrors.None || s.AcceptInvalidCertificate;
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
            string? link = null;
            var locations = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                var remote = JoinRemote(s.Directory, names[i]);
                var slice = Slice(progress, i, files.Count);
                var status = await client.UploadFile(files[i].Path, remote, FtpRemoteExists.Overwrite, true, FtpVerify.None,
                    slice is null ? null : new Progress<FtpProgress>(p => slice.Report(p.Progress / 100.0)), ct).ConfigureAwait(false);
                if (status == FtpStatus.Failed) throw new ShotwiseException(ErrorCode.UploadRejected, $"The server rejected {names[i]}.");
                locations.Add($"{s.Host}:{remote}");
                link ??= PublicLink(s, names[i]);
            }
            return new ShareResult(ShareOutcome.Succeeded, $"Uploaded {files.Count} file(s) to {s.Host}.", link, link, locations, OpenLinkAction(link));
        }
        catch (FtpAuthenticationException ex) { throw new ShotwiseException(ErrorCode.AuthenticationFailed, "FTP login failed: " + ex.Message, ex); }
        catch (FtpException ex) { throw new ShotwiseException(ErrorCode.NetworkError, "FTP error: " + ex.Message, ex); }
    }

    private Task<ShareResult> UploadSftpAsync(FtpDestinationSettings s, IReadOnlyList<ShareFile> files, List<string> names, IProgress<double>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var password = Ctx.Credentials.Read(PasswordKey(s));
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(s.PrivateKeyPath))
            {
                var pass = Ctx.Credentials.Read(PassphraseKey(s));
                var key = string.IsNullOrEmpty(pass) ? new PrivateKeyFile(s.PrivateKeyPath) : new PrivateKeyFile(s.PrivateKeyPath, pass);
                methods.Add(new PrivateKeyAuthenticationMethod(s.UserName, key));
            }
            if (!string.IsNullOrEmpty(password)) methods.Add(new PasswordAuthenticationMethod(s.UserName, password));
            if (methods.Count == 0) throw new ShotwiseException(ErrorCode.AuthorizationRequired, "No password or key is configured for SFTP.");

            var info = s.ProxyKind == FtpProxyKind.None || string.IsNullOrWhiteSpace(s.ProxyHost)
                ? new ConnectionInfo(s.Host, s.EffectivePort, s.UserName, methods.ToArray())
                : new ConnectionInfo(s.Host, s.EffectivePort, s.UserName,
                    s.ProxyKind switch { FtpProxyKind.Http => ProxyTypes.Http, FtpProxyKind.Socks4 => ProxyTypes.Socks4, _ => ProxyTypes.Socks5 },
                    s.ProxyHost, s.ProxyPort, string.IsNullOrEmpty(s.ProxyUser) ? null : s.ProxyUser,
                    Ctx.Credentials.Read(CredentialKeys.Make("ftp", $"{s.UserName}@{s.Host}:proxy")), methods.ToArray());
            info.Timeout = TimeSpan.FromSeconds(30);

            using var client = new SftpClient(info);
            var changedFingerprint = false;
            client.HostKeyReceived += (_, e) =>
            {
                var fp = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                // SSH.NET invokes this on this background connection thread, before authentication.
                e.CanTrust = TrustHostAsync(s, fp).GetAwaiter().GetResult();
                changedFingerprint = !e.CanTrust;
            };
            try { client.Connect(); }
            catch (SshAuthenticationException ex) { throw new ShotwiseException(ErrorCode.AuthenticationFailed, "SFTP login failed: " + ex.Message, ex); }
            catch (SshConnectionException ex) when (changedFingerprint) { throw new ShotwiseException(ErrorCode.AuthenticationFailed, "The server's SSH host key changed; refusing to connect.", ex); }
            catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException) { throw new ShotwiseException(ErrorCode.NetworkError, "SFTP connection failed: " + ex.Message, ex); }
            SaveSettings(s); // persists the first-use fingerprint

            ct.Register(() => { try { client.Disconnect(); } catch (Exception) { } });
            EnsureRemoteDirectory(client, s.Directory);
            string? link = null;
            var locations = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var remote = JoinRemote(s.Directory, names[i]);
                using var fs = File.OpenRead(files[i].Path);
                var total = Math.Max(1, fs.Length);
                var slice = Slice(progress, i, files.Count);
                client.UploadFile(fs, remote, true, sent => slice?.Report(sent / (double)total));
                locations.Add($"{s.Host}:{remote}");
                link ??= PublicLink(s, names[i]);
            }
            client.Disconnect();
            return new ShareResult(ShareOutcome.Succeeded, $"Uploaded {files.Count} file(s) to {s.Host}.", link, link, locations, OpenLinkAction(link));
        }, ct);
    }

    private static void EnsureRemoteDirectory(SftpClient client, string dir)
    {
        var path = string.IsNullOrWhiteSpace(dir) ? "/" : dir.Replace('\\', '/');
        if (path == "/" || client.Exists(path)) return;
        var current = path.StartsWith('/') ? "" : ".";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (!client.Exists(current)) client.CreateDirectory(current);
        }
    }
}
