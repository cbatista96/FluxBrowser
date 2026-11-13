using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using FluxBrowser.Browser;
using System.Runtime.InteropServices;
using Windows.Graphics;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FluxBrowser;

/// <summary>
/// The horizontal tab strip's wiring and tab dragging. The system's drag-and-drop shows a "forbidden" cursor over anything that is not a
/// drop target (the desktop, the page), so tabs are dragged by following the mouse instead: reorder inside the strip, join another
/// window by dropping on its strip, become a window of their own anywhere else. The only tab of a window drags the window itself.
/// </summary>
public sealed partial class MainWindow
{
    private readonly TabStrip HorizontalTabs = new();
    private DispatcherQueueTimer? _dragTimer;
    private BrowserTab? _dragTab;
    private NativePoint _dragStart;
    private PointInt32 _dragWindowStart;
    private double _dragGrab;
    private bool _dragActive;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private void WireTabStrip()
    {
        Grid.SetColumn(HorizontalTabs, 1);
        TabStripHost.Children.Add(HorizontalTabs);
        HorizontalTabs.AddEnabled = false;
        HorizontalTabs.ItemsSource = _tabs;
        HorizontalTabs.TabInvoked += tab => SelectTab(tab);
        HorizontalTabs.CloseRequested += CloseTab;
        HorizontalTabs.MuteRequested += ToggleMute;
        HorizontalTabs.AddRequested += () => TryAddTab();
        HorizontalTabs.TabPressed += OnStripTabPressed;
        HorizontalTabs.TabContextRequested += (tab, point) =>
        {
            HideTabCard();
            ShowTabMenu(tab, HorizontalTabs.TransformToVisual(PageGrid).TransformPoint(new Windows.Foundation.Point(point.X, TabStrip.StripHeight)));
        };
        HorizontalTabs.HoverChanged += OnTabHover;
        HorizontalTabs.LayoutChanged += UpdateDragRegions;
        HorizontalTabs.ItemsChanged += PersistTabs;
    }

    // ---------- Where the window can be dragged ----------

    private RectInt32? _dragRect;

    /// <summary>
    /// The free part of the title strip (right of the tabs and the add button, up to the caption buttons) moves the window. It is
    /// computed from the strip's own layout, so it is exact whatever the number of tabs, and never covers a tab or a button.
    /// </summary>
    private void UpdateDragRegions()
    {
        if (_closing || RootGrid.XamlRoot is null || RootGrid.ActualWidth <= 0) return;
        double scale = Scale, left, right = RootGrid.ActualWidth - 138;
        if (Preferences.VerticalTabs) left = SidebarColumn.ActualWidth + 40;
        else left = HorizontalTabs.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point()).X + HorizontalTabs.ContentRight + 4;
        RectInt32? rect = right - left > 8 ? new RectInt32((int)(left * scale), 0, (int)((right - left) * scale), (int)(TabStrip.StripHeight * scale)) : null;
        if (Nullable.Equals(rect, _dragRect)) return;
        _dragRect = rect;
        AppWindow.TitleBar.SetDragRectangles(rect is { } r ? [r] : []);
    }

    // ---------- Dragging a tab ----------

    private double Scale => RootGrid.XamlRoot?.RasterizationScale ?? 1;

    private void OnStripTabPressed(BrowserTab tab, PointerRoutedEventArgs e)
    {
        if (_dragTimer is not null || _closing) return;
        HideTabCard();
        if (!GetCursorPos(out _dragStart)) return;
        _dragTab = tab;
        _dragActive = false;
        _dragWindowStart = AppWindow.Position;
        _dragGrab = (_dragStart.X - AppWindow.Position.X) / Scale - HorizontalTabs.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point()).X - HorizontalTabs.LeftOf(tab);
        _dragTimer = DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(8);
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
    }

    /// <summary>Whether a screen point lies over this window's tab strip (a little taller than the strip, to be forgiving).</summary>
    internal bool IsOverStrip(NativePoint point)
    {
        if (_closing || !_sessionReady || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return false;
        PointInt32 position = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        return point.X >= position.X && point.X < position.X + size.Width && point.Y >= position.Y && point.Y < position.Y + 44 * Scale;
    }

    private MainWindow? WindowWhoseStripIsUnder(NativePoint point) =>
        BrowserApp.Windows.FirstOrDefault(w => w != this && w._privateWindow == _privateWindow && w.IsOverStrip(point));

    /// <summary>The pointer's x in the strip's own coordinates.</summary>
    private double StripX(NativePoint point) =>
        (point.X - AppWindow.Position.X) / Scale - HorizontalTabs.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point()).X;

    private void DragTick(DispatcherQueueTimer timer, object args)
    {
        if (_dragTab is not { } tab || !_tabs.Contains(tab) || !GetCursorPos(out NativePoint cursor)) { EndDrag(); return; }
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!_dragActive)
        {
            if (!down) { EndDrag(); return; }
            if (Math.Abs(cursor.X - _dragStart.X) < 6 * Scale && Math.Abs(cursor.Y - _dragStart.Y) < 6 * Scale) return;
            _dragActive = true;
            if (_tabs.Count > 1) HorizontalTabs.SetDragging(tab, true);
        }
        MainWindow? target = WindowWhoseStripIsUnder(cursor);
        bool ownStrip = IsOverStrip(cursor);
        if (down)
        {
            if (_tabs.Count == 1)
                AppWindow.Move(new PointInt32(_dragWindowStart.X + cursor.X - _dragStart.X, _dragWindowStart.Y + cursor.Y - _dragStart.Y));
            else if (ownStrip) FollowInStrip(tab, cursor);
            return;
        }
        EndDrag();
        if (target is not null)
        {
            target.AdoptTab(new TabTransfer(tab.Address, tab.Title, tab.Pinned, tab.Muted, tab.Zoom), cursor);
            DetachTab(tab);
        }
        else if (!ownStrip && _tabs.Count > 1) MoveTabToNewWindow(tab);
    }

    /// <summary>The dragged tab follows the pointer and the others slide aside into the slot it would take.</summary>
    private void FollowInStrip(BrowserTab tab, NativePoint cursor)
    {
        double left = StripX(cursor) - _dragGrab;
        HorizontalTabs.DragTo(tab, left);
        int slot = HorizontalTabs.SlotFor(tab, left + HorizontalTabs.WidthOf(tab) / 2), from = _tabs.IndexOf(tab);
        if (slot == from || slot < 0 || slot >= _tabs.Count) return;
        _tabs.Move(from, slot);
    }

    private void EndDrag()
    {
        if (_dragTimer is { } timer) { timer.Stop(); timer.Tick -= DragTick; _dragTimer = null; }
        if (_dragTab is { } tab) HorizontalTabs.SetDragging(tab, false);
        _dragTab = null;
        _dragActive = false;
    }

    /// <summary>Receives a tab dragged from another window and puts it where it was dropped on the strip.</summary>
    internal void AdoptTab(TabTransfer moved, NativePoint cursor)
    {
        if (!_sessionReady || _closing) return;
        try
        {
            BrowserTab tab = AddTab(moved.Address, activate: true, savedTitle: moved.Title);
            tab.Pinned = moved.Pinned;
            tab.Muted = moved.Muted;
            tab.Zoom = moved.Zoom;
            int slot = HorizontalTabs.SlotFor(tab, StripX(cursor));
            if (slot >= 0 && slot < _tabs.Count - 1) { _tabs.Move(_tabs.Count - 1, slot); PersistTabs(); }
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or IOException)
        {
            ShowError("No se pudo mover la pestaña", e.Message);
            return;
        }
        Activate();
    }
}
