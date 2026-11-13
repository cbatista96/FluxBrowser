using CefSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyOwnBrowser.Browser;
using MyOwnBrowser.Rendering;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Point = Windows.Foundation.Point;

namespace MyOwnBrowser;

/// <summary>What travels with a tab dragged out into a window of its own: the page is reopened, so only cheap state is carried.</summary>
internal sealed record TabTransfer(string Address, string Title, bool Pinned, bool Muted, double Zoom);

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _performanceTimer;
    private DispatcherQueueTimer? _benchmarkTimer;
    private long _lastPerformanceFrames;
    private long _lastPerformanceTime;
    private double ToolbarHeight => 36 + 42 + (Preferences.ShowBookmarks ? 30 : 0);

    private BrowserTab? TryAddTab(string? address = null, bool activate = true, bool? isPrivate = null)
    {
        if (!_sessionReady || (_compositor is null && !WebEngine.UseWebView2))
        {
            ShowError("No se pudo abrir la pestaña", "El navegador no terminó de iniciarse. Cierra las demás versiones abiertas y vuelve a intentarlo.");
            return null;
        }
        try { return AddTab(address, activate, isPrivate); }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or IOException)
        {
            ShowError("No se pudo abrir la pestaña", exception.Message);
            return null;
        }
    }

    private void OpenAnotherWindow(bool isPrivate = false)
    {
        if (!_sessionReady)
        {
            ShowError("No se pudo abrir la ventana", "El navegador no terminó de iniciarse. Cierra las demás versiones abiertas y vuelve a intentarlo.");
            return;
        }
        BrowserApp.OpenWindow(isPrivate);
    }

    private BrowserTab AddTab(string? address = null, bool activate = true, bool? isPrivate = null, bool deferred = false, string? savedTitle = null)
    {
        if (_compositor is null && !WebEngine.UseWebView2) throw new InvalidOperationException("La superficie gráfica no está preparada.");
        bool privateTab = isPrivate ?? _privateWindow;
        address ??= Preferences.NewTabPage;
        string initial = ViewportLayout.IsInternal(address) ? "about:blank" : address;
        GpuCompositor.FrameSource? source = _compositor?.CreateSource();
        // The Chromium session (a renderer process, its textures and memory) is created on first use: a restored background tab
        // costs nothing until it is selected.
        var tab = new BrowserTab(t => CreateSession(t, initial, privateTab), AfterSessionCreated, source, privateTab)
        {
            Address = address, Zoom = Preferences.DefaultZoom, Title = InitialTitle(address, savedTitle)
        };
        BrowserTab? previousTab = _activeTab;
        try
        {
            _tabs.Add(tab);
            if (activate || _activeTab is null) SelectTab(tab);
            if (!deferred) _ = tab.Session;
            ResizeBrowser();
        }
        catch
        {
            _tabs.Remove(tab);
            if (_activeTab == tab) _activeTab = null;
            tab.Dispose();
            if (previousTab is not null && _tabs.Contains(previousTab)) SelectTab(previousTab);
            throw;
        }
        PersistTabs();
        return tab;
    }

    private static string InitialTitle(string address, string? savedTitle) =>
        address == "browser:settings" ? "Configuración" :
        ViewportLayout.IsInternal(address) || !WebAddress.IsHttp(address) ? "Nueva pestaña" :
        !string.IsNullOrWhiteSpace(savedTitle) ? savedTitle : new Uri(address).Host;

    private void AfterSessionCreated(BrowserTab tab)
    {
        ResizeBrowser();
        tab.Session.Start();
    }

    private IWebSession CreateSession(BrowserTab tab, string initial, bool privateTab)
    {
        IWebSession session = WebEngine.UseWebView2
            ? new WebView2Session(WebHost, initial, _testPage, privateTab, () => Preferences.DownloadDirectory, () => Preferences.AskDownloadLocation, Preferences)
            : new BrowserSession(_windowHandle, tab.Source!, initial, _testPage, privateTab, Preferences.FrameRate, () => Preferences.DownloadDirectory,
                () => Preferences.AskDownloadLocation, Preferences);
        session.FirstFrame += () => OnUi(() =>
        {
            if (!_tabs.Contains(tab)) return;
            tab.HasFrame = true;
            if (_activeTab == tab) { _startupTimer?.Stop(); StartupText.Visibility = Visibility.Collapsed; }
            LogFirstPage();
        });
        session.Ready += () => OnUi(() =>
        {
            if (!_tabs.Contains(tab)) return;
            tab.Ready = true;
            session.SetZoom(tab.Zoom);
            session.SetMuted(tab.Muted);
            RefreshZoom(tab);
            SetPageTheme(tab);
            session.SetVisible(!ViewportLayout.IsInternal(tab.Address) && (_activeTab == tab || !Preferences.PauseHiddenTabs));
            if (_activeTab == tab) UpdateTabChrome();
        });
        session.AddressChanged += value => OnUi(() =>
        {
            if (!_tabs.Contains(tab)) return;
            if (value == "about:blank")
            {
                if (tab.HasNavigatedWeb && !ViewportLayout.IsInternal(tab.Address))
                {
                    tab.Address = "browser:home";
                    tab.Title = "Nueva pestaña";
                    tab.Session.SetVisible(false);
                    if (_activeTab == tab) { ShowInternalPage(tab); UpdateTabChrome(); }
                    PersistTabs();
                }
                return;
            }
            bool wasInternal = ViewportLayout.IsInternal(tab.Address);
            tab.HasNavigatedWeb = true;
            tab.Address = value;
            tab.Session.SetVisible(_activeTab == tab || !Preferences.PauseHiddenTabs);
            if (value == _testPage) tab.Title = "Diagnóstico";
            if (_activeTab == tab) UpdateAddress(tab.Address);
            if (wasInternal && _activeTab == tab) ShowInternalPage(tab);
            PersistTabs();
        });
        session.TitleChanged += value => OnUi(() =>
        {
            if (!_tabs.Contains(tab)) return;
            if (ViewportLayout.IsInternal(tab.Address)) return;
            tab.Title = value == "about:blank" || string.IsNullOrWhiteSpace(value) ? "Nueva pestaña" : value;
            if (VerticalTabs.ContainerFromItem(tab) is DependencyObject container)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, tab.Title);
            if (_activeTab == tab) UpdateTabChrome();
        });
        session.LoadingStateChanged += (loading, back, forward) => OnUi(() =>
        {
            if (!_tabs.Contains(tab)) return;
            tab.IsLoading = loading;
            tab.CanGoBack = back;
            tab.CanGoForward = forward;
            if (_activeTab == tab) UpdateTabChrome();
            if (!loading && !ViewportLayout.IsInternal(tab.Address) && WantsFullViewport(tab, tab.DocumentHeight) != tab.FullViewport)
            {
                // The page has settled: now (and only now) a short one may drop below the bars.
                tab.FullViewport = !tab.FullViewport;
                ResizeBrowser();
            }
            if (!loading && !tab.IsPrivate && Preferences.RememberHistory && BrowserApp.HistoryAvailable && WebAddress.IsHttp(tab.Address))
                SaveVisit(tab);
            if (!loading && WebAddress.IsHttp(tab.Address)) { RefreshTabIcon(tab); if (_activeTab == tab) ScheduleThumbnail(tab, 1200); }
        });
        session.Error += (title, message) => OnUi(() =>
        {
            if (!_tabs.Contains(tab)) return;
            if (title == BrowserSession.GpuErrorTitle) ShowGpuFailure(title, message); else ShowError(title, message);
        });
        session.CursorChanged += cursor => OnUi(() => { if (_activeTab == tab) WebInput.UpdateCursor(cursor); });
        session.ShortcutPressed += shortcut => OnUi(() => { if (_activeTab == tab) HandleWebShortcut(shortcut); });
        session.NativeMenuRequested += menu => OnUi(() =>
        {
            if (_activeTab == tab) ShowNativeMenu(tab, menu);
            else menu.Complete(null);
        });
        session.NewTabRequested += (url, selected) => OnUi(() =>
        {
            if (_tabs.Contains(tab)) TryAddTab(url, selected, tab.IsPrivate);
        });
        session.ContextMenuRequested += request => OnUi(() =>
        {
            if (_activeTab == tab) ShowWebMenu(tab, request);
            else { using (request.Callback) request.Callback.Cancel(); }
        });
        session.DownloadChanged += progress => OnUi(() => UpdateDownload(tab, progress));
        session.AudioChanged += playing => OnUi(() => { if (_tabs.Contains(tab)) tab.Audible = playing; });
        session.FindResult += (active, total) => OnUi(() => OnFindResult(tab, active, total));
        session.FaviconChanged += url => OnUi(() =>
        {
            if (_tabs.Contains(tab) && !ViewportLayout.IsInternal(tab.Address))
            {
                tab.Favicon = url;
                RefreshTabIcon(tab);
                UpdateSavedFavicon(tab);
            }
        });
        session.ScrollChanged += (y, height) => OnUi(() =>
        {
            if (!_tabs.Contains(tab) || ViewportLayout.IsInternal(tab.Address)) return;
            tab.ScrollY = y;
            tab.DocumentHeight = height;
            if (_activeTab == tab) ScheduleThumbnail(tab, 1500);
            bool full = WantsFullViewport(tab, height);
            if (full != tab.FullViewport)
            {
                tab.FullViewport = full;
                ResizeBrowser();
            }
            // Only the off-screen engine reveals the page progressively; WebView2 keeps its gap inside the page.
            else if (full && _compositor is not null && Math.Abs(ViewportLayout.RevealInset(ToolbarHeight, y * tab.Zoom) - tab.TopInset) >= 1)
                LayoutTab(tab, BrowserSurface.TransformToVisual(RootGrid).TransformPoint(new Point()),
                    (float)(BrowserSurface.XamlRoot?.RasterizationScale ?? 1));
        });
        return session;
    }

    /// <summary>
    /// Whether a page should fill the window (content scrolling under the bars) or sit below them. While it is still loading its height keeps
    /// growing, so the window never shrinks the view then: that made every navigation flash as the layout flipped twice.
    /// </summary>
    private bool WantsFullViewport(BrowserTab tab, double documentHeight)
    {
        bool full = WebEngine.UseWebView2
            ? ViewportLayout.UsesFullViewportWithGap(ToolbarHeight, BrowserSurface.ActualHeight, documentHeight * tab.Zoom, tab.FullViewport)
            : ViewportLayout.UsesFullViewport(ToolbarHeight, BrowserSurface.ActualHeight, documentHeight * tab.Zoom, tab.FullViewport);
        return full || (tab.IsLoading && tab.FullViewport);
    }

    private void SelectTab(BrowserTab tab)
    {
        if (_selectingTab || !_tabs.Contains(tab)) return;
        _selectingTab = true;
        try
        {
            bool switchingTabs = _activeTab != tab;
            _benchmarkTimer?.Stop();
            _benchmarkTimer = null;
            HideMenu();
            CloseFind();
            _activeTab?.SessionIfCreated?.SetFocus(false);
            _activeTab = tab;
            _ = tab.Session; // first selection of a restored tab starts its Chromium session
            if (tab.Source is { } frameSource) _compositor?.Select(frameSource);
            ScheduleThumbnail(tab, 1500);
            foreach (BrowserTab item in _tabs)
                item.SessionIfCreated?.SetVisible(!ViewportLayout.IsInternal(item.Address) && (item == tab || !Preferences.PauseHiddenTabs));
            HorizontalTabs.SelectedItem = tab;
            VerticalTabs.SelectedItem = tab;
            _lastPerformanceFrames = _compositor?.GetPerformance().Frames ?? 0;
            _lastPerformanceTime = Environment.TickCount64;
            StartupText.Visibility = tab.HasFrame || ViewportLayout.IsInternal(tab.Address) ? Visibility.Collapsed : Visibility.Visible;
            UpdateTabChrome(forceAddress: switchingTabs);
            ShowInternalPage(tab);
            RefreshZoom(tab);
            if (WebInput.FocusState != FocusState.Unfocused) tab.Session.SetFocus(true);
        }
        finally { _selectingTab = false; }
    }

    private async void RefreshZoom(BrowserTab tab)
    {
        if (!tab.Ready) return;
        try
        {
            double zoom = await tab.Session.GetZoomAsync();
            if (_tabs.Contains(tab) && !_closing) tab.Zoom = zoom;
        }
        catch (Exception e)
        {
            if (_tabs.Contains(tab) && !_closing) ShowError("No se pudo consultar el zoom", e.Message);
        }
    }

    private void UpdateTabChrome(bool forceAddress = false)
    {
        if (_activeTab is not { } tab) return;
        BackButton.IsEnabled = tab.CanGoBack;
        ForwardButton.IsEnabled = tab.CanGoForward;
        ForwardButton.Visibility = tab.CanGoForward ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.IsEnabled = AddressBox.IsEnabled = GoButton.IsEnabled = TestPageButton.IsEnabled = TestScrollButton.IsEnabled = tab.Ready;
        // The same icon element keeps its running animation; only the glyph changes.
        string reloadGlyph = tab.IsLoading ? "" : "";
        if (ReloadIcon.Glyph != reloadGlyph) ReloadIcon.Glyph = reloadGlyph;
        SetLoadingIndicator(tab.IsLoading);
        ToolTipService.SetToolTip(ReloadButton, tab.IsLoading ? "Detener (Esc)" : "Actualizar (Ctrl+R)");
        Title = $"{tab.Title} — My Own Browser" + (tab.IsPrivate ? (WebEngine.UseWebView2 ? " (privada)" : " (privada, sin sandbox)") : "");
        VerticalWindowTitle.Text = tab.Title;
        ModeLabel.Text = tab.IsPrivate ? (WebEngine.UseWebView2 ? "PRIVADA" : "PRIVADA · SIN SANDBOX") : (WebEngine.UseWebView2 ? "VISTA PREVIA" : "LABORATORIO");
        TestScrollButton.Visibility = tab.Address == _testPage ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddress(tab.Address, forceAddress);
    }

    private async void CloseTab(BrowserTab tab)
    {
        if (tab.Pinned || !_tabs.Contains(tab) || !_closingTabs.Add(tab)) return;
        HideMenu();
        HideTabCard();
        // The horizontal strip animates a removed tab by itself; only the vertical list needs the fade here.
        FrameworkElement? container = Preferences.VerticalTabs ? VerticalTabs.ContainerFromItem(tab) as FrameworkElement : null;
        if (container is not null) await AnimateSurface(container, false);
        _closingTabs.Remove(tab);
        if (_closing || !_tabs.Contains(tab)) return;
        if (tab.Pinned) { if (container is not null) await AnimateSurface(container, true); return; }
        int index = _tabs.IndexOf(tab);
        if (!tab.IsPrivate)
        {
            _closedTabs.Insert(0, new ClosedTab(tab.Address, tab.Pinned, tab.Title, tab.Favicon, DateTimeOffset.Now));
            if (_closedTabs.Count > 25) _closedTabs.RemoveAt(25);
        }
        bool wasActive = tab == _activeTab;
        _tabs.Remove(tab);
        if (wasActive) _activeTab = null;
        tab.Dispose();
        // Closing the last tab closes the window (and the browser, if it was the last window), like Edge and Chrome.
        if (_tabs.Count == 0) { _closeApproved = true; Close(); return; }
        if (wasActive) SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        PersistTabs();
    }

    private void ReopenTab()
    {
        if (_closedTabs.Count > 0) ReopenTab(_closedTabs[0]);
    }

    private void ReopenTab(ClosedTab closed)
    {
        BrowserTab? tab = TryAddTab(closed.Address);
        if (tab is null) return;
        _closedTabs.Remove(closed);
        tab.Pinned = closed.Pinned;
    }

    internal bool CanPersistSession => _sessionReady && !_restoring && !_closing && !_privateWindow;
    internal List<SavedTab> SnapshotTabs() => !CanPersistSession ? [] : _tabs.Where(t => !t.IsPrivate)
        .Select(t => new SavedTab(WebAddress.IsHttp(t.Address) || ViewportLayout.IsInternal(t.Address) ? t.Address : "browser:home", t.Pinned,
            WebAddress.IsHttp(t.Address) && t.Title.Length > 0 ? t.Title[..Math.Min(t.Title.Length, 200)] : null)).ToList();

    internal void PersistTabs()
    {
        if (!CanPersistSession || !BrowserApp.SessionAvailable) return;
        try { BrowserApp.PersistSession(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError("No se pudo guardar la sesión", e.Message); }
    }

    private void SaveVisit(BrowserTab tab)
    {
        List<HistoryEntry> history = BrowserApp.History;
        if (history.Count > 0 && history[0].Address == tab.Address && DateTimeOffset.Now - history[0].Visited < TimeSpan.FromSeconds(3)) return;
        var entry = new HistoryEntry(tab.Address, tab.Title, DateTimeOffset.Now, tab.Favicon);
        history.Insert(0, entry);
        if (history.Count > 2000) history.RemoveRange(2000, history.Count - 2000);
        try { ProfileStorage.WriteDeferredSnapshot("history.json", history); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError("No se pudo guardar el historial", e.Message); }
    }

    internal void ApplyPreferences()
    {
        RootGrid.RequestedTheme = Preferences.Theme switch { "Dark" => ElementTheme.Dark, "System" => ElementTheme.Default, _ => ElementTheme.Light };
        ApplyPalette();
        SidebarColumn.Width = new GridLength(Preferences.VerticalTabs ? Preferences.SidebarWidth : 0);
        Sidebar.Visibility = Preferences.VerticalTabs ? Visibility.Visible : Visibility.Collapsed;
        TabStripHost.Visibility = Preferences.VerticalTabs ? Visibility.Collapsed : Visibility.Visible;
        VerticalTitleBar.Visibility = Preferences.VerticalTabs ? Visibility.Visible : Visibility.Collapsed;
        Chrome.Margin = new Thickness(0, 36, 0, 0);
        Chrome.Height = 42 + (Preferences.ShowBookmarks ? 30 : 0);
        BookmarksRow.Height = new GridLength(Preferences.ShowBookmarks ? 30 : 0);
        BookmarksBar.Visibility = Preferences.ShowBookmarks ? Visibility.Visible : Visibility.Collapsed;
        // The new-tab page runs under the bars; its content starts where it used to (110 below the toolbar).
        NewTabLayout.Margin = new Thickness(32, ToolbarHeight + 110, 32, 40);
        BarBase.Height = InternalBarGlass.Height = ToolbarHeight;
        ResizeManagement();
        ErrorBar.Margin = new Thickness(16, ToolbarHeight + 8, 16, 0);
        FindPanel.Margin = new Thickness(0, ToolbarHeight + 8, 16, 0);
        LoadBar.Margin = new Thickness(0, ToolbarHeight - 2, 0, 0);
        // The badge reports the off-screen engine's frame rate; with WebView2 there is nothing to report and it would be an empty dark box.
        PerformanceBadge.Visibility = Preferences.ShowPerformance && _compositor is not null ? Visibility.Visible : Visibility.Collapsed;
        GoButton.Visibility = Preferences.ShowHomeButton ? Visibility.Visible : Visibility.Collapsed;
        FavoritesButton.Visibility = Preferences.ShowFavoritesButton ? Visibility.Visible : Visibility.Collapsed;
        HistoryButton.Visibility = Preferences.ShowHistoryButton ? Visibility.Visible : Visibility.Collapsed;
        DownloadsButton.Visibility = Preferences.ShowDownloadsButton ? Visibility.Visible : Visibility.Collapsed;
        if (NewTabPanel.Visibility == Visibility.Visible) BuildNewTab();
        AddressBox.PlaceholderText = $"Buscar con {Preferences.SearchEngine} o escribir una dirección";
        EventHandler<object>? updateAddress = null;
        updateAddress = (_, _) =>
        {
            ToolbarRow.LayoutUpdated -= updateAddress;
            UpdateAddressLayout();
        };
        ToolbarRow.LayoutUpdated += updateAddress;
        foreach (BrowserTab tab in _tabs)
        {
            tab.SessionIfCreated?.SetVisible(!ViewportLayout.IsInternal(tab.Address) && (tab == _activeTab || !Preferences.PauseHiddenTabs));
            tab.SessionIfCreated?.SetFrameRate(Preferences.FrameRate);
        }
        UpdateGlassRegions();
        ResizeBrowser();
        ApplyPageThemes();
        UpdateDragRegions();
    }

    private void ApplyPalette()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color tint = dark ? Windows.UI.Color.FromArgb(255, 36, 36, 36) : Windows.UI.Color.FromArgb(255, 247, 247, 247);
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray;
        // The window uses desktop acrylic; only the vertical-tab sidebar lets it show through a translucent tint.
        RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PageGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(tint);
        Sidebar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(dark ? (byte)150 : (byte)165, tint.R, tint.G, tint.B));
        if (SystemBackdrop is null) SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        _menuAcrylic = MenuBorder.Background = new Microsoft.UI.Xaml.Media.AcrylicBrush
        {
            TintColor = tint, TintOpacity = Preferences.MenuOpacity,
            FallbackColor = Microsoft.UI.Colors.Transparent,
            AlwaysUseFallback = true
        };
        _menuSolid = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(255 * Math.Max(.99, Preferences.MenuOpacity)), tint.R, tint.G, tint.B));
        AddressBox.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Windows.UI.Color.FromArgb(190, 30, 30, 30) : Windows.UI.Color.FromArgb(190, 255, 255, 255));
        AddressBox.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 75, 75, 75) : Windows.UI.Color.FromArgb(255, 200, 200, 200));
        var border = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 65, 65, 65) : Windows.UI.Color.FromArgb(255, 210, 210, 210));
        MenuBorder.BorderBrush = ManagementPanel.BorderBrush = border;
        PerformanceBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(tint);
        PerformanceText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(foreground);
        // The strip is the bar's own tint; the toolbar row and the selected tab share one lighter veil, so they join into a single surface.
        Windows.UI.Color barTint = BarTint(dark);
        Windows.UI.Color veil = dark ? Windows.UI.Color.FromArgb(50, 255, 255, 255) : Windows.UI.Color.FromArgb(200, 255, 255, 255);
        var veilBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(veil);
        BarBase.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(barTint);
        InternalBarGlass.Background = new Microsoft.UI.Xaml.Media.AcrylicBrush
        {
            TintColor = barTint, TintOpacity = Preferences.GlassOpacity, FallbackColor = Windows.UI.Color.FromArgb(235, barTint.R, barTint.G, barTint.B)
        };
        Chrome.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(1, 255, 255, 255)); // hit-testable, visually clear
        // The selected tab and a hovered one are the same pill, only lighter or fainter, so they can never differ in size.
        HorizontalTabs.SelectedFill = veilBrush;
        HorizontalTabs.HoverFill = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Windows.UI.Color.FromArgb(28, 255, 255, 255) : Windows.UI.Color.FromArgb(110, 255, 255, 255));
        HorizontalTabs.SeparatorBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Windows.UI.Color.FromArgb(80, 200, 200, 200) : Windows.UI.Color.FromArgb(90, 70, 70, 70));
        HorizontalTabs.ApplyBrushes();
        // The vertical list uses the same pills through theme resources set once (Fx.TabListItems), so they follow a theme change.
        TabCard.Background = _menuSolid;
        TabCard.BorderBrush = border;
        ModeLabel.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        // Everything that floats over the page shares one fill and one hairline border.
        FindPanel.Background = SuggestPanel.Background = _menuSolid;
        FindPanel.BorderBrush = SuggestPanel.BorderBrush = border;
        RootGrid.Resources["MenuFlyoutPresenterBackground"] = new Microsoft.UI.Xaml.Media.AcrylicBrush { TintColor = tint, TintOpacity = .76, FallbackColor = tint };
        RootGrid.Resources["ComboBoxDropDownBackground"] = new Microsoft.UI.Xaml.Media.AcrylicBrush { TintColor = tint, TintOpacity = .76, FallbackColor = tint };
    }

    /// <summary>The colour of the bars (tab strip, toolbar, favourites): the dark tint as is, a light grey-blue so the lighter veil stands out in light mode.</summary>
    private static Windows.UI.Color BarTint(bool dark) =>
        dark ? Windows.UI.Color.FromArgb(255, 36, 36, 36) : Windows.UI.Color.FromArgb(255, 226, 228, 233);

    private void StartPerformanceTimer()
    {
        _lastPerformanceTime = Environment.TickCount64;
        _performanceTimer = DispatcherQueue.CreateTimer();
        _performanceTimer.Interval = TimeSpan.FromSeconds(1);
        _performanceTimer.Tick += (_, _) =>
        {
            if (_compositor is null) return;
            GpuCompositor.Performance p = _compositor.GetPerformance();
            long now = Environment.TickCount64;
            double rate = Math.Max(0, p.Frames - _lastPerformanceFrames) * 1000d / Math.Max(1, now - _lastPerformanceTime);
            PerformanceText.Text = $"CEF {rate:F0} frames/s · copia + espera {p.CopyMilliseconds:F2} ms · dibujo CPU {p.DrawMilliseconds:F2} ms · {_tabs.Count} pestañas";
            _lastPerformanceFrames = p.Frames;
            _lastPerformanceTime = now;
        };
        _performanceTimer.Start();
    }

    private void BenchmarkScroll()
    {
        if (ViewportLayout.IsInternal(_currentAddress) && _tabs.FirstOrDefault(t => t.Address == _testPage && t.Ready) is { } diagnostic)
            SelectTab(diagnostic);
        if (_compositor is null || _activeTab is not { Ready: true } tab || tab.Address != _testPage || tab.Session is not BrowserSession { Host: { } host })
        {
            ShowError("Medición no disponible", "Abre la página local de diagnóstico en la pestaña activa antes de medir.");
            return;
        }
        CloseManagement();
        _benchmarkTimer?.Stop();
        GpuCompositor.Performance before = _compositor.GetPerformance();
        long started = Environment.TickCount64;
        int step = 0;
        _benchmarkTimer = DispatcherQueue.CreateTimer();
        _benchmarkTimer.Interval = TimeSpan.FromMilliseconds(80);
        _benchmarkTimer.Tick += (_, _) =>
        {
            if (_activeTab != tab || _closing) { _benchmarkTimer?.Stop(); _benchmarkTimer = null; return; }
            double seconds = (Environment.TickCount64 - started) / 1000d;
            if (seconds >= 5)
            {
                _benchmarkTimer?.Stop();
                _benchmarkTimer = null;
                GpuCompositor.Performance after = _compositor.GetPerformance();
                double copy = (after.CopyTotalMilliseconds - before.CopyTotalMilliseconds) / Math.Max(1, after.Copies - before.Copies);
                double draw = (after.DrawTotalMilliseconds - before.DrawTotalMilliseconds) / Math.Max(1, after.Presentations - before.Presentations);
                ErrorBar.Severity = InfoBarSeverity.Informational;
                ErrorBar.Title = "Medición de desplazamiento local";
                ErrorBar.Message = $"Objetivo: {Preferences.FrameRate} FPS. Recibidos: {(after.Frames - before.Frames) / seconds:F1} fotogramas/s durante {seconds:F1} s. " +
                    $"Copia + espera: {copy:F2} ms; dibujo CPU: {draw:F2} ms. No mide el refresco del monitor ni equivale a una comparación con Edge.";
                ErrorBar.IsOpen = true;
                return;
            }
            var point = new MouseEvent((int)(WebInput.ActualWidth / 2), (int)(WebInput.ActualHeight / 2), CefEventFlags.None);
            host.SendMouseMoveEvent(point, false);
            host.SendMouseWheelEvent(point, 0, (step++ / 12) % 2 == 0 ? -120 : 120);
        };
        _benchmarkTimer.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>
    /// A tab dropped away from the strip becomes a window of its own, placed so the pointer stays over where its tab would be.
    /// The only tab of a window cannot leave it, so the whole window follows the pointer instead (as in Edge).
    /// </summary>
    private void MoveTabToNewWindow(BrowserTab tab)
    {
        if (!_sessionReady || _closing || !_tabs.Contains(tab) || !GetCursorPos(out NativePoint cursor)) return;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        RectInt32 work = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        bool maximized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        SizeInt32 size = maximized ? new SizeInt32((int)(1240 * scale), (int)(800 * scale)) : AppWindow.Size;
        int left = Math.Clamp(cursor.X - (int)(130 * scale), work.X, Math.Max(work.X, work.X + work.Width - size.Width));
        int top = Math.Clamp(cursor.Y - (int)(20 * scale), work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));
        if (_tabs.Count == 1)
        {
            if (maximized) (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Restore();
            AppWindow.MoveAndResize(new RectInt32(left, top, size.Width, size.Height));
            return;
        }
        BrowserApp.OpenWindow(tab.IsPrivate, restore: false, new TabTransfer(tab.Address, tab.Title, tab.Pinned, tab.Muted, tab.Zoom),
            new RectInt32(left, top, size.Width, size.Height));
        DetachTab(tab);
    }

    /// <summary>Removes a tab that now lives in another window: no "recently closed" entry, no close animation.</summary>
    private void DetachTab(BrowserTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        bool wasActive = tab == _activeTab;
        _tabs.Remove(tab);
        if (wasActive) _activeTab = null;
        tab.Dispose();
        if (_tabs.Count == 0) { Close(); return; }
        if (wasActive) SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        PersistTabs();
    }

    private void NewTab_Click(object sender, RoutedEventArgs args) => TryAddTab();
    private void VerticalTabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (VerticalTabs.SelectedItem is BrowserTab tab) SelectTab(tab);
    }
    private void VerticalTabs_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is BrowserTab tab)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, tab.Title);
            if (_compositor is not null) args.ItemContainer.Opacity = 1;
            else if (!_motions.ContainsKey(args.ItemContainer)) Motion.Fx.SurfaceAt(args.ItemContainer, true, 0);
            args.ItemContainer.RenderTransform = null;
            if (_verticalHooked.Add(args.ItemContainer))
            {
                // The close button shows only while the pointer is over the row.
                args.ItemContainer.PointerEntered += (container, _) => SetVerticalClose((FrameworkElement)container, true);
                args.ItemContainer.PointerExited += (container, _) => SetVerticalClose((FrameworkElement)container, false);
            }
            if (!tab.VerticalShown && args.Phase == 0 && !args.InRecycleQueue)
            {
                tab.VerticalShown = true;
                if (!_restoring) _ = AnimateSurface(args.ItemContainer, true);
            }
        }
    }
    private readonly HashSet<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem> _verticalHooked = [];

    private void SetVerticalClose(FrameworkElement container, bool shown)
    {
        if (NamedChild(container, "VerticalClose") is not Button close) return;
        close.Opacity = shown ? 1 : 0;
        close.IsHitTestVisible = shown;
    }

    private void CloseVerticalTab_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: BrowserTab tab }) CloseTab(tab);
    }
    private void Tab_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            args.TryGetPosition(PageGrid, out Point point);
            ShowTabMenu(tab, point);
            args.Handled = true;
        }
    }
    private void TabOptions_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: BrowserTab tab } button)
            ShowTabMenu(tab, button.TransformToVisual(PageGrid).TransformPoint(new Point(0, button.ActualHeight)));
    }
    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs args) => HandleShortcut(args);
    private bool HandleShortcut(KeyRoutedEventArgs args)
    {
        if (args.Handled) return true;
        if (args.Key == VirtualKey.Escape && HandleEscape()) { args.Handled = true; return true; }
        bool handled = RunShortcut(args.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu));
        if (handled) args.Handled = true;
        return handled;
    }

    /// <summary>Escape closes the topmost overlay, otherwise stops a page that is still loading.</summary>
    private bool HandleEscape()
    {
        if (MenuBorder.Visibility == Visibility.Visible) { HideMenu(); return true; }
        if (ManagementPanel.Visibility == Visibility.Visible) { CloseManagement(); return true; }
        if (_findOpen) { CloseFind(); FocusPage(); return true; }
        if (_activeTab is { IsLoading: true } loading) { loading.Session.Stop(); return true; }
        return false;
    }

    /// <summary>A shortcut pressed while the web page had the focus, reported by the engine (WebView2 swallows keys before XAML sees them).</summary>
    private void HandleWebShortcut(WebShortcut shortcut)
    {
        if (shortcut.Key == "Escape") { HandleEscape(); return; }
        VirtualKey? key = shortcut.Key switch
        {
            { Length: 1 } k when k[0] is >= 'a' and <= 'z' => (VirtualKey)(k[0] - 'a' + (int)VirtualKey.A),
            { Length: 1 } k when k[0] is >= '0' and <= '9' => (VirtualKey)(k[0] - '0' + (int)VirtualKey.Number0),
            "+" or "=" => (VirtualKey)187, "-" or "_" => (VirtualKey)189,
            "Tab" => VirtualKey.Tab, "Delete" => VirtualKey.Delete, "Home" => VirtualKey.Home,
            "ArrowLeft" => VirtualKey.Left, "ArrowRight" => VirtualKey.Right, "PageUp" => VirtualKey.PageUp, "PageDown" => VirtualKey.PageDown,
            "F3" => VirtualKey.F3, "F5" => VirtualKey.F5, "F6" => VirtualKey.F6, "F11" => VirtualKey.F11, "F12" => VirtualKey.F12,
            _ => null
        };
        if (key is { } virtualKey) RunShortcut(virtualKey, shortcut.Ctrl, shortcut.Shift, shortcut.Alt);
    }

    private void CycleTab(int step)
    {
        if (_tabs.Count > 0 && _activeTab is { } current) SelectTab(_tabs[(_tabs.IndexOf(current) + step + _tabs.Count) % _tabs.Count]);
    }

    private bool RunShortcut(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        Action? action = key switch
        {
            VirtualKey.T when ctrl && shift => ReopenTab,
            VirtualKey.T when ctrl => () => TryAddTab(),
            VirtualKey.W when ctrl && shift => Close,
            VirtualKey.W when ctrl => () => { if (_activeTab is { } tab) CloseTab(tab); },
            VirtualKey.N when ctrl => () => OpenAnotherWindow(shift),
            VirtualKey.L when ctrl => () => { AddressBox.Focus(FocusState.Programmatic); AddressBox.SelectAll(); },
            VirtualKey.D when alt => () => { AddressBox.Focus(FocusState.Programmatic); AddressBox.SelectAll(); },
            VirtualKey.PageDown when ctrl => () => CycleTab(1),
            VirtualKey.PageUp when ctrl => () => CycleTab(-1),
            VirtualKey.M when ctrl && shift => () => { if (_activeTab is { } tab) ToggleMute(tab); },
            VirtualKey.K when ctrl && shift => () => { if (_activeTab is { } tab && TryAddTab(tab.Address, isPrivate: tab.IsPrivate) is { } copy) copy.Zoom = tab.Zoom; },
            VirtualKey.F3 => () => { if (_findOpen && FindBox.Text.Length > 0) _web?.Find(FindBox.Text, !shift, true); else ShowFind(); },
            VirtualKey.G when ctrl => () => { if (_findOpen && FindBox.Text.Length > 0) _web?.Find(FindBox.Text, !shift, true); else ShowFind(); },
            VirtualKey.F when alt => ShowMainMenu,
            VirtualKey.F when ctrl => ShowFind,
            VirtualKey.D when ctrl => () => BookmarkButton_Click(this, new RoutedEventArgs()),
            VirtualKey.O when ctrl && shift => ShowBookmarks,
            VirtualKey.H when ctrl => ShowHistory,
            VirtualKey.J when ctrl => ShowDownloads,
            VirtualKey.A when ctrl && shift => () => ShowTabActions(true),
            VirtualKey.Delete when ctrl && shift => ShowClearData,
            VirtualKey.S when ctrl && shift => TakeScreenshot,
            VirtualKey.S when ctrl => SavePage,
            VirtualKey.U when ctrl => () => { if (_activeTab is { } tab && WebAddress.IsHttp(tab.Address)) ShowPageSource(tab); },
            VirtualKey.I when ctrl && shift => () => _web?.DevTools(),
            VirtualKey.R when ctrl => () => { if (shift) _web?.ReloadWithoutCache(); else ReloadCurrent(); },
            VirtualKey.Home when alt => () => NavigateCurrent(Preferences.HomePage),
            VirtualKey.P when ctrl => () => _web?.Print(),
            VirtualKey.B when ctrl && shift => () => BrowserApp.SavePreferences(Preferences with { ShowBookmarks = !Preferences.ShowBookmarks }),
            VirtualKey.F5 => ReloadCurrent,
            VirtualKey.F6 => () => { AddressBox.Focus(FocusState.Programmatic); AddressBox.SelectAll(); },
            VirtualKey.F11 => ToggleFullscreen,
            VirtualKey.F12 => () => _web?.DevTools(),
            VirtualKey.Tab when ctrl => () =>
            {
                CycleTab(shift ? -1 : 1);
            },
            VirtualKey.Number0 when ctrl => () => SetZoom(1),
            VirtualKey.Add when ctrl => () => SetZoom((_activeTab?.Zoom ?? 1) + .1),
            VirtualKey.Subtract when ctrl => () => SetZoom((_activeTab?.Zoom ?? 1) - .1),
            (VirtualKey)187 when ctrl => () => SetZoom((_activeTab?.Zoom ?? 1) + .1),
            (VirtualKey)189 when ctrl => () => SetZoom((_activeTab?.Zoom ?? 1) - .1),
            VirtualKey.Left when alt => () => _web?.Back(),
            VirtualKey.Right when alt => () => _web?.Forward(),
            _ => null
        };
        if (ctrl && key >= VirtualKey.Number1 && key <= VirtualKey.Number9 && _tabs.Count > 0)
        {
            int index = key == VirtualKey.Number9 ? _tabs.Count - 1 : Math.Min((int)key - (int)VirtualKey.Number1, _tabs.Count - 1);
            action = () => SelectTab(_tabs[index]);
        }
        if (action is null) return false;
        try { action(); }
        catch (Exception e) { ShowError("No se pudo ejecutar la acción", e.Message); }
        return true;
    }
}
