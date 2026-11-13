using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MyOwnBrowser.Browser;

/// <summary>
/// The horizontal tab strip. Tabs share the available width and shrink as more are opened (down to an icon, like Edge) instead of
/// scrolling; every position and width eases toward its target on each composition frame, so opening, closing, reordering and
/// selecting move fluidly and can be interrupted at any moment. The selected tab and a hovered one use the same pill, so they are
/// always the same size. UI thread only.
/// </summary>
internal sealed class TabStrip : Canvas
{
    public const double StripHeight = 36, MaxTabWidth = 240, MinTabWidth = 26, SelectedMinWidth = 76, PinnedWidth = 40, AddWidth = 34, FreeSpace = 64;

    private readonly List<TabVisual> _visuals = [];
    private readonly Button _add;
    private ObservableCollection<BrowserTab>? _source;
    private BrowserTab? _selected;
    private double _addX, _addTargetX, _contentRight;
    private double? _lockedWidth;
    private bool _subscribed;
    private long _lastFrame;
    private TabVisual? _hovered;

    public event Action<BrowserTab>? TabInvoked;
    public event Action<BrowserTab>? CloseRequested;
    /// <summary>The speaker icon of a tab was clicked: mute or unmute it.</summary>
    public event Action<BrowserTab>? MuteRequested;
    public event Action? AddRequested;
    public event Action<BrowserTab, PointerRoutedEventArgs>? TabPressed;
    public event Action<BrowserTab, Point>? TabContextRequested;
    /// <summary>The hovered tab changed (null when the pointer left the strip) with its bounds in strip coordinates.</summary>
    public event Action<BrowserTab?, Rect>? HoverChanged;
    public event Action? LayoutChanged;
    public event Action? ItemsChanged;

