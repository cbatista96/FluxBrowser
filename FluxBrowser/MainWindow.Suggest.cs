using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FluxBrowser.Browser;
using FluxBrowser.Motion;
using Windows.System;
using Point = Windows.Foundation.Point;

namespace FluxBrowser;

/// <summary>
/// The address bar's suggestions and its zoom indicator. While an address or a search is being typed, a list under the box offers
/// what Enter would do, the open tabs, the favourites and the visited pages that match. Everything comes from this computer: no
/// keystroke is sent to a search engine until the user actually searches.
/// </summary>
public sealed partial class MainWindow
{
    private enum SuggestionKind { Go, Search, Tab, Bookmark, History }

    private sealed record Suggestion(SuggestionKind Kind, string Title, string Detail, string Address, string? Favicon, BrowserTab? Tab = null);

    private const int MaxSuggestions = 8;
    private readonly List<(Suggestion Item, Button Row)> _suggestions = [];
    private int _suggestionIndex = -1;
    private string _addressSetByCode = "", _typedText = "";
    private bool _suggestOpen;

    /// <summary>Sets the address box from code. Text changes arrive later and asynchronously, so what was set is remembered to tell it from typing.</summary>
    private void SetAddressText(string text)
    {
        _addressSetByCode = text;
        AddressBox.Text = text;
    }

    private void UpdateSuggestions()
    {
        string text = AddressBox.Text;
        bool editing = AddressBox.FocusState != FocusState.Unfocused;
#if DEBUG
        editing |= _suggestTest;
#endif
        if (_closing || !editing || !Preferences.AddressSuggestions) { HideSuggestions(); return; }
        // The box shows something the code put there (the page's address on focus, a suggestion being walked through): not typing.
        if (text == _addressSetByCode) return;
        _addressSetByCode = "";
        _typedText = text;
        string query = text.Trim();
        if (query.Length == 0) { HideSuggestions(); return; }
        ShowSuggestions(BuildSuggestions(query));
    }

