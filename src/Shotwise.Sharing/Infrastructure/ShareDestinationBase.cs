using Microsoft.Extensions.Logging;
using Shotwise.Core.Platform;
using Shotwise.Core.Sharing;

namespace Shotwise.Sharing.Infrastructure;

/// <summary>Destinations that can be switched off by IPolicyService report which feature gates them.</summary>
public interface IPolicyGatedDestination
{
    /// <summary>Policy features that must all be allowed (Outputs is always checked by the service).</summary>
    IReadOnlyList<PolicyFeature> RequiredPolicies { get; }
}

/// <summary>Destinations that exist in the registry but cannot work (e.g. Evernote); listed disabled with an explanation.</summary>
public interface IUnavailableDestination
{
    string UnavailableReason { get; }
}

/// <summary>Common defaults for destinations.</summary>
public abstract class ShareDestinationBase : IShareDestination, IPolicyGatedDestination
{
    protected ShareDestinationBase(ShareContext ctx)
    {
        Ctx = ctx;
        Log = ctx.Log(GetType().Name);
    }

    protected ShareContext Ctx { get; }
    protected ILogger Log { get; }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string IconGlyph { get; }
    public abstract DestinationCategory Category { get; }
    public abstract IReadOnlySet<string> SupportedFormats { get; }
    public virtual bool SupportsMultiple => true;
    public virtual bool SupportsStepCapture => false;
    public virtual bool CanShowOptions => false;
    public virtual bool RequiresAccount => false;
    public virtual bool IsConfigured => true;

    public virtual IReadOnlyList<PolicyFeature> RequiredPolicies => Category switch
    {
        DestinationCategory.BuiltIn => Array.Empty<PolicyFeature>(),
        DestinationCategory.Custom => Array.Empty<PolicyFeature>(),
        _ => new[] { PolicyFeature.CloudSharing },
    };

    public virtual Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct) => Task.FromResult(true);
    public abstract Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct);
    public virtual Task ShowOptionsAsync(IShareUi ui) => Task.CompletedTask;
    public virtual Task SignInAsync(IShareUi ui, CancellationToken ct) => Task.CompletedTask;
    public virtual Task SignOutAsync() => Task.CompletedTask;

    protected T LoadSettings<T>() where T : class, new() => Ctx.Store.Load<T>(Id);
    protected void SaveSettings<T>(T value) where T : class => Ctx.Store.Save(Id, value);

    protected static void Report(IProgress<double>? p, double fraction) => p?.Report(Math.Clamp(fraction, 0, 1));

    /// <summary>Per-file progress mapping for multi-file uploads.</summary>
    protected static IProgress<double>? Slice(IProgress<double>? p, int index, int count)
        => p is null ? null : new Progress<double>(v => p.Report((index + Math.Clamp(v, 0, 1)) / count));

    protected static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return false;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }

    protected static Action? OpenLinkAction(string? url) => url is null ? null : () => OpenUrl(url);

    /// <summary>Title shown for a file when naming uploads.</summary>
    protected static string NameOf(ShareFile f) => Path.GetFileName(f.Path);
}
