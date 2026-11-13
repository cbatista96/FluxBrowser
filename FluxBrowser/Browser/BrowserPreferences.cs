using System.Text.Json;

namespace FluxBrowser.Browser;

internal sealed record BrowserPreferences
{
    public string Theme { get; set; } = "System";
    public bool VerticalTabs { get; set; }
    public double SidebarWidth { get; set; } = 224;
    public bool ShowBookmarks { get; set; } = true;
    public bool ShowHomeButton { get; set; } = true;
    public bool ShowFavoritesButton { get; set; } = true;
    public bool ShowHistoryButton { get; set; } = true;
    public bool ShowDownloadsButton { get; set; } = true;
    public bool NewTabBackground { get; set; } = true;
    public bool NewTabQuickLinks { get; set; } = true;
    public bool AnimateInterface { get; set; } = true;
    public bool AskDownloadLocation { get; set; }
    public bool ShowDownloadsOnStart { get; set; } = true;
    public bool ConfirmCloseMultipleTabs { get; set; }
    public string ProfileName { get; set; } = "Personal";
    public string PreferredLanguages { get; set; } = "es,es-ES,en-US,en";
    public bool Spellcheck { get; set; } = true;
    public int WebFontSize { get; set; } = 16;
    public int MinimumWebFontSize { get; set; }
    public bool JavaScriptEnabled { get; set; } = true;
    public string PageTheme { get; set; } = "Browser";
    public bool StartupUseCustomPages { get; set; }
    public string StartupPages { get; set; } = "";
    public double BlurRadius { get; set; } = 12;
    public double GlassOpacity { get; set; } = 0.50;
    public double MenuOpacity { get; set; } = 0.90;
    public bool SmoothScrolling { get; set; } = true;
    /// <summary>Keep cookies and cache on disk so logins survive a restart. Off by default: the engine runs without its sandbox.</summary>
    public bool PersistSessions { get; set; }
    public bool PauseHiddenTabs { get; set; } = true;
    /// <summary>How much the engine blocks trackers: Basic, Balanced or Strict (WebView2 only).</summary>
    public string TrackingPrevention { get; set; } = "Balanced";
    /// <summary>Suggest open tabs, favourites and history while typing in the address bar. Nothing leaves the computer.</summary>
    public bool AddressSuggestions { get; set; } = true;
    public bool ShowPerformance { get; set; }
    public int FrameRate { get; set; } = 120;
    public string SearchEngine { get; set; } = "DuckDuckGo";
    public string HomePage { get; set; } = "browser:home";
    public string NewTabPage { get; set; } = "browser:home";
    public double DefaultZoom { get; set; } = 1;
    public bool RestoreSession { get; set; } = true;
    public bool RememberHistory { get; set; } = true;
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FluxBrowser");

    public void Validate()
    {
        if (Theme is not ("Light" or "Dark" or "System") ||
            SearchEngine is not ("DuckDuckGo" or "Bing" or "Google") || FrameRate is not (30 or 60 or 90 or 120 or 144 or 165 or 240) ||
            !double.IsFinite(SidebarWidth) || SidebarWidth is < 180 or > 360 ||
            !double.IsFinite(BlurRadius) || BlurRadius is < 0 or > 30 ||
            !double.IsFinite(GlassOpacity) || GlassOpacity is < 0.25 or > 0.95 ||
            !double.IsFinite(MenuOpacity) || MenuOpacity is < 0.4 or > 0.98 ||
            !double.IsFinite(DefaultZoom) || DefaultZoom is < .25 or > 5)
            throw new InvalidDataException("Los valores de apariencia o rendimiento no son válidos.");
        if (HomePage != "browser:home" && !WebAddress.IsHttp(HomePage))
            throw new InvalidDataException("La página de inicio debe ser browser:home o una dirección HTTP/HTTPS sin credenciales.");
        if (NewTabPage != "browser:home" && !WebAddress.IsHttp(NewTabPage))
            throw new InvalidDataException("La nueva pestaña debe ser browser:home o una dirección HTTP/HTTPS sin credenciales.");
        if (string.IsNullOrWhiteSpace(DownloadDirectory) || !Path.IsPathFullyQualified(DownloadDirectory))
            throw new InvalidDataException("La carpeta de descargas debe ser una ruta absoluta.");
        if (string.IsNullOrWhiteSpace(ProfileName) || ProfileName.Length > 80)
            throw new InvalidDataException("El nombre del perfil debe tener entre 1 y 80 caracteres.");
        if (TrackingPrevention is not ("Basic" or "Balanced" or "Strict"))
            throw new InvalidDataException("El nivel de prevención de seguimiento no es válido.");
        if (PageTheme is not ("Browser" or "Light" or "Dark") || WebFontSize is < 9 or > 48 || MinimumWebFontSize is < 0 or > 32)
            throw new InvalidDataException("Los ajustes de colores o fuentes no son válidos.");
        if (string.IsNullOrWhiteSpace(PreferredLanguages) || PreferredLanguages.Length > 256)
            throw new InvalidDataException("Introduce al menos un idioma, por ejemplo es-ES,en-US.");
        foreach (string language in PreferredLanguages.Split(','))
        {
            try
            {
                // A direct lookup of a culture the system knows: enumerating every culture for each language made start-up slower.
                if (language.Trim().Length == 0 || System.Globalization.CultureInfo.GetCultureInfo(language.Trim(), predefinedOnly: true).Name.Length == 0)
                    throw new InvalidDataException($"El idioma «{language}» no es válido. Usa códigos como es-ES o en-US.");
            }
            catch (System.Globalization.CultureNotFoundException)
            { throw new InvalidDataException($"El idioma «{language}» no es válido."); }
        }
        string[] pages = StartupPages.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pages.Length > 20 || pages.Any(p => !WebAddress.IsHttp(p)) || (StartupUseCustomPages && pages.Length == 0))
            throw new InvalidDataException("Introduce entre 1 y 20 páginas HTTP/HTTPS, una por línea, para el inicio personalizado.");
    }
}

