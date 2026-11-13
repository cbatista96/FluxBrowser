using Microsoft.Web.WebView2.Core;
using FluxBrowser.Diagnostics;

namespace FluxBrowser.Browser;

/// <summary>
/// Chooses the web engine once per process. WebView2 (Chromium inside its sandbox, serviced by the Edge runtime) is the default;
/// CEF is the fallback for computers without the WebView2 runtime. <c>FLUXBROWSER_ENGINE=cef</c> or <c>=webview2</c> forces one.
/// </summary>
internal static class WebEngine
{
    public static bool UseWebView2 { get; } = Choose();

    private static bool Choose()
    {
        if (Program.HasPackageIdentity) return Report(true, "paquete MSIX");
        string? requested = Environment.GetEnvironmentVariable("FLUXBROWSER_ENGINE")
            ?? Environment.GetEnvironmentVariable("MYOWNBROWSER_ENGINE");
        if (string.Equals(requested, "cef", StringComparison.OrdinalIgnoreCase)) return Report(false, "forzado por FLUXBROWSER_ENGINE");
        if (string.Equals(requested, "webview2", StringComparison.OrdinalIgnoreCase)) return Report(true, "forzado por FLUXBROWSER_ENGINE");
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrEmpty(version) ? Report(false, "no hay Runtime de WebView2") : Report(true, "Runtime de WebView2 " + version);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or FileNotFoundException)
        {
            return Report(false, "no se pudo consultar el Runtime de WebView2: " + e.Message);
        }
    }

    private static bool Report(bool webView2, string reason)
    {
        AppLog.Info($"Motor web: {(webView2 ? "WebView2 (con sandbox)" : "CEF (sin sandbox)")} — {reason}");
        return webView2;
    }
}
