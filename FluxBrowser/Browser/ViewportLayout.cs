namespace FluxBrowser.Browser;

internal static class ViewportLayout
{
    public static double TopInset(double toolbar, double surfaceHeight, double scrollY, double documentHeight)
    {
        if (!double.IsFinite(toolbar) || !double.IsFinite(surfaceHeight) ||
            !double.IsFinite(scrollY) || !double.IsFinite(documentHeight))
            throw new ArgumentOutOfRangeException(nameof(scrollY), "La geometría debe ser finita.");
        double initialHeight = Math.Max(1, surfaceHeight - toolbar);
        // A short document must retain enough scroll range after enlarging its viewport.
        double revealLimit = Math.Min(toolbar, Math.Max(0, documentHeight - initialHeight) / 2);
        return Math.Round(Math.Max(0, toolbar - Math.Min(Math.Max(0, scrollY), revealLimit)));
    }

    // Long documents keep a full-height Chromium viewport; the initial top gap is a GPU translation that the scroll consumes.
    public static bool UsesFullViewport(double toolbar, double surfaceHeight, double documentHeight, bool current) =>
        !double.IsFinite(documentHeight) || documentHeight <= 0 ? current : documentHeight >= surfaceHeight + toolbar;

    // WebView2: the page keeps the toolbar's height free itself, so only the content's natural height decides. A page that merely fills its
    // viewport (apps, short documents) sits below the toolbar; a taller one fills the window and scrolls under the bars. The two thresholds
    // differ so a page near the boundary does not flip back and forth when the viewport changes size.
    public static bool UsesFullViewportWithGap(double toolbar, double surfaceHeight, double naturalHeight, bool current) =>
        !double.IsFinite(naturalHeight) || naturalHeight <= 0 ? current
            : current ? naturalHeight > surfaceHeight + 1 : naturalHeight >= surfaceHeight + toolbar;

    public static double RevealInset(double toolbar, double scrollY) =>
        Math.Round(Math.Max(0, toolbar - Math.Clamp(double.IsFinite(scrollY) ? scrollY : 0, 0, Math.Max(0, toolbar))));

    public static bool IsInternal(string? address) => address is "browser:home" or "browser:settings";
}
