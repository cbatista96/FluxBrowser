using CefSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyOwnBrowser.Browser;
using MyOwnBrowser.Motion;
using MyOwnBrowser.Rendering;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Point = Windows.Foundation.Point;

namespace MyOwnBrowser;

public sealed partial class MainWindow
{
    private sealed record MenuAction(string Label, string Glyph, Action? Execute, string Shortcut = "", bool Enabled = true, string? Favicon = null, Action<Point>? Context = null);
    private Action? _menuDismissed;
    private double _menuHeight;
    private Action? _refreshManagement;
    private readonly Dictionary<(BrowserTab Tab, int Id), DownloadProgress> _downloads = [];
    private string _managementKind = "";
    private bool _fullscreen;
    private readonly List<Button> _menuButtons = [];

    private static StackPanel IconLabel(string glyph, string text)
    {
        // The icon sits in the same 16 px column, 10 px from the text, as the rows of every menu: labels line up everywhere.
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Width = 16, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, MaxWidth = 250, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private Grid BuildZoomRow()
    {
        var row = new Grid { Height = 38, ColumnSpacing = 4, Padding = new Thickness(10, 0, 4, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(IconLabel("\uE71E", "Zoom"));
        var value = new TextBlock { Text = $"{_activeTab?.Zoom * 100:F0} %", FontSize = 12 };
        Button Make(string glyph, string label, Action action, int column, UIElement? content = null)
        {
            var button = new Button
            {
                Content = content ?? new FontIcon { Glyph = glyph, FontSize = 14 },
                MinWidth = 28, Height = 30, MinHeight = 0, Padding = new Thickness(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += (_, _) => { action(); value.Text = $"{_activeTab?.Zoom * 100:F0} %"; };
            if (content is null) Fx.Icon(button);
            Grid.SetColumn(button, column);
            row.Children.Add(button);
            _menuButtons.Add(button);
            return button;
        }
        Make("\uE71F", "Reducir zoom", () => SetZoom((_activeTab?.Zoom ?? 1) - .1), 1);
        Make("\uE740", "Restablecer zoom", () => SetZoom(1), 2, value);
        Make("\uE710", "Ampliar zoom", () => SetZoom((_activeTab?.Zoom ?? 1) + .1), 3);
        Make("\uE740", "Pantalla completa", () => { HideMenu(); ToggleFullscreen(); }, 4);
        return row;
    }

    private void ShowMenu(IEnumerable<MenuAction?> actions, Point position, Action? dismissed = null, double width = 344)
    {
        HideMenu(immediate: true);
        MenuItems.Children.Clear();
        _menuButtons.Clear();
        double height = 10;
        Button? first = null;
        foreach (MenuAction? action in actions)
        {
            if (action is null)
            {
                MenuItems.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 110, 130, 150)) });
                height += 11;
                continue;
            }
            if (action.Label == "Zoom" && action.Execute is null)
            {
                MenuItems.Children.Add(BuildZoomRow());
                height += 38;
                continue;
            }
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(action.Favicon is not null ? SiteIcon(action.Favicon)
                : action.Glyph == "\uE8D5" ? FolderIcon(14) : new FontIcon { Glyph = action.Glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = action.Label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var shortcut = new TextBlock { Text = action.Shortcut, FontSize = action.Shortcut == "\u203A" ? 15 : 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(shortcut, 2); row.Children.Add(shortcut);
            Button button = MenuButton(row, 32);
            Fx.Icon(button, IconMotion.Pop, (FrameworkElement)row.Children[0]);
            button.IsEnabled = action.Enabled && action.Execute is not null;
            if (!button.IsEnabled) row.Opacity = .45;
            AutomationProperties.SetName(button, action.Label);
            button.Click += (_, _) =>
            {
                _menuDismissed = null;
                HideMenu(immediate: true);
                try { action.Execute?.Invoke(); }
                catch (Exception e) { ShowError("No se pudo ejecutar la opción", e.Message); }
            };
            if (action.Context is not null) button.RightTapped += (_, e) => { action.Context(e.GetPosition(PageGrid)); e.Handled = true; };
            MenuItems.Children.Add(button);
            if (button.IsEnabled) _menuButtons.Add(button);
            if (button.IsEnabled) first ??= button;
            height += 32;
        }
        PlaceMenu(position, width, height, dismissed);
        first?.Focus(FocusState.Programmatic);
    }

    private static Button MenuButton(UIElement content, double height)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var button = new Button
        {
            Content = content, Height = height, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = transparent,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(Fx.ControlRadius), Padding = new Thickness(10, 2, 10, 2)
        };
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        return button;
    }

    private void ShowPopup(UIElement content, Point position, double width, double height, Action? dismissed = null)
    {
        HideMenu();
        MenuItems.Children.Clear();
        _menuButtons.Clear();
        MenuItems.Children.Add(content);
        PlaceMenu(position, width, height, dismissed);
    }

    private void PlaceMenu(Point position, double width, double height, Action? dismissed)
    {
        MenuBorder.Width = Math.Min(width, Math.Max(180, PageGrid.ActualWidth - 16));
        MenuScroll.MaxHeight = Math.Max(80, PageGrid.ActualHeight - position.Y - 16);
        _menuHeight = Math.Min(height, MenuScroll.MaxHeight + 12);
        double x = Math.Clamp(position.X, 8, Math.Max(8, PageGrid.ActualWidth - MenuBorder.Width - 8));
        double y = Math.Clamp(position.Y, 8, Math.Max(8, PageGrid.ActualHeight - _menuHeight - 8));
        MenuBorder.Margin = new Thickness(x, y, 0, 0);
        // Over a WebView2 page the menu cannot blur what is behind it, and at the menu's opacity a blur would barely show. A nearly solid
        // fill is exact, free, and cannot drift from the menu the way a glass rectangle drawn by the page did.
        bool overWebView = WebEngine.UseWebView2 && !ViewportLayout.IsInternal(_currentAddress);
        MenuBorder.Background = overWebView ? _menuSolid : _menuAcrylic;
        if (MenuBorder.Background is AcrylicBrush acrylic)
        {
            // Over web content the GPU compositor blurs the page under the menu. Where the menu covers the XAML chrome
            // the compositor can't blur it, so the menu uses real in-app acrylic with an opaque fallback instead.
            bool overChrome = y < ToolbarHeight;
            acrylic.AlwaysUseFallback = !ViewportLayout.IsInternal(_currentAddress) && !overChrome;
            acrylic.FallbackColor = overChrome || ViewportLayout.IsInternal(_currentAddress)
                ? Windows.UI.Color.FromArgb(255, acrylic.TintColor.R, acrylic.TintColor.G, acrylic.TintColor.B)
                : Microsoft.UI.Colors.Transparent;
            acrylic.TintOpacity = overChrome ? Math.Max(.82, Preferences.MenuOpacity) : Preferences.MenuOpacity;
        }
        _menuDismissed = dismissed;
        _menuOpen = true;
        MenuBorder.IsHitTestVisible = true;
        MenuDismiss.Visibility = MenuBorder.Visibility = Visibility.Visible;
        _ = AnimateSurface(MenuBorder, true, glass: true);
        UpdateGlassRegions();
        _ = ApplyAcrylicAsync();
    }

    private async void HideMenu(bool immediate = false)
    {
        Action? dismissed = _menuDismissed;
        _menuDismissed = null;
        dismissed?.Invoke();
        MenuDismiss.Visibility = Visibility.Collapsed;
        MenuBorder.IsHitTestVisible = false;
        _menuOpen = false;
        if (MenuBorder.Visibility == Visibility.Visible)
        {
            // The page fades its glass out while the menu fades; the rectangle stays exactly where the menu is.
            if (!immediate && !_closing) UpdateGlassRegions();
            if (immediate || _closing)
            {
                SettleSurface(MenuBorder, false);
            }
            else if (!await AnimateSurface(MenuBorder, false, glass: true)) return;
            MenuBorder.Visibility = Visibility.Collapsed;
        }
        if (!_closing) UpdateGlassRegions();
    }

    // Whether each overlay is meant to be shown. The page fades its glass in and out by itself, so a closing overlay is simply "not open".
    private bool _menuOpen, _panelOpen;
    private Microsoft.UI.Xaml.Media.Brush? _menuAcrylic, _menuSolid;

    private void UpdateGlassRegions()
    {
        if ((_compositor is null && !WebEngine.UseWebView2) || _closing) return;
        bool web = !ViewportLayout.IsInternal(_currentAddress);
        // (id, rectangle in PageGrid coordinates, corner radius, how opaque, shown?) for the overlays that sit over the page.
        var overlays = new List<(string Id, Point Origin, double Width, double Height, double Radius, double Opacity, bool Open, double Fade)>();
        if (ManagementPanel.Visibility == Visibility.Visible && web)
        {
            Point top = ManagementPanel.TransformToVisual(PageGrid).TransformPoint(new Point());
            double scale = ManagementPanel.RenderTransform is CompositeTransform transform ? transform.ScaleX : 1;
            overlays.Add(("panel", top, ManagementPanel.ActualWidth * scale, ManagementPanel.ActualHeight * scale, ManagementPanel.CornerRadius.TopLeft,
                Preferences.MenuOpacity, _panelOpen, ManagementPanel.Opacity));
        }
        if (MenuBorder.Visibility == Visibility.Visible && web)
        {
            Point top = MenuBorder.TransformToVisual(PageGrid).TransformPoint(new Point());
            double scale = MenuBorder.RenderTransform is CompositeTransform transform ? transform.ScaleX : 1;
            overlays.Add(("menu", top, MenuBorder.Width * scale, (MenuBorder.ActualHeight > 0 ? MenuBorder.ActualHeight : _menuHeight) * scale,
                MenuBorder.CornerRadius.TopLeft, Preferences.MenuOpacity, _menuOpen, MenuBorder.Opacity));
        }
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        // The off-screen engine blurs in the compositor and follows the element's opacity frame by frame.
        _compositor?.Configure((float)Preferences.BlurRadius, (float)Preferences.GlassOpacity, dark,
            overlays.Select(o => new GpuCompositor.GlassRegion(o.Origin.X, o.Origin.Y, o.Width, o.Height, (float)o.Opacity, (float)o.Fade)).ToArray());
        if (_activeTab?.SessionIfCreated is { IsOffscreen: false } pageSession && web)
        {
            Windows.UI.Color bar = BarTint(dark);
            string BarTintCss(double alpha) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3})", bar.R, bar.G, bar.B, alpha);
            // Page coordinates start where the page does: at the window's top when the page fills it, below the toolbar otherwise.
            var rects = new List<GlassRect>();
            if (_activeTab.FullViewport)
                rects.Add(new GlassRect("bar", 0, 0, BrowserSurface.ActualWidth, ToolbarHeight, 0, BarTintCss(Preferences.GlassOpacity), 1, Reveal: true));
            // Menus and panels over a WebView2 page are solid (see PlaceMenu); only the bars are glass there.
            pageSession.UpdateGlass(rects, Preferences.BlurRadius, (float)(BrowserSurface.XamlRoot?.RasterizationScale ?? 1), dark);
        }
    }
    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs args) { UpdateGlassRegions(); RefreshAcrylic(); }
    private void MenuDismiss_PointerPressed(object sender, PointerRoutedEventArgs args) { HideMenu(); args.Handled = true; FocusPage(); }
    private void Menu_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_menuButtons.Count == 0 || args.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.Tab)) return;
        int index = _menuButtons.IndexOf(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as Button ?? _menuButtons[0]);
        index = args.Key switch
        {
            VirtualKey.Home => 0, VirtualKey.End => _menuButtons.Count - 1,
            VirtualKey.Up => (index - 1 + _menuButtons.Count) % _menuButtons.Count,
            VirtualKey.Tab when IsDown(VirtualKey.Shift) => (index - 1 + _menuButtons.Count) % _menuButtons.Count,
            _ => (index + 1) % _menuButtons.Count
        };
        _menuButtons[index].Focus(FocusState.Keyboard);
        _menuButtons[index].StartBringIntoView();
        args.Handled = true;
    }

    private void ShowTabMenu(BrowserTab tab, Point point) => ShowMenu([
        new("Seleccionar pestaña", "\uE8A5", () => SelectTab(tab)),
        new("Duplicar pestaña", "\uE8C8", () =>
        {
            BrowserTab? duplicate = TryAddTab(tab.Address, isPrivate: tab.IsPrivate);
            if (duplicate is null) return;
            duplicate.Zoom = tab.Zoom;
            duplicate.Muted = tab.Muted;
        }),
        new(tab.Pinned ? "Desfijar pestaña" : "Fijar pestaña", "\uE718", () => { tab.Pinned = !tab.Pinned; PersistTabs(); }),
        new(tab.Muted ? "Activar sonido" : "Silenciar pestaña", tab.Muted ? "\uE767" : "\uE74F",
            () => ToggleMute(tab)),
        null,
        new("Mover a la izquierda / arriba", "\uE72B", () => { _tabs.Move(_tabs.IndexOf(tab), _tabs.IndexOf(tab) - 1); PersistTabs(); }, Enabled: _tabs.IndexOf(tab) > 0),
        new("Mover a la derecha / abajo", "\uE72A", () => { _tabs.Move(_tabs.IndexOf(tab), _tabs.IndexOf(tab) + 1); PersistTabs(); }, Enabled: _tabs.IndexOf(tab) < _tabs.Count - 1),
        null,
        new("Cerrar pestaña", "\uE711", () => CloseTab(tab), "Ctrl+W", !tab.Pinned),
        new("Cerrar otras pestañas", "\uE8BB", () => CloseSeveral(_tabs.Where(t => t != tab && !t.Pinned).ToList())),
        new("Cerrar pestañas de la derecha", "\uE8BB", () => CloseSeveral(_tabs.Skip(_tabs.IndexOf(tab) + 1).Where(t => !t.Pinned).ToList()))
    ], point);

    private void CloseSeveral(List<BrowserTab> tabs) { foreach (BrowserTab tab in tabs) CloseTab(tab); }
    private void SetZoom(double zoom)
    {
        if (_activeTab is not { } tab) return;
        tab.Zoom = Math.Clamp(Math.Round(zoom, 2), .25, 5);
        tab.SessionIfCreated?.SetZoom(tab.Zoom);
        UpdateZoomChip();
    }
    private void ToggleFullscreen()
    {
        bool fullscreen = !_fullscreen;
        AppWindow.SetPresenter(fullscreen ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen : Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        _fullscreen = fullscreen;
    }

    private static void CompleteContext(IRunContextMenuCallback callback, CefMenuCommand? command)
    {
        using (callback)
        {
            if (callback.IsDisposed) return;
            if (command is { } selected) callback.Continue(selected, CefEventFlags.None);
            else callback.Cancel();
        }
    }
    private void ShowWebMenu(BrowserTab tab, WebMenuRequest request)
    {
        var actions = new List<MenuAction?>();
        void Add(string text, string icon, Action action) => actions.Add(new(text, icon, () => { CompleteContext(request.Callback, null); action(); }));
        if (WebAddress.IsHttp(request.Link))
        {
            Add("Abrir vínculo en una pestaña nueva", "\uE710", () => TryAddTab(request.Link, isPrivate: tab.IsPrivate));
            Add("Abrir vínculo en segundo plano", "\uE8A5", () => TryAddTab(request.Link, false, tab.IsPrivate));
            Add("Copiar vínculo", "\uE8C8", () => CopyText(request.Link));
            Add("Guardar vínculo como", "\uE896", () => tab.Session.Download(request.Link));
            actions.Add(null);
        }
        if (WebAddress.IsHttp(request.Image))
        {
            Add("Abrir imagen en una pestaña nueva", "\uEB9F", () => TryAddTab(request.Image, isPrivate: tab.IsPrivate));
            Add("Guardar imagen como", "\uE74E", () => tab.Session.Download(request.Image));
            Add("Copiar vínculo de la imagen", "\uE8C8", () => CopyText(request.Image));
            actions.Add(null);
        }
        float scale = (float)(BrowserSurface.XamlRoot?.RasterizationScale ?? 1);
        var position = new Point(request.X, request.Y + (tab.Source?.PresentedTopInset(scale) ?? 0));
        AddWebMenuItems(tab, request, request.Items, actions, position, () => ShowWebMenu(tab, request));
        void AddKey(string text, string icon, string keys, Action action) =>
            actions.Add(new(text, icon, () => { CompleteContext(request.Callback, null); action(); }, keys));
        if (!request.Items.Any(item => item.Command == CefMenuCommand.Reload))
            AddKey("Actualizar", "\uE72C", "Ctrl+R", tab.Session.Reload);
        if (!string.IsNullOrWhiteSpace(request.Selection))
            Add($"Buscar «{Shorten(request.Selection)}» en la web", "\uE721", () =>
            {
                TryAddTab(AddressResolver.Search(request.Selection, Preferences.SearchEngine), isPrivate: tab.IsPrivate);
            });
        actions.Add(null);
        AddKey("Guardar como", "\uE74E", "Ctrl+S", SavePage);
        AddKey("Captura de pantalla", "\uF7ED", "Ctrl+Mayús+S", TakeScreenshot);
        if (!request.Items.Any(item => item.Command == CefMenuCommand.Find))
            AddKey("Buscar en la página", "\uE721", "Ctrl+F", ShowFind);
        actions.Add(null);
        AddKey("Inspeccionar", "\uEC7A", "Ctrl+Mayús+I", tab.Session.DevTools);
        ShowMenu(actions, position, () => CompleteContext(request.Callback, null));
    }

    private void AddWebMenuItems(BrowserTab tab, WebMenuRequest request, IReadOnlyList<WebMenuItem> items,
        List<MenuAction?> actions, Point position, Action returnHere)
    {
        foreach (WebMenuItem item in items)
        {
            if (item.Type == MenuItemType.Separator)
                actions.Add(null);
            else if (item.Type == MenuItemType.SubMenu)
                actions.Add(new(item.Label, item.Glyph,
                    () => ShowWebSubmenu(tab, request, item.Children, position, returnHere),
                    "\u203A", item.Enabled && item.Children.Count > 0));
            else if (item.Command == CefMenuCommand.Find)
                actions.Add(new(item.Label, item.Glyph, () => { CompleteContext(request.Callback, null); ShowFind(); }, "Ctrl+F", item.Enabled));
            else if (item.Command == CefMenuCommand.ViewSource)
                actions.Add(new(item.Label, item.Glyph, () => { CompleteContext(request.Callback, null); ShowPageSource(tab); }, "Ctrl+U", item.Enabled && WebAddress.IsHttp(tab.Address)));
            else
                actions.Add(new(item.Label, item.Glyph, () => CompleteContext(request.Callback, item.Command), ShortcutFor(item.Command), item.Enabled));
        }
    }

    private static string ShortcutFor(CefMenuCommand command) => command switch
    {
        CefMenuCommand.Back => "Alt+Flecha izquierda", CefMenuCommand.Forward => "Alt+Flecha derecha",
        CefMenuCommand.Reload => "Ctrl+R", CefMenuCommand.ReloadNoCache => "Ctrl+Mayús+R",
        CefMenuCommand.StopLoad => "Esc", CefMenuCommand.Undo => "Ctrl+Z", CefMenuCommand.Redo => "Ctrl+Y",
        CefMenuCommand.Cut => "Ctrl+X", CefMenuCommand.Copy => "Ctrl+C", CefMenuCommand.Paste => "Ctrl+V",
        CefMenuCommand.Delete => "Supr", CefMenuCommand.SelectAll => "Ctrl+A", CefMenuCommand.Print => "Ctrl+P",
        _ => ""
    };

    private static string Shorten(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 24 ? text : text[..23] + "…";
    }

    private void ShowWebSubmenu(BrowserTab tab, WebMenuRequest request, IReadOnlyList<WebMenuItem> items,
        Point position, Action parent)
    {
        var actions = new List<MenuAction?> { new("Volver", "\uE72B", parent), null };
        AddWebMenuItems(tab, request, items, actions, position,
            () => ShowWebSubmenu(tab, request, items, position, parent));
        ShowMenu(actions, position, () => CompleteContext(request.Callback, null));
    }

    private void ShowPageSource(BrowserTab tab)
    {
        if (!WebAddress.IsHttp(tab.Address)) { ShowError("Código fuente no disponible", "Abre primero una página HTTP/HTTPS."); return; }
        TryAddTab("view-source:" + tab.Address, isPrivate: tab.IsPrivate);
    }

    private static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private void ShowManagement(string title, string glyph, UIElement content, string kind)
    {
        HideMenu();
        CloseFind();
        _managementKind = kind;
        ManagementTitle.Text = title;
        ManagementIcon.Glyph = glyph;
        ManagementContent.Content = content;
        ManagementActions.Children.Clear();
        _panelOpen = true;
        ManagementPanel.Visibility = Visibility.Visible;
        ManagementPanel.IsHitTestVisible = true;
        ManagementDismiss.Visibility = Visibility.Visible;
        ResizeManagement();
        _ = AnimateSurface(ManagementPanel, true, glass: true);
        UpdateGlassRegions();
        _ = ApplyAcrylicAsync();
    }
    private async void CloseManagement()
    {
        ManagementDismiss.Visibility = Visibility.Collapsed;
        _refreshManagement = null;
        _managementKind = "";
        ManagementPanel.IsHitTestVisible = false;
        _panelOpen = false;
        if (ManagementPanel.Visibility == Visibility.Visible) UpdateGlassRegions();
        if (ManagementPanel.Visibility == Visibility.Visible && !await AnimateSurface(ManagementPanel, false, glass: true)) return;
        ManagementPanel.Visibility = Visibility.Collapsed;
        ManagementContent.Content = null;
        UpdateGlassRegions();
    }
    private void CloseManagement_Click(object sender, RoutedEventArgs args)
    {
        CloseManagement();
        FocusPage();
    }
    private void Settings_Click(object sender, RoutedEventArgs args) => ShowSettings();
    private void ShowSettings()
    {
        BrowserTab? existing = _tabs.FirstOrDefault(t => t.Address == "browser:settings");
        if (existing is not null) SelectTab(existing);
        else TryAddTab("browser:settings");
    }

    private void ShowFind()
    {
        CloseManagement();
        if (!_findOpen)
        {
            _findOpen = true;
            FindPanel.IsHitTestVisible = true;
            FindPanel.Visibility = Visibility.Visible;
            _ = AnimateSurface(FindPanel, true);
        }
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        // Reopening with a term already there searches for it again, as Ctrl+F does everywhere.
        if (FindBox.Text.Length > 0) _web?.Find(FindBox.Text, true, false);
        else UpdateFindCount(0, 0);
        if (!WebEngine.UseWebView2) UpdateFindCount(0, 0);
    }

    private bool _findOpen;

    /// <summary>"3 de 12" inside the find box; an empty box shows nothing and a term without matches says so.</summary>
    private void UpdateFindCount(int active, int total)
    {
        if (!WebEngine.UseWebView2)
        {
            // The off-screen engine does not report matches: no counter, and the arrows work whenever there is something to look for.
            FindCount.Visibility = Visibility.Collapsed;
            FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = FindBox.Text.Length > 0;
            return;
        }
        FindCount.Text = FindBox.Text.Length == 0 ? "" : total == 0 ? "Sin resultados" : $"{active} de {total}";
        FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = total > 0;
    }

    private void TestWebContextMenu()
    {
        if (ViewportLayout.IsInternal(_currentAddress) && _tabs.FirstOrDefault(t => !ViewportLayout.IsInternal(t.Address) && t.Ready) is { } web)
            SelectTab(web);
        if (ViewportLayout.IsInternal(_currentAddress)) { ShowError("Prueba no disponible", "Abre primero una página web o el diagnóstico."); return; }
        if (_browser?.Host is not { } host) { ShowError("Prueba no disponible", "La pestaña todavía no está preparada."); return; }
        CloseManagement();
        var point = new MouseEvent((int)(WebInput.ActualWidth / 2), (int)(WebInput.ActualHeight / 2), CefEventFlags.None);
        host.SendMouseClickEvent(point, MouseButtonType.Right, false, 1);
        host.SendMouseClickEvent(point, MouseButtonType.Right, true, 1);
    }

    private void TestDownload()
    {
        if (ViewportLayout.IsInternal(_currentAddress) && _tabs.FirstOrDefault(t => !ViewportLayout.IsInternal(t.Address) && t.Ready) is { } web)
            SelectTab(web);
        if (ViewportLayout.IsInternal(_currentAddress)) { ShowError("Prueba no disponible", "Abre primero una página web o el diagnóstico."); return; }
        if (_browser?.Host is null) { ShowError("Prueba no disponible", "La pestaña todavía no está preparada."); return; }
        string id = Guid.NewGuid().ToString("N");
        string name = "myownbrowser-diagnostic-" + id + ".txt";
        string data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("MyOwnBrowser diagnostic download " + id));
        _browser.Download($"data:text/plain;name={name};base64,{data}");
        ShowDownloads();
    }
    private async void CloseFind()
    {
        _web?.StopFinding();
        if (!_findOpen) return;
        _findOpen = false;
        FindPanel.IsHitTestVisible = false;
        if (!await AnimateSurface(FindPanel, false) || _findOpen) return;
        FindPanel.Visibility = Visibility.Collapsed;
    }
    private void FindBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(FindBox.Text)) { _web?.StopFinding(); UpdateFindCount(0, 0); }
        else { _web?.Find(FindBox.Text, true, false); if (!WebEngine.UseWebView2) UpdateFindCount(0, 0); }
    }
    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) { _web?.Find(FindBox.Text, !IsDown(VirtualKey.Shift), true); args.Handled = true; }
    }
    private void FindPrevious_Click(object sender, RoutedEventArgs args) => _web?.Find(FindBox.Text, false, true);
    private void FindNext_Click(object sender, RoutedEventArgs args) => _web?.Find(FindBox.Text, true, true);
    private void CloseFind_Click(object sender, RoutedEventArgs args) { CloseFind(); FocusPage(); }

    private Button ActionButton(string glyph, string text, Action action)
    {
        // A standard Windows 11 button (these live in dialogs): the accent style of a primary action must be able to fill it.
        StackPanel content = IconLabel(glyph, text);
        if (glyph.Length == 0) content.Children.RemoveAt(0);
        var button = new Button { Content = content, FontSize = 12, MinHeight = 32, MinWidth = 96, Padding = new Thickness(12, 4, 12, 5),
            CornerRadius = new CornerRadius(Fx.ControlRadius) };
        AutomationProperties.SetName(button, text);
        if (content.Children.Count > 1) Fx.Icon(button, IconMotion.Pop, (FrameworkElement)content.Children[0]);
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception e) { ShowError("No se pudo ejecutar la acción", e.Message); }
        };
        return button;
    }

    private Grid SearchableList(string placeholder, Action<StackPanel, string> build)
    {
        var grid = new Grid { RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var query = new TextBox { PlaceholderText = placeholder, FontSize = 12, MinHeight = 30 };
        AutomationProperties.SetName(query, placeholder);
        var rows = new StackPanel { Spacing = 2 };
        void Refresh() { rows.Children.Clear(); build(rows, query.Text); }
        _refreshManagement = Refresh;
        query.TextChanged += (_, _) => Refresh();
        grid.Children.Add(query);
        var scroll = new ScrollViewer { Content = rows };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        Refresh();
        return grid;
    }

    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KiB" : $"{bytes / (1024d * 1024):N1} MiB";

    private void UpdateDownload(BrowserTab tab, DownloadProgress progress)
    {
        bool first = !_downloads.ContainsKey((tab, progress.Id));
        _downloads[(tab, progress.Id)] = progress;
        if (first && progress.InProgress && Preferences.ShowDownloadsOnStart) ShowDownloads();
        if (_managementKind == "downloads") _refreshManagement?.Invoke();
    }

    private async void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(Preferences.DownloadDirectory);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Preferences.DownloadDirectory);
            if (!await Launcher.LaunchFolderAsync(folder)) ShowError("No se pudo abrir la carpeta", "Windows no abrió el explorador de archivos.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowError("No se pudo abrir la carpeta de descargas", e.Message); }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string acceptLabel = "Eliminar", string acceptGlyph = "")
    {
        bool accepted = false;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, RequestedTheme = RootGrid.RequestedTheme };
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ActionButton(acceptGlyph, acceptLabel, () => { accepted = true; dialog.Hide(); }));
        buttons.Children.Add(ActionButton("\uE711", "Cancelar", dialog.Hide));
        panel.Children.Add(buttons);
        dialog.Content = panel;
        await dialog.ShowAsync();
        return accepted;
    }

    private async void ClearHistory()
    {
        if (!BrowserApp.HistoryAvailable) { ShowError("Historial no disponible", "Repara el archivo inválido antes de modificarlo."); return; }
        if (!await ConfirmAsync("Borrar historial", "Se eliminarán las visitas guardadas de todas las ventanas normales. Los marcadores se conservarán.")) return;
        try
        {
            ProfileStorage.Write("history.json", new List<HistoryEntry>());
            BrowserApp.History.Clear();
            _refreshManagement?.Invoke();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError("No se pudo borrar el historial", e.Message); }
    }

    private async void ClearCookies()
    {
        if (!await ConfirmAsync("Borrar cookies", "Se cerrarán las sesiones de sitios en las pestañas normales. No se modifican las cookies de pestañas privadas.")) return;
        try
        {
            
            int count = _web is { } cookieSession ? await cookieSession.ClearCookiesAsync() : 0;
            ErrorBar.Severity = InfoBarSeverity.Informational;
            ErrorBar.Title = "Cookies eliminadas";
            ErrorBar.Message = count < 0 ? "Se eliminaron las cookies de las pestañas normales." : $"Se eliminaron {count} cookies de las pestañas normales.";
            ErrorBar.IsOpen = true;
        }
        catch (Exception e) { ShowError("No se pudieron borrar las cookies", e.Message); }
    }

    private void ShowBookmarkMenu(Bookmark bookmark, Point point)
    {
        if (bookmark.IsFolder)
        {
            ShowMenu([
                new("Abrir carpeta", "\uE8B7", () => ShowBookmarkFolder(bookmark.FolderPath, point)),
                new("Abrir todos en nuevas pestañas", "\uE8A5", () => OpenBookmarkFolder(bookmark.FolderPath)),
                new("Nueva subcarpeta", "\uE8F4", () => EditFolder(null, bookmark.FolderPath)),
                new("Cambiar nombre", "\uE70F", () => EditFolder(bookmark)),
                new("Eliminar carpeta", "\uE74D", () => DeleteBookmark(bookmark))
            ], point, width: 300);
            return;
        }
        ShowMenu([
        new("Abrir marcador", "\uE734", () => NavigateCurrent(bookmark.Address)),
        new("Abrir en nueva pestaña", "\uE710", () => TryAddTab(bookmark.Address, isPrivate: _activeTab?.IsPrivate)),
        new("Copiar dirección", "\uE8C8", () => CopyText(bookmark.Address)),
        new("Editar marcador", "\uE70F", () => EditBookmark(bookmark)),
        new("Eliminar marcador", "\uE74D", () => DeleteBookmark(bookmark))
        ], point, width: 300);
    }

    private async void DeleteBookmark(Bookmark bookmark)
    {
        if (!await ConfirmAsync(bookmark.IsFolder ? "Eliminar carpeta" : "Eliminar marcador",
            $"Se eliminará «{bookmark.Title}»" + (bookmark.IsFolder ? " y todos sus marcadores y subcarpetas." : " de los marcadores."))) return;
        try
        {
            List<Bookmark> updated = _bookmarkStore.Load().Where(b => b != bookmark &&
                (!bookmark.IsFolder || (b.Folder != bookmark.FolderPath && !b.Folder.StartsWith(bookmark.FolderPath + "/", StringComparison.OrdinalIgnoreCase)))).ToList();
            _bookmarkStore.Save(updated);
            BrowserApp.RefreshBookmarks();
            _refreshManagement?.Invoke();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowError("No se pudo eliminar el marcador", e.Message); }
    }

    private async void EditBookmark(Bookmark bookmark)
    {
        var dialog = new ContentDialog { Title = "Editar marcador", XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        var panel = new StackPanel { Spacing = 12 };
        var title = new TextBox { Header = IconLabel("\uE70F", "Nombre"), Text = bookmark.Title };
        var address = new TextBox { Header = IconLabel("\uE774", "Dirección HTTP/HTTPS"), Text = bookmark.Address };
        var folder = new ComboBox { Header = IconLabel("\uE8B7", "Carpeta"), HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] paths = ["", .. _bookmarks.Where(b => b.IsFolder).Select(b => b.FolderPath)];
        foreach (string path in paths)
        {
            string label = path.Length == 0 ? "Barra de favoritos" : path;
            var option = new ComboBoxItem { Content = IconLabel("\uE8B7", label) };
            AutomationProperties.SetName(option, label);
            folder.Items.Add(option);
        }
        folder.SelectedIndex = Math.Max(0, Array.IndexOf(paths, bookmark.Folder));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(title); panel.Children.Add(address); panel.Children.Add(folder); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ActionButton("\uE74E", "Guardar", () =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || !WebAddress.IsHttp(address.Text.Trim())) { error.Text = "Introduce un nombre y una dirección HTTP/HTTPS sin credenciales."; return; }
            try
            {
                List<Bookmark> updated = _bookmarkStore.Load().Select(b => b == bookmark
                    ? new Bookmark(title.Text.Trim(), address.Text.Trim(), paths[folder.SelectedIndex],
                        Favicon: bookmark.Address == address.Text.Trim() ? bookmark.Favicon : null) : b).ToList();
                _bookmarkStore.Save(updated);
                BrowserApp.RefreshBookmarks();
                _refreshManagement?.Invoke();
                dialog.Hide();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { error.Text = e.Message; }
        }));
        buttons.Children.Add(ActionButton("\uE711", "Cancelar", dialog.Hide));
        panel.Children.Add(buttons); dialog.Content = panel;
        await dialog.ShowAsync();
    }

    internal void RefreshBookmarks()
    {
        LoadBookmarks();
        UpdateBookmarkButton();
        if (NewTabPanel.Visibility == Visibility.Visible) BuildQuickLinks();
        if (_managementKind == "bookmarks") _refreshManagement?.Invoke();
    }
}
