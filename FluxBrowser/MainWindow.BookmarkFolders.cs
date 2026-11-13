using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FluxBrowser.Browser;
using Point = Windows.Foundation.Point;

namespace FluxBrowser;

public sealed partial class MainWindow
{
    private void ShowBookmarkFolder(string path, Point position)
    {
        var actions = new List<MenuAction?>();
        int separator = path.LastIndexOf('/');
        if (separator >= 0)
        {
            actions.Add(new("Volver", "\uE72B", () => ShowBookmarkFolder(path[..separator], position)));
            actions.Add(null);
        }
        foreach (Bookmark bookmark in _bookmarks.Where(b => b.Folder == path))
        {
            actions.Add(new(bookmark.Title, bookmark.IsFolder ? "\uE8D5" : "\uE734",
                () =>
                {
                    if (bookmark.IsFolder) ShowBookmarkFolder(bookmark.FolderPath, position);
                    else NavigateCurrent(bookmark.Address);
                }, bookmark.IsFolder ? "\u203A" : "", Favicon: bookmark.IsFolder ? null : FaviconLoader.Address(bookmark.Favicon, bookmark.Address) ?? "",
                Context: p => ShowBookmarkMenu(bookmark, p)));
        }
        if (actions.Count == 0) actions.Add(new("La carpeta está vacía", "\uE8B7", null));
        actions.Add(null);
        actions.Add(new("Abrir todos en nuevas pestañas", "\uE8A5", () => OpenBookmarkFolder(path),
            Enabled: _bookmarks.Any(b => !b.IsFolder && (b.Folder == path || b.Folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))));
        actions.Add(new("Nueva subcarpeta", "\uE8F4", () => EditFolder(null, path)));
        ShowMenu(actions, position, width: 280);
    }

    private void OpenBookmarkFolder(string path)
    {
        foreach (Bookmark bookmark in _bookmarks.Where(b => !b.IsFolder &&
            (b.Folder == path || b.Folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))).ToList())
            if (TryAddTab(bookmark.Address, activate: false, isPrivate: _activeTab?.IsPrivate) is null) break;
    }

    private async void EditFolder(Bookmark? folder, string parent = "")
    {
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme,
            Title = folder is null ? "Nueva carpeta" : "Cambiar nombre de carpeta" };
        var panel = new StackPanel { Spacing = 12 };
        var name = new TextBox { Header = IconLabel("\uE8B7", "Nombre"), Text = folder?.Title ?? "" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(name);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ActionButton("\uE74E", "Guardar", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Contains('/'))
            {
                error.Text = "Introduce un nombre sin barras (/).";
                return;
            }
            try
            {
                List<Bookmark> updated = _bookmarkStore.Load();
                var replacement = new Bookmark(name.Text.Trim(), "", folder?.Folder ?? parent, true);
                if (folder is null) updated.Add(replacement);
                else
                {
                    updated = updated.Select(b => b == folder ? replacement :
                        b.Folder == folder.FolderPath ? b with { Folder = replacement.FolderPath } :
                        b.Folder.StartsWith(folder.FolderPath + "/", StringComparison.OrdinalIgnoreCase)
                            ? b with { Folder = replacement.FolderPath + b.Folder[folder.FolderPath.Length..] } : b).ToList();
                }
                _bookmarkStore.Save(updated);
                BrowserApp.RefreshBookmarks();
                _refreshManagement?.Invoke();
                dialog.Hide();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { error.Text = e.Message; }
        }));
        buttons.Children.Add(ActionButton("\uE711", "Cancelar", dialog.Hide));
        panel.Children.Add(buttons);
        dialog.Content = panel;
        await dialog.ShowAsync();
    }
}
