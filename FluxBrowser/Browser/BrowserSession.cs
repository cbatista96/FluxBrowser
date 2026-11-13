using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.DevTools;
using CefSharp.DevTools.Emulation;
using System.Text.Json;
using FluxBrowser.Rendering;

namespace FluxBrowser.Browser;

internal sealed class BrowserSession : IWebSession
{
    public const string GpuErrorTitle = "Error de composición GPU";
    private readonly ChromiumWebBrowser _browser;
    private readonly CefRenderHandler _renderHandler;
    private readonly WindowInfo _windowInfo;
    private readonly BrowserSettings _browserSettings;
    private readonly string _initialPage;
    private readonly string _testPage;
    private readonly RequestContext? _privateContext;
    private readonly BrowserDownloadHandler _downloads;
    private IBrowserHost? _host;
    private volatile bool _disposed;
    private int _frameRate;
    private DevToolsClient? _devTools;

    public BrowserSession(IntPtr window, GpuCompositor.FrameSource source, string initialPage, string testPage,
        bool isPrivate, int frameRate, Func<string> downloadDirectory, Func<bool> askDownloadLocation, BrowserPreferences preferences)
    {
        _initialPage = initialPage;
        _testPage = testPage;
        _frameRate = frameRate;
        _renderHandler = new CefRenderHandler(source, window);
        _renderHandler.FirstFrame += () => FirstFrame?.Invoke();
        _renderHandler.RenderingFailed += exception => Error?.Invoke(GpuErrorTitle, exception.Message);
        _renderHandler.CursorChanged += cursor => CursorChanged?.Invoke(cursor);

        if (isPrivate) _privateContext = new RequestContext(new RequestContextSettings { CachePath = "" });
        _downloads = new BrowserDownloadHandler(downloadDirectory, askDownloadLocation, progress => DownloadChanged?.Invoke(progress),
            (title, message) => Error?.Invoke(title, message));
        _browser = new ChromiumWebBrowser("about:blank", requestContext: _privateContext, automaticallyCreateBrowser: false)
        {
            RenderHandler = _renderHandler,
            DisplayHandler = new BrowserDisplayHandler(url => FaviconChanged?.Invoke(url)),
            LifeSpanHandler = new PrototypeLifeSpanHandler((address, activate) => NewTabRequested?.Invoke(address, activate),
                (title, message) => Error?.Invoke(title, message)),
            PermissionHandler = new PrototypePermissionHandler((title, message) => Error?.Invoke(title, message)),
            MenuHandler = new WebContextMenu(request => ContextMenuRequested?.Invoke(request)),
            DownloadHandler = _downloads
        };
        _browser.BrowserInitialized += OnBrowserInitialized;
        _browser.AddressChanged += (_, args) => AddressChanged?.Invoke(args.Address);
        _browser.TitleChanged += (_, args) => TitleChanged?.Invoke(args.Title);
        _browser.LoadingStateChanged += (_, args) => LoadingStateChanged?.Invoke(args.IsLoading, args.CanGoBack, args.CanGoForward);
        _browser.FrameLoadStart += (_, args) =>
        {
            if (args.Frame.IsMain) ScrollChanged?.Invoke(0, 0);
        };
        _browser.FrameLoadEnd += (_, args) =>
        {
            if (!args.Frame.IsMain) return;
            args.Frame.ExecuteJavaScriptAsync("""
                (() => {
                    if (window.__mobViewportObserver) return;
                    window.__mobViewportObserver = true;
                    let pending = false;
                    const send = () => {
                        pending = false;
                        CefSharp.PostMessage(JSON.stringify({
                            kind: "viewport",
                            y: window.scrollY,
                            height: Math.max(document.documentElement.scrollHeight, document.body?.scrollHeight || 0)
                        }));
                    };
                    const schedule = () => {
                        if (!pending) { pending = true; requestAnimationFrame(send); }
                    };
                    addEventListener("scroll", schedule, { passive: true });
                    addEventListener("resize", schedule, { passive: true });
                    new ResizeObserver(schedule).observe(document.documentElement);
                    send();
                })();
                """);
        };
        _browser.JavascriptMessageReceived += (_, args) =>
        {
            if (args.Message is not string json) return;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind) &&
                    kind.ValueKind == JsonValueKind.String && kind.GetString() == "viewport" && root.TryGetProperty("y", out var y) &&
                    root.TryGetProperty("height", out var height) && y.ValueKind == JsonValueKind.Number &&
                    height.ValueKind == JsonValueKind.Number && y.TryGetDouble(out double offset) &&
                    height.TryGetDouble(out double size) && double.IsFinite(offset) && double.IsFinite(size) && size >= 0)
                    ScrollChanged?.Invoke(Math.Clamp(offset, 0, 1_000_000_000), Math.Min(size, 1_000_000_000));
            }
            catch (JsonException e) { System.Diagnostics.Debug.WriteLine($"Mensaje de página no válido: {e.Message}"); }
        };
        _browser.LoadError += (_, args) =>
        {
            if (args.Frame.IsMain && args.ErrorCode != CefErrorCode.Aborted)
            {
                Error?.Invoke("No se pudo cargar la página", $"{args.ErrorText} ({args.ErrorCode}).");
            }
        };

        _windowInfo = new WindowInfo();
        _windowInfo.SetAsWindowless(window);
        _windowInfo.SharedTextureEnabled = true;
        _browserSettings = new BrowserSettings
        {
            WindowlessFrameRate = frameRate, DefaultFontSize = preferences.WebFontSize, MinimumFontSize = preferences.MinimumWebFontSize,
            Javascript = preferences.JavaScriptEnabled ? CefState.Enabled : CefState.Disabled
        };
        _preferredLanguages = preferences.PreferredLanguages;
        _spellcheck = preferences.Spellcheck;
    }

    private readonly string _preferredLanguages;
    private readonly bool _spellcheck;

    public event Action? FirstFrame;
    public event Action? Ready;
    public event Action<string>? AddressChanged;
    public event Action<string>? TitleChanged;
    public event Action<bool, bool, bool>? LoadingStateChanged;
    public event Action<string, string>? Error;
    public event Action<CursorType>? CursorChanged;
    public event Action<string, bool>? NewTabRequested;
    public event Action<WebMenuRequest>? ContextMenuRequested;
    public event Action<DownloadProgress>? DownloadChanged;
    public event Action<double, double>? ScrollChanged;
    public event Action<string>? FaviconChanged;

    public IBrowserHost? Host => _host;
    public bool IsOffscreen => true;
    public event Action<WebShortcut>? ShortcutPressed { add { } remove { } }
    public event Action<WebNativeMenu>? NativeMenuRequested { add { } remove { } }
    // The off-screen engine reports neither of these.
    public event Action<bool>? AudioChanged { add { } remove { } }
    public event Action<int, int>? FindResult { add { } remove { } }
    public void SetLayout(double topInset, bool fullViewport, double toolbarHeight) { }
    public void UpdateGlass(IReadOnlyList<GlassRect> regions, double blurDip, float scale, bool dark) { }
    public async Task<int> ClearCookiesAsync()
    {
        using ICookieManager manager = Cef.GetGlobalCookieManager();
        return await manager.DeleteCookiesAsync("", "");
    }
    public void SetFocus(bool focused) => _host?.SetFocus(focused);

    public void Start() => _browser.CreateBrowser(_windowInfo, _browserSettings);

    public void Resize(double width, double height, float scale, double offsetX = 0, double offsetY = 0)
    {
        if (!_renderHandler.SetGeometry(width, height, scale, offsetX, offsetY)) return;
        _host?.NotifyScreenInfoChanged();
        _host?.WasResized();
    }

    public void Navigate(string address) => _browser.LoadUrl(address);
    public void ShowTestPage() => Navigate(_testPage);
    public void Back() => _browser.Back();
    public void Forward() => _browser.Forward();
    public void Reload() => _browser.Reload();
    public void ReloadWithoutCache() => _browser.Reload(ignoreCache: true);
    public void Stop() => _browser.Stop();
    public void SetVisible(bool visible) => _host?.WasHidden(!visible);
    public void SetMuted(bool muted) => _host?.SetAudioMuted(muted);
    public async Task SetThemeAsync(bool dark)
    {
        if (_disposed || _host is null) return;
        _devTools ??= _browser.GetDevToolsClient();
        await _devTools.Emulation.SetEmulatedMediaAsync(features:
            [new MediaFeature { Name = "prefers-color-scheme", Value = dark ? "dark" : "light" }]);
    }
    public void SetFrameRate(int rate)
    {
        _frameRate = rate;
        if (_host is null || _disposed) return;
        Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!_disposed && _host is { } host) host.WindowlessFrameRate = rate;
        }).ContinueWith(task => Error?.Invoke("No se pudo cambiar la frecuencia de fotogramas",
            task.Exception!.GetBaseException().Message), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
    public void SetZoom(double factor) => _host?.SetZoomLevel(Math.Log(factor, 1.2));
    public async Task<double> GetZoomAsync() => Math.Pow(1.2, await (_host ??
        throw new InvalidOperationException("La pestaña todavía no está preparada.")).GetZoomLevelAsync());
    public void Find(string text, bool forward, bool next) => _host?.Find(text, forward, false, next);
    public void StopFinding() => _host?.StopFinding(true);
    public void Print() => _host?.Print();
    public void DevTools() => _browser.ShowDevTools();
    public void ViewSource() { using IFrame frame = _browser.GetMainFrame(); frame.ViewSource(); }
    public void Download(string address) => _host?.StartDownload(address);
    public void CancelDownload(int id) => _downloads.Cancel(id);
    public Task<long> GetMemoryBytesAsync() => Task.FromResult(-1L);

    public async Task<byte[]> CaptureScreenshotAsync()
    {
        if (_disposed || _host is null) throw new InvalidOperationException("La pestaña todavía no está preparada.");
        _devTools ??= _browser.GetDevToolsClient();
        var result = await _devTools.Page.CaptureScreenshotAsync(CefSharp.DevTools.Page.CaptureScreenshotFormat.Png);
        return result.Data;
    }
    public async Task ClearCacheAsync()
    {
        if (_disposed || _host is null) return;
        _devTools ??= _browser.GetDevToolsClient();
        await _devTools.Network.ClearBrowserCacheAsync();
    }

    public async Task<string> CapturePageArchiveAsync()
    {
        if (_disposed || _host is null) throw new InvalidOperationException("La pestaña todavía no está preparada.");
        _devTools ??= _browser.GetDevToolsClient();
        return (await _devTools.Page.CaptureSnapshotAsync()).Data;
    }

    private void OnBrowserInitialized(object? sender, EventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _host = _browser.GetBrowser().GetHost();
        using (IRequestContext context = _browser.GetRequestContext())
        {
            if (!context.SetPreference("intl.accept_languages", _preferredLanguages, out string languageError))
                Error?.Invoke("No se pudieron aplicar los idiomas", languageError);
            if (!context.SetPreference("browser.enable_spellchecking", _spellcheck, out string spellingError))
                Error?.Invoke("No se pudo aplicar la revisión ortográfica", spellingError);
        }
        _host.WindowlessFrameRate = _frameRate;
        _host.NotifyScreenInfoChanged();
        _host.WasResized();
        _browser.LoadUrl(_initialPage);
        Ready?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _devTools?.Dispose();
        _browser.Dispose();
        _downloads.Dispose();
        _host?.Dispose();
        _privateContext?.Dispose();
        _browserSettings.Dispose();
        _windowInfo.Dispose();
    }
}
