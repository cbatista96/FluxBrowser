using MyOwnBrowser.Browser;
using Point = Windows.Foundation.Point;

namespace MyOwnBrowser;

public sealed partial class MainWindow
{
    // Items of the engine's own menu that the window replaces with its own actions or does not offer (Edge-only services).
    private static readonly HashSet<string> ReplacedEngineItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "openLinkInNewWindow", "openLinkInNewTab", "openLinkInInPrivateWindow", "openLinkInNewForegroundTab", "openLinkInNewBackgroundTab",
        "copyLinkLocation", "saveLinkAs", "openImageInNewTab", "saveImageAs", "copyImageLocation", "openImage",
        "inspectElement", "inspect", "webCapture", "screenshot", "saveAs", "share", "collections", "addToCollections", "webSelect",
        "emoji", "readAloud", "translate", "searchWebFor", "sendToDevices", "createQrCode", "viewSource"
    };

    private static string GlyphFor(string name) => name.ToLowerInvariant() switch
    {
        "back" => "", "forward" => "", "reload" => "",
        "cut" => "", "copy" => "", "paste" or "pasteasplaintext" => "", "delete" => "",
        "selectall" => "", "undo" => "", "redo" => "", "print" => "",
        _ => ""
    };

    /// <summary>The window draws the engine's context menu (so it gets the glass material) and reports the choice back.</summary>
    private void ShowNativeMenu(BrowserTab tab, WebNativeMenu menu)
    {
        var actions = new List<MenuAction?>();
        void Add(string text, string icon, Action action, string keys = "") =>
            actions.Add(new(text, icon, () => { menu.Complete(null); action(); }, keys));
        if (WebAddress.IsHttp(menu.Link))
        {
            Add("Abrir vínculo en una pestaña nueva", "", () => TryAddTab(menu.Link, isPrivate: tab.IsPrivate));
            Add("Abrir vínculo en segundo plano", "", () => TryAddTab(menu.Link, false, tab.IsPrivate));
            Add("Copiar vínculo", "", () => CopyText(menu.Link));
            Add("Guardar vínculo como", "", () => tab.Session.Download(menu.Link));
            actions.Add(null);
        }
        if (WebAddress.IsHttp(menu.Image))
        {
            Add("Abrir imagen en una pestaña nueva", "", () => TryAddTab(menu.Image, isPrivate: tab.IsPrivate));
            Add("Guardar imagen como", "", () => tab.Session.Download(menu.Image));
            Add("Copiar vínculo de la imagen", "", () => CopyText(menu.Image));
            actions.Add(null);
        }

        int before = actions.Count;
        bool hasReload = false;
        void AddEngine(IEnumerable<NativeMenuItem> items, string indent = "")
        {
            foreach (NativeMenuItem item in items)
            {
                if (item.IsSeparator) { if (actions.Count > before && actions[^1] is not null) actions.Add(null); continue; }
                if (item.IsSubmenu) { AddEngine(item.Children, "   "); continue; }
                if (ReplacedEngineItems.Contains(item.Name) || string.IsNullOrWhiteSpace(item.Label)) continue;
                if (item.Name.Equals("reload", StringComparison.OrdinalIgnoreCase)) hasReload = true;
                int command = item.CommandId;
                actions.Add(new(indent + item.Label, GlyphFor(item.Name), () => menu.Complete(command), item.Shortcut, item.Enabled));
            }
        }
        AddEngine(menu.Items);
        while (actions.Count > 0 && actions[^1] is null) actions.RemoveAt(actions.Count - 1);
        if (actions.Count > 0) actions.Add(null);

        if (!hasReload) Add("Actualizar", "", () => _web?.Reload(), "Ctrl+R");
        if (!string.IsNullOrWhiteSpace(menu.Selection))
            Add($"Buscar «{Shorten(menu.Selection)}» en la web", "",
                () => TryAddTab(AddressResolver.Search(menu.Selection, Preferences.SearchEngine), isPrivate: tab.IsPrivate));
        actions.Add(null);
        Add("Guardar como", "", SavePage, "Ctrl+S");
        Add("Captura de pantalla", "", TakeScreenshot, "Ctrl+Mayús+S");
        Add("Buscar en la página", "", ShowFind, "Ctrl+F");
        Add("Ver código fuente de página", "", () => ShowPageSource(tab), "Ctrl+U");
        actions.Add(null);
        Add("Inspeccionar", "", () => tab.Session.DevTools(), "Ctrl+Mayús+I");
        // The page sits below the toolbar by the gap the scroll has not yet revealed.
        ShowMenu(actions, new Point(menu.X, menu.Y + tab.TopInset), () => menu.Complete(null));
    }
}
