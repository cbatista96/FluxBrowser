using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using MyOwnBrowser.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using MyOwnBrowser.Browser;
using Windows.Foundation;
using Windows.Storage.Streams;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MyOwnBrowser;

/// <summary>
/// The card that appears when the pointer rests on a tab: a preview of the page, its title and address and the memory it uses. Once
/// shown it follows the pointer from tab to tab with a glide instead of disappearing and reappearing.
/// </summary>
public sealed partial class MainWindow
{
    // Timers must stay referenced: a collected DispatcherQueueTimer silently stops ticking.
    private DispatcherQueueTimer? _cardShowTimer, _cardHideTimer, _thumbTimer;
#if DEBUG
    private DispatcherQueueTimer? _hoverTestTimer;
#endif
    private BrowserTab? _cardTab;
    private Rect _cardBounds;
    private bool _cardVisible, _capturing, _translationReady;
    private double _cardX;
    private MyOwnBrowser.Motion.FrameLoop.Animation? _cardAnim;

    private void OnTabHover(BrowserTab? tab, Rect bounds)
    {
        if (tab is null)
        {
            _cardShowTimer?.Stop();
            _cardHideTimer ??= NewTimer(130, HideTabCard);
            _cardHideTimer.Stop();
            _cardHideTimer.Start();
            return;
        }
        _cardHideTimer?.Stop();
        _cardTab = tab;
        _cardBounds = bounds;
        if (_cardVisible) { ShowCard(animate: true); return; }
        _cardShowTimer ??= NewTimer(480, () => { if (_cardTab is not null) ShowCard(animate: false); });
        _cardShowTimer.Stop();
        _cardShowTimer.Start();
    }

    private DispatcherQueueTimer NewTimer(double milliseconds, Action tick)
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void HideTabCard()
    {
        _cardShowTimer?.Stop();
        _cardHideTimer?.Stop();
        _cardTab = null;
        if (!_cardVisible) return;
        _cardVisible = false;
        if (!MotionEnabled) { TabCard.Opacity = 0; TabCard.Visibility = Visibility.Collapsed; return; }
        AnimateCard(_cardX, 0, null, 110, () => { if (!_cardVisible) TabCard.Visibility = Visibility.Collapsed; });
    }

    /// <summary>Glides the card to <paramref name="toX"/> while fading it to <paramref name="toOpacity"/> (and, optionally, its contents in from a dimmer state).</summary>
    private void AnimateCard(double toX, double toOpacity, double? contentFrom, double milliseconds, Action? done = null)
    {
        _cardAnim?.Stop();
        double fromX = _cardX, fromOpacity = TabCard.Opacity;
        _cardAnim = _frames.Start(milliseconds, t =>
        {
            double eased = Easing.OutCubic(t);
            _cardX = Easing.Lerp(fromX, toX, eased);
            TabCard.Translation = new Vector3((float)_cardX, 0, Fx.OverlayDepth);
            TabCard.Opacity = Easing.Lerp(fromOpacity, toOpacity, eased);
            if (contentFrom is { } from) TabCardContent.Opacity = Easing.Lerp(from, 1, eased);
        }, done);
    }

    private void ShowCard(bool animate)
    {
        if (_cardTab is not { } tab || !_tabs.Contains(tab) || _closing) return;
        if (!_translationReady) { ElementCompositionPreview.SetIsTranslationEnabled(TabCard, true); _translationReady = true; }
        double stripLeft = HorizontalTabs.TransformToVisual(PageGrid).TransformPoint(new Point()).X;
        double x = Math.Clamp(stripLeft + _cardBounds.X + _cardBounds.Width / 2 - TabCard.Width / 2, 8, Math.Max(8, PageGrid.ActualWidth - TabCard.Width - 8));
        TabCard.Margin = new Thickness(0, TabStrip.StripHeight + 6, 0, 0);
        FillCard(tab);
        bool wasVisible = _cardVisible;
        _cardVisible = true;
        TabCard.Visibility = Visibility.Visible;
        TabCardContent.Opacity = 1;
        if (!wasVisible)
        {
            _cardX = x;
            TabCard.Translation = new Vector3((float)x, 0, Fx.OverlayDepth);
            TabCard.Opacity = 0;
            if (MotionEnabled) AnimateCard(x, 1, null, 150); else TabCard.Opacity = 1;
        }
        else if (MotionEnabled) AnimateCard(x, 1, .35, 210); // glide to the new tab; the contents swap with a short fade
        else { _cardX = x; TabCard.Translation = new Vector3((float)x, 0, Fx.OverlayDepth); TabCard.Opacity = 1; }
        if (tab == _activeTab) _ = CaptureThumbnailAsync(tab, force: Environment.TickCount64 - tab.ThumbnailTick > 3000);
        _ = RefreshCardMemoryAsync(tab);
    }

