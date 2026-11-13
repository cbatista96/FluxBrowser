using System.Text.Json;
using CefSharp.Enums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using FluxBrowser.Diagnostics;
using Windows.Storage.Streams;

namespace FluxBrowser.Browser;

/// <summary>
/// A tab on WebView2: Chromium runs sandboxed in the Edge runtime and is serviced by it. The control is a XAML element, so the
/// window's translucent chrome is drawn over it directly; the blur under that chrome is drawn by the page itself (see page-host.js).
/// </summary>
internal sealed class WebView2Session : IWebSession
{
    private static Task<CoreWebView2Environment>? _environment;
    private static readonly string PageScript = LoadScript("page-host.js");
    private static readonly string KeyScript = LoadScript("page-keys.js");

    private readonly Panel _host;
    private readonly WebView2 _view = new() { Visibility = Visibility.Visible };
    private string _initialPage;
    private bool _ready;
    private readonly string _testPage;
    private readonly bool _isPrivate;
    private readonly Func<string> _downloadDirectory;
    private readonly Func<bool> _askDownloadLocation;
    private readonly BrowserPreferences _preferences;
    private readonly Dictionary<int, CoreWebView2DownloadOperation> _downloads = [];
    private CoreWebView2? _core;
    // DevTools event receivers must stay referenced: if they are collected, the engine later calls into a freed object and crashes the process.
    private readonly List<object> _receivers = [];
    private bool _disposed, _loading, _firstFrame;
    private double _zoom = 1;
    private int _nextDownload;
    private IReadOnlyList<GlassRect> _glass = [];
    private double _blur = 12;
    private float _scale = 1;
    private bool _dark = true;
    private double _gap;
    // The first paint of every new document needs these before the host's "state" message can arrive, so they are baked into the injected script.
    private string? _scriptId, _installedScript;
    private bool _installing, _installAgain;

