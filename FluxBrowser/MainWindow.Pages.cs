using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FluxBrowser.Browser;
using Windows.Foundation;

namespace FluxBrowser;

public sealed partial class MainWindow
{
    private void NavigateCurrent(string address)
    {
        if (_activeTab is not { } tab) return;
        tab.ScrollY = tab.DocumentHeight = 0;
        tab.Address = address;
        tab.Favicon = null;
        ErrorBar.IsOpen = false;
        tab.InternalContent = null;
        tab.Title = address == "browser:settings" ? "Configuración" : address == "browser:home" ? "Nueva pestaña" : "Cargando…";
        ShowInternalPage(tab);
        ResizeBrowser();
        tab.Session.Navigate(ViewportLayout.IsInternal(address) ? "about:blank" : address);
        tab.Session.SetVisible(!ViewportLayout.IsInternal(address));
        UpdateTabChrome();
        PersistTabs();
    }

    private void ShowInternalPage(BrowserTab tab)
    {
        ManagementContent.Content = null;
        SettingsPageContent.Content = null;
        SettingsPagePanel.Visibility = Visibility.Collapsed;
        ManagementPanel.Visibility = ManagementDismiss.Visibility = Visibility.Collapsed;
        _managementKind = "";
        _refreshManagement = null;
        bool newTabWasVisible = NewTabPanel.Visibility == Visibility.Visible;
        NewTabPanel.Visibility = InternalBarGlass.Visibility = tab.Address == "browser:home" ? Visibility.Visible : Visibility.Collapsed;
        if (ViewportLayout.IsInternal(tab.Address))
        {
            StartupText.Visibility = Visibility.Collapsed;
            _startupTimer?.Stop();
        }
        if (tab.Address == "browser:home")
        {
            tab.Title = "Nueva pestaña";
            BuildNewTab();
            if (!newTabWasVisible) RevealNewTab();
            // Typing on a fresh tab goes straight into its search box.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closing && _activeTab == tab && NewTabPanel.Visibility == Visibility.Visible) NewTabSearch.Focus(FocusState.Programmatic);
            });
        }
        else if (tab.Address == "browser:settings")
        {
            tab.Title = "Configuración";
            tab.InternalContent ??= new SettingsEditor(Preferences, p =>
            {
                BrowserApp.SavePreferences(p);
                BrowserApp.PersistSession();
            }, ClearHistory, ClearCookies, TestWebContextMenu, TestDownload, BenchmarkScroll,
                () => TryAddTab(_testPage), new SettingsActions(ShowClearData, ImportBookmarks, ExportBookmarks, ShowBookmarks,
                    ShowAbout, PickDownloadDirectory));
            SettingsPageContent.Content = tab.InternalContent;
            SettingsPagePanel.Visibility = Visibility.Visible;
        }
        ResizeManagement();
        UpdateGlassRegions();
    }

    private void History_Click(object sender, RoutedEventArgs args) => ShowHistory();
    private void Downloads_Click(object sender, RoutedEventArgs args) => ShowDownloads();
    private void ManagementDismiss_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        CloseManagement();
        args.Handled = true;
    }

    private void ResizeManagement()
    {
        NewTabLayout.Width = Math.Max(200, Math.Min(720, PageGrid.ActualWidth - 64));
        SettingsPagePanel.Margin = new Thickness(0, ToolbarHeight, 0, 0);
        ManagementPanel.Width = Math.Max(200, Math.Min(360, PageGrid.ActualWidth - 16));
        ManagementPanel.HorizontalAlignment = HorizontalAlignment.Right;
        ManagementPanel.VerticalAlignment = VerticalAlignment.Top;
        ManagementPanel.Margin = new Thickness(0, 80, 8, 8);
        ManagementPanel.Height = Math.Max(100, Math.Min(600, PageGrid.ActualHeight - 96));
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var tint = dark ? Windows.UI.Color.FromArgb(255, 36, 36, 36) : Windows.UI.Color.FromArgb(255, 247, 247, 247);
        ManagementPanel.Background = ViewportLayout.IsInternal(_currentAddress)
            ? new AcrylicBrush { TintColor = tint, TintOpacity = Preferences.MenuOpacity, FallbackColor = tint }
            : WebEngine.UseWebView2
                ? new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * Math.Max(.99, Preferences.MenuOpacity)), tint.R, tint.G, tint.B))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static Grid SiteIcon(string? address, double size = 16)
    {
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new FontIcon { Glyph = address == "browser:settings" ? "\uE713" : address == "browser:home" ? "\uE80F" : "\uE774", FontSize = size });
        if (WebAddress.IsHttp(address))
        {
            var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
            grid.Loaded += async (_, _) =>
            {
                image.Source = await FaviconLoader.LoadAsync(address);
                grid.Children[0].Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed;
            };
            grid.Children.Add(image);
        }
        return grid;
    }

    private async void RefreshTabIcon(BrowserTab tab)
    {
        string? address = FaviconLoader.Address(tab.Favicon, tab.Address);
        var image = await FaviconLoader.LoadAsync(address);
        if (!_closing && _tabs.Contains(tab) && address == FaviconLoader.Address(tab.Favicon, tab.Address))
            tab.FaviconImage = image;
    }

    private void UpdateSavedFavicon(BrowserTab tab)
    {
        if (tab.IsPrivate || !WebAddress.IsHttp(tab.Favicon)) return;
        try
        {
            bool historyChanged = false;
            for (int i = 0; i < BrowserApp.History.Count; i++)
                if (BrowserApp.History[i].Address == tab.Address && BrowserApp.History[i].Favicon != tab.Favicon)
                {
                    BrowserApp.History[i] = BrowserApp.History[i] with { Favicon = tab.Favicon };
                    historyChanged = true;
                }
            if (historyChanged && BrowserApp.HistoryAvailable) ProfileStorage.WriteDeferredSnapshot("history.json", BrowserApp.History);
            if (_bookmarksAvailable && _bookmarks.Any(b => b.Address == tab.Address && b.Favicon != tab.Favicon))
            {
                _bookmarkStore.Save(_bookmarkStore.Load().Select(b => b.Address == tab.Address ? b with { Favicon = tab.Favicon } : b).ToList());
                BrowserApp.RefreshBookmarks();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowError("No se pudo guardar el icono del sitio", e.Message); }
    }

    private void NavigateSettings(string category)
    {
        ShowSettings();
        if (_activeTab?.InternalContent is SettingsEditor editor) editor.NavigateTo(category);
    }

    private async void SetPageTheme(BrowserTab tab)
    {
        try { await tab.Session.SetThemeAsync(Preferences.PageTheme == "Dark" || (Preferences.PageTheme == "Browser" && RootGrid.ActualTheme == ElementTheme.Dark)); }
        catch (Exception e)
        {
            if (!_closing && _tabs.Contains(tab)) ShowError("No se pudo aplicar el tema a la página", e.Message);
        }
    }

    private void ApplyPageThemes()
    {
        foreach (BrowserTab tab in _tabs) if (tab.Ready) SetPageTheme(tab);
        // Internal pages paint part of themselves from code; they repaint once the new theme has reached their elements.
        foreach (BrowserTab tab in _tabs)
            if (tab.InternalContent is SettingsEditor editor) DispatcherQueue.TryEnqueue(() => { if (!_closing) editor.RefreshTheme(); });
        ResizeManagement();
    }
}
