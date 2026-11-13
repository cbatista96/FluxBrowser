using Microsoft.UI.Xaml;
using MyOwnBrowser.Browser;

namespace MyOwnBrowser;

/// <summary>Small pieces of chrome behaviour shared by the toolbar, the menus and the tab strip.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Returns the keyboard to the page after the chrome is done with it (an address was entered, a menu or the find bar closed).
    /// The off-screen engine takes keys through an input layer; WebView2 is a control of its own.
    /// </summary>
    private void FocusPage()
    {
        if (_closing || _activeTab is not { } tab) return;
        if (ViewportLayout.IsInternal(tab.Address))
        {
            if (tab.Address == "browser:home") NewTabSearch.Focus(FocusState.Programmatic);
            else RootGrid.Focus(FocusState.Programmatic);
        }
        else if (WebEngine.UseWebView2) tab.SessionIfCreated?.SetFocus(true);
        else WebInput.Focus(FocusState.Programmatic);
    }

    private void ToggleMute(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        tab.Muted = !tab.Muted;
        tab.SessionIfCreated?.SetMuted(tab.Muted);
    }

    /// <summary>Find in page reported its position; only the active tab's result is shown.</summary>
    private void OnFindResult(BrowserTab tab, int active, int total)
    {
        if (tab == _activeTab && _findOpen) UpdateFindCount(active, total);
    }

    private bool _firstPageLogged;

    /// <summary>Start-up measurement for the log: when the first page of the session painted.</summary>
    private void LogFirstPage()
    {
        if (_firstPageLogged || !_restoreWindow) return;
        _firstPageLogged = true;
        Diagnostics.AppLog.Info($"Arranque: primera página a los {Program.SinceStart()} ms");
    }
}
