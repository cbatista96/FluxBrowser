using CefSharp;
using CefSharp.Handler;

namespace FluxBrowser.Browser;

internal sealed class PrototypeLifeSpanHandler(Action<string, bool> open, Action<string, string> report) : LifeSpanHandler
{
    protected override bool OnBeforePopup(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string targetUrl,
        string targetFrameName,
        WindowOpenDisposition targetDisposition,
        bool userGesture,
        IPopupFeatures popupFeatures,
        IWindowInfo windowInfo,
        IBrowserSettings browserSettings,
        ref bool noJavascriptAccess,
        out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        if (userGesture && WebAddress.IsHttp(targetUrl))
            open(targetUrl, targetDisposition != WindowOpenDisposition.NewBackgroundTab);
        else
            report("Ventana emergente bloqueada", "Solo se abren enlaces HTTP/HTTPS solicitados por un gesto del usuario.");
        return true;
    }

    protected override bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser) => false;
}

internal sealed class PrototypePermissionHandler(Action<string, string> report) : PermissionHandler
{
    protected override bool OnRequestMediaAccessPermission(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string requestingOrigin,
        MediaAccessPermissionType requestedPermissions,
        IMediaAccessCallback callback)
    {
        using (callback)
        {
            callback.Cancel();
        }

        report("Permiso bloqueado", "La cámara y el micrófono no están disponibles en este prototipo.");
        return true;
    }

    protected override bool OnShowPermissionPrompt(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        ulong promptId,
        string requestingOrigin,
        PermissionRequestType requestedPermissions,
        IPermissionPromptCallback callback)
    {
        using (callback)
        {
            callback.Continue(PermissionRequestResult.Deny);
        }

        report("Permiso bloqueado", "Los permisos de sitios web todavía no están disponibles en este prototipo.");
        return true;
    }
}
