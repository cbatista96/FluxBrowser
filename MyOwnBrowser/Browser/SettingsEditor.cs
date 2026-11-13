using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;
using Windows.System;

namespace MyOwnBrowser.Browser;

internal sealed record SettingsActions(Action ClearData, Action ImportBookmarks, Action ExportBookmarks,
    Action ShowBookmarks, Action ShowAbout, Func<Task<string?>> PickDownloadDirectory);

internal sealed class SettingsEditor : UserControl
{
    private BrowserPreferences _draft;
    private readonly Action<BrowserPreferences> _save;
    private readonly Action _clearHistory, _clearCookies, _testContext, _testDownload, _benchmark, _diagnostic;
    private readonly SettingsActions _actions;
    private readonly StackPanel _fields = new() { Spacing = 16, MaxWidth = 810, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _search = new() { PlaceholderText = "Buscar en configuración", Height = 32, MinHeight = 0, FontSize = 13, Padding = new Thickness(12, 5, 12, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly InfoBar _message = new() { IsOpen = false, IsClosable = true };
    private readonly HashSet<NumberBox> _invalidNumbers = [];
    private readonly Grid _pane = new() { Width = 300 };
    private readonly StackPanel _items = new() { Spacing = 2 };
    private readonly Border _indicator = new() { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TranslateTransform _indicatorMove = new();
    private readonly TranslateTransform _contentMove = new();
    private readonly ScrollViewer _scroll;
    private readonly List<Button> _buttons = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private bool _compact;
    private readonly Grid _menuHeader = new() { Visibility = Visibility.Collapsed, ColumnSpacing = 10 };
    private readonly TextBlock _menuTitle = new() { Text = "Configuración", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button? _menuButton;
    private Flyout? _menuFlyout;
    private StackPanel? _group;
    private string _category = "Perfiles";
    private string _renderingCategory = "";
    private readonly List<(FrameworkElement Row, string Search)> _rows = [];
    private static readonly (string Name, string Icon)[] Categories =
    [
        ("Perfiles", "\uE77B"), ("Contraseñas y autorrellenar", "\uE8D7"),
        ("Privacidad, búsqueda y servicios", "\uE72E"),
        ("Apariencia", "\uE790"), ("Navegador predeterminado", "\uE774"),
        ("Página de inicio, inicio y pestaña nueva", "\uE80F"), ("Idiomas", "\uE8C1"),
        ("Descargas", "\uE896"), ("Accesibilidad", "\uE776"),
        ("Sistema y rendimiento", "\uE9D9"), ("Restablecer configuración", "\uE777"),
        ("Extensiones", "\uEA86"), ("Acerca de My Own Browser", "\uE946"), ("Diagnóstico", "\uE9CE")
    ];

    public SettingsEditor(BrowserPreferences preferences, Action<BrowserPreferences> save, Action clearHistory, Action clearCookies,
        Action testContext, Action testDownload, Action benchmark, Action diagnostic, SettingsActions actions)
    {
        _draft = preferences with { };
        _save = save; _clearHistory = clearHistory; _clearCookies = clearCookies;
        _testContext = testContext; _testDownload = testDownload; _benchmark = benchmark; _diagnostic = diagnostic;
        _actions = actions;
        _saveTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();

        _pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _pane.Children.Add(new TextBlock { Text = "Configuración", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(24, 24, 16, 18) });
        _search.Margin = new Thickness(16, 0, 16, 14);
        AutomationProperties.SetName(_search, "Buscar en configuración");
        _search.TextChanged += (_, _) => { Render(); ApplyNavigationTheme(); MoveIndicator(false); AnimateContent(); };
        Grid.SetRow(_search, 1); _pane.Children.Add(_search);
        var itemsHost = new Grid { Margin = new Thickness(12, 0, 12, 12) };
        _indicator.RenderTransform = _indicatorMove;
        _indicator.Margin = new Thickness(4, 0, 0, 0);
        itemsHost.Children.Add(_items);
        itemsHost.Children.Add(_indicator);
        foreach (var category in Categories) AddNavigationItem(category.Name, category.Icon);
        var paneScroll = new ScrollViewer { Content = itemsHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(paneScroll, 2); _pane.Children.Add(paneScroll);

        var content = new Grid { Padding = new Thickness(28, 24, 28, 16), RowSpacing = 16 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        BuildMenuHeader();
        content.Children.Add(_menuHeader);
        _fields.RenderTransform = _contentMove;
        _scroll = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Center };
        _scroll.SizeChanged += (_, _) => _fields.Width = Math.Max(1, Math.Min(810, _scroll.ActualWidth - 32));
        Grid.SetRow(_scroll, 1); content.Children.Add(_scroll);
        _message.MaxWidth = 810;
        Grid.SetRow(_message, 2); content.Children.Add(_message);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_pane);
        Grid.SetColumn(content, 1); root.Children.Add(content);
        Content = root;
        SizeChanged += (_, _) => SetCompact(ActualWidth < 820);
        ActualThemeChanged += (_, _) => { ApplyNavigationTheme(); Render(); };
        Loaded += (_, _) => { ApplyNavigationTheme(); MoveIndicator(false); };
        Render();
    }

    private void AddNavigationItem(string name, string glyph)
    {
        var grid = new Grid { ColumnSpacing = 14, Height = 36, Padding = new Thickness(14, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var text = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var button = new Button
        {
            Content = grid, Tag = name, Height = 36, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(Motion.Fx.ControlRadius),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Select(name);
        _buttons.Add(button);
        _items.Children.Add(button);
    }

    /// <summary>
    /// A narrow window folds the whole category list into a hamburger menu. The entries always keep their text next to the icon:
    /// a row of bare icons says nothing about what each one opens.
    /// </summary>
    private void SetCompact(bool compact)
    {
        if (_compact == compact && _pane.Visibility == (compact ? Visibility.Collapsed : Visibility.Visible)) return;
        _compact = compact;
        _pane.Width = 300;
        _pane.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _menuHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildMenuHeader()
    {
        _menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _menuButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 18 }, Width = 40, Height = 40, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(Motion.Fx.ControlRadius)
        };
        AutomationProperties.SetName(_menuButton, "Categorías de configuración");
        ToolTipService.SetToolTip(_menuButton, "Categorías de configuración");
        _menuButton.Click += (_, _) => ShowCategoryMenu();
        _menuHeader.Children.Add(_menuButton);
        Grid.SetColumn(_menuTitle, 1);
        _menuHeader.Children.Add(_menuTitle);
    }

    private void ShowCategoryMenu()
    {
        var list = new StackPanel { Spacing = 2, MinWidth = 300 };
        var search = new TextBox { PlaceholderText = "Buscar en configuración", Text = _search.Text, Height = 32, MinHeight = 0, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
        search.TextChanged += (_, _) => { if (_search.Text != search.Text) _search.Text = search.Text; };
        list.Children.Add(search);
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach ((string name, string glyph) in Categories)
        {
            var row = new Grid { ColumnSpacing = 14, Height = 36, Padding = new Thickness(10, 0, 8, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            var text = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            string captured = name;
            var button = new Button
            {
                Content = row, Height = 36, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(Motion.Fx.ControlRadius),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(name == _category && _search.Text.Length == 0
                    ? (dark ? Windows.UI.Color.FromArgb(28, 255, 255, 255) : Windows.UI.Color.FromArgb(18, 0, 0, 0)) : Microsoft.UI.Colors.Transparent)
            };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => { _menuFlyout?.Hide(); Select(captured); };
            list.Children.Add(button);
        }
        _menuFlyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        _menuFlyout.ShowAt(_menuButton);
    }

    private void ApplyNavigationTheme()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _indicator.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 96, 205, 255) : Windows.UI.Color.FromArgb(255, 0, 95, 184));
        foreach (Button button in _buttons)
            button.Background = new SolidColorBrush((string)button.Tag == _category && _search.Text.Length == 0
                ? (dark ? Windows.UI.Color.FromArgb(22, 255, 255, 255) : Windows.UI.Color.FromArgb(14, 0, 0, 0))
                : Microsoft.UI.Colors.Transparent);
    }

    private void Select(string name)
    {
        if (_category == name && _search.Text.Length == 0) return;
        _category = name;
        if (_search.Text.Length > 0) _search.Text = "";
        else Render();
        ApplyNavigationTheme();
        MoveIndicator(true);
        _scroll.ChangeView(null, 0, null, true);
        AnimateContent();
    }

    private void MoveIndicator(bool animate)
    {
        int index = Array.FindIndex(Categories, c => c.Name == _category);
        _indicator.Visibility = index < 0 || _search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (index < 0) return;
        double target = index * 38 + 10;
        if (!animate || !MotionEnabled) { _indicatorMove.Y = target; return; }
        // A single indicator slides between rows, so it never stretches or splits like NavigationView's.
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = target, Duration = TimeSpan.FromMilliseconds(260), EnableDependentAnimation = false,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
        };
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, _indicatorMove);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Y");
        board.Children.Add(animation);
        board.Begin();
    }

    private void AnimateContent()
    {
        if (!MotionEnabled) return;
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, _fields);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 14, To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = ease };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, _contentMove);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "Y");
        board.Children.Add(fade); board.Children.Add(slide);
        board.Begin();
    }

    private bool MotionEnabled => _draft.AnimateInterface && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    public void NavigateTo(string category)
    {
        if (Categories.Any(c => c.Name == category)) Select(category);
    }

    // Every change is applied after a short pause, so sliders and text boxes don't rewrite the profile on each keystroke.
    private void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        _saveTimer.Stop();
        try
        {
            if (_invalidNumbers.Count > 0) return;
            _draft.Validate();
            _save(_draft with { });
            if (_message.Severity == InfoBarSeverity.Error) _message.IsOpen = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { Message(InfoBarSeverity.Error, "No se aplicó este cambio", e.Message); }
    }

    private void Message(InfoBarSeverity severity, string title, string text)
    { _message.Severity = severity; _message.Title = title; _message.Message = text; _message.IsOpen = true; }
    private void Render()
    {
        _fields.Children.Clear(); _rows.Clear(); _invalidNumbers.Clear(); _group = null;
        string query = _search.Text.Trim();
        foreach (string category in query.Length == 0 ? new[] { _category } : Categories.Select(c => c.Name))
        {
            _renderingCategory = category;
            _group = null;
            var title = new TextBlock { Text = category, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
            int before = _fields.Children.Count;
            _fields.Children.Add(title);
            BuildCategory(category);
            if (query.Length > 0)
            {
                foreach (var row in _rows) row.Row.Visibility = row.Search.Contains(query, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                bool any = false;
                foreach (var element in _fields.Children.Skip(before + 1).OfType<FrameworkElement>())
                {
                    if (element is Border { Child: StackPanel group })
                    {
                        bool visible = group.Children.OfType<FrameworkElement>().Any(e => e.Visibility == Visibility.Visible);
                        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                        any |= visible;
                    }
                    else element.Visibility = Visibility.Collapsed;
                }
                title.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        if (query.Length > 0 && !_rows.Any(r => r.Row.Visibility == Visibility.Visible))
            _fields.Children.Add(new TextBlock { Text = "No se encontraron opciones. Prueba con «pestañas», «fuentes» o «descargas».", TextWrapping = TextWrapping.Wrap });
    }

    private void BuildCategory(string category)
    {
        switch (category)
        {
            case "Perfiles":
                Group("Perfil local");
                Text("\uE77B", "Nombre del perfil", _draft.ProfileName, value => _draft.ProfileName = value.Trim(), "Este perfil se guarda en este equipo. No has iniciado sesión en Microsoft.");
                Note("Sincronización y cuentas", "No hay sincronización en la nube, Microsoft Rewards, áreas de trabajo ni perfiles separados implementados. No se simula una sesión de Microsoft.");
                Group("Importar y exportar datos");
                ActionButton("\uE8B5", "Importar favoritos desde HTML", _actions.ImportBookmarks, "Conserva las carpetas y tus favoritos anteriores.");
                ActionButton("\uE74E", "Exportar favoritos a HTML", _actions.ExportBookmarks, "Archivo compatible con Edge, Chrome y Firefox.");
                break;
            case "Contraseñas y autorrellenar":
                Group("Datos sensibles");
                Note("Administrador de contraseñas", "No disponible: faltan el sandbox y una bóveda segura integrada con Windows. No se guardan contraseñas ni passkeys.");
                Note("Métodos de pago", "No se almacenan ni se autorrellenan tarjetas de pago.");
                Note("Direcciones y autorrellenado mejorado", "No hay un almacén de direcciones ni un servicio de autorrellenado. Los formularios de los sitios siguen funcionando.");
                break;
            case "Privacidad, búsqueda y servicios":
                Group("Datos de exploración");
                Toggle("\uE81C", "Guardar historial de navegación", _draft.RememberHistory, value => _draft.RememberHistory = value);
                Toggle("\uE8D7", "Mantener las sesiones iniciadas al cerrar el navegador", _draft.PersistSessions, v => _draft.PersistSessions = v,
                    WebEngine.UseWebView2
                        ? "Mantiene tu sesión en los sitios al cerrar el navegador. Los datos se guardan en tu carpeta de perfil y el motor los ejecuta dentro del sandbox de Chromium. Requiere cerrar todas las ventanas y volver a abrir."
                        : "Guarda cookies y caché en disco. Sin el sandbox de Chromium, una página maliciosa que explotara el motor podría leerlas: actívalo solo si aceptas ese riesgo. Requiere cerrar todas las ventanas y volver a abrir.");
                ActionButton("\uE74D", "Borrar datos de exploración", _actions.ClearData, "Selecciona historial, cookies, caché y pestañas cerradas.");
                ActionButton("\uE81C", "Borrar historial", _clearHistory);
                ActionButton("\uE774", "Borrar cookies", _clearCookies, "Cierra las sesiones de los sitios de las pestañas normales.");
                Group("Permisos del sitio");
                Toggle("\uE943", "Permitir JavaScript en nuevas pestañas", _draft.JavaScriptEnabled, v => _draft.JavaScriptEnabled = v, "Desactivarlo puede impedir que algunos sitios funcionen.");
                Note("Cámara, micrófono y otros permisos", WebEngine.UseWebView2
                    ? "Cada sitio pregunta la primera vez y el motor recuerda tu respuesta. No se concede ningún permiso automáticamente."
                    : "Las solicitudes están bloqueadas en este laboratorio. No se conceden permisos automáticamente.");
                Group("Búsqueda");
                Choice("\uE721", "Motor de búsqueda", ["DuckDuckGo", "Bing", "Google"], ["DuckDuckGo", "Bing", "Google"], _draft.SearchEngine, v => _draft.SearchEngine = v);
                Toggle("", "Sugerencias en la barra de direcciones", _draft.AddressSuggestions, v => _draft.AddressSuggestions = v,
                    "Mientras escribes se ofrecen tus pestañas abiertas, favoritos e historial. Se calculan en este equipo: lo que tecleas no se envía a ningún buscador hasta que buscas.");
                if (WebEngine.UseWebView2)
                {
                    Group("Prevención de seguimiento");
                    Choice("", "Bloqueo de rastreadores", ["Basic", "Balanced", "Strict"], ["Básico", "Equilibrado (recomendado)", "Estricto"], _draft.TrackingPrevention, v => _draft.TrackingPrevention = v);
                    Note("Qué hace cada nivel", "Básico bloquea solo los rastreadores dañinos. Equilibrado bloquea además los de sitios que no has visitado. Estricto bloquea la mayoría: las páginas cargan menos contenido de terceros, pero alguna puede dejar de funcionar. Se aplica a las pestañas que abras a partir de ahora. InPrivate no significa anonimato.");
                    break;
                }
                Note("Seguridad y prevención de seguimiento", "Sin sandbox de Chromium, lista antirrastreadores ni protección SmartScreen de Microsoft. No uses contraseñas ni datos sensibles. InPrivate no significa anonimato.");
                break;
            case "Apariencia":
                Group("Apariencia general");
                Choice("\uE790", "Tema del navegador", ["System", "Light", "Dark"], ["Sistema", "Claro", "Oscuro"], _draft.Theme, v => _draft.Theme = v);
                Choice("\uE793", "Colores preferidos de las páginas", ["Browser", "Light", "Dark"], ["Seguir el navegador", "Claro", "Oscuro"], _draft.PageTheme, v => _draft.PageTheme = v);
                Toggle("\uE7F4", "Animaciones de pestañas y menús", _draft.AnimateInterface, v => _draft.AnimateInterface = v, "También respeta la opción de animaciones de Windows.");
                Group("Cristal y acrílico");
                Slider("\uE7B3", "Desenfoque del cristal", 0, 30, _draft.BlurRadius, 1, v => _draft.BlurRadius = v);
                Slider("\uE7C3", "Opacidad de las barras", .25, .95, _draft.GlassOpacity, .01, v => _draft.GlassOpacity = v);
                Slider("\uE712", "Opacidad de menús y paneles", .4, .98, _draft.MenuOpacity, .01, v => _draft.MenuOpacity = v);
                Group("Barra de herramientas");
                Toggle("\uE80F", "Mostrar botón Inicio", _draft.ShowHomeButton, v => _draft.ShowHomeButton = v);
                Toggle("\uE728", "Mostrar botón Favoritos", _draft.ShowFavoritesButton, v => _draft.ShowFavoritesButton = v);
                Toggle("\uE81C", "Mostrar botón Historial", _draft.ShowHistoryButton, v => _draft.ShowHistoryButton = v);
                Toggle("\uE896", "Mostrar botón Descargas", _draft.ShowDownloadsButton, v => _draft.ShowDownloadsButton = v);
                Toggle("\uE734", "Mostrar barra de favoritos", _draft.ShowBookmarks, v => _draft.ShowBookmarks = v);
                ActionButton("\uE728", "Administrar favoritos", _actions.ShowBookmarks);
                Group("Pestañas");
                Toggle("\uE8A5", "Usar pestañas verticales", _draft.VerticalTabs, v => _draft.VerticalTabs = v);
                Slider("\uE745", "Anchura de pestañas verticales", 180, 360, _draft.SidebarWidth, 4, v => _draft.SidebarWidth = v);
                Toggle("\uE7BA", "Preguntar antes de cerrar varias pestañas", _draft.ConfirmCloseMultipleTabs, v => _draft.ConfirmCloseMultipleTabs = v);
                Group("Fuentes y zoom");
                Slider("\uE740", "Zoom predeterminado", .25, 5, _draft.DefaultZoom, .05, v => _draft.DefaultZoom = v);
                Slider("\uE8D2", "Tamaño de fuente en nuevas pestañas", 9, 48, _draft.WebFontSize, 1, v => _draft.WebFontSize = (int)v);
                Slider("\uE8D2", "Tamaño mínimo de fuente", 0, 32, _draft.MinimumWebFontSize, 1, v => _draft.MinimumWebFontSize = (int)v);
                break;
            case "Navegador predeterminado":
                Group("Aplicaciones predeterminadas de Windows");
                if (Program.HasPackageIdentity)
                {
                    Note("Registro del navegador", "El paquete registra HTTP y HTTPS. Elige My Own Browser en Aplicaciones predeterminadas de Windows.");
                    ActionButton("\uE713", "Abrir aplicaciones predeterminadas", () => OpenWindowsSettings("ms-settings:defaultapps"));
                    break;
                }
                bool registered = DefaultBrowser.IsRegistered(Environment.ProcessPath ?? "");
                Note("Registro del navegador", registered
                    ? "Registrado para HTTP y HTTPS. Elígelo en «Aplicaciones predeterminadas»: Windows exige que seas tú quien confirme el cambio."
                    : "No registrado. El registro es solo para tu usuario (no requiere administrador) y hace que aparezca en la lista de navegadores de Windows.");
                if (!registered)
                    ActionButton("\uE73E", "Registrar en Windows", () =>
                    {
                        try
                        {
                            DefaultBrowser.Register(Environment.ProcessPath ?? throw new InvalidOperationException("No se conoce la ruta del ejecutable."));
                            Render();
                            OpenWindowsSettings("ms-settings:defaultapps");
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                        { Message(InfoBarSeverity.Error, "No se pudo registrar el navegador", e.Message); }
                    }, "Si mueves la carpeta de la aplicación, vuelve a registrarla.");
                else
                    ActionButton("\uE74D", "Quitar el registro", () =>
                    {
                        try { DefaultBrowser.Unregister(); Render(); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        { Message(InfoBarSeverity.Error, "No se pudo quitar el registro", e.Message); }
                    });
                ActionButton("\uE713", "Abrir aplicaciones predeterminadas", () => OpenWindowsSettings("ms-settings:defaultapps"));
                Group("Compatibilidad de Internet Explorer");
                Note("Modo Internet Explorer", "No disponible en este motor. Es una integración exclusiva de Microsoft Edge, no una función de Chromium.");
                break;
            case "Página de inicio, inicio y pestaña nueva":
                Group("En el inicio");
                Choice("\uE777", "Al abrir el navegador", ["new", "restore", "custom"], ["Abrir nueva pestaña", "Restaurar sesión anterior", "Abrir sitios personalizados"],
                    _draft.StartupUseCustomPages ? "custom" : _draft.RestoreSession ? "restore" : "new",
                    v => { _draft.RestoreSession = v == "restore"; _draft.StartupUseCustomPages = v == "custom"; });
                Text("\uE8A7", "Sitios personalizados (uno por línea)", _draft.StartupPages, v => _draft.StartupPages = v, "Se usan al elegir «Abrir sitios personalizados».", multiline: true);
                Group("Botón Inicio");
                Toggle("\uE80F", "Mostrar el botón Inicio", _draft.ShowHomeButton, v => _draft.ShowHomeButton = v);
                Text("\uE80F", "Página del botón Inicio", _draft.HomePage, v => _draft.HomePage = v.Trim(), "browser:home o una dirección HTTP/HTTPS.");
                Group("Página de nueva pestaña");
                Text("\uE710", "Página de nuevas pestañas", _draft.NewTabPage, v => _draft.NewTabPage = v.Trim(), "Usa browser:home para la página integrada.");
                Toggle("\uEB9F", "Imagen del día de Bing", _draft.NewTabBackground, v => _draft.NewTabBackground = v);
                Toggle("\uE8A9", "Mostrar vínculos rápidos", _draft.NewTabQuickLinks, v => _draft.NewTabQuickLinks = v);
                Note("Conexiones de nueva pestaña", "El fondo se obtiene de Bing e incluye su crédito. No se activa inicio automático con Windows ni notificaciones de la barra de tareas.");
                break;
            case "Idiomas":
                Group("Idiomas preferidos");
                Text("\uE8C1", "Idiomas de las páginas, en orden", _draft.PreferredLanguages, v => _draft.PreferredLanguages = v.Trim(), "Códigos separados por comas: es,es-ES,en-US,en. Se aplican a nuevas pestañas.");
                Toggle("\uE8F2", "Revisión ortográfica básica de Chromium", _draft.Spellcheck, v => _draft.Spellcheck = v, "Se aplica a nuevas pestañas; puede necesitar descargar diccionarios de Chromium.");
                Note("Traducción y asistencia de escritura", "Microsoft Translator, Editor de Microsoft y predicción de texto en la nube no están integrados. La interfaz de este navegador está en español.");
                break;
            case "Descargas":
                Group("Ubicación");
                Text("\uE8B7", "Carpeta de descargas", _draft.DownloadDirectory, v => _draft.DownloadDirectory = v.Trim());
                ActionButton("\uE838", "Cambiar carpeta", PickFolder);
                Toggle("\uE8B5", "Preguntar dónde guardar cada archivo", _draft.AskDownloadLocation, v => _draft.AskDownloadLocation = v);
                Toggle("\uE896", "Mostrar menú al iniciar una descarga", _draft.ShowDownloadsOnStart, v => _draft.ShowDownloadsOnStart = v);
                Note("Abrir archivos", "Los archivos nunca se ejecutan automáticamente. No se integra el visor de Office de Microsoft.");
                break;
            case "Accesibilidad":
                Group("Visibilidad");
                Slider("\uE740", "Zoom de página predeterminado", .25, 5, _draft.DefaultZoom, .05, v => _draft.DefaultZoom = v);
                Slider("\uE8D2", "Tamaño mínimo de fuente en nuevas pestañas", 0, 32, _draft.MinimumWebFontSize, 1, v => _draft.MinimumWebFontSize = (int)v);
                Choice("\uE793", "Colores preferidos de página", ["Browser", "Light", "Dark"], ["Seguir el navegador", "Claro", "Oscuro"], _draft.PageTheme, v => _draft.PageTheme = v);
                ActionButton("\uE713", "Abrir preferencias de subtítulos de Windows", () => OpenWindowsSettings("ms-settings:easeofaccess-closedcaptioning"));
                Group("Facilidad de uso");
                Toggle("\uE7F4", "Animaciones de la interfaz", _draft.AnimateInterface, v => _draft.AnimateInterface = v, "Si Windows desactiva animaciones, no se muestran aunque esta opción esté activa.");
                Toggle("\uE7BA", "Preguntar antes de cerrar varias pestañas", _draft.ConfirmCloseMultipleTabs, v => _draft.ConfirmCloseMultipleTabs = v);
                Toggle("\uE896", "Mostrar menú al iniciar descargas", _draft.ShowDownloadsOnStart, v => _draft.ShowDownloadsOnStart = v);
                Note("Accesibilidad web pendiente", "Los controles WinUI tienen nombres y navegación por teclado. El árbol de accesibilidad del contenido CEF, descripciones automáticas de imágenes y gestos táctiles todavía no están integrados.");
                break;
            case "Sistema y rendimiento":
                if (WebEngine.UseWebView2)
                {
                    Group("Motor");
                    Note("WebView2", "El renderizado, la frecuencia de fotogramas y el desplazamiento los gestiona el motor de WebView2, aislado en el sandbox de Chromium.");
                    Group("Memoria");
                    Toggle("\uE708", "Pestañas en suspensión", _draft.PauseHiddenTabs, v => _draft.PauseHiddenTabs = v,
                        "Una pestaña que no miras libera memoria al minuto y se suspende a los cinco, salvo que reproduzca sonido, descargue algo o siga cargando. Al volver a ella continúa donde estaba.");
                    break;
                }
                Group("Renderizado");
                Choice("\uE9D9", "Frecuencia solicitada", ["30", "60", "90", "120", "144", "165", "240"], ["30 FPS · ahorro", "60 FPS", "90 FPS", "120 FPS", "144 FPS", "165 FPS", "240 FPS"], _draft.FrameRate.ToString(), v => _draft.FrameRate = int.Parse(v));
                Toggle("\uE708", "Pausar renderizado de pestañas ocultas", _draft.PauseHiddenTabs, v => _draft.PauseHiddenTabs = v, "No detiene scripts, audio ni descargas.");
                Toggle("\uE74B", "Desplazamiento suave de Chromium", _draft.SmoothScrolling, v => _draft.SmoothScrolling = v, "Requiere cerrar todas las ventanas y volver a abrir.");
                Toggle("\uE9D9", "Mostrar mediciones de rendimiento", _draft.ShowPerformance, v => _draft.ShowPerformance = v);
                Note("Aceleración gráfica", "Direct3D 11 y composición GPU están siempre activos: son necesarios para difuminar la web. No se muestra un interruptor ficticio para desactivarlos.");
                ActionButton("\uE74B", "Medir desplazamiento durante 5 segundos", _benchmark);
                break;
            case "Restablecer configuración":
                Group("Restaurar valores predeterminados");
                ActionButton("\uE777", "Restablecer ajustes", Reset, "No elimina favoritos, historial ni archivos descargados.");
                break;
            case "Extensiones":
                Group("Compatibilidad de extensiones");
                Note("Extensiones de Edge y Chrome", "No disponibles: este prototipo no integra la API, el administrador ni la instalación de extensiones. No se simula compatibilidad con las tiendas.");
                break;
            case "Acerca de My Own Browser":
                Group("Versiones");
                if (WebEngine.UseWebView2)
                {
                    Note("My Own Browser", $"Chromium {WebView2Session.RuntimeVersion} (Runtime de WebView2) · WinUI 3");
                    Note("Actualizaciones y sandbox", "El motor se ejecuta dentro del sandbox de Chromium y lo actualiza Windows junto con el Runtime de WebView2. La aplicación todavía no tiene actualizador propio: es una vista previa técnica.");
                }
                else
                {
                    Note("My Own Browser", $"Chromium {CefSharp.Cef.ChromiumVersion} · CEF {CefSharp.Cef.CefVersion} · WinUI 3");
                    Note("Actualizaciones y sandbox", "Compilación de laboratorio sin sandbox ni actualizador automático. No es apta para navegación con datos sensibles.");
                }
                ActionButton("\uE946", "Ver información de la aplicación", _actions.ShowAbout);
                break;
            case "Diagnóstico":
                Group("Herramientas");
                ActionButton("\uE9CE", "Abrir página de diagnóstico", _diagnostic);
                ActionButton("\uE712", "Probar menú contextual web", _testContext);
                ActionButton("\uE896", "Probar descarga de texto", _testDownload);
                ActionButton("\uE74B", "Medir desplazamiento durante 5 segundos", _benchmark);
                break;
        }
    }

    private static StackPanel Label(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void Group(string title)
    {
        _fields.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(4, 12, 0, 0) });
        _group = new StackPanel();
        // The Windows 11 card: its fill and stroke are theme resources, so a card follows a theme change by itself instead of keeping
        // the colours of the theme it was built in (which left white cards with white text after switching to dark).
        var card = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' BorderThickness='1' Padding='16,4,16,4' " +
            "Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'/>");
        card.CornerRadius = new CornerRadius(Motion.Fx.OverlayRadius);
        card.Child = _group;
        _fields.Children.Add(card);
    }

    /// <summary>Called by the window after the theme changed: recolours what is painted from code (the navigation list).</summary>
    public void RefreshTheme()
    {
        ApplyNavigationTheme();
        MoveIndicator(false);
    }

    private Grid Row(string glyph, string title, FrameworkElement? control, string detail = "")
    {
        if (_group is null) Group("Opciones");
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 12, 0, 12), MinHeight = 52 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control is not null) { Grid.SetColumn(control, 2); grid.Children.Add(control); }
        _group!.Children.Add(grid);
        _rows.Add((grid, _renderingCategory + " " + title + " " + detail));
        return grid;
    }

    private void Toggle(string glyph, string label, bool value, Action<bool> change, string detail = "")
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, label);
        toggle.Toggled += (_, _) => { change(toggle.IsOn); Changed(); };
        Row(glyph, label, toggle, detail);
    }

    private void Text(string glyph, string label, string value, Action<string> change, string detail = "", bool multiline = false)
    {
        var input = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 80 : 32, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, label);
        input.TextChanged += (_, _) => { change(input.Text); Changed(); };
        input.LostFocus += (_, _) => { if (_saveTimer.IsRunning) Save(); };
        Row(glyph, label, null, detail);
        var panel = new StackPanel { Margin = new Thickness(32, 0, 0, 12) };
        panel.Children.Add(input);
        _group!.Children.Add(panel);
        _rows.Add((panel, _renderingCategory + " " + label + " " + detail));
    }

