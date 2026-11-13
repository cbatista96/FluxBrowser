using Microsoft.UI.Xaml;
using CefSharp;
using CefSharp.OffScreen;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using FluxBrowser.Browser;
using FluxBrowser.Diagnostics;

namespace FluxBrowser;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    internal IReadOnlyList<MainWindow> Windows => _windows;
    private bool _engineStarted;
    private bool _activationPending;
    internal BrowserPreferences Preferences { get; private set; } = new();
    internal string? ProfileError { get; private set; }
    internal List<HistoryEntry> History { get; private set; } = [];
    internal bool HistoryAvailable { get; private set; } = true;
    internal bool SessionAvailable { get; private set; } = true;
    internal List<SavedTab> SavedTabs { get; private set; } = [];

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        ProfileStorage.DeferredWriteFailed += OnDeferredWriteFailed;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool firstRun = !File.Exists(Path.Combine(ProfileStorage.DirectoryPath, "settings.json"));
        try { BrowserPreferences loaded = ProfileStorage.Read("settings.json", new BrowserPreferences()); loaded.Validate(); Preferences = loaded; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { ProfileError = $"No se cargaron los ajustes; no se sobrescribirá el archivo hasta que guardes ajustes válidos. {e.Message}"; }
        if (firstRun && WebEngine.UseWebView2) Preferences = Preferences with { PersistSessions = true }; // sandboxed engine: staying signed in is safe
        Motion.Fx.Allowed = () => Preferences.AnimateInterface && MainWindow.SystemSettings.AnimationsEnabled;
        // The browser process takes longer to start than the window: get it going now so both are ready at about the same time.
        if (WebEngine.UseWebView2) WebView2Session.Prewarm(Preferences);
        try
        {
            History = ProfileStorage.Read("history.json", new List<HistoryEntry>());
            if (History.Any(h => h is null || !WebAddress.IsHttp(h.Address) || h.Title is null))
                throw new InvalidDataException("El historial contiene entradas inválidas.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { History.Clear(); HistoryAvailable = false; ProfileError = $"No se pudo cargar el historial. {e.Message}"; }
        try
        {
            SavedTabs = ProfileStorage.Read("session.json", new List<SavedTab>());
            if (SavedTabs.Any(t => t is null || (!ViewportLayout.IsInternal(t.Address) && !WebAddress.IsHttp(t.Address))))
                throw new InvalidDataException("La sesión contiene direcciones inválidas.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { SavedTabs.Clear(); SessionAvailable = false; ProfileError = $"No se pudo cargar la sesión. {e.Message}"; }
        OpenWindow(isPrivate: Program.PrivateRequested, restore: !Program.PrivateRequested);
        if (_activationPending)
        {
            _activationPending = false;
            ActivateExistingWindow();
        }
    }

    internal void InitializeEngine()
    {
        if (_engineStarted || WebEngine.UseWebView2) return;
        string path = Path.Combine(ProfileStorage.DirectoryPath, "PrototypeCef");
        Directory.CreateDirectory(path);
        using var settings = new CefSettings
        {
            RootCachePath = path, Locale = "es", LogFile = Path.Combine(path, "cef.log"),
            // In-memory by default; the opt-in setting keeps cookies and cache between runs. Private windows always use their own in-memory context.
            CachePath = Preferences.PersistSessions ? Path.Combine(path, "Cache") : "",
            PersistSessionCookies = Preferences.PersistSessions
        };
        settings.EnableAudio();
        settings.CefCommandLineArgs.Add("disable-extensions");
        // No background traffic to Google services (sync, domain reliability, hyperlink auditing pings): privacy by default.
        foreach (string privacySwitch in new[] { "disable-background-networking", "disable-sync", "disable-domain-reliability", "no-pings" })
            settings.CefCommandLineArgs.Add(privacySwitch);
        settings.CefCommandLineArgs.Add(Preferences.SmoothScrolling ? "enable-smooth-scrolling" : "disable-smooth-scrolling");
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        var handler = new PrototypeBrowserProcessHandler(() =>
        {
            if (!dispatcher.TryEnqueue(ActivateExistingWindow))
                System.Diagnostics.Debug.WriteLine("No se pudo activar la ventana existente de Chromium.");
        });
        if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: handler))
            throw new InvalidOperationException("Chromium no se pudo iniciar. Si hay otra versión del navegador abierta, ciérrala y vuelve a intentarlo.");
        _engineStarted = true;
    }

    internal void ActivateExistingWindow() => ActivateExistingWindow([]);

    internal void ActivateExistingWindow(IReadOnlyList<string> addresses)
    {
        if (_windows.Count == 0)
        {
            _activationPending = true;
            Program.QueueAddresses(addresses);
            return;
        }
        MainWindow window = _windows[^1];
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        window.Activate();
        if (addresses.Count > 0) window.OpenExternal(addresses);
    }

    internal void OpenWindow(bool isPrivate = false, bool restore = false, TabTransfer? transfer = null, Windows.Graphics.RectInt32? bounds = null)
    {
        var window = new MainWindow(isPrivate, restore, transfer, bounds);
        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            _windows.FirstOrDefault(w => w.CanPersistSession)?.PersistTabs();
            if (_windows.Count > 0) return;
            ProfileStorage.Flush();
            if (_engineStarted) { Cef.Shutdown(); _engineStarted = false; }
        };
        window.Activate();
    }

    internal void SavePreferences(BrowserPreferences preferences)
    {
        preferences.Validate();
        ProfileStorage.Write("settings.json", preferences);
        Preferences = preferences;
        foreach (MainWindow window in _windows) window.ApplyPreferences();
    }

    internal void PersistSession(bool force = false)
    {
        if (!SessionAvailable || !_windows.Any(w => w.CanPersistSession)) return;
        ProfileStorage.WriteDeferred("session.json", force || Preferences.RestoreSession
            ? _windows.SelectMany(w => w.SnapshotTabs()).ToList() : new List<SavedTab>());
    }

    internal static void ReportAsyncFault(Exception exception)
    {
        AppLog.Error("Excepción en una operación asíncrona", exception);
        (Current as App)?.ReportProblem("Se produjo un error inesperado", exception.Message);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Excepción no controlada en la interfaz", e.Exception);
        // The UI survives most handler faults; losing every open tab to a cosmetic error is worse than continuing.
        e.Handled = e.Exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException);
        if (e.Handled) ReportProblem("Se produjo un error inesperado", e.Exception.Message);
    }

    private void OnDeferredWriteFailed(string file, Exception exception)
    {
        AppLog.Error($"No se pudo guardar {file}", exception);
        ReportProblem($"No se pudo guardar {file}", exception.Message);
    }

    private void ReportProblem(string title, string message)
    {
        if (_windows.Count == 0) return;
        MainWindow window = _windows[^1];
        window.DispatcherQueue.TryEnqueue(() => window.ReportProblem(title, message));
    }

    /// <summary>Saves the normal-window tabs, starts a fresh process that waits for this one to exit, and closes every window.</summary>
    internal void RestartBrowser()
    {
        AppLog.Info("Reiniciando el navegador.");
        PersistSession(force: true);
        ProfileStorage.Flush();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Environment.ProcessPath ?? throw new InvalidOperationException("No se conoce la ruta del ejecutable."),
                $"--restart-after={Environment.ProcessId} --restore-session") { UseShellExecute = false });
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("No se pudo reiniciar el navegador", e);
            ReportProblem("No se pudo reiniciar el navegador", e.Message);
            return;
        }
        foreach (MainWindow window in _windows.ToArray()) window.CloseForRestart();
    }
    internal void RefreshBookmarks() { foreach (MainWindow window in _windows) window.RefreshBookmarks(); }
}
