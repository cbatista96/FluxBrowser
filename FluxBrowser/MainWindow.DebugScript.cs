#if DEBUG
using Microsoft.UI.Xaml;
using FluxBrowser.Browser;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FluxBrowser;

/// <summary>
/// Debug builds only. FLUXBROWSER_TEST_UI="suggest:wiki|find:Windows|zoom:1.25|menu" runs those steps a few seconds apart, so
/// every surface of the chrome can be opened and captured without sending keys or clicks to the desktop.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _uiScriptTimer;
    private bool _suggestTest;

    private void StartUiScript()
    {
        if (Environment.GetEnvironmentVariable("FLUXBROWSER_TEST_UI") is not { Length: > 0 } spec) return;
        string[] steps = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int step = 0;
        _uiScriptTimer = DispatcherQueue.CreateTimer();
        _uiScriptTimer.Interval = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("FLUXBROWSER_TEST_UI_STEP"),
            System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : 4);
        _uiScriptTimer.Tick += (_, _) =>
        {
            if (step >= steps.Length || _closing) { _uiScriptTimer?.Stop(); return; }
            string[] p = steps[step++].Split(':', 2);
            string value = p.Length > 1 ? p[1] : "";
            Diagnostics.AppLog.Info($"Paso de prueba: {p[0]} {value}");
            try
            {
                switch (p[0])
                {
                    case "suggest": _suggestTest = true; AddressBox.Text = value; break;
                    case "suggest-down": MoveSuggestion(1); break;
                    case "suggest-accept": AcceptSuggestion(); _suggestTest = false; break;
                    case "suggest-off": _suggestTest = false; HideSuggestions(); UpdateAddress(_currentAddress, force: true); break;
                    case "find": ShowFind(); FindBox.Text = value; break;
                    case "find-next": _web?.Find(FindBox.Text, true, true); break;
                    case "find-off": CloseFind(); break;
                    case "zoom": SetZoom(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                    case "menu": ShowMainMenu(); break;
                    case "hit": LogHit(value); break;
                    case "tools": ShowMoreTools(); break;
                    case "menu-off": HideMenu(); break;
                    case "history": ShowHistory(); break;
                    case "downloads": ShowDownloads(); break;
                    case "bookmarks": ShowBookmarks(); break;
                    case "bookmark-add": BookmarkButton_Click(this, new RoutedEventArgs()); break;
                    case "panel-off": CloseManagement(); break;
                    case "tabs": ShowTabActions(false); break;
                    case "settings": ShowSettings(); break;
                    case "settings-page": NavigateSettings(value); break;
                    case "select": if (int.Parse(value) < _tabs.Count) SelectTab(_tabs[int.Parse(value)]); break;
                    case "newtab": TryAddTab(); break;
                    case "newtab-input":
                        if (NewTabPanel.Visibility != Visibility.Visible || NewTabSearch.FocusState == FocusState.Unfocused)
                            throw new InvalidOperationException("La búsqueda de Nueva pestaña no tiene el foco.");
                        NewTabSearch.Text = value;
                        Diagnostics.AppLog.Info("Nueva pestaña: campo de búsqueda enfocado.");
                        break;
                    case "newtab-submit":
                        if (!NavigateInput(NewTabSearch.Text))
                            throw new InvalidOperationException("No se pudo enviar la entrada de Nueva pestaña.");
                        NewTabSearch.Text = "";
                        break;
                    case "go": NavigateCurrent(value); break;
                    case "mute": if (_activeTab is { } tab) ToggleMute(tab); break;
                    case "about": ShowAbout(); break;
                    case "help": ShowHelpMenu(); break;
                    case "cleardata": ShowClearData(); break;
                    case "theme": BrowserApp.SavePreferences(Preferences with { Theme = value }); break;
                    case "vertical": BrowserApp.SavePreferences(Preferences with { VerticalTabs = value != "off" }); break;
                }
            }
            catch (Exception e) { Diagnostics.AppLog.Error($"Paso de prueba fallido: {p[0]}", e); }
        };
        _uiScriptTimer.Start();
    }

    /// <summary>Logs what a pointer press at "x,y" (DIP, window client) would reach, topmost first: what XAML's own hit testing sees.</summary>
    private void LogHit(string value)
    {
        string[] xy = value.Split(',');
        var point = new Windows.Foundation.Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
        var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(point, RootGrid).Take(12)
            .Select(e => $"{e.GetType().Name}{(e is FrameworkElement { Name.Length: > 0 } f ? "#" + f.Name : "")}");
        Diagnostics.AppLog.Info($"Impacto en {value}: " + string.Join(" > ", hits));
        var origin = MenuBorder.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
        Diagnostics.AppLog.Info($"Menú: vis={MenuBorder.Visibility} hit={MenuBorder.IsHitTestVisible} op={MenuBorder.Opacity} " +
            $"size={MenuBorder.ActualWidth}x{MenuBorder.ActualHeight} origin={origin} translation={MenuBorder.Translation} scale={MenuBorder.Scale} " +
            $"bg={MenuBorder.Background?.GetType().Name}");
    }
}
#endif
