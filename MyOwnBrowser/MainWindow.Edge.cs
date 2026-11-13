using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CefSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyOwnBrowser.Browser;
using MyOwnBrowser.Motion;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Point = Windows.Foundation.Point;

namespace MyOwnBrowser;

internal sealed record ClosedTab(string Address, bool Pinned, string Title, string? Favicon, DateTimeOffset Closed);
internal sealed record QuickLink(string Title, string Address);
internal sealed record QuickLinkStore(List<QuickLink> Pinned, List<string> Hidden);

public sealed partial class MainWindow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static Task<(string Url, string Credit)?>? _bingImage;
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private readonly HashSet<string> _expandedFolders = [];

    private SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(a, r, g, b));
    private bool IsDark => RootGrid.ActualTheme == ElementTheme.Dark;

    private static FontIcon FolderIcon(double size = 16) => new()
    {
        Glyph = "\uE8D5", FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 178, 54))
    };

    private void ShowNotice(string title, string message)
    {
        ErrorBar.ActionButton = null;
        ErrorBar.Severity = InfoBarSeverity.Success;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void Favorites_Click(object sender, RoutedEventArgs args) => ShowBookmarks();
    private void CloseSandboxBanner_Click(object sender, RoutedEventArgs args) => SandboxBanner.Visibility = Visibility.Collapsed;
    private void MainMenu_Click(object sender, RoutedEventArgs args) => ShowMainMenu();

    private Point MainMenuPoint => new(PageGrid.ActualWidth - 328, 76);

    private void ShowMainMenu()
    {
        bool web = _sessionReady && (WebAddress.IsHttp(_currentAddress) || _currentAddress == _testPage);
        ShowMenu([
            new("Nueva pestaña", "\uE710", () => TryAddTab(), "Ctrl+T", _sessionReady),
            new("Nueva ventana", "\uE8A7", () => OpenAnotherWindow(), "Ctrl+N", _sessionReady),
            new("Nueva ventana InPrivate", "\uE727", () => OpenAnotherWindow(true), "Ctrl+Mayús+N", _sessionReady),
            null,
            new("Zoom", "\uE71E", null),
            null,
            new("Favoritos", "\uE728", ShowBookmarks, "Ctrl+Mayús+O"),
            new("Historial", "\uE81C", ShowHistory, "Ctrl+H"),
            new("Descargas", "\uE896", ShowDownloads, "Ctrl+J"),
            null,
            new("Eliminar datos de navegación", "\uE74D", ShowClearData, "Ctrl+Mayús+Supr"),
            new("Imprimir", "\uE749", () => _web?.Print(), "Ctrl+P", web),
            new("Captura de pantalla", "\uF7ED", TakeScreenshot, "Ctrl+Mayús+S", web),
            new("Buscar en la página", "\uE721", ShowFind, "Ctrl+F", web),
            new("Más herramientas", "\uEC7A", ShowMoreTools, "\u203A"),
            null,
            new("Configuración", "\uE713", ShowSettings),
            new("Ayuda y comentarios", "\uE897", ShowHelpMenu, "\u203A"),
            null,
            new("Cerrar Flux Browser", "\uE8BB", Close)
        ], MainMenuPoint, width: 320);
    }

    private void ShowMoreTools() => ShowMenu([
        new("Volver", "\uE72B", ShowMainMenu),
        null,
        new("Guardar página como", "\uE74E", SavePage, "Ctrl+S", WebAddress.IsHttp(_currentAddress)),
        new("Ver código fuente de página", "\uE943", () => { if (_activeTab is { } tab) ShowPageSource(tab); }, "Ctrl+U", WebAddress.IsHttp(_currentAddress)),
        new("Herramientas de desarrollo", "\uEC7A", () => _web?.DevTools(), "Ctrl+Mayús+I"),
        new(_fullscreen ? "Salir de pantalla completa" : "Pantalla completa", "\uE740", ToggleFullscreen, "F11"),
        new(Preferences.ShowBookmarks ? "Ocultar barra de favoritos" : "Mostrar barra de favoritos", "\uE728",
            () => BrowserApp.SavePreferences(Preferences with { ShowBookmarks = !Preferences.ShowBookmarks }), "Ctrl+Mayús+B"),
        new("Página de diagnóstico", "\uE9D9", () => NavigateCurrent(_testPage)),
        new("Reiniciar Flux Browser", "\uE72C", BrowserApp.RestartBrowser)
    ], MainMenuPoint, width: 320);

    private void ShowHelpMenu() => ShowMenu([
        new("Volver", "\uE72B", ShowMainMenu),
        null,
        new("Acerca de Flux Browser", "\uE946", ShowAbout),
        new("World Marketing Labs", "\uE774", () => TryAddTab("https://www.worldmarketinglabs.com/es")),
        new("Informar un fallo", "\uE90F", () => TryAddTab("https://github.com/cbatista96/FluxBrowser/issues/new/choose")),
        new("Métodos abreviados de teclado", "\uE765", ShowShortcuts),
        new("Medir desplazamiento", "\uE9D9", BenchmarkScroll)
    ], MainMenuPoint, width: 320);

    private async void SavePage()
    {
        if (!WebAddress.IsHttp(_currentAddress)) { ShowError("No se puede guardar", "Solo se pueden guardar páginas web http o https."); return; }
        if (_web is not { } session) return;
        try
        {
            string title = string.Concat(_currentTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = string.IsNullOrWhiteSpace(title) ? "Página web" : title };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            picker.FileTypeChoices.Add("Página web completa (MHTML)", new List<string> { ".mhtml" });
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, await session.CapturePageArchiveAsync(), new System.Text.UTF8Encoding(false));
            ShowNotice("Página guardada", $"Se guardó la página y sus recursos en {file.Path}.");
        }
        catch (Exception e) { ShowError("No se pudo guardar la página", e.Message); }
    }

    private async void TakeScreenshot()
    {
        if (_web is not { } session || ViewportLayout.IsInternal(_currentAddress))
        {
            ShowError("Captura no disponible", "Abre una página web para capturarla.");
            return;
        }
        try
        {
            byte[] png = await session.CaptureScreenshotAsync();
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MyOwnBrowser");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"Captura {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            await File.WriteAllBytesAsync(file, png);
            StorageFile stored = await StorageFile.GetFileFromPathAsync(file);
            var data = new DataPackage();
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(stored));
            Clipboard.SetContent(data);
            ShowNotice("Captura guardada", $"Se copió al portapapeles y se guardó en {file}.");
        }
        catch (Exception e) { ShowError("No se pudo capturar la página", e.Message); }
    }

    private async Task ShowDialog(string title, UIElement body, string primary = "Cerrar", Func<bool>? accept = null)
    {
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, RequestedTheme = RootGrid.ActualTheme };
        var panel = new StackPanel { Spacing = 16, MinWidth = 380 };
        panel.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (accept is not null)
        {
            Button ok = ActionButton("\uE73E", primary, () => { if (accept()) dialog.Hide(); });
            ok.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            buttons.Children.Add(ok);
            buttons.Children.Add(ActionButton("\uE711", "Cancelar", dialog.Hide));
        }
        else buttons.Children.Add(ActionButton("\uE711", primary, dialog.Hide));
        panel.Children.Add(buttons);
        dialog.Content = panel;
        await dialog.ShowAsync();
    }

    /// <summary>A line of a dialog: icon and text that wraps (and can be selected and copied) instead of being cut short.</summary>
    private static Grid InfoLine(string glyph, string text)
    {
        var line = new Grid { ColumnSpacing = 10, MaxWidth = 460 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var label = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(label, 1);
        line.Children.Add(label);
        return line;
    }

    private async void ShowAbout()
    {
        var body = new StackPanel { Spacing = 6 };
        string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        body.Children.Add(InfoLine("\uE774", $"Flux Browser {version} · WinUI 3 + Chromium"));
        var publisher = new HyperlinkButton { Content = "World Marketing Labs" };
        publisher.Click += (_, _) => TryAddTab("https://www.worldmarketinglabs.com/es");
        body.Children.Add(publisher);
        body.Children.Add(InfoLine("\uE8A5", $"Registro de errores: {Diagnostics.AppLog.FilePath}"));
        if (WebEngine.UseWebView2)
            body.Children.Add(InfoLine("\uE950", $"Chromium {WebView2Session.RuntimeVersion} (Runtime de WebView2, con sandbox)"));
        else
        {
            body.Children.Add(InfoLine("\uE950", $"Chromium {Cef.ChromiumVersion}"));
            body.Children.Add(InfoLine("\uE943", $"CEF {Cef.CefVersion}"));
        }
        if (!WebEngine.UseWebView2) body.Children.Add(InfoLine("\uE7BA", "Sin sandbox de Chromium: no uses este navegador para datos sensibles."));
        await ShowDialog("Acerca de Flux Browser", body);
    }

    private async void ShowShortcuts()
    {
        (string Keys, string Action)[] shortcuts =
        [
            ("Ctrl+T", "Nueva pestaña"), ("Ctrl+Mayús+T", "Reabrir pestaña cerrada"), ("Ctrl+W", "Cerrar pestaña"),
            ("Ctrl+N", "Nueva ventana"), ("Ctrl+Mayús+N", "Nueva ventana InPrivate"), ("Ctrl+Tab", "Pestaña siguiente"),
            ("Ctrl+Mayús+A", "Buscar pestañas"), ("Ctrl+L / F6", "Barra de direcciones"), ("Ctrl+R / F5", "Actualizar"),
            ("Alt+← / Alt+→", "Atrás / Adelante"), ("Alt+Inicio", "Página principal"), ("Ctrl+D", "Agregar a favoritos"),
            ("Ctrl+Mayús+O", "Favoritos"), ("Ctrl+Mayús+B", "Barra de favoritos"), ("Ctrl+H", "Historial"),
            ("Ctrl+J", "Descargas"), ("Ctrl+F", "Buscar en la página"), ("Ctrl+P", "Imprimir"), ("Ctrl+S", "Guardar página"),
            ("Ctrl+U", "Código fuente"), ("Ctrl+Mayús+S", "Captura de pantalla"), ("Ctrl+Mayús+Supr", "Eliminar datos de navegación"),
            ("Ctrl + / Ctrl − / Ctrl+0", "Zoom"), ("F11", "Pantalla completa"), ("F12 / Ctrl+Mayús+I", "Herramientas de desarrollo"),
            ("Alt+F", "Configuración y más"), ("Alt+D", "Barra de direcciones"), ("Ctrl+RePág / Ctrl+AvPág", "Pestaña anterior / siguiente"),
            ("F3 / Ctrl+G", "Siguiente coincidencia"), ("Ctrl+Mayús+M", "Silenciar pestaña"), ("Ctrl+Mayús+K", "Duplicar pestaña")
        ];
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = shortcuts[i].Keys, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var label = new TextBlock { Text = shortcuts[i].Action, FontSize = 12 };
            Grid.SetRow(keys, i); Grid.SetRow(label, i); Grid.SetColumn(label, 1);
            grid.Children.Add(keys); grid.Children.Add(label);
        }
        await ShowDialog("Métodos abreviados de teclado", new ScrollViewer { Content = grid, MaxHeight = 420 });
    }

    private async void ShowClearData()
    {
        HideMenu();
        CheckBox Option(string glyph, string text, string detail, bool value)
        {
            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(IconLabel(glyph, text));
            content.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = .7, Margin = new Thickness(23, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
            return new CheckBox { Content = content, IsChecked = value };
        }
        CheckBox history = Option("\uE81C", "Historial de exploración", $"{BrowserApp.History.Count} elementos", true);
        CheckBox cookies = Option("\uE774", "Cookies y otros datos del sitio", "Cierra la sesión de la mayoría de los sitios", true);
        CheckBox cache = Option("\uE8B7", "Imágenes y archivos en caché", "Algunos sitios pueden cargarse más lento la próxima vez", true);
        CheckBox closed = Option("\uE7A7", "Pestañas cerradas recientemente", $"{_closedTabs.Count} pestañas", false);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Intervalo de tiempo: todo el tiempo. Se aplica a las pestañas normales; las ventanas InPrivate no guardan datos.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .8 });
        body.Children.Add(history); body.Children.Add(cookies); body.Children.Add(cache); body.Children.Add(closed);
        bool accepted = false;
        await ShowDialog("Eliminar datos de navegación", body, "Borrar ahora", () => accepted = true);
        if (!accepted) return;
        var done = new List<string>();
        try
        {
            if (history.IsChecked == true && BrowserApp.HistoryAvailable)
            {
                ProfileStorage.Write("history.json", new List<HistoryEntry>());
                BrowserApp.History.Clear();
                done.Add("historial");
            }
            if (cookies.IsChecked == true)
            {
                
                int count = _web is { } cookieSession ? await cookieSession.ClearCookiesAsync() : 0;
                done.Add(count < 0 ? "cookies" : $"{count} cookies");
            }
            if (cache.IsChecked == true)
            {
                if (_tabs.FirstOrDefault(t => t.Ready && !t.IsPrivate && !ViewportLayout.IsInternal(t.Address)) is { } tab)
                {
                    await tab.Session.ClearCacheAsync();
                    done.Add("caché");
                }
                else done.Add("caché (omitida: abre una página web primero)");
            }
            if (closed.IsChecked == true) { _closedTabs.Clear(); done.Add("pestañas cerradas"); }
            _refreshManagement?.Invoke();
            ShowNotice("Datos eliminados", done.Count == 0 ? "No se seleccionó nada." : "Se eliminó: " + string.Join(", ", done) + ".");
        }
        catch (Exception e) { ShowError("No se pudieron eliminar todos los datos", e.Message); }
    }

    // ---------- Tab actions (Edge "Acciones de pestaña") ----------

    private void TabActions_Click(object sender, RoutedEventArgs args) => ShowTabActions(false);

    private void ShowTabActions(bool focusSearch)
    {
        var root = new StackPanel { Spacing = 0, Padding = new Thickness(0, 0, 0, 4) };
        Button Action(string glyph, string label, string shortcut, Action execute, bool enabled = true)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var keys = new TextBlock { Text = shortcut, FontSize = 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(keys, 2); row.Children.Add(keys);
            Button button = MenuButton(row, 32);
            Fx.Icon(button, IconMotion.Pop, (FrameworkElement)row.Children[0]);
            button.IsEnabled = enabled;
            if (!enabled) row.Opacity = .45;
            button.Click += (_, _) => { HideMenu(); execute(); };
            AutomationProperties.SetName(button, label);
            return button;
        }
        root.Children.Add(Action("\uE8A9", Preferences.VerticalTabs ? "Desactivar pestañas verticales" : "Activar pestañas verticales", "Ctrl+Mayús+,",
            () => BrowserApp.SavePreferences(Preferences with { VerticalTabs = !Preferences.VerticalTabs })));
        root.Children.Add(Action("\uE8CB", "Organizar pestañas", "", OrganizeTabs, _tabs.Count > 1));
        root.Children.Add(Action("\uE7A7", "Reabrir pestaña cerrada", "Ctrl+Mayús+T", ReopenTab, _closedTabs.Count > 0));
        root.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 6, 4, 8), Background = Brush(40, 128, 128, 128) });

        var searchHost = new Grid { Margin = new Thickness(4, 0, 4, 6) };
        var search = new TextBox { PlaceholderText = "Buscar pestañas", FontSize = 12, Padding = new Thickness(30, 6, 8, 5), MinHeight = 32, CornerRadius = new CornerRadius(Fx.ControlRadius) };
        AutomationProperties.SetName(search, "Buscar pestañas");
        searchHost.Children.Add(search);
        searchHost.Children.Add(new FontIcon { Glyph = "\uE721", FontSize = 12, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false });
        var keysHint = new TextBlock { Text = "Ctrl+Mayús+A", FontSize = 11, Opacity = .55, Margin = new Thickness(0, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchHost.Children.Add(keysHint);
        root.Children.Add(searchHost);
        var list = new StackPanel();
        root.Children.Add(list);
        bool openExpanded = true, closedExpanded = true;

        void Build()
        {
            list.Children.Clear();
            string query = search.Text.Trim();
            keysHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool Match(string title, string address) => query.Length == 0 ||
                title.Contains(query, StringComparison.OrdinalIgnoreCase) || address.Contains(query, StringComparison.OrdinalIgnoreCase);
            List<BrowserTab> open = _tabs.Where(t => Match(t.Title, t.Address)).ToList();
            list.Children.Add(Section($"Pestañas abiertas ({open.Count})", openExpanded, () => { openExpanded = !openExpanded; Build(); }));
            if (openExpanded)
                foreach (BrowserTab tab in open)
                    list.Children.Add(TabRow(FaviconLoader.Address(tab.Favicon, tab.Address), tab.Title, DisplayHost(tab.Address), tab == _activeTab,
                        () => { HideMenu(); SelectTab(tab); }, tab.Pinned ? null : () => { CloseTab(tab); Build(); }));
            List<ClosedTab> recent = _closedTabs.Where(c => Match(c.Title, c.Address)).ToList();
            if (recent.Count == 0) return;
            list.Children.Add(Section("Cerradas recientemente", closedExpanded, () => { closedExpanded = !closedExpanded; Build(); }));
            if (closedExpanded)
                foreach (ClosedTab closed in recent.Take(10))
                    list.Children.Add(TabRow(FaviconLoader.Address(closed.Favicon, closed.Address), closed.Title, $"{DisplayHost(closed.Address)} · {Ago(closed.Closed)}", false,
                        () => { HideMenu(); ReopenTab(closed); }, null));
        }
        search.TextChanged += (_, _) => Build();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && list.Children.OfType<Button>().Skip(1).FirstOrDefault() is { } first)
            {
                first.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
        };
        Build();
        double height = 3 * 32 + 15 + 44 + 34 + Math.Min(_tabs.Count, 12) * 44 + (_closedTabs.Count > 0 ? 34 + Math.Min(_closedTabs.Count, 10) * 44 : 0) + 16;
        double x = Preferences.VerticalTabs ? Math.Max(8, Preferences.SidebarWidth - SidebarColumn.ActualWidth + 8) : 8;
        ShowPopup(root, new Point(x, Preferences.VerticalTabs ? 40 : 38), 380, height);
        if (focusSearch) search.Focus(FocusState.Programmatic);
    }

    private Button Section(string title, bool expanded, Action toggle)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var chevron = new FontIcon { Glyph = expanded ? "\uE70E" : "\uE70D", FontSize = 11 };
        Grid.SetColumn(chevron, 1); row.Children.Add(chevron);
        Button button = MenuButton(row, 32);
        Fx.Icon(button, expanded ? IconMotion.Lift : IconMotion.Drop, chevron);
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => toggle();
        return button;
    }

    private Grid TabRow(string? favicon, string title, string detail, bool active, Action open, Action? close)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(SiteIcon(favicon));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Button button = MenuButton(row, 44);
        button.Padding = new Thickness(10, 2, close is null ? 10 : 38, 2);
        if (active) button.Background = Brush(28, 128, 128, 128);
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, $"{title}\n{detail}");
        button.Click += (_, _) => open();
        var host = new Grid();
        host.Children.Add(button);
        if (close is not null)
        {
            var x = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Width = 26, Height = 26, MinWidth = 0, Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0), Opacity = active ? 1 : 0
            };
            AutomationProperties.SetName(x, "Cerrar pestaña " + title);
            ToolTipService.SetToolTip(x, "Cerrar pestaña");
            x.Click += (_, _) => close();
            Fx.Icon(x);
            host.PointerEntered += (_, _) => Fx.Fade(x, 1);
            host.PointerExited += (_, _) => Fx.Fade(x, active ? 1 : 0);
            x.GotFocus += (_, _) => Fx.Fade(x, 1);
            host.Children.Add(x);
        }
        return host;
    }

    private void OrganizeTabs()
    {
        List<BrowserTab> ordered = _tabs.OrderByDescending(t => t.Pinned).ThenBy(t => DisplayHost(t.Address), StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int from = _tabs.IndexOf(ordered[i]);
            if (from != i) _tabs.Move(from, i);
        }
        PersistTabs();
    }

    private string DisplayHost(string address) =>
        address == "browser:home" ? "Nueva pestaña" : address == "browser:settings" ? "Configuración" : address == _testPage ? "Diagnóstico" :
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0 ? (uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host) : address;

    private static string Ago(DateTimeOffset time)
    {
        TimeSpan span = DateTimeOffset.Now - time;
        return span.TotalMinutes < 1 ? "ahora" : span.TotalHours < 1 ? $"hace {(int)span.TotalMinutes} min" :
            span.TotalDays < 1 ? $"hace {(int)span.TotalHours} h" : time.ToString("d MMM", Spanish);
    }

    // ---------- Edge-style panels ----------

    private Button HeaderButton(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Width = 30, Height = 30, MinWidth = 0, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(Fx.ControlRadius)
        };
        AutomationProperties.SetName(button, tip);
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception e) { ShowError("No se pudo ejecutar la acción", e.Message); }
        };
        Fx.Icon(button);
        ManagementActions.Children.Add(button);
        return button;
    }

    private (Grid Root, StackPanel Rows, TextBox Search) PanelLayout(string placeholder, UIElement? tabs = null)
    {
        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var searchHost = new Grid();
        var search = new TextBox { PlaceholderText = placeholder, FontSize = 12, MinHeight = 32, Padding = new Thickness(30, 6, 8, 5), CornerRadius = new CornerRadius(Fx.ControlRadius) };
        AutomationProperties.SetName(search, placeholder);
        searchHost.Children.Add(search);
        searchHost.Children.Add(new FontIcon { Glyph = "\uE721", FontSize = 12, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false });
        root.Children.Add(searchHost);
        if (tabs is not null) { Grid.SetRow((FrameworkElement)tabs, 1); root.Children.Add(tabs); }
        var rows = new StackPanel { Spacing = 0 };
        var scroll = new ScrollViewer { Content = rows, Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        return (root, rows, search);
    }

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(8, 10, 0, 4)
    };

    private static TextBlock EmptyText(string text) => new()
    {
        Text = text, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 12, 8, 0)
    };

    private Grid PanelRow(UIElement icon, string title, string detail, string trailing, Action open,
        (string Glyph, string Tip, Action Act)[]? hoverActions = null, Action<Point>? context = null)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var time = new TextBlock { Text = trailing, FontSize = 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(time, 2); row.Children.Add(time);
        Button button = MenuButton(row, detail.Length > 0 ? 44 : 34);
        button.Padding = new Thickness(8, 2, 8, 2);
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, detail.Length > 0 ? $"{title}\n{detail}" : title);
        button.Click += (_, _) => open();
        if (context is not null)
            button.RightTapped += (_, e) => { context(e.GetPosition(PageGrid)); e.Handled = true; };
        var host = new Grid();
        host.Children.Add(button);
        if (hoverActions is { Length: > 0 })
        {
            // The action buttons are built the first time the row is touched: a list of hundreds of rows would otherwise
            // create hundreds of controls nobody asked for, which made long panels slow to open and to filter.
            StackPanel? actions = null;
            void Show()
            {
                if (actions is null)
                {
                    actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) };
                    foreach (var hover in hoverActions)
                    {
                        var b = new Button
                        {
                            Content = new FontIcon { Glyph = hover.Glyph, FontSize = 11 }, Width = 26, Height = 26, MinWidth = 0, Padding = new Thickness(0),
                            Background = Brush(1, 0, 0, 0), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
                        };
                        AutomationProperties.SetName(b, hover.Tip);
                        ToolTipService.SetToolTip(b, hover.Tip);
                        b.Click += (_, _) =>
                        {
                            try { hover.Act(); }
                            catch (Exception e) { ShowError("No se pudo ejecutar la acción", e.Message); }
                        };
                        Fx.Icon(b);
                        actions.Children.Add(b);
                    }
                    host.Children.Add(actions);
                }
                actions.Visibility = Visibility.Visible;
                time.Opacity = 0;
            }
            host.PointerEntered += (_, _) => Show();
            host.PointerExited += (_, _) => { if (actions is not null) actions.Visibility = Visibility.Collapsed; time.Opacity = .65; };
            button.GotFocus += (_, _) => Show();
        }
        return host;
    }

    // Kept referenced: a collected timer silently stops. The load-more hook belongs to the list currently on screen.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _managementTimer;
    private Action? _loadMoreRows;

    private void ShowHistory()
    {
        _loadMoreRows = null;
        bool recent = false;
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(6, 0, 0, 0) };
        (Grid root, StackPanel rows, TextBox search) = PanelLayout("Buscar en el historial", tabs);
        void Tabs()
        {
            tabs.Children.Clear();
            foreach ((string label, bool value) in new[] { ("Todos", false), ("Cerradas recientemente", true) })
            {
                var content = new StackPanel { Spacing = 4 };
                content.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = recent == value ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
                content.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = recent == value ? Brush(255, 0, 120, 212) : Brush(0, 0, 0, 0) });
                var button = new Button { Content = content, Padding = new Thickness(2, 2, 2, 0), MinHeight = 0, Background = Brush(0, 0, 0, 0), BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(button, label);
                button.Click += (_, _) => { recent = value; Build(); };
                tabs.Children.Add(button);
            }
        }
        void Build()
        {
            Tabs();
            rows.Children.Clear();
            string query = search.Text.Trim();
            if (recent)
            {
                foreach (ClosedTab closed in _closedTabs.Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Address.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    rows.Children.Add(PanelRow(SiteIcon(FaviconLoader.Address(closed.Favicon, closed.Address)), closed.Title, DisplayHost(closed.Address), Ago(closed.Closed),
                        () => { CloseManagement(); ReopenTab(closed); }));
                if (rows.Children.Count == 0) rows.Children.Add(EmptyText("No hay pestañas cerradas recientemente."));
                return;
            }
            // Only a screenful is built; the rest is added as the list is scrolled (history can hold thousands of visits).
            List<HistoryEntry> matches = BrowserApp.History.Where(entry => query.Length == 0 ||
                entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Address.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(2000).ToList();
            int rendered = 0;
            string group = "";
            void RenderMore(int count)
            {
                for (int added = 0; added < count && rendered < matches.Count; added++, rendered++)
                {
                    HistoryEntry entry = matches[rendered];
                    DateTime day = entry.Visited.LocalDateTime.Date;
                    string label = day == DateTime.Today ? "Hoy" : day == DateTime.Today.AddDays(-1) ? "Ayer" : Spanish.TextInfo.ToTitleCase(day.ToString("dddd, d 'de' MMMM 'de' yyyy", Spanish));
                    if (label != group) { rows.Children.Add(GroupHeader(label)); group = label; }
                    HistoryEntry captured = entry;
                    rows.Children.Add(PanelRow(SiteIcon(FaviconLoader.Address(entry.Favicon, entry.Address)), string.IsNullOrWhiteSpace(entry.Title) ? entry.Address : entry.Title, "",
                        entry.Visited.LocalDateTime.ToString("H:mm"), () => { CloseManagement(); NavigateCurrent(captured.Address); },
                        [("\uE8A7", "Abrir en una pestaña nueva", () => { CloseManagement(); TryAddTab(captured.Address); }),
                         ("\uE711", "Eliminar", () => DeleteHistoryEntry(captured))]));
                }
            }
            RenderMore(40);
            _loadMoreRows = () => { if (rendered < matches.Count) RenderMore(40); };
            if (rows.Children.Count == 0) rows.Children.Add(EmptyText(query.Length > 0 ? "No se encontraron resultados." : "Las páginas que visites aparecerán aquí. Las ventanas InPrivate no guardan historial."));
        }
        // Filtering waits for a pause in typing instead of rebuilding the list on every key.
        var searchTimer = DispatcherQueue.CreateTimer();
        searchTimer.Interval = TimeSpan.FromMilliseconds(180);
        searchTimer.IsRepeating = false;
        searchTimer.Tick += (_, _) => Build();
        _managementTimer = searchTimer;
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        if (root.Children.OfType<ScrollViewer>().FirstOrDefault() is { } listScroll)
            listScroll.ViewChanged += (_, _) =>
            {
                if (listScroll.ScrollableHeight - listScroll.VerticalOffset < 240) _loadMoreRows?.Invoke();
            };
        _refreshManagement = Build;
        Build();
        ShowManagement("Historial", "\uE81C", root, "history");
        HeaderButton("\uE74D", "Eliminar datos de navegación", ShowClearData);
    }

    private void DeleteHistoryEntry(HistoryEntry entry)
    {
        if (!BrowserApp.HistoryAvailable) { ShowError("Historial no disponible", "Repara el archivo inválido antes de modificarlo."); return; }
        BrowserApp.History.Remove(entry);
        ProfileStorage.WriteDeferredSnapshot("history.json", BrowserApp.History);
        _refreshManagement?.Invoke();
    }

    private void ShowDownloads()
    {
        (Grid root, StackPanel rows, TextBox search) = PanelLayout("Buscar descargas");
        void Build()
        {
            rows.Children.Clear();
            string query = search.Text.Trim();
            foreach (var pair in _downloads.Reverse())
            {
                DownloadProgress item = pair.Value;
                string name = string.IsNullOrWhiteSpace(item.Name) ? item.Address : item.Name;
                if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase) && !item.Address.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                string status = item.InProgress
                    ? $"{FormatBytes(item.Received)}" + (item.Total > 0 ? $" de {FormatBytes(item.Total)}" : "") + (item.Percent >= 0 ? $" · {item.Percent} %" : "")
                    : item.State + (item.Total > 0 ? $" · {FormatBytes(item.Total)}" : "");
                var actions = new List<(string, string, Action)>();
                bool exists = !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path);
                if (exists)
                {
                    actions.Add(("\uE8E5", "Abrir archivo", () => OpenDownloadedFile(item.Path)));
                    actions.Add(("\uE838", "Mostrar en la carpeta", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\"")));
                }
                if (item.InProgress && _tabs.Contains(pair.Key.Tab))
                    actions.Add(("\uE711", "Cancelar", () => pair.Key.Tab.Session.CancelDownload(pair.Key.Id)));
                var card = new StackPanel();
                card.Children.Add(PanelRow(new FontIcon { Glyph = "\uE8A5", FontSize = 16 }, name, status, "",
                    () => { if (exists) OpenDownloadedFile(item.Path); }, actions.ToArray()));
                if (item.InProgress)
                    card.Children.Add(new ProgressBar { Value = Math.Max(0, item.Percent), IsIndeterminate = item.Percent < 0, Maximum = 100, Margin = new Thickness(34, -4, 10, 6) });
                rows.Children.Add(card);
            }
            if (rows.Children.Count == 0) rows.Children.Add(EmptyText(_downloads.Count == 0
                ? "Aún no hay descargas en esta ventana. Ningún archivo se abre automáticamente." : "No se encontraron descargas."));
        }
        search.TextChanged += (_, _) => Build();
        _refreshManagement = Build;
        Build();
        ShowManagement("Descargas", "\uE896", root, "downloads");
        HeaderButton("\uE838", "Abrir carpeta de descargas", OpenDownloadFolder);
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".lnk", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar", ".reg", ".cpl", ".msc", ".dll", ".iso", ".img"
    };

    private async void OpenDownloadedFile(string path)
    {
        try
        {
            // Programs and scripts downloaded from the web are the most common way to get infected: make running one a deliberate act.
            if (ExecutableExtensions.Contains(System.IO.Path.GetExtension(path)) && !await ConfirmAsync("¿Ejecutar este archivo?",
                $"{System.IO.Path.GetFileName(path)} puede ejecutar código en tu equipo. Ábrelo solo si confías en el sitio del que lo descargaste.",
                "Abrir de todos modos", ""))
                return;
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file)) ShowError("No se pudo abrir el archivo", "Windows no encontró una aplicación para abrirlo.");
        }
        catch (Exception e) { ShowError("No se pudo abrir el archivo", e.Message); }
    }

    private void ShowBookmarks()
    {
        (Grid root, StackPanel rows, TextBox search) = PanelLayout("Buscar en favoritos");
        void AddLevel(string folder, int depth, string query)
        {
            foreach (Bookmark bookmark in _bookmarks.Where(b => b.Folder == folder).OrderByDescending(b => b.IsFolder))
            {
                Bookmark captured = bookmark;
                Grid row;
                if (bookmark.IsFolder)
                {
                    bool expanded = _expandedFolders.Contains(bookmark.FolderPath);
                    row = PanelRow(FolderIcon(), bookmark.Title, "", expanded ? "\uE70E" : "\uE70D", () =>
                    {
                        if (!_expandedFolders.Remove(captured.FolderPath)) _expandedFolders.Add(captured.FolderPath);
                        _refreshManagement?.Invoke();
                    }, context: p => ShowBookmarkMenu(captured, p));
                    if (row.Children[0] is Button { Content: Grid content } && content.Children[2] is TextBlock chevron)
                        chevron.FontFamily = new FontFamily("Segoe Fluent Icons");
                }
                else
                {
                    row = PanelRow(SiteIcon(FaviconLoader.Address(bookmark.Favicon, bookmark.Address)), bookmark.Title, "", "", () => { CloseManagement(); NavigateCurrent(captured.Address); },
                        [("\uE712", "Más opciones", () => ShowBookmarkMenu(captured, new Point(Math.Max(8, PageGrid.ActualWidth - 300), 120)))],
                        p => ShowBookmarkMenu(captured, p));
                }
                row.Margin = new Thickness(depth * 18, 0, 0, 0);
                rows.Children.Add(row);
                if (bookmark.IsFolder && _expandedFolders.Contains(bookmark.FolderPath)) AddLevel(bookmark.FolderPath, depth + 1, query);
            }
        }
        void Build()
        {
            rows.Children.Clear();
            string query = search.Text.Trim();
            if (query.Length > 0)
            {
                foreach (Bookmark bookmark in _bookmarks.Where(b => !b.IsFolder && (b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Address.Contains(query, StringComparison.OrdinalIgnoreCase))))
                {
                    Bookmark captured = bookmark;
                    rows.Children.Add(PanelRow(SiteIcon(FaviconLoader.Address(bookmark.Favicon, bookmark.Address)), bookmark.Title, DisplayHost(bookmark.Address), "",
                        () => { CloseManagement(); NavigateCurrent(captured.Address); }, context: p => ShowBookmarkMenu(captured, p)));
                }
                if (rows.Children.Count == 0) rows.Children.Add(EmptyText("No se encontraron favoritos."));
                return;
            }
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(FolderIcon());
            header.Children.Add(new TextBlock { Text = "Barra de favoritos", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            header.Margin = new Thickness(8, 4, 0, 4);
            rows.Children.Add(header);
            AddLevel("", 1, query);
            if (_bookmarks.Count == 0) rows.Children.Add(EmptyText("Aún no hay favoritos. Pulsa la estrella de la barra de direcciones (Ctrl+D) para agregar esta página."));
        }
        search.TextChanged += (_, _) => Build();
        _refreshManagement = Build;
        Build();
        ShowManagement("Favoritos", "\uE728", root, "bookmarks");
        HeaderButton("\uECCD", "Agregar esta página a favoritos", () => BookmarkButton_Click(this, new RoutedEventArgs()));
        HeaderButton("\uE8F4", "Agregar carpeta", () => EditFolder(null));
        HeaderButton("\uE712", "Más opciones de favoritos", () => ShowMenu([
            new("Importar favoritos desde HTML", "\uE8B5", ImportBookmarks),
            new("Exportar favoritos a HTML", "\uE74E", ExportBookmarks),
            null,
            new("Agregar carpeta", "\uE8F4", () => EditFolder(null)),
            new(Preferences.ShowBookmarks ? "Ocultar barra de favoritos" : "Mostrar barra de favoritos", "\uE728",
                () => BrowserApp.SavePreferences(Preferences with { ShowBookmarks = !Preferences.ShowBookmarks }))
        ], new Point(Math.Max(8, PageGrid.ActualWidth - 320), 114), width: 310));
    }

    // ---------- New tab page ----------

    private void NewTabPanel_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled) return;
        if (!args.TryGetPosition(PageGrid, out Point point)) point = new Point(PageGrid.ActualWidth / 2, ToolbarHeight + 80);
        ShowNewTabMenu(point);
        args.Handled = true;
    }

    private void NewTabPanel_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (args.Handled) return;
        ShowNewTabMenu(args.GetPosition(PageGrid));
        args.Handled = true;
    }

    private void SettingsPagePanel_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled) return;
        if (!args.TryGetPosition(PageGrid, out Point point)) point = new Point(PageGrid.ActualWidth / 2, ToolbarHeight + 80);
        ShowSettingsMenu(point);
        args.Handled = true;
    }

    private void SettingsPagePanel_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (args.Handled || args.OriginalSource is TextBox or Microsoft.UI.Xaml.Documents.Run) return;
        ShowSettingsMenu(args.GetPosition(PageGrid));
        args.Handled = true;
    }

    private void ShowSettingsMenu(Point point)
    {
        ShowMenu([
            new("Atrás", "\uE72B", () => _web?.Back(), "Alt+←", _activeTab?.CanGoBack == true),
            new("Actualizar", "\uE72C", () => { if (_activeTab is { } tab) { tab.InternalContent = null; ShowInternalPage(tab); } }, "Ctrl+R"),
            null,
            new("Apariencia", "\uE790", () => NavigateSettings("Apariencia")),
            new("Privacidad, búsqueda y servicios", "\uE72E", () => NavigateSettings("Privacidad, búsqueda y servicios")),
            new("Sistema y rendimiento", "\uE9D9", () => NavigateSettings("Sistema y rendimiento")),
            null,
            new("Nueva pestaña", "\uE710", () => TryAddTab(), "Ctrl+T"),
            new("Favoritos", "\uE728", ShowBookmarks, "Ctrl+Mayús+O"),
            new("Historial", "\uE81C", ShowHistory, "Ctrl+H"),
            new("Descargas", "\uE896", ShowDownloads, "Ctrl+J")
        ], point, width: 310);
    }

    private void ShowNewTabMenu(Point point)
    {
        ShowMenu([
            new("Atrás", "\uE72B", () => _web?.Back(), "Alt+←", _activeTab?.CanGoBack == true),
            new("Actualizar", "\uE72C", BuildNewTab, "Ctrl+R"),
            null,
            new("Agregar vínculo rápido", "\uE710", AddQuickLink),
            new(Preferences.NewTabQuickLinks ? "Ocultar vínculos rápidos" : "Mostrar vínculos rápidos", "\uE8A9",
                () => BrowserApp.SavePreferences(Preferences with { NewTabQuickLinks = !Preferences.NewTabQuickLinks })),
            new(Preferences.NewTabBackground ? "Ocultar imagen de fondo" : "Mostrar imagen de fondo", "\uEB9F",
                () => BrowserApp.SavePreferences(Preferences with { NewTabBackground = !Preferences.NewTabBackground })),
            null,
            new("Importar favoritos", "\uE8B5", ImportBookmarks),
            new("Exportar favoritos", "\uE74E", ExportBookmarks),
            new("Personalizar nueva pestaña", "\uE713", () => NavigateSettings("Página de inicio, inicio y pestaña nueva"))
        ], point, width: 310);
    }

    private void NewTabSearch_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(NewTabSearch.Text)) return;
        SetAddressText(NewTabSearch.Text);
        NewTabSearch.Text = "";
        NavigateAddress();
        args.Handled = true;
    }

    private void BuildNewTab()
    {
        bool dark = IsDark;
        NewTabSearchHost.Background = dark ? Brush(235, 43, 43, 43) : Brush(245, 255, 255, 255);
        NewTabSearchHost.BorderBrush = dark ? Brush(255, 70, 70, 70) : Brush(255, 218, 218, 218);
        NewTabSearch.Resources["TextControlBackground"] = Brush(0, 0, 0, 0);
        NewTabSearch.Resources["TextControlBackgroundPointerOver"] = Brush(0, 0, 0, 0);
        NewTabSearch.Resources["TextControlBackgroundFocused"] = Brush(0, 0, 0, 0);
        NewTabSearch.Resources["TextControlBorderBrushFocused"] = Brush(0, 0, 0, 0);
        NewTabGreeting.Text = DateTime.Now.Hour switch { < 12 => "Buenos días", < 20 => "Buenas tardes", _ => "Buenas noches" } + ", ¿qué quieres buscar?";
        ApplyNewTabBackground(null);
        if (Preferences.NewTabBackground) LoadBingBackground();
        QuickLinks.Visibility = Preferences.NewTabQuickLinks ? Visibility.Visible : Visibility.Collapsed;
        BuildQuickLinks();
    }

    private void ApplyNewTabBackground((string Url, string Credit)? image)
    {
        bool show = image is not null && Preferences.NewTabBackground;
        NewTabScrim.Visibility = NewTabCredit.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            NewTabBackgroundImage.Source = null;
            NewTabBackgroundImage.Opacity = 0;
            NewTabGreeting.ClearValue(TextBlock.ForegroundProperty);
            return;
        }
        NewTabCredit.Text = image!.Value.Credit;
        var bitmap = new BitmapImage(new Uri(image.Value.Url));
        bitmap.ImageOpened += (_, _) => NewTabBackgroundImage.Opacity = 1;
        bitmap.ImageFailed += (_, _) => ApplyNewTabBackground(null);
        NewTabBackgroundImage.Source = bitmap;
        NewTabGreeting.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private async void LoadBingBackground()
    {
        _bingImage ??= FetchBingImage();
        (string Url, string Credit)? image = await _bingImage;
        if (image is null) _bingImage = null;
        if (NewTabPanel.Visibility == Visibility.Visible && Preferences.NewTabBackground) ApplyNewTabBackground(image);
    }

    private static async Task<(string Url, string Credit)?> FetchBingImage()
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(await Http.GetStringAsync("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=es-ES"));
            JsonElement first = json.RootElement.GetProperty("images")[0];
            string path = first.GetProperty("url").GetString() ?? "";
            if (!path.StartsWith('/')) return null;
            return ("https://www.bing.com" + path, first.TryGetProperty("copyright", out JsonElement c) ? c.GetString() ?? "" : "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            System.Diagnostics.Debug.WriteLine($"Imagen de Bing no disponible: {e.Message}");
            return null;
        }
    }

    private static QuickLinkStore? _quickLinks;

    private static QuickLinkStore LoadQuickLinks()
    {
        // Read once per run: the new-tab page is rebuilt on every visit and must not touch the disk each time.
        if (_quickLinks is not null) return _quickLinks;
        try { return _quickLinks = ProfileStorage.Read("quicklinks.json", new QuickLinkStore([], [])); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return _quickLinks = new QuickLinkStore([], []); }
    }

    private static string HostOf(string address) => Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ? uri.Host.ToLowerInvariant() : address;

    private void BuildQuickLinks()
    {
        QuickLinks.Children.Clear();
        if (!Preferences.NewTabQuickLinks) return;
        QuickLinkStore store = LoadQuickLinks();
        var links = new List<(QuickLink Link, string? Favicon, bool Pinned)>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? FaviconFor(string host) => BrowserApp.History.FirstOrDefault(h => HostOf(h.Address) == host && h.Favicon is not null)?.Favicon
            ?? _bookmarks.FirstOrDefault(b => HostOf(b.Address) == host && b.Favicon is not null)?.Favicon;
        foreach (QuickLink link in store.Pinned)
            if (hosts.Add(HostOf(link.Address))) links.Add((link, FaviconFor(HostOf(link.Address)), true));
        foreach (var group in BrowserApp.History.Where(h => WebAddress.IsHttp(h.Address)).GroupBy(h => HostOf(h.Address)).OrderByDescending(g => g.Count()))
        {
            if (links.Count >= 7) break;
            if (store.Hidden.Contains(group.Key) || !hosts.Add(group.Key)) continue;
            HistoryEntry latest = group.First();
            Uri uri = new(latest.Address);
            string title = group.Key.StartsWith("www.") ? group.Key[4..] : group.Key;
            title = title.Split('.')[0];
            title = title.Length > 0 ? char.ToUpper(title[0], Spanish) + title[1..] : group.Key;
            links.Add((new QuickLink(title, $"{uri.Scheme}://{uri.Host}/"), group.FirstOrDefault(h => h.Favicon is not null)?.Favicon, false));
        }
        foreach (var (link, favicon, pinned) in links.Take(7))
            QuickLinks.Children.Add(QuickTile(SiteIcon(FaviconLoader.Address(favicon, link.Address), 24), link.Title, () => NavigateCurrent(link.Address),
                p => ShowMenu([
                    new("Abrir en una pestaña nueva", "\uE8A7", () => TryAddTab(link.Address, activate: false)),
                    new("Copiar vínculo", "\uE8C8", () => CopyText(link.Address)),
                    null,
                    new("Quitar", "\uE74D", () => RemoveQuickLink(link, pinned))
                ], p, width: 260)));
        QuickLinks.Children.Add(QuickTile(new FontIcon { Glyph = "\uE710", FontSize = 18 }, "Agregar", AddQuickLink, null));
    }

    private Button QuickTile(UIElement icon, string title, Action open, Action<Point>? context)
    {
        bool imageBackground = NewTabBackgroundImage.Source is not null && Preferences.NewTabBackground;
        var plate = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(Fx.OverlayRadius), HorizontalAlignment = HorizontalAlignment.Center,
            Background = IsDark ? Brush(220, 45, 45, 45) : Brush(235, 255, 255, 255),
            BorderBrush = IsDark ? Brush(255, 64, 64, 64) : Brush(255, 225, 225, 225), BorderThickness = new Thickness(1), Child = icon
        };
        if (icon is FrameworkElement element) { element.HorizontalAlignment = HorizontalAlignment.Center; element.VerticalAlignment = VerticalAlignment.Center; }
        var content = new StackPanel { Spacing = 6, Width = 76 };
        content.Children.Add(plate);
        var label = new TextBlock { Text = title, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        if (imageBackground) label.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        content.Children.Add(label);
        var button = new Button
        {
            Content = content, Width = 86, Height = 88, Padding = new Thickness(4, 6, 4, 4), CornerRadius = new CornerRadius(Fx.OverlayRadius),
            Background = Brush(0, 0, 0, 0), BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, title);
        button.Click += (_, _) => open();
        Fx.Interactive(button, hover: 1.07f, pressed: .95f);
        if (context is not null) button.ContextRequested += (_, e) =>
        {
            if (!e.TryGetPosition(PageGrid, out Point point)) point = button.TransformToVisual(PageGrid).TransformPoint(new Point(0, button.ActualHeight));
            context(point);
            e.Handled = true;
        };
        if (context is not null) button.RightTapped += (_, e) => { context(e.GetPosition(PageGrid)); e.Handled = true; };
        return button;
    }

    private void RemoveQuickLink(QuickLink link, bool pinned)
    {
        QuickLinkStore store = LoadQuickLinks();
        if (pinned) store.Pinned.RemoveAll(l => l.Address == link.Address);
        else store.Hidden.Add(HostOf(link.Address));
        SaveQuickLinks(store);
    }

    private void SaveQuickLinks(QuickLinkStore store)
    {
        _quickLinks = store;
        try { ProfileStorage.Write("quicklinks.json", store); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError("No se pudieron guardar los vínculos rápidos", e.Message); }
        BuildQuickLinks();
    }

    private async void AddQuickLink()
    {
        var name = new TextBox { Header = IconLabel("\uE8AC", "Nombre"), PlaceholderText = "Ejemplo" };
        var url = new TextBox { Header = IconLabel("\uE71B", "Dirección URL"), PlaceholderText = "https://www.ejemplo.com" };
        var error = new TextBlock { FontSize = 12, Foreground = Brush(255, 196, 43, 28), TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(name); body.Children.Add(url); body.Children.Add(error);
        await ShowDialog("Agregar sitio", body, "Agregar", () =>
        {
            string text = url.Text.Trim();
            if (!text.Contains("://")) text = "https://" + text;
            if (!WebAddress.IsHttp(text) || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || !uri.Host.Contains('.'))
            {
                error.Text = "Escribe una dirección web válida, por ejemplo https://www.ejemplo.com.";
                return false;
            }
            QuickLinkStore store = LoadQuickLinks();
            store.Pinned.RemoveAll(l => HostOf(l.Address) == uri.Host.ToLowerInvariant());
            store.Hidden.Remove(uri.Host.ToLowerInvariant());
            store.Pinned.Add(new QuickLink(string.IsNullOrWhiteSpace(name.Text) ? uri.Host : name.Text.Trim(), uri.AbsoluteUri));
            SaveQuickLinks(store);
            return true;
        });
    }
}
