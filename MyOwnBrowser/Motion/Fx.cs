using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MyOwnBrowser.Motion;

/// <summary>What an icon does when its button is clicked, on top of the press squeeze every icon shares.</summary>
internal enum IconMotion
{
    /// <summary>Squeeze and spring back, slightly past its size.</summary>
    Pop,
    /// <summary>One full turn (reload, restart).</summary>
    Spin,
    /// <summary>A short turn and back (settings gear, more).</summary>
    Tilt,
    /// <summary>A push to the left and back (back).</summary>
    NudgeLeft,
    /// <summary>A push to the right and back (forward).</summary>
    NudgeRight,
    /// <summary>A dip and back (downloads, expand).</summary>
    Drop,
    /// <summary>A hop and back (home, new tab, collapse).</summary>
    Lift
}

/// <summary>
/// Compositor-thread motion: every animation here runs on the composition thread, so it stays smooth while the UI thread is busy
/// building a menu or laying out a page. The values follow the Windows 11 motion guidance (short, decelerating, no bounce on
/// surfaces; a light spring on what the pointer touches).
/// </summary>
internal static class Fx
{
    /// <summary>Radii of the Windows 11 geometry: controls (buttons, rows, inputs) and surfaces that float above the page.</summary>
    public const double ControlRadius = 4, OverlayRadius = 8;

    /// <summary>How far a floating surface is lifted for its shadow.</summary>
    public const float OverlayDepth = 32;

    /// <summary>
    /// Whether motion is wanted right now: the user's own setting and the Windows "animation effects" option. Evaluated on every
    /// interaction, so a change takes effect without a restart. Set once by the application.
    /// </summary>
    public static Func<bool> Allowed { get; set; } = () => true;

    private static CubicBezierEasingFunction? _decelerate, _accelerate, _standard;
    private static Compositor? _easingOwner;