    private void FillCard(BrowserTab tab)
    {
        bool internalPage = ViewportLayout.IsInternal(tab.Address);
        TabCardTitle.Text = tab.Title;
        TabCardHost.Text = internalPage ? "Flux Browser" : DisplayHost(tab.Address);
        TabCardIcon.Children.Clear();
        TabCardIcon.Children.Add(SiteIcon(internalPage ? tab.Address : FaviconLoader.Address(tab.Favicon, tab.Address)));
        double aspect = PageGrid.ActualWidth > 0 ? PageGrid.ActualHeight / PageGrid.ActualWidth : .6;
        TabCardPreview.Height = Math.Clamp((TabCard.Width - 24) * aspect, 110, 190);
        TabCardImage.Source = tab.Thumbnail;
        TabCardImage.Visibility = tab.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        TabCardPlaceholder.Glyph = tab.Glyph;
        TabCardPlaceholder.Visibility = tab.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        TabCardPreview.Visibility = Visibility.Visible;
        TabCardMemory.Text = internalPage ? "Página interna" : tab.SessionIfCreated is null ? "Pestaña en suspensión: aún no usa memoria" : "Calculando memoria…";
    }

    private async Task RefreshCardMemoryAsync(BrowserTab tab)
    {
        if (tab.SessionIfCreated is not { } session || ViewportLayout.IsInternal(tab.Address)) return;
        try
        {
            long bytes = await session.GetMemoryBytesAsync();
            if (_cardTab != tab || !_cardVisible) return;
            TabCardMemory.Text = bytes < 0 ? "" : $"Memoria: {bytes / (1024d * 1024):N0} MB" + (tab == _activeTab ? "" : " · en segundo plano");
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (_cardTab == tab) TabCardMemory.Text = "";
        }
    }

#if DEBUG
    /// <summary>Debug builds only: MYOWNBROWSER_TEST_TABS="move:0:2,close:1,add" runs those strip actions 4 s apart.</summary>
    private void StartTabScriptTest()
    {
        if (Environment.GetEnvironmentVariable("MYOWNBROWSER_TEST_TABS") is not { Length: > 0 } spec) return;
        string[] steps = spec.Split(',');
        int step = 0;
        _tabScriptTimer = DispatcherQueue.CreateTimer();
        _tabScriptTimer.Interval = TimeSpan.FromSeconds(4);
        _tabScriptTimer.Tick += (_, _) =>
        {
            if (step >= steps.Length) { _tabScriptTimer.Stop(); return; }
            string[] p = steps[step++].Split(':');
            if (p[0] == "move" && _tabs.Count > 1) _tabs.Move(int.Parse(p[1]), Math.Min(int.Parse(p[2]), _tabs.Count - 1));
            else if (p[0] == "close" && int.Parse(p[1]) < _tabs.Count) CloseTab(_tabs[int.Parse(p[1])]);
            else if (p[0] == "add") TryAddTab();
        };
        _tabScriptTimer.Start();
    }

    private DispatcherQueueTimer? _tabScriptTimer;

    /// <summary>Debug builds only: MYOWNBROWSER_TEST_HOVER="1,2" hovers those tab indexes in turn (3 s apart) so the card can be inspected without OS input.</summary>
    private void StartHoverTest()
    {
        if (Environment.GetEnvironmentVariable("MYOWNBROWSER_TEST_HOVER") is not { Length: > 0 } spec) return;
        int[] order = spec.Split(',').Select(int.Parse).ToArray();
        int step = 0;
        DispatcherQueueTimer timer = _hoverTestTimer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3.5);
        timer.Tick += (_, _) =>
        {
            int index = order[step++ % order.Length];
            if (index >= 0 && index < _tabs.Count && HorizontalTabs.BoundsOf(_tabs[index]) is { } bounds) OnTabHover(_tabs[index], bounds);
        };
        timer.Start();
    }
#endif

    // ---------- Thumbnails ----------

    /// <summary>Takes a preview of the active page a moment after it settles (load finished, scrolling stopped).</summary>
    private void ScheduleThumbnail(BrowserTab tab, double delayMilliseconds = 1400)
    {
        if (_closing) return;
        _thumbTimer?.Stop();
        _thumbTimer = NewTimer(delayMilliseconds, () => { if (_activeTab == tab) _ = CaptureThumbnailAsync(tab, force: false); });
        _thumbTimer.Start();
    }

    private async Task CaptureThumbnailAsync(BrowserTab tab, bool force)
    {
        if (_closing || _capturing || !tab.Ready || tab != _activeTab || ViewportLayout.IsInternal(tab.Address) || tab.SessionIfCreated is not { } session) return;
        if (!force && Environment.TickCount64 - tab.ThumbnailTick < 2500) return;
        _capturing = true;
        try
        {
            byte[] png = await session.CaptureScreenshotAsync();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 560 };
            await bitmap.SetSourceAsync(stream);
            if (_closing || !_tabs.Contains(tab)) return;
            tab.Thumbnail = bitmap;
            tab.ThumbnailTick = Environment.TickCount64;
            if (_cardTab == tab && _cardVisible)
            {
                TabCardImage.Source = bitmap;
                TabCardImage.Visibility = Visibility.Visible;
                TabCardPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or ArgumentException)
        {
            Diagnostics.AppLog.Warning("No se pudo capturar la vista previa de la pestaña", e);
        }
        finally { _capturing = false; }
    }
}