    private List<Suggestion> BuildSuggestions(string query)
    {
        var list = new List<Suggestion>(MaxSuggestions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) words = [query];
        bool Matches(string title, string address)
        {
            foreach (string word in words)
                if (!title.Contains(word, StringComparison.OrdinalIgnoreCase) && !address.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        static string Key(string address) => address.TrimEnd('/');

        // First row: exactly what Enter does with the text as typed.
        string searchAddress = AddressResolver.Search(query, Preferences.SearchEngine);
        bool isAddress = AddressResolver.TryResolve(query, out string resolved, out _, Preferences.SearchEngine) && resolved != searchAddress;
        if (isAddress)
        {
            list.Add(new Suggestion(SuggestionKind.Go, DisplayHost(resolved), "Ir a esta dirección", resolved, FaviconFromHistory(resolved)));
            seen.Add(Key(resolved));
        }
        var search = new Suggestion(SuggestionKind.Search, query, $"Buscar con {Preferences.SearchEngine}", searchAddress, null);
        if (!isAddress) list.Add(search);

        foreach (BrowserTab tab in _tabs)
        {
            if (list.Count >= 3 + (isAddress ? 1 : 0)) break;
            if (tab == _activeTab || !WebAddress.IsHttp(tab.Address) || !Matches(tab.Title, tab.Address) || !seen.Add(Key(tab.Address))) continue;
            list.Add(new Suggestion(SuggestionKind.Tab, tab.Title, "Cambiar a esta pestaña", tab.Address, tab.Favicon, tab));
        }

        int bookmarks = 0;
        foreach (Bookmark bookmark in _bookmarks)
        {
            if (bookmarks >= 3 || list.Count >= MaxSuggestions - 1) break;
            if (bookmark.IsFolder || !Matches(bookmark.Title, bookmark.Address) || !seen.Add(Key(bookmark.Address))) continue;
            list.Add(new Suggestion(SuggestionKind.Bookmark, bookmark.Title, DisplayHost(bookmark.Address), bookmark.Address, bookmark.Favicon));
            bookmarks++;
        }

        // History: pages visited often and recently come first, and a site whose name starts with what was typed beats a mere mention.
        List<HistoryEntry> history = BrowserApp.History;
        var ranked = new Dictionary<string, (HistoryEntry Entry, double Score)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < history.Count; i++)
        {
            HistoryEntry entry = history[i];
            if (!Matches(entry.Title, entry.Address)) continue;
            string key = Key(entry.Address);
            if (ranked.TryGetValue(key, out var known)) { ranked[key] = (known.Entry, known.Score + 4); continue; }
            string host = DisplayHost(entry.Address);
            double score = 30 * (1 - (double)i / Math.Max(1, history.Count));
            if (host.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) score += 60;
            if (Uri.TryCreate(entry.Address, UriKind.Absolute, out Uri? uri) && uri.AbsolutePath.Length <= 1 && uri.Query.Length == 0) score += 12;
            ranked[key] = (entry, score);
        }
        foreach (var (entry, _) in ranked.Values.OrderByDescending(r => r.Score))
        {
            if (list.Count >= MaxSuggestions - (isAddress ? 1 : 0)) break;
            if (!seen.Add(Key(entry.Address))) continue;
            list.Add(new Suggestion(SuggestionKind.History, string.IsNullOrWhiteSpace(entry.Title) ? entry.Address : entry.Title, DisplayHost(entry.Address), entry.Address, entry.Favicon));
        }
        // Something that looks like an address can still be searched for: that choice closes the list.
        if (isAddress) list.Add(search);
        return list;
    }

    private string? FaviconFromHistory(string address)
    {
        string host = HostOf(address);
        return BrowserApp.History.FirstOrDefault(h => h.Favicon is not null && HostOf(h.Address) == host)?.Favicon;
    }

    private void ShowSuggestions(List<Suggestion> items)
    {
        SuggestItems.Children.Clear();
        _suggestions.Clear();
        _suggestionIndex = -1;
        foreach (Suggestion item in items)
        {
            Button row = SuggestionRow(item);
            SuggestItems.Children.Add(row);
            _suggestions.Add((item, row));
        }
        if (_suggestions.Count == 0) { HideSuggestions(); return; }
        PlaceSuggestions();
        if (_suggestOpen) return;
        _suggestOpen = true;
        SuggestPanel.Background = _menuSolid;
        SuggestPanel.BorderBrush = MenuBorder.BorderBrush;
        SuggestPanel.IsHitTestVisible = true;
        SuggestPanel.Visibility = Visibility.Visible;
        _ = AnimateSurface(SuggestPanel, true);
    }

    /// <summary>Under the address box, as wide as the box will be once its focus animation ends (never narrower than a readable row).</summary>
    private void PlaceSuggestions()
    {
        Point toolbar = ToolbarRow.TransformToVisual(PageGrid).TransformPoint(new Point());
        double width = Math.Min(Math.Max(_addressTarget, 440), Math.Max(240, PageGrid.ActualWidth - 16));
        double centre = toolbar.X + ToolbarRow.ActualWidth / 2;
        SuggestPanel.Width = width;
        SuggestPanel.Margin = new Thickness(Math.Clamp(centre - width / 2, 8, Math.Max(8, PageGrid.ActualWidth - width - 8)), toolbar.Y + ToolbarRow.ActualHeight + 2, 0, 0);
    }

    private Button SuggestionRow(Suggestion item)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = item.Kind switch
        {
            SuggestionKind.Search => new FontIcon { Glyph = "", FontSize = 14 },
            SuggestionKind.Bookmark when item.Favicon is null => new FontIcon { Glyph = "", FontSize = 14 },
            _ => SiteIcon(FaviconLoader.Address(item.Favicon, item.Address))
        };
        row.Children.Add(icon);
        var title = new TextBlock { Text = item.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        Grid.SetColumn(title, 1); row.Children.Add(title);
        var detail = new TextBlock { Text = item.Detail, FontSize = 12, Opacity = .62, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(detail, 2); row.Children.Add(detail);
        string badge = item.Kind switch { SuggestionKind.Tab => "", SuggestionKind.Bookmark => "", SuggestionKind.History => "", _ => "" };
        if (badge.Length > 0)
        {
            var mark = new FontIcon { Glyph = badge, FontSize = 11, Opacity = .5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(mark, 3); row.Children.Add(mark);
        }
        Button button = MenuButton(row, 36);
        // The row must not take the focus away from the box: typing continues while the pointer is over the list.
        button.IsTabStop = false;
        button.AllowFocusOnInteraction = false;
        AutomationProperties.SetName(button, $"{item.Title}, {item.Detail}");
        button.Click += (_, _) => OpenSuggestion(item);
        Fx.Icon(button, IconMotion.Pop, icon);
        return button;
    }

    private void MoveSuggestion(int step)
    {
        if (_suggestions.Count == 0) return;
        // One step past either end returns to the text as typed, like every other browser.
        int count = _suggestions.Count + 1;
        int index = ((_suggestionIndex + 1 + step) % count + count) % count - 1;
        SelectSuggestion(index);
    }

    private void SelectSuggestion(int index)
    {
        for (int i = 0; i < _suggestions.Count; i++)
            _suggestions[i].Row.Background = i == index ? HorizontalTabs.HoverFill : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _suggestionIndex = index;
        string text = index < 0 ? _typedText : _suggestions[index].Item.Kind == SuggestionKind.Search ? _suggestions[index].Item.Title : _suggestions[index].Item.Address;
        SetAddressText(text);
        AddressBox.SelectionStart = text.Length;
        if (index >= 0) AutomationProperties.SetName(AddressBox, $"Dirección o búsqueda: {_suggestions[index].Item.Title}, {_suggestions[index].Item.Detail}");
        else AutomationProperties.SetName(AddressBox, "Dirección o búsqueda");
    }

    /// <summary>Enter with a suggestion highlighted. False when none is: the text itself is what gets opened.</summary>
    private bool AcceptSuggestion()
    {
        if (!_suggestOpen || _suggestionIndex < 0 || _suggestionIndex >= _suggestions.Count) return false;
        OpenSuggestion(_suggestions[_suggestionIndex].Item);
        return true;
    }

    private void OpenSuggestion(Suggestion item)
    {
        HideSuggestions();
        if (item.Tab is { } tab && _tabs.Contains(tab))
        {
            UpdateAddress(_currentAddress, force: true);
            SelectTab(tab);
        }
        else NavigateCurrent(item.Address);
        FocusPage();
    }

    private async void HideSuggestions()
    {
        AutomationProperties.SetName(AddressBox, "Dirección o búsqueda");
        _suggestionIndex = -1;
        if (!_suggestOpen) return;
        _suggestOpen = false;
        SuggestPanel.IsHitTestVisible = false;
        if (!await AnimateSurface(SuggestPanel, false) || _suggestOpen) return;
        SuggestPanel.Visibility = Visibility.Collapsed;
        SuggestItems.Children.Clear();
        _suggestions.Clear();
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_suggestOpen) return;
        if (args.Key is VirtualKey.Down or VirtualKey.Up)
        {
            MoveSuggestion(args.Key == VirtualKey.Down ? 1 : -1);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            // First Escape: back to the typed text with the list closed. A second one restores the page's address.
            if (_suggestionIndex >= 0) SelectSuggestion(-1);
            HideSuggestions();
            args.Handled = true;
        }
    }

    // ---------- Zoom indicator ----------

    /// <summary>The box shows the page's zoom only while it is not 100 %; the chip puts it back with one click.</summary>
    private void UpdateZoomChip()
    {
        bool shown = _activeTab is { } tab && !ViewportLayout.IsInternal(tab.Address) && Math.Abs(tab.Zoom - 1) > .004;
        if (shown) ZoomChipText.Text = $"{_activeTab!.Zoom * 100:F0} %";
        if (shown == (ZoomChip.Visibility == Visibility.Visible)) return;
        ZoomChip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        AddressBox.Padding = new Thickness(34, 5, shown ? 96 : 38, 6);
        if (shown) Fx.Play(ZoomChip, IconMotion.Pop);
    }

    private void ZoomChip_Click(object sender, RoutedEventArgs args) => SetZoom(1);
}
