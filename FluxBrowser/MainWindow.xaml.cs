using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using CefSharp;
using CefSharp.OffScreen;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using FluxBrowser.Browser;
using FluxBrowser.Rendering;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Point = Windows.Foundation.Point;

namespace FluxBrowser;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _windowHandle;
    private readonly BookmarkStore _bookmarkStore = new();
    private readonly List<Bookmark> _bookmarks = [];
    private GpuCompositor? _compositor;
    private IWebSession? _web => _activeTab?.Session;
    private BrowserSession? _browser => _activeTab?.Session as BrowserSession;
    private readonly ObservableCollection<BrowserTab> _tabs = [];
    private BrowserTab? _activeTab;
    private readonly bool _privateWindow;
    private readonly bool _restoreWindow;
    private bool _restoring;
    private bool _sessionReady;
    private bool _selectingTab;
    private readonly List<ClosedTab> _closedTabs = [];
    private DispatcherQueueTimer? _startupTimer;
    private string _testPage = string.Empty;
    private string _currentAddress => _activeTab?.Address ?? "";
    private string _currentTitle => _activeTab?.Title ?? "";
    private App BrowserApp => (App)Application.Current;
    private BrowserPreferences Preferences => BrowserApp.Preferences;
    private bool _bookmarksAvailable = true;
    private bool _closing;
    private long _lastClickTime;
    private Point _lastClickPosition;
    private MouseButtonType _lastButton;
    private int _clickCount;

    private readonly TabTransfer? _transfer;

    internal MainWindow(bool isPrivate = false, bool restore = false, TabTransfer? transfer = null, RectInt32? bounds = null)
    {
        InitializeComponent();
        // One look for every button of the chrome (no plate at rest or disabled, the same faint one on hover) and one elevation for
        // everything that floats above the page. Done before the first layout, so the templates pick the resources up.
        foreach (FrameworkElement bar in new FrameworkElement[] { Sidebar, TabStripHost, VerticalTitleBar, Chrome, ManagementPanel, FindPanel, MenuBorder, SuggestPanel })
            bar.Resources.MergedDictionaries.Add(Motion.Fx.SubtleButtons());
        VerticalTabs.Resources.MergedDictionaries.Add(Motion.Fx.TabListItems());
        foreach (UIElement surface in new UIElement[] { MenuBorder, ManagementPanel, FindPanel, SuggestPanel, TabCard })
            surface.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _privateWindow = isPrivate;
        _restoreWindow = restore;
        _transfer = transfer;
        ExtendsContentIntoTitleBar = true;
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        WireTabStrip();
        VerticalTabs.ItemsSource = _tabs;
        RootGrid.SizeChanged += (_, _) => UpdateDragRegions();
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;
        RootGrid.ActualThemeChanged += (_, _) => { ApplyPalette(); UpdateGlassRegions(); ApplyPageThemes(); };
        if (bounds is { } placed) AppWindow.MoveAndResize(placed);
        else AppWindow.Resize(new SizeInt32(1240, 800));
        RootGrid.Loaded += RootGrid_Loaded;
        BrowserSurface.SizeChanged += (_, _) => { ResizeBrowser(); ResizeManagement(); };
        BrowserSurface.CompositionScaleChanged += (_, _) => ResizeBrowser();
        Closed += MainWindow_Closed;
        AppWindow.Closing += AppWindow_Closing;
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        LoadBookmarks();
        WireInteractiveEffects();

        try
        {
            BrowserApp.InitializeEngine();
            _testPage = LoadTestPage();
            if (WebEngine.UseWebView2)
            {
                // WebView2 draws itself: no GPU compositor, no input overlay, and the "no sandbox" warning does not apply.
                WebInput.Visibility = Visibility.Collapsed;
                SandboxBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                _compositor = new GpuCompositor(BrowserSurface);
                _compositor.RenderingFailed += exception => OnUi(() => ShowGpuFailure("Error de presentación GPU", exception.Message));
            }
            ApplyPreferences();
            _restoring = true;
            if (_transfer is { } moved)
            {
                // A tab dragged out of another window: reopened here with the state that is cheap to carry over.
                BrowserTab tab = AddTab(moved.Address, activate: true, savedTitle: moved.Title);
                tab.Pinned = moved.Pinned;
                tab.Muted = moved.Muted;
                tab.Zoom = moved.Zoom;
            }
            if (_restoreWindow && (Preferences.RestoreSession || Program.RestoreRequested) && BrowserApp.SavedTabs.Count > 0)
                foreach (SavedTab saved in BrowserApp.SavedTabs)
                {
                    BrowserTab tab = AddTab(saved.Address, activate: false, deferred: true, savedTitle: saved.Title);
                    tab.Pinned = saved.Pinned;
                }
            if (_restoreWindow && Preferences.StartupUseCustomPages)
                foreach (string address in Preferences.StartupPages.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddTab(address, activate: false);
            List<string> external = _restoreWindow || _privateWindow ? Program.TakePendingAddresses() : [];
            if (_tabs.Count == 0 && external.Count == 0) AddTab();
            else if (_tabs.Count > 0) SelectTab(_tabs[0]);
            for (int i = 0; i < external.Count; i++) AddTab(external[i], activate: i == external.Count - 1);
            _restoring = false;
            _sessionReady = true;
            HorizontalTabs.AddEnabled = true;
            VerticalNewTabButton.IsEnabled = true;
            PersistTabs();
            ResizeBrowser();
            _startupTimer = DispatcherQueue.CreateTimer();
            _startupTimer.Interval = TimeSpan.FromSeconds(15);
            _startupTimer.IsRepeating = false;
            _startupTimer.Tick += (_, _) =>
            {
                if (_activeTab is { HasFrame: false } tab && !ViewportLayout.IsInternal(tab.Address))
                    ShowError("Chromium no produjo ningún fotograma", "Comprueba la GPU, sus controladores y el registro de CEF.");
            };
            if (_activeTab is { } active && !ViewportLayout.IsInternal(active.Address)) _startupTimer.Start();
            StartPerformanceTimer();
#if DEBUG
            StartHoverTest();
            StartTabScriptTest();
            StartUiScript();
#endif
            if (BrowserApp.ProfileError is { } error) ShowError("No se cargaron todos los datos del perfil", error);
            if (_restoreWindow) Diagnostics.AppLog.Info($"Arranque: ventana lista a los {Program.SinceStart()} ms");
        }
        catch (Exception exception)
        {
            StartupText.Visibility = Visibility.Collapsed;
            ShowError("No se pudo iniciar el navegador de prueba", exception.Message);
        }
    }

    private static string LoadTestPage()
    {
        using Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream("FluxBrowser.Assets.glass-test.html")
            ?? throw new InvalidOperationException("No se encontró la página local de diagnóstico.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return "data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(reader.ReadToEnd()));
    }

    private void ResizeBrowser()
    {
        if ((_compositor is null && !WebEngine.UseWebView2) || _closing || BrowserSurface.ActualWidth <= 0 || BrowserSurface.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            float scale = (float)(BrowserSurface.XamlRoot?.RasterizationScale ?? 1);
            _compositor?.Resize(BrowserSurface.ActualWidth, BrowserSurface.ActualHeight, scale, ToolbarHeight);
            Point origin = BrowserSurface.TransformToVisual(RootGrid).TransformPoint(new Point());
            foreach (BrowserTab tab in _tabs) LayoutTab(tab, origin, scale);
            UpdateGlassRegions();
        }
        catch (Exception exception)
        {
            ShowError("No se pudo actualizar la superficie gráfica", exception.Message);
        }
    }

    private void LayoutTab(BrowserTab tab, Point origin, float scale)
    {
        double height = BrowserSurface.ActualHeight;
        tab.TopInset = tab.FullViewport ? (WebEngine.UseWebView2 ? 0 : ViewportLayout.RevealInset(ToolbarHeight, tab.ScrollY * tab.Zoom)) : ToolbarHeight;
        tab.SessionIfCreated?.Resize(BrowserSurface.ActualWidth, Math.Max(1, tab.FullViewport ? height : height - ToolbarHeight),
            scale, origin.X, origin.Y + tab.TopInset);
        tab.Source?.SetLayout(tab.FullViewport, tab.TopInset);
        tab.SessionIfCreated?.SetLayout(tab.TopInset, tab.FullViewport, ToolbarHeight);
        // Glass rectangles are in page coordinates, which move with the unrevealed gap.
        if (tab == _activeTab && tab.SessionIfCreated is { IsOffscreen: false }) UpdateGlassRegions();
    }

    /// <summary>What the address box shows: only the domain while it is not being edited, the full address once it has the focus.</summary>
    private string AddressText(string address, bool full)
    {
        if (address == "browser:home") return "";
        if (address == _testPage) return "browser:diagnostics";
        if (!full && WebAddress.IsHttp(address) && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0)
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return address;
    }

    private void UpdateAddress(string address, bool force = false)
    {
        if (force || AddressBox.FocusState == FocusState.Unfocused)
            SetAddressText(AddressText(address, AddressBox.FocusState != FocusState.Unfocused));
        UpdateZoomChip();
        SiteInfoIcon.Glyph = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "\uE72E"
            : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "\uE7BA" : "\uE721";
        ToolTipService.SetToolTip(SiteInfoIcon, address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "La conexión es segura"
            : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "No es seguro" : "Buscar o escribir una dirección");
        UpdateBookmarkButton();
    }

    private void LoadBookmarks()
    {
        try
        {
            _bookmarks.Clear();
            while (BookmarksPanel.Children.Count > 2) BookmarksPanel.Children.RemoveAt(2);
            _bookmarks.AddRange(_bookmarkStore.Load());
            _bookmarksAvailable = true;
            foreach (Bookmark bookmark in _bookmarks.Where(b => b.Folder.Length == 0))
            {
                AddBookmarkButton(bookmark);
            }
            BookmarksEmpty.Visibility = _bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _bookmarksAvailable = false;
            ShowError("No se pudieron cargar los marcadores", exception.Message);
        }
    }

    private void AddBookmarkButton(Bookmark bookmark)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        FrameworkElement icon = bookmark.IsFolder ? FolderIcon() : SiteIcon(FaviconLoader.Address(bookmark.Favicon, bookmark.Address));
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = bookmark.Title, FontSize = 12, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 7, 2), MinHeight = 24, Height = 26, MaxWidth = 180,
            FontSize = 12, CornerRadius = new CornerRadius(Motion.Fx.ControlRadius)
        };
        Motion.Fx.Icon(button, Motion.IconMotion.Pop, icon);
        ToolTipService.SetToolTip(button, bookmark.IsFolder ? bookmark.Title : $"{bookmark.Title}\n{bookmark.Address}");
        AutomationProperties.SetName(button, $"Abrir marcador {bookmark.Title}");
        button.Click += (_, _) =>
        {
            if (bookmark.IsFolder) ShowBookmarkFolder(bookmark.FolderPath,
                button.TransformToVisual(PageGrid).TransformPoint(new Point(0, button.ActualHeight)));
            else NavigateCurrent(bookmark.Address);
        };
        button.RightTapped += (_, args) => { ShowBookmarkMenu(bookmark, args.GetPosition(PageGrid)); args.Handled = true; };
        BookmarksPanel.Children.Add(button);
    }

    private void UpdateBookmarkButton()
    {
        BookmarkButton.IsEnabled = _bookmarksAvailable &&
            Uri.TryCreate(_currentAddress, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            string.IsNullOrEmpty(uri.UserInfo);
        bool saved = _bookmarks.Any(b => !b.IsFolder && string.Equals(b.Address, _currentAddress, StringComparison.Ordinal));
        BookmarkIcon.Glyph = saved ? "\uE735" : "\uE734";
        if (saved) BookmarkIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
        else BookmarkIcon.ClearValue(FontIcon.ForegroundProperty);
        ToolTipService.SetToolTip(BookmarkButton, saved ? "Editar favorito para esta página (Ctrl+D)" : "Agregar esta página a favoritos (Ctrl+D)");
    }

    private void BookmarkButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_bookmarksAvailable || !WebAddress.IsHttp(_currentAddress) ||
            !Uri.TryCreate(_currentAddress, UriKind.Absolute, out Uri? uri))
        {
            ShowError("No se pudo añadir el marcador", "La dirección actual no es válida.");
            return;
        }

        if (_bookmarks.FirstOrDefault(bookmark => !bookmark.IsFolder && string.Equals(bookmark.Address, uri.AbsoluteUri, StringComparison.Ordinal)) is { } existing)
        {
            EditBookmark(existing);
            return;
        }

        string title = string.IsNullOrWhiteSpace(_currentTitle) ? uri.Host : _currentTitle;
        var bookmark = new Bookmark(title, uri.AbsoluteUri, Favicon: _activeTab?.Favicon);
        try
        {
            List<Bookmark> updated = _bookmarkStore.Load();
            if (!updated.Any(b => b.Address == bookmark.Address)) updated.Add(bookmark);
            _bookmarkStore.Save(updated);
            BrowserApp.RefreshBookmarks();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowError("No se pudo guardar el marcador", exception.Message);
        }
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            HideSuggestions();
            UpdateAddress(_currentAddress, force: true);
            AddressBox.SelectAll();
            args.Handled = true;
            return;
        }

        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            if (!AcceptSuggestion()) { HideSuggestions(); NavigateAddress(); }
        }
    }

    private void GoButton_Click(object sender, RoutedEventArgs args) => NavigateCurrent(Preferences.HomePage);

    private void NavigateAddress()
    {
        if (ViewportLayout.IsInternal(AddressBox.Text.Trim())) { NavigateCurrent(AddressBox.Text.Trim()); return; }
        if (AddressBox.Text.Trim() == "browser:diagnostics") { NavigateCurrent(_testPage); return; }
        if (!AddressResolver.TryResolve(AddressBox.Text, out string address, out string error, Preferences.SearchEngine))
        {
            ShowError("Dirección no válida", error);
            return;
        }

        NavigateCurrent(address);
        FocusPage();
    }

    private void BackButton_Click(object sender, RoutedEventArgs args) => _web?.Back();
    private void ForwardButton_Click(object sender, RoutedEventArgs args) => _web?.Forward();
    private void ReloadButton_Click(object sender, RoutedEventArgs args)
    {
        if (_activeTab?.IsLoading == true) _web?.Stop();
        else ReloadCurrent();
    }
    private void ReloadCurrent()
    {
        if (_currentAddress == "browser:home") BuildNewTab();
        else if (!ViewportLayout.IsInternal(_currentAddress)) _web?.Reload();
    }
    private void TestPageButton_Click(object sender, RoutedEventArgs args) => NavigateCurrent(_testPage);
    private void TestScrollButton_Click(object sender, RoutedEventArgs args)
    {
        if (_currentAddress != _testPage)
        {
            ShowError("Prueba no disponible", "Abre primero la página local de prueba.");
            return;
        }

        IBrowserHost? host = _browser?.Host;
        if (host is null)
        {
            ShowError("Prueba no disponible", "Chromium todavía no está preparado.");
            return;
        }

        var point = new MouseEvent((int)(WebInput.ActualWidth / 2), (int)(WebInput.ActualHeight / 2), CefEventFlags.None);
        host.SendMouseMoveEvent(point, false);
        host.SendMouseWheelEvent(point, 0, -360);
    }

    private void WebInput_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        IBrowserHost? host = _browser?.Host;
        if (host is null)
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(WebInput);
        MouseButtonType? button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButtonType.Left,
            PointerUpdateKind.MiddleButtonPressed => MouseButtonType.Middle,
            PointerUpdateKind.RightButtonPressed => MouseButtonType.Right,
            _ => null
        };
        if (button is null)
        {
            return;
        }

        long now = Environment.TickCount64;
        bool repeated = button == _lastButton && now - _lastClickTime <= 500 &&
            Math.Abs(point.Position.X - _lastClickPosition.X) <= 4 &&
            Math.Abs(point.Position.Y - _lastClickPosition.Y) <= 4;
        _clickCount = repeated ? Math.Min(_clickCount + 1, 3) : 1;
        _lastButton = button.Value;
        _lastClickTime = now;
        _lastClickPosition = point.Position;

        WebInput.Focus(FocusState.Pointer);
        // GotFocus doesn't fire again when WebInput already had focus (e.g. after switching tabs), so focus CEF explicitly.
        host.SetFocus(true);
        WebInput.CapturePointer(args.Pointer);
        host.SendMouseClickEvent(ToMouseEvent(point), button.Value, false, _clickCount);
        args.Handled = true;
    }

    private void WebInput_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        IBrowserHost? host = _browser?.Host;
        if (host is null)
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(WebInput);
        MouseButtonType? button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => MouseButtonType.Left,
            PointerUpdateKind.MiddleButtonReleased => MouseButtonType.Middle,
            PointerUpdateKind.RightButtonReleased => MouseButtonType.Right,
            _ => null
        };
        if (button is not null)
        {
            host.SendMouseClickEvent(ToMouseEvent(point), button.Value, true, _clickCount);
            WebInput.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
    }

    private void WebInput_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        _browser?.Host?.SendMouseMoveEvent(ToMouseEvent(args.GetCurrentPoint(WebInput)), false);
    }

    private void WebInput_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _browser?.Host?.SendMouseMoveEvent(ToMouseEvent(args.GetCurrentPoint(WebInput)), true);
    }

    private void WebInput_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        IBrowserHost? host = _browser?.Host;
        if (host is null)
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(WebInput);
        int delta = point.Properties.MouseWheelDelta;
        MouseEvent wheelEvent = ToMouseEvent(point);
        // Like Edge: wheel notches use Chromium's animated scroll; precision touchpads scroll pixel-for-pixel.
        if (point.PointerDeviceType == PointerDeviceType.Touchpad)
        {
            wheelEvent = new MouseEvent(wheelEvent.X, wheelEvent.Y, wheelEvent.Modifiers | PrecisionScrollingDelta);
        }
        host.SendMouseWheelEvent(wheelEvent,
            point.Properties.IsHorizontalMouseWheel ? delta : 0,
            point.Properties.IsHorizontalMouseWheel ? 0 : delta);
        args.Handled = true;
    }

    // CEF_EVENTFLAG_PRECISION_SCROLLING_DELTA; CefSharp 154 does not name it yet.
    private const CefEventFlags PrecisionScrollingDelta = (CefEventFlags)(1 << 14);

    private MouseEvent ToMouseEvent(PointerPoint point)
    {
        CefEventFlags flags = GetKeyboardModifiers();
        if (point.Properties.IsLeftButtonPressed) flags |= CefEventFlags.LeftMouseButton;
        if (point.Properties.IsMiddleButtonPressed) flags |= CefEventFlags.MiddleMouseButton;
        if (point.Properties.IsRightButtonPressed) flags |= CefEventFlags.RightMouseButton;
        float scale = (float)(BrowserSurface.XamlRoot?.RasterizationScale ?? 1);
        double inset = _activeTab?.Source?.PresentedTopInset(scale) ?? 0;
        return new MouseEvent((int)point.Position.X, (int)(point.Position.Y - inset), flags);
    }

    private void WebInput_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || HandleShortcut(args)) return;
        CefEventFlags modifiers = GetKeyboardModifiers();
        if (args.Key == VirtualKey.L && modifiers.HasFlag(CefEventFlags.ControlDown))
        {
            AddressBox.Focus(FocusState.Programmatic);
            AddressBox.SelectAll();
            args.Handled = true;
            return;
        }

        if (args.Key == VirtualKey.F5)
        {
            _web?.Reload();
            args.Handled = true;
            return;
        }

        SendKey(args, KeyEventType.RawKeyDown, modifiers);
    }

    private void WebInput_PreviewKeyUp(object sender, KeyRoutedEventArgs args) =>
        SendKey(args, KeyEventType.KeyUp, GetKeyboardModifiers());

    private void SendKey(KeyRoutedEventArgs args, KeyEventType type, CefEventFlags modifiers)
    {
        IBrowserHost? host = _browser?.Host;
        if (host is null)
        {
            return;
        }

        host.SendKeyEvent(new KeyEvent
        {
            Type = type,
            WindowsKeyCode = (int)args.Key,
            NativeKeyCode = GetNativeKeyCode(args.KeyStatus, type == KeyEventType.KeyUp),
            Modifiers = modifiers,
            IsSystemKey = modifiers.HasFlag(CefEventFlags.AltDown)
        });
        // Handling a key down suppresses WinUI's CharacterReceived, which Chromium needs to insert text.
        // Only keys that XAML would otherwise use for focus navigation are consumed here.
        if (type == KeyEventType.KeyUp || args.Key is VirtualKey.Tab or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up
            or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Escape)
            args.Handled = true;
    }

    private void WebInput_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        IBrowserHost? host = _browser?.Host;
        if (host is null)
        {
            return;
        }

        host.SendKeyEvent(new KeyEvent
        {
            Type = KeyEventType.Char,
            WindowsKeyCode = args.Character,
            NativeKeyCode = GetNativeKeyCode(args.KeyStatus, false),
            Modifiers = GetKeyboardModifiers()
        });
        args.Handled = true;
    }

    private static int GetNativeKeyCode(CorePhysicalKeyStatus status, bool released)
    {
        int code = (int)(status.RepeatCount | (status.ScanCode << 16));
        if (status.IsExtendedKey) code |= 1 << 24;
        if (status.WasKeyDown) code |= 1 << 30;
        if (released) code |= unchecked((int)0x80000000);
        return code;
    }

    private static CefEventFlags GetKeyboardModifiers()
    {
        CefEventFlags flags = CefEventFlags.None;
        if (IsDown(VirtualKey.Shift)) flags |= CefEventFlags.ShiftDown;
        if (IsDown(VirtualKey.Control)) flags |= CefEventFlags.ControlDown;
        if (IsDown(VirtualKey.Menu)) flags |= CefEventFlags.AltDown;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.CapitalLock).HasFlag(CoreVirtualKeyStates.Locked))
            flags |= CefEventFlags.CapsLockOn;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.NumberKeyLock).HasFlag(CoreVirtualKeyStates.Locked))
            flags |= CefEventFlags.NumLockOn;
        return flags;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void WebInput_GotFocus(object sender, RoutedEventArgs args) => _browser?.Host?.SetFocus(true);
    private void WebInput_LostFocus(object sender, RoutedEventArgs args) => _browser?.Host?.SetFocus(false);

    private void OnUi(Action action)
    {
        if (!_closing && !DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) action();
        }))
        {
            Debug.WriteLine("No se pudo enviar una actualización a la interfaz.");
        }
    }

    internal void ReportProblem(string title, string message) => ShowError(title, message);

    /// <summary>Opens addresses handed over by another process (command line or a second launch); the last one is shown.</summary>
    internal void OpenExternal(IReadOnlyList<string> addresses)
    {
        for (int i = 0; i < addresses.Count; i++) TryAddTab(addresses[i], activate: i == addresses.Count - 1);
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.ActionButton = null;
        Debug.WriteLine($"{title}: {message}");
        _startupTimer?.Stop();
        StartupText.Visibility = Visibility.Collapsed;
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    /// <summary>
    /// A lost graphics device cannot be rebuilt under live Chromium surfaces, so the recovery is a clean restart:
    /// the normal-window tabs are saved and reopened by the new process.
    /// </summary>
    private void ShowGpuFailure(string title, string message)
    {
        ShowError(title, message + " Reinicia el navegador para recuperar los gráficos; se conservarán las pestañas normales.");
        var restart = new Button { Content = "Reiniciar navegador" };
        restart.Click += (_, _) => BrowserApp.RestartBrowser();
        ErrorBar.ActionButton = restart;
    }

    internal void CloseForRestart()
    {
        _closeApproved = true;
        Close();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        PersistTabs();
        _closing = true;
        EndDrag();
        StopMotion();
        _startupTimer?.Stop();
        _performanceTimer?.Stop();
        _benchmarkTimer?.Stop();
        HideMenu();
        foreach (BrowserTab tab in _tabs) tab.Dispose();
        _tabs.Clear();
        _compositor?.Dispose();
    }
}
