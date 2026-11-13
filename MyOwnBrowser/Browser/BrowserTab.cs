using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyOwnBrowser.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MyOwnBrowser.Browser;

internal sealed class BrowserTab : INotifyPropertyChanged, IDisposable
{
    private string _title = "Nueva pestaña";
    private bool _pinned;
    private bool _muted;
    private string? _favicon;
    private ImageSource? _faviconImage;
    private string _address = "browser:home";
    private readonly Func<BrowserTab, IWebSession> _createSession;
    private readonly Action<BrowserTab> _sessionCreated;
    private IWebSession? _session;

    public BrowserTab(Func<BrowserTab, IWebSession> createSession, Action<BrowserTab> sessionCreated,
        GpuCompositor.FrameSource? source, bool isPrivate)
    {
        _createSession = createSession;
        _sessionCreated = sessionCreated;
        Source = source;
        IsPrivate = isPrivate;
    }

    /// <summary>The Chromium session; created on first access so tabs restored in the background cost nothing until shown.</summary>
    public IWebSession Session
    {
        get
        {
            if (_session is null)
            {
                _session = _createSession(this);
                _sessionCreated(this);
            }
            return _session;
        }
    }

    /// <summary>The session if it has been created; use for work that must not wake a dormant tab.</summary>
    public IWebSession? SessionIfCreated => _session;
    public GpuCompositor.FrameSource? Source { get; }
    public bool IsPrivate { get; }
    public string Address { get => _address; set { _address = value; Changed(); IconChanged(); } }
    public bool CanGoBack { get; set; }
    public bool CanGoForward { get; set; }
    public bool IsLoading { get => _loading; set { if (_loading == value) return; _loading = value; Changed(); IconChanged(); Changed(nameof(LoadingVisibility)); } }
    private bool _loading, _audible;
    /// <summary>The page is producing sound right now (shown on the tab; clicking the icon mutes it).</summary>
    public bool Audible { get => _audible; set { if (_audible == value) return; _audible = value; Changed(); } }
    public Visibility LoadingVisibility => _loading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FaviconVisibility => _loading || _faviconImage is null ? Visibility.Collapsed : Visibility.Visible;
    public bool Ready { get; set; }
    public bool HasFrame { get; set; }
    public bool HasNavigatedWeb { get; set; }
    public double Zoom { get; set; } = 1;
    public double ScrollY { get; set; }
    public double DocumentHeight { get; set; }
    public double TopInset { get; set; }
    public bool FullViewport { get; set; } = true;
    public bool Shown { get; set; }
    /// <summary>Last rendering of the page, kept for the hover card (set while the tab is visible, so background tabs have one too).</summary>
    public ImageSource? Thumbnail { get; set; }
    public long ThumbnailTick { get; set; }
    public bool VerticalShown { get; set; }
    public UIElement? InternalContent { get; set; }
    public string? Favicon { get => _favicon; set { _favicon = value; _faviconImage = null; Changed(); IconChanged(); } }
    public ImageSource? FaviconImage { set { _faviconImage = value; IconChanged(); } }
    public ImageSource? FaviconSource => _faviconImage;
    public string Glyph => Address == "browser:settings" ? "\uE713" : Address == "browser:home" ? "\uE80F" : "\uE774";
    public Visibility GlyphVisibility => _faviconImage is null && !_loading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloseVisibility => Pinned ? Visibility.Collapsed : Visibility.Visible;
    public IconSource Icon => _faviconImage is not null
        ? new ImageIconSource { ImageSource = _faviconImage }
        : new FontIconSource { Glyph = Glyph };
    private void IconChanged()
    {
        Changed(nameof(Icon)); Changed(nameof(FaviconSource)); Changed(nameof(Glyph)); Changed(nameof(GlyphVisibility)); Changed(nameof(FaviconVisibility));
    }
    public string Title { get => _title; set { _title = value; Changed(); } }
    public bool Pinned { get => _pinned; set { _pinned = value; Changed(); Changed(nameof(Closable)); Changed(nameof(CloseVisibility)); Changed(nameof(StateLabel)); } }
    public bool Closable => !Pinned;
    public bool Muted { get => _muted; set { _muted = value; Changed(); Changed(nameof(StateLabel)); } }
    public string StateLabel => string.Join(" · ", new[] { Pinned ? "Fijada" : "", IsPrivate ? "Privada" : "", Muted ? "Silenciada" : "" }.Where(s => s.Length > 0));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() { _session?.Dispose(); Source?.Dispose(); }
}
