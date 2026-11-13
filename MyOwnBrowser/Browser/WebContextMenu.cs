using CefSharp;
using CefSharp.Handler;

namespace MyOwnBrowser.Browser;

internal sealed record WebMenuItem(CefMenuCommand Command, string Label, string Glyph, bool Enabled,
    MenuItemType Type, IReadOnlyList<WebMenuItem> Children);
internal sealed record WebMenuRequest(int X, int Y, string Link, string Image, string Selection,
    IReadOnlyList<WebMenuItem> Items, IRunContextMenuCallback Callback);

internal sealed class WebContextMenu(Action<WebMenuRequest> show) : ContextMenuHandler
{
    protected override bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, IMenuModel model, IRunContextMenuCallback callback)
    {
        show(new WebMenuRequest(parameters.XCoord, parameters.YCoord, parameters.LinkUrl,
            parameters.HasImageContents ? parameters.SourceUrl : "", parameters.SelectionText, Snapshot(model), callback));
        return true;
    }

    private static IReadOnlyList<WebMenuItem> Snapshot(IMenuModel model)
    {
        var items = new List<WebMenuItem>();
        for (int i = 0; i < model.Count; i++)
        {
            if (!model.IsVisibleAt(i)) continue;
            MenuItemType type = model.GetTypeAt(i);
            if (type == MenuItemType.Separator)
            {
                items.Add(new(CefMenuCommand.NotFound, "", "", false, type, []));
                continue;
            }
            CefMenuCommand command = model.GetCommandIdAt(i);
            string label = command switch
            {
                CefMenuCommand.Back => "Atrás", CefMenuCommand.Forward => "Adelante",
                CefMenuCommand.Reload => "Actualizar", CefMenuCommand.ReloadNoCache => "Actualizar sin caché",
                CefMenuCommand.StopLoad => "Detener", CefMenuCommand.Undo => "Deshacer",
                CefMenuCommand.Redo => "Rehacer", CefMenuCommand.Cut => "Cortar",
                CefMenuCommand.Copy => "Copiar", CefMenuCommand.Paste => "Pegar",
                CefMenuCommand.Delete => "Eliminar", CefMenuCommand.SelectAll => "Seleccionar todo",
                CefMenuCommand.Find => "Buscar en la página", CefMenuCommand.Print => "Imprimir",
                CefMenuCommand.ViewSource => "Ver código fuente de página",
                _ => model.GetLabelAt(i).Replace("&", "")
            };
            if (command == CefMenuCommand.NotFound || string.IsNullOrWhiteSpace(label)) continue;
            string glyph = type switch
            {
                MenuItemType.Check => model.IsCheckedAt(i) ? "\uE73A" : "\uE739",
                MenuItemType.Radio => model.IsCheckedAt(i) ? "\uECCB" : "\uECCA",
                _ => IconFor(command)
            };
            IReadOnlyList<WebMenuItem> children = type == MenuItemType.SubMenu ? Snapshot(model.GetSubMenuAt(i)) : [];
            items.Add(new WebMenuItem(command, label, glyph, model.IsEnabledAt(i), type, children));
        }
        return items;
    }

    private static string IconFor(CefMenuCommand command) => command switch
    {
        CefMenuCommand.Back => "\uE72B", CefMenuCommand.Forward => "\uE72A",
        CefMenuCommand.Reload or CefMenuCommand.ReloadNoCache => "\uE72C",
        CefMenuCommand.Cut => "\uE8C6", CefMenuCommand.Copy => "\uE8C8", CefMenuCommand.Paste => "\uE77F",
        CefMenuCommand.Delete => "\uE74D", CefMenuCommand.SelectAll => "\uE8B3",
        CefMenuCommand.Undo => "\uE7A7", CefMenuCommand.Redo => "\uE7A6",
        CefMenuCommand.Print => "\uE749", CefMenuCommand.Find => "\uE721",
        CefMenuCommand.ViewSource => "\uE943", _ => "\uE73E"
    };
}
