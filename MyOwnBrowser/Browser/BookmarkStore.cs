using System.Text.Json;

namespace MyOwnBrowser.Browser;

internal sealed record Bookmark(string Title, string Address, string Folder = "", bool IsFolder = false, string? Favicon = null)
{
    public string FolderPath => string.IsNullOrEmpty(Folder) ? Title : Folder + "/" + Title;
}

internal sealed class BookmarkStore
{
    private readonly string _path;

    public BookmarkStore(string? path = null)
    {
        _path = path ?? Path.Combine(ProfileStorage.DirectoryPath, "bookmarks.json");
    }

    public List<Bookmark> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        string json = File.ReadAllText(_path);
        List<Bookmark> bookmarks = JsonSerializer.Deserialize<List<Bookmark>>(json)
            ?? throw new InvalidDataException("El archivo de marcadores está vacío o no es válido.");
        Validate(bookmarks);
        return bookmarks;
    }

    private static void Validate(IReadOnlyCollection<Bookmark> bookmarks)
    {
        if (bookmarks.Any(bookmark => bookmark is null || string.IsNullOrWhiteSpace(bookmark.Title) || bookmark.Folder is null ||
            (bookmark.IsFolder ? bookmark.Address != "" || bookmark.Title.Contains('/') :
                !WebAddress.IsHttp(bookmark.Address))))
        {
            throw new InvalidDataException("El archivo contiene un marcador no válido.");
        }
        var folders = bookmarks.Where(b => b.IsFolder).Select(b => b.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (folders.Count != bookmarks.Count(b => b.IsFolder) ||
            bookmarks.Any(b => b.Folder.Length > 0 && !folders.Contains(b.Folder)))
            throw new InvalidDataException("Una carpeta de marcadores está duplicada o no existe.");
    }

    public void Save(IReadOnlyCollection<Bookmark> bookmarks)
    {
        Validate(bookmarks);
        ProfileStorage.WriteAtomic(_path, JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