    private static void Easings(Compositor compositor)
    {
        if (_easingOwner == compositor) return;
        _easingOwner = compositor;
        // Fast-out/slow-in for things arriving, the reverse for things leaving, and the standard curve for flourishes.
        _decelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));
        _accelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(.7f, 0f), new Vector2(1f, .5f));
        _standard = compositor.CreateCubicBezierEasingFunction(new Vector2(.8f, 0f), new Vector2(.2f, 1f));
    }

    public static CubicBezierEasingFunction Decelerate(Compositor compositor) { Easings(compositor); return _decelerate!; }
    public static CubicBezierEasingFunction Accelerate(Compositor compositor) { Easings(compositor); return _accelerate!; }

    /// <summary>
    /// Gives <paramref name="element"/> a springy hover lift and press squeeze (whole tiles, where the plate is the thing touched).
    /// </summary>
    public static void Interactive(FrameworkElement element, float hover = 1.06f, float pressed = .93f)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        bool inside = false;

        void Go(float target)
        {
            if (!Allowed()) { visual.StopAnimation("Scale"); visual.Scale = Vector3.One; return; }
            Centre(visual, element);
            Spring(visual, target);
        }

        element.PointerEntered += (_, _) => { inside = true; Go(hover); };
        element.PointerExited += (_, _) => { inside = false; Go(1); };
        element.PointerCanceled += (_, _) => { inside = false; Go(1); };
        element.PointerCaptureLost += (_, _) => Go(inside ? hover : 1);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Go(pressed)), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Go(inside ? hover : 1)), true);
    }

    /// <summary>
    /// Animates the icon inside <paramref name="button"/> (its content, so the hover plate itself stays put, as in Windows 11): it
    /// squeezes while pressed, springs back on release and plays <paramref name="motion"/> when the button is actually invoked, by
    /// mouse, touch or keyboard alike. <paramref name="icon"/> names the element to move when the content is a whole row.
    /// </summary>
    public static void Icon(Button button, IconMotion motion = IconMotion.Pop, FrameworkElement? icon = null)
    {
        MarkWired(button);
        bool pressed = false;

        // The content can be replaced after wiring, so it is looked up on every interaction.
        (Visual Visual, FrameworkElement Element)? Target() =>
            (icon ?? button.Content as FrameworkElement) is { } content ? (ElementCompositionPreview.GetElementVisual(content), content) : null;

        void Squeeze(bool down)
        {
            if (Target() is not { } target) return;
            if (!Allowed()) { target.Visual.StopAnimation("Scale"); target.Visual.Scale = Vector3.One; return; }
            Centre(target.Visual, target.Element);
            Spring(target.Visual, down ? .82f : 1f);
        }

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            pressed = true;
            Squeeze(true);
        }), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { if (pressed) { pressed = false; Squeeze(false); } }), true);
        button.PointerExited += (_, _) => { if (pressed) { pressed = false; Squeeze(false); } };
        button.PointerCanceled += (_, _) => { if (pressed) { pressed = false; Squeeze(false); } };
        button.PointerCaptureLost += (_, _) => { if (pressed) { pressed = false; Squeeze(false); } };
        button.Click += (_, _) =>
        {
            pressed = false;
            if (Target() is { } target && Allowed()) Play(target.Visual, target.Element, motion);
        };
    }

    /// <summary>Plays an icon's flourish once, without a click (used when a state changes by itself, such as a page starting to load).</summary>
    public static void Play(FrameworkElement element, IconMotion motion)
    {
        if (Allowed()) Play(ElementCompositionPreview.GetElementVisual(element), element, motion);
    }

    private static void Play(Visual visual, FrameworkElement element, IconMotion motion)
    {
        Compositor compositor = visual.Compositor;
        Easings(compositor);
        Centre(visual, element);
        switch (motion)
        {
            case IconMotion.Spin:
            case IconMotion.Tilt:
            {
                ScalarKeyFrameAnimation turn = compositor.CreateScalarKeyFrameAnimation();
                turn.InsertKeyFrame(0, 0);
                if (motion == IconMotion.Spin) turn.InsertKeyFrame(1, 360, _decelerate);
                else { turn.InsertKeyFrame(.45f, 50, _decelerate); turn.InsertKeyFrame(1, 0, _standard); }
                turn.Duration = TimeSpan.FromMilliseconds(motion == IconMotion.Spin ? 520 : 420);
                visual.StartAnimation("RotationAngleInDegrees", turn);
                Settle(visual);
                break;
            }
            case IconMotion.NudgeLeft: Shift(visual, element, new Vector3(-4, 0, 0)); break;
            case IconMotion.NudgeRight: Shift(visual, element, new Vector3(4, 0, 0)); break;
            case IconMotion.Drop: Shift(visual, element, new Vector3(0, 3.5f, 0)); break;
            case IconMotion.Lift: Shift(visual, element, new Vector3(0, -3.5f, 0)); break;
            default:
            {
                Vector3KeyFrameAnimation pop = compositor.CreateVector3KeyFrameAnimation();
                pop.InsertKeyFrame(.4f, new Vector3(1.18f, 1.18f, 1), _decelerate);
                pop.InsertKeyFrame(1, Vector3.One, _standard);
                pop.Duration = TimeSpan.FromMilliseconds(320);
                visual.StartAnimation("Scale", pop);
                break;
            }
        }
    }

    private static void Shift(Visual visual, FrameworkElement element, Vector3 by)
    {
        // Layout owns the visual's offset, so the push goes through the separate translation property.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        Vector3KeyFrameAnimation shift = visual.Compositor.CreateVector3KeyFrameAnimation();
        shift.InsertKeyFrame(0, Vector3.Zero);
        shift.InsertKeyFrame(.4f, by, _decelerate);
        shift.InsertKeyFrame(1, Vector3.Zero, _standard);
        shift.Duration = TimeSpan.FromMilliseconds(340);
        visual.StartAnimation("Translation", shift);
        Settle(visual);
    }

    /// <summary>Lets go of the press squeeze when a flourish that does not animate the scale takes over.</summary>
    private static void Settle(Visual visual) => Spring(visual, 1);

    private static void Spring(Visual visual, float target)
    {
        SpringVector3NaturalMotionAnimation spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(target, target, 1);
        spring.DampingRatio = .62f;
        spring.Period = TimeSpan.FromMilliseconds(42);
        visual.StartAnimation("Scale", spring);
    }

    private static void Centre(Visual visual, FrameworkElement element) =>
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);

    /// <summary>Wires <see cref="Icon"/> on every button under <paramref name="root"/> that has not been wired yet (lists and menus built at run time).</summary>
    public static void IconsIn(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.Content is FrameworkElement && !WiredButtons.TryGetValue(button, out _))
            {
                WiredButtons.Add(button, WiredMark);
                Icon(button);
            }
            IconsIn(child);
        }
    }

    /// <summary>Marks a button as already animated, so a later sweep of its container does not wire it twice.</summary>
    public static void MarkWired(Button button) => WiredButtons.AddOrUpdate(button, WiredMark);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> WiredButtons = new();
    private static readonly object WiredMark = new();

    // ---------- Surfaces ----------
    // Surfaces are animated through the element's own XAML properties (Opacity, Scale, Translation) with UIElement.StartAnimation, never
    // through its composition visual. A surface's elevation (the Z of Translation, which casts its ThemeShadow) is a XAML property, and
    // XAML hit-tests from those properties: driving Translation on the composition visual of an element that also sets it in XAML made
    // the element invisible to hit testing, so clicks on a menu went straight through it to whatever was underneath.

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, List<CompositionAnimation>> Running = new();

    private static Compositor Compositor() => Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();

    private static void Start(UIElement element, CompositionAnimation animation, string target)
    {
        animation.Target = target;
        Running.GetOrCreateValue(element).Add(animation);
        element.StartAnimation(animation);
    }

    private static void StopAll(UIElement element)
    {
        if (!Running.TryGetValue(element, out List<CompositionAnimation>? list)) return;
        foreach (CompositionAnimation animation in list) element.StopAnimation(animation);
        list.Clear();
    }

    /// <summary>
    /// XAML refuses CenterPoint/Scale/Translation/Opacity animations on an element whose RenderTransform is in use (list rows
    /// get one from their presenter), and throws. Those elements are animated on their composition visual instead, with opacity
    /// and scale only: moving that visual would not break hit testing for them, but they never set an elevation either.
    /// </summary>
    private static bool UsesFacades(UIElement element) =>
        element is not Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
        && element.ReadLocalValue(UIElement.RenderTransformProperty) == DependencyProperty.UnsetValue;

    private static void StopVisual(Visual visual)
    {
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
    }

    private static CompositionScopedBatch SurfaceOnVisual(FrameworkElement element, bool opening, bool fresh)
    {
        Compositor compositor = Compositor();
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        double width = element.ActualWidth > 0 ? element.ActualWidth : 0;
        visual.CenterPoint = new Vector3((float)width / 2, 0, 0);
        StopVisual(visual);
        if (opening && fresh)
        {
            visual.Opacity = 0;
            visual.Scale = new Vector3(.97f, .97f, 1);
        }
        CompositionEasingFunction easing = opening ? _decelerate! : _accelerate!;
        TimeSpan duration = TimeSpan.FromMilliseconds(opening ? 220 : 130);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opening ? 1 : 0, easing);
        fade.Duration = duration;
        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, opening ? Vector3.One : new Vector3(.98f, .98f, 1), easing);
        scale.Duration = duration;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", scale);
        batch.End();
        return batch;
    }

    /// <summary>
    /// Fades a floating surface (menu, panel, card) in or out with the small rise and settle of a Windows 11 flyout.
    /// Returns the batch that completes when the motion ends; a later call on the same element takes over from the current values,
    /// so opening and closing can interrupt each other at any moment. Call <see cref="SurfaceAt"/> once it completes.
    /// </summary>
    public static CompositionScopedBatch Surface(FrameworkElement element, bool opening, bool fresh, float depth)
    {
        Compositor compositor = Compositor();
        Easings(compositor);
        if (!UsesFacades(element)) return SurfaceOnVisual(element, opening, fresh);
        double width = element.ActualWidth > 0 ? element.ActualWidth : double.IsNaN(element.Width) ? 0 : element.Width;
        element.CenterPoint = new Vector3((float)width / 2, 0, 0);
        if (opening && fresh)
        {
            StopAll(element);
            element.Opacity = 0;
            element.Scale = new Vector3(.97f, .97f, 1);
            element.Translation = new Vector3(0, -6, depth);
        }
        else StopAll(element);
        CompositionEasingFunction easing = opening ? _decelerate! : _accelerate!;
        TimeSpan duration = TimeSpan.FromMilliseconds(opening ? 220 : 130);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opening ? 1 : 0, easing);
        fade.Duration = duration;
        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, opening ? Vector3.One : new Vector3(.98f, .98f, 1), easing);
        scale.Duration = duration;
        Vector3KeyFrameAnimation move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1, new Vector3(0, opening ? 0 : -3, depth), easing);
        move.Duration = duration;
        Start(element, fade, "Opacity");
        Start(element, scale, "Scale");
        Start(element, move, "Translation");
        batch.End();
        return batch;
    }

    /// <summary>Puts a surface in its final state at once (animations off, a surface hidden without its motion, or a motion that ended).</summary>
    public static void SurfaceAt(UIElement element, bool shown, float depth)
    {
        if (!UsesFacades(element))
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            StopVisual(visual);
            visual.Opacity = shown ? 1 : 0;
            visual.Scale = Vector3.One;
            element.Opacity = shown ? 1 : 0;
            return;
        }
        StopAll(element);
        element.Opacity = shown ? 1 : 0;
        element.Scale = Vector3.One;
        element.Translation = new Vector3(0, 0, depth);
    }

    /// <summary>An element rises into place while it fades in, after <paramref name="delayMilliseconds"/> (staggered entrances).</summary>
    public static void Rise(FrameworkElement element, double delayMilliseconds, float from = 14, double milliseconds = 360)
    {
        Compositor compositor = Compositor();
        Easings(compositor);
        if (!UsesFacades(element)) { SurfaceAt(element, true, 0); return; }
        StopAll(element);
        element.Opacity = 0;
        element.Translation = new Vector3(0, from, 0);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, _decelerate);
        Vector3KeyFrameAnimation move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(0, from, 0));
        move.InsertKeyFrame(1, Vector3.Zero, _decelerate);
        foreach (KeyFrameAnimation animation in new KeyFrameAnimation[] { fade, move })
        {
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            animation.DelayTime = TimeSpan.FromMilliseconds(delayMilliseconds);
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Start(element, fade, "Opacity");
        Start(element, move, "Translation");
        batch.End();
        // Leave the element's own properties at their resting values, so later layout, hit testing and fades start from there.
        batch.Completed += (_, _) =>
        {
            if (!Running.TryGetValue(element, out var list) || !list.Contains(fade)) return;
            // Scale is left alone: tiles also spring their scale on hover through the composition visual.
            list.Remove(fade); list.Remove(move);
            element.StopAnimation(fade); element.StopAnimation(move);
            element.Opacity = 1;
            element.Translation = Vector3.Zero;
        };
    }

    /// <summary>Fades an element's opacity (used for small state changes such as a button appearing on hover).</summary>
    public static void Fade(UIElement element, float to, double milliseconds = 120)
    {
        Compositor compositor = Compositor();
        Easings(compositor);
        if (!UsesFacades(element)) { element.Opacity = to; return; }
        StopAll(element);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to, _decelerate);
        fade.Duration = TimeSpan.FromMilliseconds(milliseconds);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Start(element, fade, "Opacity");
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!Running.TryGetValue(element, out var list) || !list.Contains(fade)) return;
            list.Remove(fade);
            element.StopAnimation(fade);
            element.Opacity = to;
        };
    }

    /// <summary>
    /// Theme resources for the vertical tab list: the selected row is the same veil as the selected horizontal tab, a hovered one the
    /// fainter pill, and there is no accent bar. Being theme dictionaries, they switch with the theme instead of keeping the colours
    /// of the theme the list was first shown in.
    /// </summary>
    public static ResourceDictionary TabListItems()
    {
        static ResourceDictionary Theme(byte selected, byte hover)
        {
            var veil = new SolidColorBrush(Windows.UI.Color.FromArgb(selected, 255, 255, 255));
            var faint = new SolidColorBrush(Windows.UI.Color.FromArgb(hover, 255, 255, 255));
            return new ResourceDictionary
            {
                ["ListViewItemBackgroundSelected"] = veil,
                ["ListViewItemBackgroundSelectedPointerOver"] = veil,
                ["ListViewItemBackgroundSelectedPressed"] = veil,
                ["ListViewItemBackgroundPointerOver"] = faint,
                ["ListViewItemBackgroundPressed"] = faint,
                ["ListViewItemSelectionIndicatorBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
        }
        var resources = new ResourceDictionary();
        resources.ThemeDictionaries["Default"] = Theme(50, 28);
        resources.ThemeDictionaries["Light"] = Theme(200, 110);
        resources.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
        return resources;
    }

    // ---------- Buttons without a plate ----------

    /// <summary>
    /// Theme resources that turn every <see cref="Button"/> below the element they are merged into a Windows 11 "subtle" button:
    /// no plate at rest or when disabled, a faint one under the pointer, a fainter one while pressed. The browser chrome uses one
    /// instance per bar, so all its buttons share exactly the same states.
    /// </summary>
    public static ResourceDictionary SubtleButtons()
    {
        static SolidColorBrush B(byte a, byte v) => new(Windows.UI.Color.FromArgb(a, v, v, v));
        ResourceDictionary Theme(byte v, byte hover, byte press, byte disabledText)
        {
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var theme = new ResourceDictionary
            {
                ["ButtonBackground"] = clear,
                ["ButtonBackgroundPointerOver"] = B(hover, v),
                ["ButtonBackgroundPressed"] = B(press, v),
                ["ButtonBackgroundDisabled"] = clear,
                ["ButtonBorderBrush"] = clear,
                ["ButtonBorderBrushPointerOver"] = clear,
                ["ButtonBorderBrushPressed"] = clear,
                ["ButtonBorderBrushDisabled"] = clear,
                ["ButtonForegroundDisabled"] = B(disabledText, v)
            };
            return theme;
        }
        var resources = new ResourceDictionary();
        // "Default" is what a dark theme resolves to; high contrast keeps the system's own button colours.
        resources.ThemeDictionaries["Default"] = Theme(255, 0x1C, 0x10, 0x5D);
        resources.ThemeDictionaries["Light"] = Theme(0, 0x12, 0x0A, 0x5C);
        resources.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
        return resources;
    }
}
