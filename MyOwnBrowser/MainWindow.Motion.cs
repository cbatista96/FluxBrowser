using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyOwnBrowser.Browser;
using MyOwnBrowser.Motion;
using Windows.UI.ViewManagement;

namespace MyOwnBrowser;

public sealed partial class MainWindow
{
    internal static readonly UISettings SystemSettings = new();
    private readonly FrameLoop _frames = new();
    private readonly Dictionary<FrameworkElement, (FrameLoop.Animation? Animation, TaskCompletionSource<bool> Done)> _motions = [];
    private readonly HashSet<BrowserTab> _closingTabs = [];
    private FrameLoop.Animation? _addressAnimation;
    private FrameLoop.Animation? _revealAnimation;
    private FrameLoop.Animation? _loadBarAnimation;
    private double _addressTarget;
    private bool MotionEnabled => Preferences.AnimateInterface && SystemSettings.AnimationsEnabled;

    private static float DepthOf(FrameworkElement element) => element.Shadow is null ? 0 : Fx.OverlayDepth;

    /// <summary>
    /// Opens or closes a floating surface. The task yields false when a later call on the same element took over before this one
    /// finished. The motion runs on the compositor thread (it stays smooth while the UI thread builds the surface's contents); only
    /// the off-screen engine, whose glass has to follow the element frame by frame, still steps it from the UI thread.
    /// </summary>
    private Task<bool> AnimateSurface(FrameworkElement element, bool opening, bool glass = false)
    {
        if (_compositor is not null) return AnimateSurfaceFrames(element, opening, glass);
        bool interrupted = _motions.Remove(element, out var previous);
        if (interrupted) previous.Done.TrySetResult(false);
        float depth = DepthOf(element);
        if (!MotionEnabled || _closing)
        {
            Fx.SurfaceAt(element, opening, depth);
            return Task.FromResult(true);
        }
        var done = new TaskCompletionSource<bool>();
        _motions[element] = (null, done);
        Fx.Surface(element, opening, fresh: !interrupted, depth).Completed += (_, _) =>
        {
            // A batch also completes when a newer animation replaces its own: only the motion still on record may finish.
            if (!_motions.TryGetValue(element, out var current) || current.Done != done) return;
            _motions.Remove(element);
            // The animation only moved what is drawn; the element's own properties (what XAML hit-tests) are set to where it ended.
            Fx.SurfaceAt(element, opening, depth);
            done.TrySetResult(true);
        };
        return done.Task;
    }

    /// <summary>Forgets a surface's running motion and leaves it fully shown or hidden.</summary>
    private void SettleSurface(FrameworkElement element, bool shown)
    {
        if (_motions.Remove(element, out var motion)) { motion.Animation?.Stop(); motion.Done.TrySetResult(false); }
        if (_compositor is null) Fx.SurfaceAt(element, shown, DepthOf(element));
        else { element.Opacity = shown ? 1 : 0; element.RenderTransform = null; }
    }

    private Task<bool> AnimateSurfaceFrames(FrameworkElement element, bool opening, bool glass)
    {
        bool interrupted = _motions.Remove(element, out var previous);
        if (interrupted)
        {
            previous.Animation?.Stop();
            previous.Done.TrySetResult(false);
        }
        double from = opening && !interrupted ? 0 : element.Opacity;
        if (!MotionEnabled || _closing)
        {
            element.Opacity = opening ? 1 : 0;
            element.RenderTransform = null;
            return Task.FromResult(true);
        }
        // Glass over a WebView2 page is drawn by the page and reaches it by message, so it cannot follow a moving element frame by frame
        // (it trails and smears at the edges). Those surfaces only fade; the page fades its own glass at the same time.
        bool fadeOnly = false;
        var oldTransform = element.RenderTransform as CompositeTransform;
        double fromY = interrupted ? oldTransform?.TranslateY ?? 0 : opening ? -5 : 0;
        double fromScale = interrupted ? oldTransform?.ScaleX ?? 1 : opening ? .975 : 1;
        var transform = new CompositeTransform { CenterX = element.ActualWidth / 2 };
        element.RenderTransform = fadeOnly ? null : transform;
        var done = new TaskCompletionSource<bool>();
        FrameLoop.Animation animation = _frames.Start(opening ? 200 : 130, t =>
        {
            double eased = opening ? Easing.OutCubic(t) : Easing.InQuad(t);
            element.Opacity = Easing.Lerp(from, opening ? 1 : 0, eased);
            if (fadeOnly) return;
            transform.TranslateY = Easing.Lerp(fromY, opening ? 0 : 3, eased);
            transform.ScaleX = transform.ScaleY = Easing.Lerp(fromScale, opening ? 1 : .975, eased);
            // The GPU glass behind a menu is drawn by the compositor, so it must follow the element every frame.
            if (glass && _compositor is not null) UpdateGlassRegions();
        }, () =>
        {
            _motions.Remove(element);
            done.TrySetResult(true);
        });
        _motions[element] = (animation, done);
        return done.Task;
    }