internal static class WebAddress
{
    public static bool IsHttp(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// True for loopback, link-local, private-range and single-label hosts. Requests the browser process makes on its own
    /// (favicons, new-tab image) must never be steered to these by a page; Chromium's own requests are unaffected.
    /// </summary>
    public static bool IsLocalOrPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)) return true;
            byte[] b = ip.GetAddressBytes();
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? b[0] is 10 or 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                  (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127)
                : (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return !host.Contains('.') || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal) ||
            host.EndsWith(".lan", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal);
    }
}

internal static class ProfileStorage
{
    /// <summary>Overrides the profile folder (portable installs, automated tests). Must be an absolute path.</summary>
    public const string ProfileVariable = "FLUXBROWSER_PROFILE";
    private const long MaxFileBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Directory, Func<string> Json)> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _flushTimer;

    public static string? ProfileOverride =>
        Environment.GetEnvironmentVariable(ProfileVariable) ?? Environment.GetEnvironmentVariable("MYOWNBROWSER_PROFILE");

    public static string DirectoryPath => ProfileOverride is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxBrowser");

    internal static void MigrateLegacyProfile(string localAppData)
    {
        string previous = Path.Combine(localAppData, "MyOwnBrowser");
        string current = Path.Combine(localAppData, "FluxBrowser");
        if (!Directory.Exists(previous) || Directory.Exists(current)) return;
        try { Directory.Move(previous, current); }
        catch (IOException) when (Directory.Exists(current) && !Directory.Exists(previous))
        {
            // Another launch completed the same migration.
        }
    }

    /// <summary>Raised on a background thread when a deferred write fails.</summary>
    public static event Action<string, Exception>? DeferredWriteFailed;

    public static T Read<T>(string name, T missing, string? directory = null)
    {
        string path = Path.Combine(directory ?? DirectoryPath, name);
        if (!File.Exists(path)) return missing;
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException($"El archivo {name} supera el tamaño permitido.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"El archivo {name} no es válido.");
    }

    /// <summary>Writes immediately and discards any deferred write of the same file so stale data cannot overwrite it.</summary>
    public static void Write<T>(string name, T value, string? directory = null)
    {
        directory ??= DirectoryPath;
        string json = JsonSerializer.Serialize(value, Indented);
        lock (Gate)
        {
            Pending.Remove(Key(directory, name));
            WriteAtomic(Path.Combine(directory, name), json);
        }
    }

    /// <summary>
    /// Serializes now (so later mutations of <paramref name="value"/> are not observed) and writes shortly afterwards.
    /// Bursts of changes (navigation, tab reordering) collapse into one disk write off the UI thread.
    /// </summary>
    public static void WriteDeferred<T>(string name, T value, string? directory = null)
    {
        directory ??= DirectoryPath;
        string json = JsonSerializer.Serialize(value, Indented);
        Defer(directory, name, () => json);
    }

    /// <summary>
    /// Like <see cref="WriteDeferred{T}"/> for a list of immutable items that changes often and can be long (the history): only the
    /// references are copied now, and the JSON is produced when the file is written, off the calling thread.
    /// </summary>
    public static void WriteDeferredSnapshot<T>(string name, IReadOnlyCollection<T> items, string? directory = null)
    {
        T[] snapshot = [.. items];
        Defer(directory ?? DirectoryPath, name, () => JsonSerializer.Serialize(snapshot));
    }

    private static void Defer(string directory, string name, Func<string> json)
    {
        lock (Gate)
        {
            Pending[Key(directory, name)] = (directory, json);
            _flushTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(400, Timeout.Infinite);
        }
    }

    /// <summary>Writes every deferred file now. Call before the process exits.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            foreach (var (key, entry) in Pending.ToArray())
            {
                Pending.Remove(key);
                string name = Path.GetFileName(key);
                try { WriteAtomic(Path.Combine(entry.Directory, name), entry.Json()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { DeferredWriteFailed?.Invoke(name, e); }
            }
        }
    }

    public static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Key(string directory, string name) => Path.Combine(directory, name);
}

internal sealed record HistoryEntry(string Address, string Title, DateTimeOffset Visited, string? Favicon = null);
internal sealed record SavedTab(string Address, bool Pinned, string? Title = null);
