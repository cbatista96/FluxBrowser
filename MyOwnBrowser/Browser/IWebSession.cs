using CefSharp.Enums;

namespace MyOwnBrowser.Browser;

/// <summary>
/// What the window needs from a tab's web engine. <see cref="BrowserSession"/> (CEF, off-screen rendering) and
/// <see cref="WebView2Session"/> (WebView2, sandboxed) implement it; the UI never talks to an engine directly.
/// </summary>
internal interface IWebSession : IDisposable
{
    /// <summary>True when the engine draws into the app's own surface and needs mouse/keyboard forwarded to it.</summary>
    bool IsOffscreen { get; }

    event Action? FirstFrame;
    event Action? Ready;
    event Action<string>? AddressChanged;
    event Action<string>? TitleChanged;
    event Action<bool, bool, bool>? LoadingStateChanged;
    event Action<string, string>? Error;
    event Action<CursorType>? CursorChanged;
    event Action<string, bool>? NewTabRequested;
    event Action<WebMenuRequest>? ContextMenuRequested;
    event Action<DownloadProgress>? DownloadChanged;
    event Action<double, double>? ScrollChanged;
    event Action<string>? FaviconChanged;
    /// <summary>A browser shortcut pressed while the page had the focus (engines that swallow keys before XAML sees them).</summary>
    event Action<WebShortcut>? ShortcutPressed;
    /// <summary>The engine wants a context menu; the window draws it and calls <see cref="WebNativeMenu.Complete"/> with the chosen engine command (or null).</summary>
    event Action<WebNativeMenu>? NativeMenuRequested;
    /// <summary>The page started or stopped producing sound.</summary>
    event Action<bool>? AudioChanged;
    /// <summary>Find in page: the 1-based match that is highlighted (0 when none) and how many there are.</summary>
    event Action<int, int>? FindResult;

    void Start();
    void Resize(double width, double height, float scale, double offsetX = 0, double offsetY = 0);
    void Navigate(string address);
    void ShowTestPage();
    void Back();
    void Forward();
    void Reload();
    void ReloadWithoutCache();
    void Stop();
    void SetVisible(bool visible);
    void SetFocus(bool focused);
    void SetMuted(bool muted);
    Task SetThemeAsync(bool dark);
    void SetFrameRate(int rate);
    void SetZoom(double factor);
    Task<double> GetZoomAsync();
    void Find(string text, bool forward, bool next);
    void StopFinding();
    void Print();
    void DevTools();
    void ViewSource();
    void Download(string address);
    void CancelDownload(int id);
    Task<byte[]> CaptureScreenshotAsync();

    /// <summary>Private memory of the processes this tab renders in, or -1 when the engine cannot tell.</summary>
    Task<long> GetMemoryBytesAsync();
    Task ClearCacheAsync();
    Task<string> CapturePageArchiveAsync();

    /// <summary>Deletes cookies; returns how many were removed, or -1 when the engine cannot say.</summary>
    Task<int> ClearCookiesAsync();

    /// <summary>
    /// Places the page relative to the toolbar. With <paramref name="fullViewport"/> the page fills the whole area and keeps the toolbar's
    /// height free at its top (content scrolls under the translucent bars); otherwise it starts below the toolbar.
    /// </summary>
    void SetLayout(double topInset, bool fullViewport, double toolbarHeight);

    /// <summary>Glass rectangles in page coordinates (DIP). Engines that blur in the page draw them; the off-screen engine ignores this.</summary>
    void UpdateGlass(IReadOnlyList<GlassRect> regions, double blurDip, float scale, bool dark);
}

/// <summary>
/// A translucent region over the page: stable id, DIP rectangle, corner radius, CSS <c>rgba()</c> tint and visibility (0..1, faded by the page).
/// <paramref name="Reveal"/> makes the page start the region opaque and let it thin out to <c>Tint</c> as content scrolls under it.
/// </summary>
internal readonly record struct GlassRect(string Id, double X, double Y, double Width, double Height, double Radius, string Tint, double Visibility, bool Reveal = false);


/// <summary>A key combination reported by the engine: <c>KeyboardEvent.key</c> plus the modifiers held.</summary>
internal readonly record struct WebShortcut(string Key, bool Ctrl, bool Shift, bool Alt);

internal sealed record NativeMenuItem(int CommandId, string Name, string Label, bool Enabled, bool IsSeparator, bool IsSubmenu,
    string Shortcut, IReadOnlyList<NativeMenuItem> Children);

/// <summary>A context menu requested by an engine that does not draw its own: position in DIP relative to the page, what was clicked, and the engine's items.</summary>
internal sealed record WebNativeMenu(double X, double Y, string Link, string Image, string Selection,
    IReadOnlyList<NativeMenuItem> Items, Action<int?> Complete);