    private void Choice(string glyph, string label, string[] values, string[] labels, string selected, Action<string> change)
    {
        var combo = new ComboBox { MinWidth = 130, MaxWidth = 270, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        foreach (string text in labels) combo.Items.Add(text);
        combo.SelectedIndex = Array.IndexOf(values, selected);
        AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { change(values[combo.SelectedIndex]); Changed(); } };
        Row(glyph, label, combo);
    }

    private void Slider(string glyph, string label, double min, double max, double value, double step, Action<double> change)
    {
        var number = new NumberBox
        {
            Minimum = min, Maximum = max, Value = value, SmallChange = step, Width = 104,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new DecimalFormatter
            {
                FractionDigits = step < 1 ? (int)Math.Ceiling(-Math.Log10(step)) : 0,
                NumberRounder = new IncrementNumberRounder { Increment = step }
            }
        };
        AutomationProperties.SetName(number, label);
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value };
        AutomationProperties.SetName(slider, label);
        int digits = step < 1 ? (int)Math.Ceiling(-Math.Log10(step)) : 0;
        slider.ValueChanged += (_, args) => { double value = Math.Round(args.NewValue, digits); number.Value = value; change(value); Changed(); };
        number.ValueChanged += (_, args) =>
        {
            if (!double.IsFinite(args.NewValue)) { _invalidNumbers.Add(number); Message(InfoBarSeverity.Error, "Valor no válido", $"Introduce un número entre {min} y {max} para «{label}»."); return; }
            _invalidNumbers.Remove(number); double value = Math.Round(args.NewValue, digits); slider.Value = value; change(value); Changed();
        };
        Row(glyph, label, number);
        var panel = new StackPanel { Margin = new Thickness(32, -4, 0, 4) };
        panel.Children.Add(slider); _group!.Children.Add(panel); _rows.Add((panel, _renderingCategory + " " + label));
    }

    private void Note(string label, string detail) => Row("\uE946", label, null, detail);
    private void ActionButton(string glyph, string label, Action action, string detail = "")
    {
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid row = Row(glyph, label, chevron, detail);
        _group!.Children.Remove(row);
        _rows.RemoveAt(_rows.Count - 1);
        var button = new Button
        {
            Content = row, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), CornerRadius = new CornerRadius(Motion.Fx.ControlRadius)
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        _group.Children.Add(button);
        _rows.Add((button, _renderingCategory + " " + label + " " + detail));
    }

    private async void PickFolder()
    {
        try
        {
            string? path = await _actions.PickDownloadDirectory();
            if (path is not null) { _draft.DownloadDirectory = path; Save(); Render(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { Message(InfoBarSeverity.Error, "No se pudo seleccionar la carpeta", e.Message); }
    }

    private async void Reset()
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Restablecer ajustes", Content = "Se restaurarán las preferencias del navegador. Se conservarán favoritos, historial y descargas.", PrimaryButtonText = "Restablecer", CloseButtonText = "Cancelar" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _draft = new BrowserPreferences();
        Save();
        Render();
    }

    private async void OpenWindowsSettings(string uri)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(uri))) Message(InfoBarSeverity.Error, "No se abrió Configuración de Windows", "Abre Configuración manualmente desde Inicio.");
        }
        catch (System.Runtime.InteropServices.COMException e) { Message(InfoBarSeverity.Error, "No se abrió Configuración de Windows", e.Message); }
    }
}
