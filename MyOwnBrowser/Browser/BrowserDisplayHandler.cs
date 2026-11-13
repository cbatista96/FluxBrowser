using CefSharp;
using CefSharp.Handler;

namespace MyOwnBrowser.Browser;

internal sealed class BrowserDisplayHandler(Action<string> faviconChanged) : DisplayHandler
{
    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
    {
        string? address = urls.FirstOrDefault(WebAddress.IsHttp);
        if (address is not null) faviconChanged(address);
    }
}
