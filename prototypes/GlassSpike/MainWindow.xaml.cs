using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace GlassSpike;

/// <summary>
/// Runs a fixed list of scenarios against a WebView2 whose glass is drawn by the page, then writes results.json and PNGs to --out and exits.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly string _out;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _waiters = [];
    private readonly Dictionary<string, object?> _results = [];
    private int _messageId;

    public MainWindow()
    {
        InitializeComponent();
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--out");
        _out = at >= 0 && at + 1 < args.Length ? args[at + 1] : Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(_out);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        Root.Loaded += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception e) { _results["fatal"] = e.ToString(); }
            File.WriteAllText(Path.Combine(_out, "results.json"), JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        };
    }

    private static string GlassScript()
    {
        using Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream("GlassSpike.glass.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnMessage(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.Clone();
        string type = root.GetProperty("type").GetString() ?? "";
        string key = type == "ack" ? "ack:" + root.GetProperty("id").GetInt32() : type;
        if (_waiters.Remove(key, out var waiter)) waiter.TrySetResult(root);
    }

    private async Task RunAsync()
    {
        await Web.EnsureCoreWebView2Async();
        _results["xaml_control_members"] = typeof(Microsoft.UI.Xaml.Controls.WebView2).GetMembers().Select(m => m.Name).Distinct().Where(n => !n.StartsWith("get_") && !n.StartsWith("set_") && !n.StartsWith("add_") && !n.StartsWith("remove_")).OrderBy(n => n).ToArray();
        _results["core_webview2_zoom_or_controller_members"] = typeof(Microsoft.Web.WebView2.Core.CoreWebView2).GetMembers().Select(m => m.Name).Distinct().Where(n => n.Contains("Zoom") || n.Contains("Controller") || n.Contains("Composition")).ToArray();
        await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GlassScript());
        Web.CoreWebView2.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);

        // 0. Can the control be shifted down without resizing it (so the page can start below the bars and be revealed by scrolling)?
        try
        {
            await Navigate(LongPage);
            await Task.Delay(500);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(Web, true);
            Web.Translation = new System.Numerics.Vector3(0, 108, 0);
            await Task.Delay(600);
            Shot("0-translation.png");
            string size = await Web.CoreWebView2.ExecuteScriptAsync("innerWidth + 'x' + innerHeight");
            _results["translation_probe"] = new { pageViewport = size, webActualHeight = Web.ActualHeight };
            Web.Translation = System.Numerics.Vector3.Zero;
        }
        catch (Exception e) { _results["translation_probe"] = e.GetType().Name + ": " + e.Message; }

        // 1. Scroll performance on a long striped page: glass off, toolbar only, toolbar + menu.
        await Navigate(LongPage);
        await Task.Delay(700);
        foreach (string mode in new[] { "off", "bar", "bar+menu" })
        {
            Menu.Visibility = mode == "bar+menu" ? Visibility.Visible : Visibility.Collapsed;
            await Task.Delay(100);
            await SendRegions(mode);
            _results["perf_" + mode] = await Perf();
            if (mode == "bar+menu") Shot("1-scroll-bar-menu.png");
        }

        // 2. The same at full screen (a 4K-class surface).
        ((OverlappedPresenter)AppWindow.Presenter).Maximize();
        await Task.Delay(1500);
        _results["maximized_size_dip"] = $"{Root.ActualWidth:F0}x{Root.ActualHeight:F0}";
        await SendRegions("bar+menu");
        _results["perf_maximized_bar+menu"] = await Perf();
        Shot("2-maximized.png");
        ((OverlappedPresenter)AppWindow.Presenter).Restore();
        await Task.Delay(1000);

        // 3. Real page zoom through a controller we host ourselves (the XAML control does not expose ZoomFactor).
        await ZoomCheck();
        await CompositionHostingCheck();

        // 4. Latency of a region update (what a menu animation would feel).
        await SendRegions("bar+menu");
        var rtt = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            var sw = Stopwatch.StartNew();
            await SendRegions("bar+menu", wait: 2000);
            rtt.Add(sw.Elapsed.TotalMilliseconds);
            await Task.Delay(8);
        }
        rtt.Sort();
        _results["region_roundtrip_ms"] = new { avg = rtt.Average(), p50 = rtt[rtt.Count / 2], p95 = rtt[(int)(rtt.Count * .95)], max = rtt[^1] };

        // 5. Pages where the script cannot run, and a modal dialog in the top layer.
        string pdf = Path.Combine(_out, "test.pdf");
        string content = "BT /F1 40 Tf 72 700 Td (A PDF page under the glass) Tj ET";
        File.WriteAllText(pdf, "%PDF-1.1\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
            $"4 0 obj<</Length {content.Length}>>stream\n{content}\nendstream endobj\n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");
        await Probe("pdf", () => Web.CoreWebView2.Navigate(new Uri(pdf).AbsoluteUri));
        await Probe("error_page", () => Web.CoreWebView2.Navigate("https://glass-spike-no-such-host.invalid/"));
        await Probe("view_source", () => Web.CoreWebView2.Navigate("view-source:https://example.com/"));
        await Probe("ordinary_site", () => Web.CoreWebView2.Navigate("https://example.com/"));
        await Probe("modal_dialog", () => Web.CoreWebView2.NavigateToString(DialogPage));
        Fallback.Visibility = Visibility.Collapsed;
    }

    /// <summary>Navigates, tries to reach the injected script, and shows the XAML fallback when it does not answer.</summary>
    private async Task Probe(string name, Action navigate)
    {
        bool navigated = true;
        try { await Navigate(navigate, 15000); } catch (TimeoutException) { navigated = false; }
        await Task.Delay(1000);
        bool glass = await TryRegions("bar+menu", 1500);
        Fallback.Visibility = glass ? Visibility.Collapsed : Visibility.Visible;
        await Task.Delay(300);
        Shot($"5-{name}.png");
        _results["dom_" + name] = new { navigated, pageDrawsGlass = glass, fallbackShown = !glass };
    }

    private Task Navigate(string html) => Navigate(() => Web.CoreWebView2.NavigateToString(html), 15000);

    private async Task Navigate(Action start, int timeoutMs)
    {
        var done = new TaskCompletionSource();
        void Handler(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        Web.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            start();
            if (await Task.WhenAny(done.Task, Task.Delay(timeoutMs)) != done.Task) throw new TimeoutException();
        }
        finally { Web.CoreWebView2.NavigationCompleted -= Handler; }
    }

    /// <summary>The XAML control hides ZoomFactor but exposes RasterizationScale; scale * zoom gives real zoom (DPR and CSS viewport both change).</summary>
    private async Task RasterizationZoomCheck()
    {
        double scale = Root.XamlRoot.RasterizationScale;
        var rows = new List<object>();
        try
        {
            foreach (double zoom in new[] { 1.0, 1.5, 0.75 })
            {
                Web.RasterizationScale = scale * zoom;
                await Task.Delay(900);
                string dpr = await Web.CoreWebView2.ExecuteScriptAsync("window.devicePixelRatio + ' ' + innerWidth + 'x' + innerHeight");
                await SendRegions("bar+menu");
                var measured = new TaskCompletionSource<JsonElement>();
                _waiters["measure"] = measured;
                Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "measure", id = 0 }));
                double worst = -1;
                if (await Task.WhenAny(measured.Task, Task.Delay(3000)) == measured.Task)
                {
                    var expected = new[] { (0.0, 0.0, Root.ActualWidth * scale, 108.0 * scale), (Root.ActualWidth * scale - 316 * scale, 116.0 * scale, 300.0 * scale, 220.0 * scale) };
                    int i = 0; worst = 0;
                    foreach (JsonElement r in measured.Task.Result.GetProperty("rects").EnumerateArray())
                    {
                        var (ex, ey, ew, eh) = expected[i++];
                        worst = Math.Max(worst, new[] { Math.Abs(r.GetProperty("x").GetDouble() - ex), Math.Abs(r.GetProperty("y").GetDouble() - ey),
                            Math.Abs(r.GetProperty("w").GetDouble() - ew), Math.Abs(r.GetProperty("h").GetDouble() - eh) }.Max());
                    }
                }
                rows.Add(new { zoom, rasterizationScaleSet = scale * zoom, pageReports = dpr, worstErrorPhysicalPx = Math.Round(worst, 2) });
                if (zoom == 1.5) Shot("3-rasterization-zoom-1.50.png");
            }
        }
        catch (Exception e) { rows.Add(new { error = e.GetType().Name + ": " + e.Message }); }
        finally { try { Web.RasterizationScale = scale; } catch { } }
        _results["rasterization_zoom_check"] = rows;
    }

    /// <summary>Can WebView2 be hosted in composition inside a WinUI 3 visual tree (so XAML overlays work AND the controller's ZoomFactor is available)?</summary>
    private async Task CompositionHostingCheck()
    {
        var report = new Dictionary<string, object?>();
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var reference = Microsoft.Web.WebView2.Core.CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd);
            var comp = await Web.CoreWebView2.Environment.CreateCoreWebView2CompositionControllerAsync(reference);
            report["created"] = true;
            report["compositionControllerType"] = comp.GetType().FullName;
            report["members"] = comp.GetType().GetMembers().Select(m => m.Name).Distinct()
                .Where(n => !n.StartsWith("get_") && !n.StartsWith("set_") && !n.StartsWith("add_") && !n.StartsWith("remove_")).OrderBy(n => n).ToArray();
            var host = new Microsoft.UI.Xaml.Controls.Border { Width = 600, Height = 400, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            Root.Children.Add(host);
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host);
            var container = visual.Compositor.CreateContainerVisual();
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host, container);
            try { comp.RootVisualTarget = container; report["rootVisualTargetAccepted"] = true; }
            catch (Exception e) { report["rootVisualTargetAccepted"] = false; report["rootVisualTargetError"] = e.GetType().Name + ": " + e.Message; }
        }
        catch (Exception e) { report["error"] = e.GetType().Name + ": " + e.Message; }
        _results["composition_hosting"] = report;
    }

    private async Task ZoomCheck()
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var reference = Microsoft.Web.WebView2.Core.CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd);
        var controller = await Web.CoreWebView2.Environment.CreateCoreWebView2ControllerAsync(reference);
        var rows = new List<object>();
        try
        {
            double scale = Root.XamlRoot.RasterizationScale;
            controller.Bounds = new Windows.Foundation.Rect(0, 150 * scale, 760 * scale, 560 * scale);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(GlassScript());
            var done = new TaskCompletionSource();
            core.NavigationCompleted += (_, _) => done.TrySetResult();
            core.NavigateToString(LongPage);
            await Task.WhenAny(done.Task, Task.Delay(10000));
            await Task.Delay(600);
            foreach (double zoom in new[] { 1.0, 1.5, 0.75, 2.0 })
            {
                controller.ZoomFactor = zoom;
                await Task.Delay(700);
                int id = ++_messageId;
                var ack = new TaskCompletionSource<JsonElement>();
                _waiters["ack:" + id] = ack;
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "regions", id, scale = (float)scale, blur = 12, regions = RegionList("bar+menu") }));
                await Task.WhenAny(ack.Task, Task.Delay(3000));
                var measured = new TaskCompletionSource<JsonElement>();
                _waiters["measure"] = measured;
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "measure", id = 0 }));
                if (await Task.WhenAny(measured.Task, Task.Delay(3000)) != measured.Task) { rows.Add(new { zoom, error = "no measure reply" }); continue; }
                JsonElement m = measured.Task.Result;
                double dpr = m.GetProperty("dpr").GetDouble();
                // Regions were sent in DIP; the glass must land on exactly dip * scale physical pixels whatever the zoom.
                var expected = new[] { (0.0, 0.0, Root.ActualWidth * scale, 108.0 * scale), (Root.ActualWidth * scale - 316 * scale, 116.0 * scale, 300.0 * scale, 220.0 * scale) };
                double worst = 0; int i = 0;
                foreach (JsonElement r in m.GetProperty("rects").EnumerateArray())
                {
                    var (ex, ey, ew, eh) = expected[i++];
                    worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(r.GetProperty("x").GetDouble() - ex), Math.Abs(r.GetProperty("y").GetDouble() - ey)),
                        Math.Max(Math.Abs(r.GetProperty("w").GetDouble() - ew), Math.Abs(r.GetProperty("h").GetDouble() - eh))));
                }
                rows.Add(new { zoom, devicePixelRatio = dpr, expectedDprScaleTimesZoom = scale * zoom, worstErrorPhysicalPx = Math.Round(worst, 2) });
                if (zoom == 1.5) Shot("3-real-zoom-1.50.png");
            }
        }
        finally { controller.Close(); }
        _results["zoom_check"] = rows;
    }

    private object RegionList(string mode)
    {
        double w = Root.ActualWidth;
        var list = new List<object>();
        if (mode != "off")
            list.Add(new { x = 0, y = 0, w, h = 108.0, r = 0, tint = "rgba(255,255,255,0.18)", v = 1 });
        if (mode == "bar+menu")
            list.Add(new { x = w - 316, y = 116.0, w = 300, h = 220, r = 10, tint = "rgba(255,255,255,0.18)", v = 1 });
        return list;
    }

    private async Task<bool> TryRegions(string mode, int wait)
    {
        try { await SendRegions(mode, wait); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task SendRegions(string mode, int wait = 3000)
    {
        int id = ++_messageId;
        var tcs = new TaskCompletionSource<JsonElement>();
        _waiters["ack:" + id] = tcs;
        float scale = (float)Root.XamlRoot.RasterizationScale;
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "regions", id, scale, blur = 12, regions = RegionList(mode) }));
        if (await Task.WhenAny(tcs.Task, Task.Delay(wait)) != tcs.Task) { _waiters.Remove("ack:" + id); throw new TimeoutException(); }
    }

    private async Task<object> Perf()
    {
        var tcs = new TaskCompletionSource<JsonElement>();
        _waiters["perf"] = tcs;
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "scrollTest", seconds = 3, speed = 1500 }));
        if (await Task.WhenAny(tcs.Task, Task.Delay(8000)) != tcs.Task) return "timeout";
        return JsonSerializer.Deserialize<object>(tcs.Task.Result.GetRawText())!;
    }

    private void Shot(string name)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        GetWindowRect(hwnd, out RECT r);
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            IntPtr dc = g.GetHdc();
            PrintWindow(hwnd, dc, 2);
            g.ReleaseHdc(dc);
        }
        bitmap.Save(Path.Combine(_out, name));
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private const string LongPage = """
        <!doctype html><meta charset="utf-8"><style>
        html,body{margin:0}
        body{height:20000px;font:700 54px Segoe UI;
          background:repeating-linear-gradient(90deg,#e11d48 0 80px,#2563eb 80px 160px,#16a34a 160px 240px,#f59e0b 240px 320px)}
        p{margin:0;padding:10px 24px;color:#fff;text-shadow:0 2px 0 #0008}
        </style>
        <script>document.addEventListener('DOMContentLoaded',()=>{for(let i=0;i<400;i++){const p=document.createElement('p');p.textContent='LINE '+i+' — scrolling under the glass';document.body.appendChild(p)}})</script>
        """;

    private const string DialogPage = """
        <!doctype html><meta charset="utf-8"><style>
        body{margin:0;height:900px;font:700 48px Segoe UI;background:repeating-linear-gradient(90deg,#e11d48 0 80px,#2563eb 80px 160px,#16a34a 160px 240px,#f59e0b 240px 320px)}
        dialog{width:520px;height:160px;margin:0;position:fixed;left:80px;top:40px;font:600 24px Segoe UI}
        </style><p>page</p><dialog id="d">Modal dialog in the top layer</dialog>
        <script>document.getElementById('d').showModal()</script>
        """;
}