    public WebView2Session(Panel host, string initialPage, string testPage, bool isPrivate, Func<string> downloadDirectory,
        Func<bool> askDownloadLocation, BrowserPreferences preferences)
    {
        _host = host;
        _initialPage = initialPage;
        _testPage = testPage;
        _isPrivate = isPrivate;
        _downloadDirectory = downloadDirectory;
        _askDownloadLocation = askDownloadLocation;
        _preferences = preferences;
        // An opaque default avoids the dark flash (the window behind shows through) between navigations and before a page paints.
        _view.DefaultBackgroundColor = BackgroundFor(preferences.PageTheme == "Dark");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_view, "Contenido de la página web");
    }

    public bool IsOffscreen => false;

    public event Action? FirstFrame;
    public event Action? Ready;
    public event Action<string>? AddressChanged;
    public event Action<string>? TitleChanged;
    public event Action<bool, bool, bool>? LoadingStateChanged;
    public event Action<string, string>? Error;
    public event Action<string, bool>? NewTabRequested;
    public event Action<DownloadProgress>? DownloadChanged;
    public event Action<double, double>? ScrollChanged;
    public event Action<string>? FaviconChanged;
    public event Action<WebShortcut>? ShortcutPressed;
    public event Action<WebNativeMenu>? NativeMenuRequested;
    public event Action<bool>? AudioChanged;
    public event Action<int, int>? FindResult;
    // WebView2 shows its own cursor and context menu; these exist for the off-screen engine only.
    public event Action<CursorType>? CursorChanged { add { } remove { } }
    public event Action<WebMenuRequest>? ContextMenuRequested { add { } remove { } }

    public static string RuntimeVersion { get; private set; } = "";

    private static string LoadScript(string name)
    {
        using Stream stream = typeof(WebView2Session).Assembly.GetManifestResourceStream("FluxBrowser.Assets." + name)
            ?? throw new InvalidOperationException($"No se encontró el script {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Task<CoreWebView2Environment> EnvironmentAsync(BrowserPreferences preferences) => _environment ??= CreateEnvironmentAsync(preferences);

    /// <summary>
    /// Starts the browser process as soon as the settings are known, while the window is still being built, so the first tab finds
    /// the engine ready instead of waiting for it. Safe to call more than once.
    /// </summary>
    public static void Prewarm(BrowserPreferences preferences) => _ = EnvironmentAsync(preferences);

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(BrowserPreferences preferences)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string folder = Path.Combine(ProfileStorage.DirectoryPath, "WebView2");
        // Without "keep sessions signed in" every run starts from an empty profile (cookies, cache, site data). Deleting a profile with
        // a large cache can take seconds, so it is renamed out of the way (instant) and removed in the background.
        if (!preferences.PersistSessions && Directory.Exists(folder))
        {
            try { Directory.Move(folder, folder + ".old-" + Guid.NewGuid().ToString("N")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { await Task.Run(() => Directory.Delete(folder, true)); }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { AppLog.Warning("No se pudo vaciar el perfil de WebView2", inner); }
            }
        }
        _ = Task.Run(() =>
        {
            try
            {
                foreach (string stale in Directory.EnumerateDirectories(ProfileStorage.DirectoryPath, "WebView2.old-*"))
                    Directory.Delete(stale, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Warning("No se pudo eliminar un perfil antiguo de WebView2", e); }
        });
        Directory.CreateDirectory(folder);
        string language = preferences.PreferredLanguages.Split(',')[0].Trim();
        var options = new CoreWebView2EnvironmentOptions { Language = language.Length > 0 ? language : "es" };
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync("", folder, options);
        RuntimeVersion = environment.BrowserVersionString;
        AppLog.Info($"Entorno de WebView2 listo en {watch.ElapsedMilliseconds} ms");
        return environment;
    }

    public void Start()
    {
        if (!_host.Children.Contains(_view)) _host.Children.Add(_view);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            CoreWebView2Environment environment = await EnvironmentAsync(_preferences);
            if (_disposed) return;
            CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = _isPrivate;
            await _view.EnsureCoreWebView2Async(environment, options);
            if (_disposed) return;
            _core = _view.CoreWebView2;
            Configure(_core);
            await RefreshPageScriptAsync();
            await InstallKeyChannelAsync(_core);
            if (_disposed) return;
            AppLog.Info($"WebView2 listo (privada={_isPrivate}, perfil privado del motor={_core.Profile.IsInPrivateModeEnabled}, perfil={_core.Profile.ProfileName})");
            _ready = true;
            _core.Navigate(_initialPage);
            Ready?.Invoke();
        }
        catch (Exception exception)
        {
            AppLog.Error("No se pudo iniciar WebView2", exception);
            Error?.Invoke("No se pudo iniciar WebView2", exception.Message + " Comprueba que el Runtime de WebView2 está instalado.");
        }
    }

    private async Task InstallKeyChannelAsync(CoreWebView2 core)
    {
        const string world = "FluxBrowserKeys";
        await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding",
            JsonSerializer.Serialize(new { name = "__mobKey", executionContextName = world }));
        await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument",
            JsonSerializer.Serialize(new { source = KeyScript, worldName = world }));
        CoreWebView2DevToolsProtocolEventReceiver bindingReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
        _receivers.Add(bindingReceiver);
        bindingReceiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            try
            {
                using JsonDocument call = JsonDocument.Parse(args.ParameterObjectAsJson);
                if (call.RootElement.GetProperty("name").GetString() != "__mobKey") return;
                using JsonDocument payload = JsonDocument.Parse(call.RootElement.GetProperty("payload").GetString() ?? "{}");
                JsonElement p = payload.RootElement;
                string key = p.GetProperty("key").GetString() ?? "";
                if (key.Length is 0 or > 12) return;
                ShortcutPressed?.Invoke(new WebShortcut(key, p.GetProperty("ctrl").GetBoolean(), p.GetProperty("shift").GetBoolean(), p.GetProperty("alt").GetBoolean()));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            { System.Diagnostics.Debug.WriteLine($"Atajo no válido: {e.Message}"); }
        };
    }

    private void Configure(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.IsScriptEnabled = _preferences.JavaScriptEnabled;
        settings.IsZoomControlEnabled = false;
        // The app owns the shortcuts (print, find, reload, zoom...); the engine must not also act on them.
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = true;
        settings.AreDevToolsEnabled = true;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.NavigationStarting += (_, args) => { _loading = true; RaiseLoading(); };
        core.NavigationCompleted += (_, _) =>
        {
            _loading = false;
            RaiseLoading();
            if (!_firstFrame)
            {
                _firstFrame = true;
                FirstFrame?.Invoke();
#if DEBUG
                if (Environment.GetEnvironmentVariable("FLUXBROWSER_TEST_KEYS") is { Length: > 0 } testKeys) _ = SendTestKeysAsync(testKeys);
#endif
            }
        };
        core.HistoryChanged += (_, _) => RaiseLoading();
        core.SourceChanged += (_, _) => AddressChanged?.Invoke(core.Source);
        core.DocumentTitleChanged += (_, _) => TitleChanged?.Invoke(core.DocumentTitle);
        core.FaviconChanged += (_, _) => { if (WebAddress.IsHttp(core.FaviconUri)) FaviconChanged?.Invoke(core.FaviconUri); };
        core.IsDocumentPlayingAudioChanged += (_, _) => AudioChanged?.Invoke(core.IsDocumentPlayingAudio);
        CoreWebView2Find find = core.Find;
        // The engine counts the active match from 1.
        void ReportFind() => FindResult?.Invoke(find.MatchCount > 0 ? Math.Clamp(find.ActiveMatchIndex, 1, find.MatchCount) : 0, find.MatchCount);
        find.MatchCountChanged += (_, _) => ReportFind();
        find.ActiveMatchIndexChanged += (_, _) => ReportFind();
        try
        {
            core.Profile.PreferredTrackingPreventionLevel = _preferences.TrackingPrevention switch
            {
                "Basic" => CoreWebView2TrackingPreventionLevel.Basic,
                "Strict" => CoreWebView2TrackingPreventionLevel.Strict,
                _ => CoreWebView2TrackingPreventionLevel.Balanced
            };
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or NotImplementedException)
        { AppLog.Warning("No se pudo aplicar el nivel de prevención de seguimiento", e); }
        core.NewWindowRequested += OnNewWindow;
        core.ContextMenuRequested += OnContextMenu;
        core.DownloadStarting += OnDownloadStarting;
        // Permissions (camera, microphone, location...) use the engine's own per-site prompt, which also remembers the answer.
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, args) => Error?.Invoke("El proceso del navegador se detuvo", args.Reason.ToString());
    }

