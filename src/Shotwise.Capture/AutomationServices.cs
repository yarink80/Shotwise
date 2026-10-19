using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Interop.UIAutomationClient;

namespace Shotwise.Capture;

public sealed class ElementFinder : IElementFinder, IDisposable
{
    private readonly MessageThread worker;
    private readonly ILogger<ElementFinder> logger;
    private IUIAutomation? automation;
    private int disposed;
    public ElementFinder(ILogger<ElementFinder> logger) { this.logger = logger; worker = new(logger); }
    private IUIAutomation Uia => automation ??= new CUIAutomation();
    internal Task<T> Run<T>(Func<IUIAutomation, T> action, CancellationToken ct) => worker.Invoke(() => action(Uia), ct).WaitAsync(ct);
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    /// <summary>
    /// Provider failures surfaced by the UIA interop (R1-#3): HRESULT mapping turns UIA_E_INVALIDOPERATION (0x80131509) into
    /// <see cref="InvalidOperationException"/>, UIA_E_TIMEOUT (0x80131505) into <see cref="TimeoutException"/>, invalid arguments into
    /// <see cref="ArgumentException"/>; everything else is a <see cref="COMException"/>. Cancellation (OperationCanceledException) is
    /// none of these and is never swallowed.
    /// </summary>
    internal static bool IsUiaFailure(Exception ex) => ex is COMException or InvalidOperationException or TimeoutException or ArgumentException;
    internal static RectI Bounds(tagRECT r) => new(r.left, r.top, Math.Max(0, r.right - r.left), Math.Max(0, r.bottom - r.top));
    public Task<ElementInfo?> ElementAtAsync(PointI screenPx, CancellationToken ct = default) => Run<ElementInfo?>(uia =>
    {
        IUIAutomationCacheRequest? cache = null; IUIAutomationElement? element = null;
        try
        {
            cache = uia.CreateCacheRequest(); cache.AddProperty(30005); cache.AddProperty(30003); cache.AddProperty(30001); cache.AddProperty(30020);
            element = uia.ElementFromPointBuildCache(new tagPOINT { x = screenPx.X, y = screenPx.Y }, cache);
            return new(element.CachedName ?? "", element.CachedControlType.ToString(System.Globalization.CultureInfo.InvariantCulture), Bounds(element.CachedBoundingRectangle), element.CachedNativeWindowHandle);
        }
        catch (Exception ex) when (IsUiaFailure(ex)) { logger.LogDebug(ex, "UI Automation element unavailable"); return null; }
        finally { Release(element); Release(cache); }
    }, ct);
    public Task<IReadOnlyList<RectI>> ElementRectsForWindowAsync(IntPtr hwnd, CancellationToken ct = default) => Run<IReadOnlyList<RectI>>(uia =>
    {
        IUIAutomationElement? root = null; IUIAutomationTreeWalker? walker = null;
        var result = new List<RectI>();
        try
        {
            root = uia.ElementFromHandle(hwnd); walker = uia.ControlViewWalker;
            var stack = new Stack<IUIAutomationElement>();
            var first = walker.GetFirstChildElement(root); if (first is not null) stack.Push(first);
            try
            {
                int visited = 0;
                while (stack.Count > 0 && visited++ < 10000)
                {
                    ct.ThrowIfCancellationRequested();
                    var element = stack.Pop();
                    try
                    {
                        var bounds = Bounds(element.CurrentBoundingRectangle); if (!bounds.IsEmpty && element.CurrentIsOffscreen == 0) result.Add(bounds);
                        var sibling = walker.GetNextSiblingElement(element); if (sibling is not null) stack.Push(sibling);
                        var child = walker.GetFirstChildElement(element); if (child is not null) stack.Push(child);
                    }
                    catch (Exception ex) when (IsUiaFailure(ex)) { logger.LogDebug(ex, "UI Automation subtree changed"); }
                    finally { Release(element); }
                }
            }
            finally { foreach (var e in stack) Release(e); }
        }
        catch (Exception ex) when (IsUiaFailure(ex)) { logger.LogDebug(ex, "UI Automation window unavailable"); }
        finally { Release(walker); Release(root); }
        return result.AsReadOnly();
    }, ct);
    internal Task<string?> BrowserUrl(IntPtr hwnd, CancellationToken ct) => Run<string?>(uia =>
    {
        IUIAutomationElement? root = null; IUIAutomationCondition? condition = null; IUIAutomationElementArray? edits = null;
        try
        {
            root = uia.ElementFromHandle(hwnd); condition = uia.CreatePropertyCondition(30003, 50004);
            edits = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            for (int i = 0; i < Math.Min(edits.Length, 256); i++)
            {
                ct.ThrowIfCancellationRequested(); var edit = edits.GetElement(i);
                try
                {
                    string id = edit.CurrentAutomationId ?? "", name = edit.CurrentName ?? "";
                    if (!(id.Contains("urlbar", StringComparison.OrdinalIgnoreCase) || id.Contains("address", StringComparison.OrdinalIgnoreCase) || name.Contains("address", StringComparison.OrdinalIgnoreCase) || name.Contains("search or enter", StringComparison.OrdinalIgnoreCase))) continue;
                    var value = edit.GetCurrentPattern(10002) as IUIAutomationValuePattern;
                    try { var url = value?.CurrentValue; if (!string.IsNullOrWhiteSpace(url)) return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? url : "https://" + url; }
                    finally { Release(value); }
                }
                finally { Release(edit); }
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex)) { logger.LogDebug(ex, "Browser URL unavailable"); }
        finally { Release(edits); Release(condition); Release(root); }
        return null;
    }, ct);
    internal Task<ScrollProbe> Probe(IntPtr hwnd, PointI at, CancellationToken ct) => Run(uia =>
    {
        IUIAutomationElement? element = null; IUIAutomationTreeWalker? walker = null;
        try
        {
            element = uia.ElementFromPoint(new tagPOINT { x = at.X, y = at.Y }); walker = uia.ControlViewWalker;
            for (int depth = 0; element is not null && depth < 30; depth++)
            {
                var pattern = element.GetCurrentPattern(10004) as IUIAutomationScrollPattern;
                try { if (pattern is not null && (pattern.CurrentVerticallyScrollable != 0 || pattern.CurrentHorizontallyScrollable != 0)) return new ScrollProbe(pattern.CurrentVerticallyScrollable != 0, pattern.CurrentHorizontallyScrollable != 0, Bounds(element.CurrentBoundingRectangle)); }
                finally { Release(pattern); }
                var parent = walker.GetParentElement(element); Release(element); element = parent;
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex)) { logger.LogDebug(ex, "UI Automation scroll probe unavailable"); }
        finally { Release(walker); Release(element); }
        return new ScrollProbe(false, false, WindowFinder.Read(hwnd, false)?.ClientPx ?? RectI.Empty);
    }, ct);
    /// <summary>
    /// Scrolls the scrollable element under <paramref name="at"/> (the capture area centre; walks up to the first ancestor with a
    /// ScrollPattern, as <see cref="Probe"/> does) or, when nothing under the point scrolls, the first scrollable element of the window.
    /// Returns false when UIA cannot scroll in that direction so the caller falls back to scrollbar messages / input.
    /// A reset in a direction the element cannot scroll is a successful no-op.
    /// </summary>
    internal Task<bool> Scroll(IntPtr hwnd, PointI at, bool horizontal, bool reset, CancellationToken ct) => Run(uia =>
    {
        IUIAutomationScrollPattern? pattern = null;
        try
        {
            pattern = ScrollPatternAt(uia, at) ?? FirstScrollPattern(uia, hwnd);
            if (pattern is null) return false;
            bool can = horizontal ? pattern.CurrentHorizontallyScrollable != 0 : pattern.CurrentVerticallyScrollable != 0;
            if (!can) return reset;
            if (reset) pattern.SetScrollPercent(horizontal ? 0 : -1, horizontal ? -1 : 0);
            else pattern.Scroll(horizontal ? ScrollAmount.ScrollAmount_LargeIncrement : ScrollAmount.ScrollAmount_NoAmount, horizontal ? ScrollAmount.ScrollAmount_NoAmount : ScrollAmount.ScrollAmount_LargeIncrement);
            return true;
        }
        catch (Exception ex) when (IsUiaFailure(ex)) { logger.LogDebug(ex, "UI Automation scroll failed"); return false; }
        finally { Release(pattern); }
    }, ct);
    private static IUIAutomationScrollPattern? ScrollPatternAt(IUIAutomation uia, PointI at)
    {
        IUIAutomationElement? element = null; IUIAutomationTreeWalker? walker = null;
        try
        {
            element = uia.ElementFromPoint(new tagPOINT { x = at.X, y = at.Y }); walker = uia.ControlViewWalker;
            for (int depth = 0; element is not null && depth < 30; depth++)
            {
                if (element.GetCurrentPattern(10004) is IUIAutomationScrollPattern pattern)
                {
                    if (pattern.CurrentVerticallyScrollable != 0 || pattern.CurrentHorizontallyScrollable != 0) return pattern;
                    Release(pattern);
                }
                var parent = walker.GetParentElement(element); Release(element); element = parent;
            }
            return null;
        }
        finally { Release(walker); Release(element); }
    }
    private static IUIAutomationScrollPattern? FirstScrollPattern(IUIAutomation uia, IntPtr hwnd)
    {
        IUIAutomationElement? root = null; IUIAutomationCondition? condition = null; IUIAutomationElement? scroll = null;
        try
        {
            root = uia.ElementFromHandle(hwnd); condition = uia.CreatePropertyCondition(30034, true);
            scroll = root.FindFirst(TreeScope.TreeScope_Subtree, condition);
            return scroll?.GetCurrentPattern(10004) as IUIAutomationScrollPattern;
        }
        finally { Release(scroll); Release(condition); Release(root); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { worker.Invoke(() => { Release(automation); automation = null; return true; }).GetAwaiter().GetResult(); }
        finally { worker.Dispose(); }
    }
}

public sealed class CaptureMetadataProvider(ElementFinder elements, ILogger<CaptureMetadataProvider> logger) : ICaptureMetadataProvider
{
    public async Task<DocumentMetadata> CollectAsync(IntPtr? hwnd, PointI? cursor, CancellationToken ct = default)
    {
        var handle = hwnd ?? Native.GetForegroundWindow();
        var metadata = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = DocumentMetadata.Create() with { CursorPosition = cursor, WindowTitle = Native.Title(handle) };
            try
            {
                Native.GetWindowThreadProcessId(handle, out var pid); using var process = Process.GetProcessById((int)pid);
                var file = process.MainModule?.FileName;
                var version = file is null ? null : FileVersionInfo.GetVersionInfo(file);
                return result with { AppName = version?.ProductName ?? process.ProcessName, AppCompany = version?.CompanyName, AppVersion = version?.ProductVersion };
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { logger.LogDebug(ex, "Capture process metadata unavailable"); return result; }
        }, ct).ConfigureAwait(false);
        Native.GetWindowThreadProcessId(handle, out var processId);
        string name = "";
        try { using var process = Process.GetProcessById((int)processId); name = process.ProcessName; } catch (ArgumentException) { }
        if (new[] { "msedge", "chrome", "firefox", "brave" }.Contains(name, StringComparer.OrdinalIgnoreCase)) metadata = metadata with { Url = await elements.BrowserUrl(handle, ct).ConfigureAwait(false) };
        return metadata;
    }
}
