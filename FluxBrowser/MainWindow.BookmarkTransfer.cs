using System.Text;
using FluxBrowser.Browser;
using Windows.Storage.Pickers;

namespace FluxBrowser;

public sealed partial class MainWindow
{
    private async void ImportBookmarks()
    {
        try
        {
            if (!_bookmarksAvailable) throw new InvalidDataException("Repara el archivo de favoritos antes de importar.");
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".htm");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            if (new FileInfo(file.Path).Length > 10 * 1024 * 1024) throw new InvalidDataException("El archivo supera el límite de 10 MB.");
            BookmarkImport import = BookmarkHtml.Parse(await File.ReadAllTextAsync(file.Path));
            if (import.Items.Count == 0) throw new InvalidDataException($"No se encontraron favoritos compatibles. Se omitieron {import.Skipped} direcciones no HTTP/HTTPS.");
            List<Bookmark> merged = BookmarkHtml.Merge(_bookmarkStore.Load(), import.Items, $"Importados {DateTime.Now:yyyy-MM-dd}");
            _bookmarkStore.Save(merged);
            BrowserApp.RefreshBookmarks();
            ShowNotice("Favoritos importados", $"{import.Items.Count(b => !b.IsFolder)} favoritos y {import.Items.Count(b => b.IsFolder)} carpetas. Se omitieron {import.Skipped} direcciones no compatibles. Tus favoritos anteriores se conservaron.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException or System.Runtime.InteropServices.COMException)
        { ShowError("No se pudieron importar los favoritos", e.Message); }
    }

    private async void ExportBookmarks()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"Favoritos {DateTime.Now:yyyy-MM-dd}" };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
            picker.FileTypeChoices.Add("Favoritos HTML", new List<string> { ".html" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, BookmarkHtml.Export(_bookmarkStore.Load()), new UTF8Encoding(false));
            ShowNotice("Favoritos exportados", $"Se guardó un archivo HTML compatible con Edge, Chrome y Firefox en {file.Path}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        { ShowError("No se pudieron exportar los favoritos", e.Message); }
    }

    private async Task<string?> PickDownloadDirectory()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