#if DEBUG
    /// <summary>Debug builds only: replays key combinations through DevTools so the shortcut path can be tested without OS-level input.</summary>
    private static bool _testKeysSent;

    private async Task SendTestKeysAsync(string list)
    {
        if (_core is null || _testKeysSent) return;
        _testKeysSent = true;
        await Task.Delay(1500);
        foreach (string combo in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = combo.ToLowerInvariant().Split('+');
            int modifiers = (parts.Contains("alt") ? 1 : 0) | (parts.Contains("ctrl") ? 2 : 0) | (parts.Contains("shift") ? 8 : 0);
            string name = parts[^1];
            (string key, string code, int vk) = name switch
            {
                "tab" => ("Tab", "Tab", 9), "delete" => ("Delete", "Delete", 46), "f5" => ("F5", "F5", 116), "f12" => ("F12", "F12", 123),
                "escape" => ("Escape", "Escape", 27),
                _ when name.Length == 1 && char.IsDigit(name[0]) => (name, "Digit" + name, name[0]),
                _ => ((modifiers & 8) != 0 ? name.ToUpperInvariant() : name, "Key" + name.ToUpperInvariant(), char.ToUpperInvariant(name[0]))
            };
            foreach (string type in new[] { "rawKeyDown", "keyUp" })
                await _core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    JsonSerializer.Serialize(new { type, modifiers, key, code, windowsVirtualKeyCode = vk }));
            await Task.Delay(900);
        }
    }