    public Brush SelectedFill { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 255, 255, 255));
    public Brush HoverFill { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255));
    public Brush SeparatorBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 160, 160, 160));
    public bool AddEnabled { get => _add.IsEnabled; set => _add.IsEnabled = value; }

    /// <summary>Right edge (strip coordinates) of the last tab and the add button: what lies beyond it is free space.</summary>
    public double ContentRight => _contentRight;

    public TabStrip()
    {
        Height = StripHeight;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _add = new Button
        {
            Width = 30, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(Motion.Fx.ControlRadius),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 12 }
        };
        AutomationProperties.SetName(_add, "Nueva pestaña");
        ToolTipService.SetToolTip(_add, "Nueva pestaña (Ctrl+T)");
        _add.Click += (_, _) => AddRequested?.Invoke();
        Motion.Fx.Icon(_add, Motion.IconMotion.Lift);
        Children.Add(_add);
        SetTop(_add, (StripHeight - 28) / 2);
        SizeChanged += (_, _) => Relayout(animate: false);
        PointerExited += (_, _) => { _lockedWidth = null; SetHover(null); Relayout(true); };
        Unloaded += (_, _) => Unsubscribe();
    }

    // ---------- Items ----------

    public ObservableCollection<BrowserTab>? ItemsSource
    {
        get => _source;
        set
        {
            if (_source is not null) _source.CollectionChanged -= OnCollectionChanged;
            foreach (TabVisual visual in _visuals.ToArray()) Discard(visual);
            _source = value;
            if (_source is null) return;
            _source.CollectionChanged += OnCollectionChanged;
            foreach (BrowserTab tab in _source) _visuals.Add(Create(tab, appear: false));
            Relayout(animate: false);
        }
    }

    public BrowserTab? SelectedItem
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            foreach (TabVisual visual in _visuals) visual.Selected = visual.Tab == value;
            Relayout(animate: true);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (int i = 0; i < e.NewItems!.Count; i++)
                    _visuals.Insert(Math.Min(e.NewStartingIndex + i, _visuals.Count), Create((BrowserTab)e.NewItems[i]!, appear: true));
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (BrowserTab tab in e.OldItems!)
                    if (_visuals.FirstOrDefault(v => v.Tab == tab && !v.Closing) is { } gone) BeginClose(gone);
                break;
            default: // Move, Replace, Reset: rebuild the order from the source, keeping the visuals that still exist
                var byTab = _visuals.Where(v => !v.Closing).ToDictionary(v => v.Tab);
                var ordered = new List<TabVisual>();
                foreach (BrowserTab tab in _source!)
                    ordered.Add(byTab.TryGetValue(tab, out TabVisual? kept) ? kept : Create(tab, appear: true));
                foreach (TabVisual stale in _visuals.Where(v => !v.Closing && !_source.Contains(v.Tab)).ToArray()) BeginClose(stale);
                _visuals.RemoveAll(v => !v.Closing);
                _visuals.AddRange(ordered);
                break;
        }
        Relayout(animate: true);
        ItemsChanged?.Invoke();
    }

    private TabVisual Create(BrowserTab tab, bool appear)
    {
        var visual = new TabVisual(this, tab) { Selected = tab == _selected };
        if (appear) { visual.W = 0; visual.X = double.NaN; }
        Children.Add(visual);
        return visual;
    }

    private void BeginClose(TabVisual visual)
    {
        visual.Closing = true;
        visual.IsHitTestVisible = false;
        if (_hovered == visual) SetHover(null);
    }

    private void Discard(TabVisual visual)
    {
        visual.Detach();
        Children.Remove(visual);
        _visuals.Remove(visual);
    }

    // ---------- Layout ----------

    private IEnumerable<TabVisual> Live => _visuals.Where(v => !v.Closing);

    private void Relayout(bool animate)
    {
        double total = ActualWidth;
        if (total <= 0) return;
        List<TabVisual> live = Live.ToList();
        int pinned = live.Count(v => v.Tab.Pinned), flex = live.Count - pinned;
        double avail = total - AddWidth - FreeSpace - pinned * PinnedWidth;
        double w = flex > 0 ? Math.Clamp(avail / flex, MinTabWidth, MaxTabWidth) : MaxTabWidth;
        double selectedWidth = w;
        TabVisual? selected = live.FirstOrDefault(v => v.Selected && !v.Tab.Pinned);
        if (selected is not null && flex > 1 && w < SelectedMinWidth)
        {
            // Everything shrinks to make room for the selected tab's close button, like Edge.
            selectedWidth = SelectedMinWidth;
            w = Math.Clamp((avail - selectedWidth) / (flex - 1), MinTabWidth, MaxTabWidth);
        }
        else if (selected is not null && flex == 1) selectedWidth = w = Math.Clamp(avail, MinTabWidth, MaxTabWidth);
        // After closing a tab with the mouse the others keep their width until the pointer leaves, so the next close button stays put.
        if (_lockedWidth is { } locked && w < locked && flex > 0) w = Math.Min(Math.Min(locked, MaxTabWidth), avail / flex);
        double x = 0;
        foreach (TabVisual visual in live)
        {
            double width = visual.Tab.Pinned ? PinnedWidth : visual == selected ? Math.Max(selectedWidth, Math.Min(w, MaxTabWidth)) : w;
            visual.TargetX = x;
            visual.TargetW = width;
            x += width;
        }
        foreach (TabVisual visual in _visuals.Where(v => v.Closing)) visual.TargetW = 0;
        _addTargetX = x + 2;
        _contentRight = x + AddWidth;
        if (!animate)
            foreach (TabVisual visual in _visuals) { visual.X = visual.TargetX; visual.W = visual.TargetW; }
        else
            foreach (TabVisual visual in _visuals)
                if (double.IsNaN(visual.X)) visual.X = visual.TargetX; // newly added: grows in place from zero width
        if (!animate || double.IsNaN(_addX)) _addX = _addTargetX;
        UpdateSeparators(live);
        Apply();
        EnsureAnimating();
        LayoutChanged?.Invoke();
    }

    private void UpdateSeparators(List<TabVisual> live)
    {
        for (int i = 0; i < live.Count; i++)
        {
            TabVisual current = live[i];
            TabVisual? next = i + 1 < live.Count ? live[i + 1] : null;
            bool quiet = current.Selected || current.Hovered || next is null || next.Selected || next.Hovered;
            current.SetSeparator(!quiet, SeparatorBrush);
        }
    }

    private void Apply()
    {
        foreach (TabVisual visual in _visuals) visual.Place();
        SetLeft(_add, _addX);
    }

    // ---------- Animation ----------

    private void EnsureAnimating()
    {
        if (_subscribed) return;
        _subscribed = true;
        _lastFrame = Stopwatch.GetTimestamp();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(.05, (now - _lastFrame) / (double)Stopwatch.Frequency);
        _lastFrame = now;
        double move = 1 - Math.Exp(-dt * 17), fade = 1 - Math.Exp(-dt * 30);
        bool busy = false;
        foreach (TabVisual visual in _visuals.ToArray())
        {
            if (!visual.Dragging) visual.X = Approach(visual.X, visual.TargetX, move, ref busy);
            visual.W = Approach(visual.W, visual.TargetW, move, ref busy);
            visual.PillOpacity = Approach(visual.PillOpacity, visual.TargetPill, fade, ref busy, .01);
            if (visual.Closing && visual.W < 1.5) { Discard(visual); continue; }
            visual.Place();
        }
        _addX = Approach(_addX, _addTargetX, move, ref busy);
        SetLeft(_add, _addX);
        if (!busy) Unsubscribe();
        LayoutChanged?.Invoke();
    }

    private static double Approach(double current, double target, double factor, ref bool busy, double epsilon = .25)
    {
        if (double.IsNaN(current)) return target;
        double difference = target - current;
        if (Math.Abs(difference) <= epsilon) return target;
        busy = true;
        return current + difference * factor;
    }

    // ---------- Hover, drag helpers ----------

    internal void SetHover(TabVisual? visual)
    {
        if (_hovered == visual) return;
        if (_hovered is not null) _hovered.Hovered = false;
        _hovered = visual;
        if (visual is not null) visual.Hovered = true;
        UpdateSeparators(Live.ToList());
        EnsureAnimating();
        HoverChanged?.Invoke(visual?.Tab, visual is null ? Rect.Empty : new Rect(visual.TargetX, 0, visual.TargetW, StripHeight));
    }

    internal void RaisePressed(TabVisual visual, PointerRoutedEventArgs e)
    {
        TabInvoked?.Invoke(visual.Tab);
        TabPressed?.Invoke(visual.Tab, e);
    }

    internal void RaiseClose(TabVisual visual)
    {
        // Keep the tabs' width while the pointer stays here, so closing several in a row hits the next close button in the same place.
        if (Live.Count() > 1) _lockedWidth = visual.TargetW;
        CloseRequested?.Invoke(visual.Tab);
    }

    internal void RaiseContext(TabVisual visual, Point point) => TabContextRequested?.Invoke(visual.Tab, point);

    /// <summary>Re-reads the fills and separator colour after the palette changed.</summary>
    public void ApplyBrushes()
    {
        foreach (TabVisual visual in _visuals) visual.Selected = visual.Tab == _selected;
        UpdateSeparators(Live.ToList());
    }

    public bool Contains(BrowserTab tab) => _visuals.Any(v => v.Tab == tab && !v.Closing);

    public void SetDragging(BrowserTab tab, bool dragging)
    {
        if (_visuals.FirstOrDefault(v => v.Tab == tab && !v.Closing) is not { } visual) return;
        visual.Dragging = dragging;
        SetZIndex(visual, dragging ? 10 : visual.Selected ? 2 : 0);
        if (dragging) SetHover(null);
        EnsureAnimating();
    }

    /// <summary>Moves a dragged tab with the pointer (strip coordinates of its left edge).</summary>
    public void DragTo(BrowserTab tab, double left)
    {
        if (_visuals.FirstOrDefault(v => v.Tab == tab && !v.Closing) is not { } visual) return;
        visual.X = Math.Clamp(left, 0, Math.Max(0, _contentRight - AddWidth - visual.W));
        visual.Place();
    }

    public double LeftOf(BrowserTab tab) => _visuals.FirstOrDefault(v => v.Tab == tab && !v.Closing)?.X ?? 0;
    public double WidthOf(BrowserTab tab) => _visuals.FirstOrDefault(v => v.Tab == tab && !v.Closing)?.TargetW ?? 0;

    /// <summary>The index a dragged tab with this centre would take among the other tabs.</summary>
    public int SlotFor(BrowserTab dragged, double centre)
    {
        int slot = 0;
        foreach (TabVisual visual in Live)
        {
            if (visual.Tab == dragged) continue;
            if (centre > visual.TargetX + visual.TargetW / 2) slot++;
        }
        return slot;
    }

    public Rect? BoundsOf(BrowserTab tab) =>
        _visuals.FirstOrDefault(v => v.Tab == tab && !v.Closing) is { } v ? new Rect(v.TargetX, 0, v.TargetW, StripHeight) : null;

    // ---------- One tab ----------

    internal sealed class TabVisual : Grid
    {
        private readonly TabStrip _owner;
        private readonly Border _pill = new() { CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 3, 2, 3), IsHitTestVisible = false, Opacity = 0 };
        private readonly Border _separator = new() { Width = 1, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly Grid _body = new() { Margin = new Thickness(11, 3, 5, 3), ColumnSpacing = 8, IsHitTestVisible = true };
        private readonly Grid _iconHost = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        private readonly Image _favicon = new() { Width = 16, Height = 16, Stretch = Stretch.Uniform };
        private readonly FontIcon _glyph = new() { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        private readonly ProgressRing _spinner = new() { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed };
        private readonly FontIcon _audioIcon = new() { FontSize = 11 };
        private readonly Button _audio;
        private bool _syncQueued, _relayoutQueued;
        private readonly Button _close;
        private bool _hovered, _selected;

        public BrowserTab Tab { get; }
        public double X { get; set; }
        public double W { get; set; }
        public double TargetX { get; set; }
        public double TargetW { get; set; }
        public double PillOpacity { get; set; }
        public double TargetPill { get; private set; }
        public bool Closing { get; set; }
        public bool Dragging { get; set; }

        public bool Selected { get => _selected; set { _selected = value; Refresh(); Canvas.SetZIndex(this, value ? 2 : 0); } }
        public bool Hovered { get => _hovered; set { _hovered = value; Refresh(); } }

        public TabVisual(TabStrip owner, BrowserTab tab)
        {
            _owner = owner;
            Tab = tab;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Height = StripHeight;
            _close = new Button
            {
                Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(Motion.Fx.ControlRadius),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { FontSize = 10, Glyph = "" }
            };
            AutomationProperties.SetName(_close, "Cerrar pestaña");
            _close.Click += (_, _) => _owner.RaiseClose(this);
            Motion.Fx.Icon(_close);
            _audio = new Button
            {
                Width = 20, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(Motion.Fx.ControlRadius),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Content = _audioIcon, Visibility = Visibility.Collapsed
            };
            _audio.Click += (_, _) => _owner.MuteRequested?.Invoke(Tab);
            Motion.Fx.Icon(_audio);
            _iconHost.Children.Add(_spinner);
            _iconHost.Children.Add(_glyph);
            _iconHost.Children.Add(_favicon);
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(_title, 1); SetColumn(_audio, 2); SetColumn(_close, 3);
            _body.Children.Add(_iconHost); _body.Children.Add(_title); _body.Children.Add(_audio); _body.Children.Add(_close);
            Children.Add(_pill); Children.Add(_body); Children.Add(_separator);
            AutomationProperties.SetAutomationId(this, "Tab");
            PointerEntered += (_, _) => { if (!Closing) _owner.SetHover(this); };
            PointerExited += (_, e) => { if (_owner._hovered == this && !_owner.IsPointerOver(e)) _owner.SetHover(null); };
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressed), true);
            ContextRequested += (_, e) =>
            {
                if (!e.TryGetPosition(_owner, out Point point)) point = new Point(TargetX, StripHeight);
                _owner.RaiseContext(this, point);
                e.Handled = true;
            };
            tab.PropertyChanged += OnTabChanged;
            Refresh();
            Sync();
        }

        public void Detach() => Tab.PropertyChanged -= OnTabChanged;

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            Microsoft.UI.Input.PointerPointProperties p = e.GetCurrentPoint(this).Properties;
            if (IsOverClose(e.OriginalSource as DependencyObject)) return;
            if (p.IsMiddleButtonPressed) { _owner.RaiseClose(this); e.Handled = true; return; }
            if (p.IsLeftButtonPressed) _owner.RaisePressed(this, e);
        }

        private bool IsOverClose(DependencyObject? element)
        {
            for (; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
                if (element == _close || element == _audio) return true;
            return false;
        }

        private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
        {
            // One change of a tab raises several notifications (icon, glyph, visibility...): they collapse into a single update.
            if (e.PropertyName is nameof(BrowserTab.Pinned)) _relayoutQueued = true;
            if (_syncQueued) return;
            _syncQueued = true;
            if (DispatcherQueue?.TryEnqueue(() =>
            {
                _syncQueued = false;
                Sync();
                if (!_relayoutQueued) return;
                _relayoutQueued = false;
                _owner.Relayout(animate: true);
            }) != true) _syncQueued = false;
        }

        /// <summary>Copies the tab's title, icon and state onto the controls.</summary>
        private void Sync()
        {
            _title.Text = Tab.Title;
            bool loading = Tab.IsLoading;
            // While the page loads the icon gives way to a small progress ring, as in every browser.
            _spinner.IsActive = loading;
            _spinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            _favicon.Source = Tab.FaviconSource;
            _favicon.Visibility = loading || Tab.FaviconSource is null ? Visibility.Collapsed : Visibility.Visible;
            _glyph.Glyph = Tab.Glyph;
            _glyph.Visibility = loading || Tab.FaviconSource is not null ? Visibility.Collapsed : Visibility.Visible;
            _audioIcon.Glyph = Tab.Muted ? "" : "";
            string audio = Tab.Muted ? "Activar el sonido de la pestaña" : "Silenciar la pestaña";
            AutomationProperties.SetName(_audio, audio);
            ToolTipService.SetToolTip(_audio, audio);
            AutomationProperties.SetName(this, Tab.Title);
            Detail();
        }

        private void Refresh()
        {
            TargetPill = _selected || _hovered ? 1 : 0;
            _pill.Background = _selected ? _owner.SelectedFill : _owner.HoverFill;
            _owner.EnsureAnimating();
            Detail();
        }

        public void SetSeparator(bool visible, Brush brush)
        {
            _separator.Background = brush;
            _separator.Opacity = visible ? 1 : 0;
        }

        /// <summary>Which parts fit: title, close button and audio icon appear as the tab widens; a tab too narrow for text is just its icon.</summary>
        private void Detail()
        {
            double width = Math.Max(W, TargetW);
            bool pinned = Tab.Pinned;
            bool close = !pinned && !Closing && ((_selected && width >= 60) || (_hovered && width >= 84) || width >= 140);
            // Room for text: 56 px beyond the icon, plus 30 more when the close button shares the tab.
            bool title = !pinned && width >= (close ? 112 : 78);
            _title.Visibility = title ? Visibility.Visible : Visibility.Collapsed;
            _audio.Visibility = (Tab.Muted || Tab.Audible) && width >= 110 ? Visibility.Visible : Visibility.Collapsed;
            _close.Visibility = close ? Visibility.Visible : Visibility.Collapsed;
            bool iconOnly = !title && !close;
            SetColumnSpan(_iconHost, iconOnly ? 4 : 1);
            _iconHost.HorizontalAlignment = iconOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _body.Margin = iconOnly ? new Thickness(2, 3, 2, 3) : new Thickness(11, 3, 5, 3);
        }

        public void Place()
        {
            if (double.IsNaN(X)) return;
            Canvas.SetLeft(this, X);
            Width = Math.Max(0, W);
            Opacity = Closing ? Math.Clamp(W / 24, 0, 1) : Math.Clamp(W / Math.Max(20, Math.Min(TargetW, 40)), 0, 1);
            _pill.Opacity = PillOpacity;
            Detail();
        }
    }

    private bool IsPointerOver(PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(this).Position;
        return p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight &&
            _visuals.Any(v => !v.Closing && p.X >= v.X && p.X < v.X + v.W);
    }
}