    private void StopMotion()
    {
        foreach (var motion in _motions.Values) motion.Done.TrySetResult(false);
        _motions.Clear();
        _frames.StopAll();
    }

    private static FrameworkElement? NamedChild(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (NamedChild(child, name) is { } nested) return nested;
        }
        return null;
    }

    private void AnimateAddressWidth(double target, bool animate)
    {
        bool running = _addressAnimation?.IsRunning == true;
        _addressTarget = target;
        if (running && !animate) return;
        double from = double.IsNaN(AddressHost.Width) || AddressHost.Width <= 1 ? target : AddressHost.Width;
        _addressAnimation?.Stop();
        if (!animate || !MotionEnabled || Math.Abs(from - target) < 2) { AddressHost.Width = target; return; }
        _addressAnimation = _frames.Start(240, t => AddressHost.Width = Easing.Lerp(from, _addressTarget, Easing.OutQuart(t)));
    }

    private void ToolbarRow_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateAddressLayout();
    private void AddressBox_FocusChanged(object sender, RoutedEventArgs args)
    {
        // The domain sits in the middle of the box; once it is being edited the full address reads from the left.
        AddressBox.TextAlignment = AddressBox.FocusState == FocusState.Unfocused ? TextAlignment.Center : TextAlignment.Left;
        if (_activeTab is { } tab)
        {
            if (AddressBox.FocusState == FocusState.Unfocused) UpdateAddress(tab.Address);
            else
            {
                // Clicking the box reveals the complete address, selected so typing replaces it.
                SetAddressText(AddressText(tab.Address, true));
                DispatcherQueue.TryEnqueue(() => { if (AddressBox.FocusState != FocusState.Unfocused) AddressBox.SelectAll(); });
            }
        }
        if (AddressBox.FocusState == FocusState.Unfocused) HideSuggestions();
        UpdateAddressLayout(true);
    }
    private void AddressBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        UpdateAddressLayout();
        UpdateSuggestions();
    }

    private void UpdateAddressLayout(bool animate = false)
    {
        if (AddressHost is null || ToolbarRow is null || ToolbarRow.ActualWidth <= 0) return;
        double left = ToolbarRow.ColumnDefinitions.Take(4).Sum(c => c.ActualWidth) + 8;
        double right = ToolbarRow.ColumnDefinitions.Skip(5).Sum(c => c.ActualWidth) + 8;
        double offset = (right - left) / 2;
        double available = ToolbarRow.ColumnDefinitions[4].ActualWidth - 8 - 2 * Math.Abs(offset);
        if (available <= 0) return;
        var measure = new TextBlock { Text = string.IsNullOrEmpty(AddressBox.Text) ? AddressBox.PlaceholderText : AddressBox.Text, FontSize = AddressBox.FontSize, FontFamily = AddressBox.FontFamily };
        measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double wanted = measure.DesiredSize.Width + 90;
        if (AddressBox.FocusState != FocusState.Unfocused) wanted = Math.Max(600, wanted);
        AnimateAddressWidth(Math.Max(1, Math.Min(available, Math.Clamp(wanted, 360, 900))), animate);
        AddressHost.RenderTransform = new TranslateTransform { X = offset };
    }

    /// <summary>
    /// Every icon answers a click: it squeezes while pressed and then plays a small motion that says what the button does (the arrow
    /// pushes its way, reload turns, downloads dips). The button's hover plate stays still. Runs on the compositor thread.
    /// </summary>
    private void WireInteractiveEffects()
    {
        Fx.Icon(BackButton, IconMotion.NudgeLeft);
        Fx.Icon(ForwardButton, IconMotion.NudgeRight);
        Fx.Icon(ReloadButton, IconMotion.Spin);
        Fx.Icon(GoButton, IconMotion.Lift);
        Fx.Icon(FavoritesButton);
        Fx.Icon(HistoryButton, IconMotion.Tilt);
        Fx.Icon(DownloadsButton, IconMotion.Drop);
        Fx.Icon(MainMenuButton);
        Fx.Icon(BookmarkButton);
        Fx.Icon(ZoomChip);
        Fx.Icon(TabActionsButton, IconMotion.Drop);
        Fx.Icon(VerticalTabActionsButton, IconMotion.Drop);
        Fx.Icon(VerticalNewTabButton, IconMotion.Lift, VerticalNewTabIcon);
        Fx.Icon(VerticalSettingsButton, IconMotion.Tilt, VerticalSettingsIcon);
        Fx.Icon(ManagementCloseButton);
        Fx.Icon(FindPreviousButton, IconMotion.Lift);
        Fx.Icon(FindNextButton, IconMotion.Drop);
        Fx.Icon(FindCloseButton);
        Fx.Icon(SandboxCloseButton);
    }

    /// <summary>Thin progress rail under the toolbar while the active tab loads; it fades instead of blinking.</summary>
    private void SetLoadingIndicator(bool loading)
    {
        bool shown = LoadBar.Visibility == Visibility.Visible;
        _loadBarAnimation?.Stop();
        _loadBarAnimation = null;
        if (loading)
        {
            LoadBar.Visibility = Visibility.Visible;
            LoadBar.IsIndeterminate = true;
            if (!MotionEnabled) { LoadBar.Opacity = 1; return; }
            double from = shown ? LoadBar.Opacity : 0;
            _loadBarAnimation = _frames.Start(180, t => LoadBar.Opacity = Easing.Lerp(from, 1, t), () => _loadBarAnimation = null);
            return;
        }
        if (!shown || !MotionEnabled)
        {
            LoadBar.IsIndeterminate = false;
            LoadBar.Visibility = Visibility.Collapsed;
            return;
        }
        double start = LoadBar.Opacity;
        _loadBarAnimation = _frames.Start(260, t => LoadBar.Opacity = Easing.Lerp(start, 0, Easing.OutCubic(t)), () =>
        {
            LoadBar.IsIndeterminate = false;
            LoadBar.Visibility = Visibility.Collapsed;
            _loadBarAnimation = null;
        });
    }

    /// <summary>Greeting, search and shortcuts rise into place one after another when a new-tab page appears.</summary>
    private void RevealNewTab()
    {
        _revealAnimation?.Stop();
        var targets = new List<FrameworkElement> { NewTabGreeting, NewTabSearchHost };
        targets.AddRange(QuickLinks.Children.OfType<FrameworkElement>());
        if (_compositor is null)
        {
            // Compositor thread: the stagger keeps its rhythm while the UI thread is still building the page's tiles.
            bool move = MotionEnabled && !_closing;
            for (int i = 0; i < targets.Count; i++)
            {
                if (move) Fx.Rise(targets[i], i * 45);
                else Fx.SurfaceAt(targets[i], true, 0);
            }
            return;
        }
        foreach (FrameworkElement element in targets) { element.Opacity = 1; element.RenderTransform = null; }
        if (!MotionEnabled || _closing) return;
        const double step = 55, duration = 320;
        double total = duration + step * (targets.Count - 1);
        var transforms = targets.Select(_ => new TranslateTransform { Y = 14 }).ToList();
        for (int i = 0; i < targets.Count; i++) { targets[i].Opacity = 0; targets[i].RenderTransform = transforms[i]; }
        _revealAnimation = _frames.Start(total, overall =>
        {
            double elapsed = overall * total;
            for (int i = 0; i < targets.Count; i++)
            {
                double eased = Easing.OutQuart(Math.Clamp((elapsed - i * step) / duration, 0, 1));
                targets[i].Opacity = eased;
                transforms[i].Y = Easing.Lerp(14, 0, eased);
            }
        }, () => { foreach (FrameworkElement element in targets) element.RenderTransform = null; });
    }
}