#endif

    private void RaiseLoading() => LoadingStateChanged?.Invoke(_loading, _core?.CanGoBack == true, _core?.CanGoForward == true);

    private void OnContextMenu(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (NativeMenuRequested is null) return;
        var deferral = args.GetDeferral();
        bool completed = false;
        void Complete(int? command)
        {
            // Idempotent: the window completes once on a choice and again when the menu hides.
            if (completed) return;
            completed = true;
            try { if (command is { } id) args.SelectedCommandId = id; }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
            { AppLog.Warning("No se pudo devolver la opción del menú contextual", e); }
            finally { deferral.Complete(); }
        }
        try
        {
            CoreWebView2ContextMenuTarget target = args.ContextMenuTarget;
            IReadOnlyList<NativeMenuItem> items = Snapshot(args.MenuItems);
#if DEBUG
            AppLog.Info("Menú contextual del motor: " + string.Join(", ", Flatten(items).Select(i => i.Name)));
#endif
            var menu = new WebNativeMenu(args.Location.X, args.Location.Y,
                target.HasLinkUri ? target.LinkUri : "",
                target.Kind == CoreWebView2ContextMenuTargetKind.Image && target.HasSourceUri ? target.SourceUri : "",
                target.HasSelection ? target.SelectionText : "", items, Complete);
            args.Handled = true;
            NativeMenuRequested(menu);
        }
        catch (Exception e)
        {
            // Fall back to the engine's own menu instead of leaving the request pending.
            AppLog.Error("No se pudo preparar el menú contextual", e);
            args.Handled = false;
            Complete(null);
        }
    }

    private static IEnumerable<NativeMenuItem> Flatten(IEnumerable<NativeMenuItem> items) =>
        items.SelectMany(i => i.IsSubmenu ? Flatten(i.Children) : [i]);

    private static IReadOnlyList<NativeMenuItem> Snapshot(IList<CoreWebView2ContextMenuItem> items)
    {
        var list = new List<NativeMenuItem>();
        foreach (CoreWebView2ContextMenuItem item in items)
        {
            bool separator = item.Kind == CoreWebView2ContextMenuItemKind.Separator;
            bool submenu = item.Kind == CoreWebView2ContextMenuItemKind.Submenu;
            list.Add(new NativeMenuItem(item.CommandId, item.Name, item.Label.Replace("&", ""), item.IsEnabled, separator, submenu,
                item.ShortcutKeyDescription ?? "", submenu ? Snapshot(item.Children) : []));
        }
        return list;
    }

    private void OnNewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (args.IsUserInitiated && WebAddress.IsHttp(args.Uri)) NewTabRequested?.Invoke(args.Uri, true);
        else Error?.Invoke("Ventana emergente bloqueada", "Solo se abren enlaces HTTP/HTTPS solicitados por un gesto del usuario.");
    }

    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            string folder = _downloadDirectory();
            Directory.CreateDirectory(folder);
            string name = Path.GetFileName(args.ResultFilePath);
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name)) name = "descarga";
            string path = Path.Combine(folder, name);
            for (int n = 1; File.Exists(path); n++)
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            args.ResultFilePath = path;
            args.Handled = !_askDownloadLocation();
            CoreWebView2DownloadOperation operation = args.DownloadOperation;
            int id = ++_nextDownload;
            _downloads[id] = operation;
            void Report()
            {
                long total = Math.Max(0, (long)operation.TotalBytesToReceive);
                bool inProgress = operation.State == CoreWebView2DownloadState.InProgress;
                string state = operation.State switch
                {
                    CoreWebView2DownloadState.Completed => "Completada",
                    CoreWebView2DownloadState.Interrupted => $"Interrumpida: {operation.InterruptReason}",
                    _ => "Descargando"
                };
                int percent = total > 0 ? (int)Math.Min(100, operation.BytesReceived * 100 / total) : -1;
                DownloadChanged?.Invoke(new DownloadProgress(id, Path.GetFileName(operation.ResultFilePath), operation.ResultFilePath,
                    operation.Uri, (long)operation.BytesReceived, total, percent, state, inProgress));
                if (!inProgress) _downloads.Remove(id);
            }
            operation.BytesReceivedChanged += (_, _) => Report();
            operation.StateChanged += (_, _) => Report();
            Report();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            args.Cancel = true;
            Error?.Invoke("No se pudo iniciar la descarga", e.Message);
        }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // The page (any page) can call chrome.webview.postMessage: validate everything.
        try
        {
            using JsonDocument document = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.String) return;
            switch (kind.GetString())
            {
                case "hello":
                    SendState();
                    break;
                case "viewport" when root.TryGetProperty("y", out JsonElement y) && root.TryGetProperty("height", out JsonElement height) &&
                    y.ValueKind == JsonValueKind.Number && height.ValueKind == JsonValueKind.Number &&
                    y.TryGetDouble(out double offset) && height.TryGetDouble(out double size) &&
                    double.IsFinite(offset) && double.IsFinite(size) && size >= 0:
                    ScrollChanged?.Invoke(Math.Clamp(offset, 0, 1_000_000_000), Math.Min(size, 1_000_000_000));
                    break;
            }
        }
        catch (JsonException e) { System.Diagnostics.Debug.WriteLine($"Mensaje de página no válido: {e.Message}"); }
    }

    private static object Region(GlassRect r) =>
        new { id = r.Id, x = r.X, y = r.Y, w = r.Width, h = r.Height, r = r.Radius, tint = r.Tint, v = r.Visibility, reveal = r.Reveal };

    private void SendState()
    {
        if (_core is null || _disposed) return;
        try
        {
            _core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                kind = "state", zoom = _zoom, scale = _scale, blur = _blur, dark = _dark, gap = _gap, regions = _glass.Select(Region)
            }));
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            // The document is being replaced or the view is not ready; the page asks for the state again ("hello") when it loads.
            AppLog.Info($"No se pudo enviar el estado a la página: 0x{e.HResult:X8}");
        }
    }

    private string BuildPageScript()
    {
        // Only what shapes the first paint: the gap, the zoom and the bars. Transient overlays (menus, panels) arrive by message.
        string seed = JsonSerializer.Serialize(new
        {
            zoom = _zoom, scale = _scale, blur = _blur, dark = _dark, gap = _gap, regions = _glass.Where(r => r.Id == "bar").Select(Region)
        });
        return PageScript.Replace("/*MOB_INIT*/null", seed);
    }

    /// <summary>Re-registers the injected script when its baked-in values changed; the new one is added before the old one is removed.</summary>
    private async Task RefreshPageScriptAsync()
    {
        if (_core is null || _disposed) return;
        if (_installing) { _installAgain = true; return; }
        _installing = true;
        try
        {
            do
            {
                _installAgain = false;
                string script = BuildPageScript();
                if (script == _installedScript) continue;
                string id = await _core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                if (_scriptId is not null && !_disposed) _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
                _scriptId = id;
                _installedScript = script;
            } while (_installAgain && !_disposed);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
        { AppLog.Warning("No se pudo actualizar el script de la página", e); }
        finally { _installing = false; }
    }

    public void Resize(double width, double height, float scale, double offsetX = 0, double offsetY = 0) { }

    public void SetLayout(double topInset, bool fullViewport, double toolbarHeight)
    {
        // Full view: the control covers the whole page area and the page itself keeps the toolbar's height free at its top, so content
        // can scroll under the translucent bars with no host round trip. Otherwise (short documents, app-like pages that fill their
        // viewport) the control simply starts below the toolbar, like any other browser.
        _view.Margin = new Thickness(0, fullViewport ? 0 : toolbarHeight, 0, 0);
        double gap = fullViewport ? toolbarHeight : 0;
        if (Math.Abs(gap - _gap) < .5) return;
        _gap = gap;
        SendState();
        _ = RefreshPageScriptAsync();
    }

    public void UpdateGlass(IReadOnlyList<GlassRect> regions, double blurDip, float scale, bool dark)
    {
        _glass = regions;
        _blur = blurDip;
        _scale = scale;
        _dark = dark;
        SendState();
        _ = RefreshPageScriptAsync();
    }

    public void Navigate(string address)
    {
        if (!_ready || _core is null)
        {
            _initialPage = address;
            return;
        }
        if (_core.IsSuspended) _core.Resume(); // a sleeping tab asked to go somewhere wakes up first
        _core.Navigate(address);
    }
    public void ShowTestPage() => Navigate(_testPage);
    public void Back() => _core?.GoBack();
    public void Forward() => _core?.GoForward();
    public void Reload() => _core?.Reload();
    public void ReloadWithoutCache() => _ = _core?.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}""");
    public void Stop() => _core?.Stop();
    public void SetVisible(bool visible)
    {
        bool was = _view.Visibility == Visibility.Visible;
        _view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) Wake();
        else if (was || _sleepTimer is null) ScheduleSleep();
    }

    // ---------- Sleeping tabs ----------
    // A tab nobody is looking at first gives memory back (after a minute) and then has its renderer suspended (after five), unless it
    // is playing sound, downloading or still loading. Showing it again resumes it at once, exactly where it was.

    private static readonly TimeSpan TrimAfter = SleepTime(60), SuspendAfter = SleepTime(240);

    private static TimeSpan SleepTime(double seconds)
    {
#if DEBUG
        // Debug builds only: FLUXBROWSER_TEST_SLEEP_SECONDS shortens both waits so suspension can be tested in seconds.
        if (double.TryParse(Environment.GetEnvironmentVariable("FLUXBROWSER_TEST_SLEEP_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out double test) && test > 0)
            seconds = test;
#endif
        return TimeSpan.FromSeconds(seconds);
    }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sleepTimer;
    private bool _trimmed;

    private void ScheduleSleep()
    {
        if (_disposed) return;
        if (_sleepTimer is null)
        {
            _sleepTimer = _view.DispatcherQueue.CreateTimer();
            _sleepTimer.IsRepeating = false;
            _sleepTimer.Tick += (_, _) => _ = SleepStepAsync();
        }
        _trimmed = false;
        _sleepTimer.Stop();
        _sleepTimer.Interval = TrimAfter;
        _sleepTimer.Start();
    }

    private async Task SleepStepAsync()
    {
        if (_disposed || _view.Visibility == Visibility.Visible) return;
        // Hidden before the engine finished starting: look again once it has.
        if (_core is null) { _sleepTimer?.Start(); return; }
        try
        {
            if (!_trimmed)
            {
                _trimmed = true;
                _core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
            else if (!_core.IsSuspended)
            {
                bool busy = _loading || _downloads.Count > 0 || _core.IsDocumentPlayingAudio;
                if (!busy && await _core.TrySuspendAsync()) { AppLog.Info("Pestaña en segundo plano suspendida."); return; }
            }
            else return;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or NotImplementedException)
        {
            // Not suspendable right now (a debugger attached, the view became visible meanwhile): try again later.
            System.Diagnostics.Debug.WriteLine($"No se pudo suspender la pestaña: {e.Message}");
        }
        if (_disposed || _view.Visibility == Visibility.Visible || _sleepTimer is null) return;
        _sleepTimer.Interval = SuspendAfter;
        _sleepTimer.Start();
    }

    private void Wake()
    {
        _sleepTimer?.Stop();
        if (_core is null || _disposed) return;
        try
        {
            if (_core.IsSuspended) _core.Resume();
            if (_trimmed) _core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        { AppLog.Warning("No se pudo reanudar la pestaña", e); }
        _trimmed = false;
    }
    public void SetFocus(bool focused) { if (focused) _view.Focus(FocusState.Programmatic); }
    public void SetMuted(bool muted) { if (_core is not null) _core.IsMuted = muted; }

    private static Windows.UI.Color BackgroundFor(bool dark) =>
        dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 255, 255, 255);

    public Task SetThemeAsync(bool dark)
    {
        _view.DefaultBackgroundColor = BackgroundFor(dark);
        if (_core is not null)
            _core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        return Task.CompletedTask;
    }

    public void SetFrameRate(int rate) { }

    public void SetZoom(double factor)
    {
        _zoom = Math.Clamp(factor, 0.25, 5);
        SendState();
        _ = RefreshPageScriptAsync();
    }

    public Task<double> GetZoomAsync() => Task.FromResult(_zoom);

    private string _findTerm = "";

    public void Find(string text, bool forward, bool next)
    {
        if (_core is null || text.Length == 0) return;
        CoreWebView2Find find = _core.Find;
        if (next && text == _findTerm)
        {
            if (forward) find.FindNext(); else find.FindPrevious();
            return;
        }
        _findTerm = text;
        CoreWebView2FindOptions options = _core.Environment.CreateFindOptions();
        options.FindTerm = text;
        options.ShouldHighlightAllMatches = true;
        options.SuppressDefaultFindDialog = true;
        _ = find.StartAsync(options);
    }

    public void StopFinding()
    {
        _findTerm = "";
        _core?.Find.Stop();
    }
    public void Print() => _core?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    public void DevTools() => _core?.OpenDevToolsWindow();
    public void ViewSource() { if (_core is not null) _core.Navigate("view-source:" + _core.Source); }
    public void Download(string address) => Navigate(address);
    public void CancelDownload(int id) { if (_downloads.TryGetValue(id, out CoreWebView2DownloadOperation? operation)) operation.Cancel(); }

    public async Task<long> GetMemoryBytesAsync()
    {
        if (_core is null || _disposed) return -1;
        try
        {
            // The renderer(s) holding this tab's frames: the main frame's process plus out-of-process frames that descend from it.
            uint mainFrame = _core.FrameId;
            var pids = new HashSet<int>();
            foreach (CoreWebView2ProcessExtendedInfo extended in await _core.Environment.GetProcessExtendedInfosAsync())
            {
                if (extended.ProcessInfo.Kind != CoreWebView2ProcessKind.Renderer) continue;
                foreach (CoreWebView2FrameInfo frame in extended.AssociatedFrameInfos)
                {
                    CoreWebView2FrameInfo? root = frame;
                    while (root.ParentFrameInfo is { } parent) root = parent;
                    if (root.FrameId == mainFrame) { pids.Add(extended.ProcessInfo.ProcessId); break; }
                }
            }
            long total = 0;
            foreach (int pid in pids)
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); total += process.PrivateMemorySize64; }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { /* exited meanwhile */ }
            }
            return pids.Count == 0 ? -1 : total;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            AppLog.Warning("No se pudo medir la memoria de la pestaña", e);
            return -1;
        }
    }

    public async Task<byte[]> CaptureScreenshotAsync()
    {
        if (_core is null) throw new InvalidOperationException("La pestaña todavía no está preparada.");
        using var stream = new InMemoryRandomAccessStream();
        await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        byte[] bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public async Task ClearCacheAsync()
    {
        if (_core is null) return;
        await _core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
    }

    public Task<int> ClearCookiesAsync()
    {
        _core?.CookieManager.DeleteAllCookies();
        return Task.FromResult(-1);
    }

    public async Task<string> CapturePageArchiveAsync()
    {
        if (_core is null) throw new InvalidOperationException("La pestaña todavía no está preparada.");
        using JsonDocument document = JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Page.captureSnapshot", "{}"));
        return document.RootElement.GetProperty("data").GetString() ?? "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sleepTimer?.Stop();
        _host.Children.Remove(_view);
        try { _view.Close(); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { AppLog.Warning("WebView2 no se cerró limpiamente", e); }
    }
}
