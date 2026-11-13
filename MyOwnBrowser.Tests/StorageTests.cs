using System.Text.Json;
using MyOwnBrowser.Browser;
using Xunit;

namespace MyOwnBrowser.Tests;

/// <summary>Creates a throw-away folder that is removed when the test ends.</summary>
public abstract class TempFolderTests : IDisposable
{
    protected string Folder { get; } = Path.Combine(Path.GetTempPath(), "MyOwnBrowser.Tests-" + Guid.NewGuid().ToString("N"));

    protected TempFolderTests() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        ProfileStorage.Flush();
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
    }
}

public class BookmarkStoreTests : TempFolderTests
{
    private BookmarkStore Store => new(Path.Combine(Folder, "bookmarks.json"));
    private string FilePath => Path.Combine(Folder, "bookmarks.json");

    private static readonly List<Bookmark> Nested =
    [
        new("Trabajo", "", IsFolder: true), new("Código", "", "Trabajo", true),
        new("Ejemplo", "https://example.com/", "Trabajo/Código")
    ];

    [Fact]
    public void A_new_collection_is_empty() => Assert.Empty(Store.Load());

    [Fact]
    public void Bookmarks_round_trip()
    {
        var expected = new List<Bookmark> { new("Prueba", "https://example.com/") };
        Store.Save(expected);
        Assert.Equal(expected, Store.Load());
        Store.Save(Nested);
        Assert.Equal(Nested, Store.Load());
    }

    [Fact]
    public void Rejected_writes_keep_the_previous_data()
    {
        Store.Save(Nested);
        Assert.Throws<InvalidDataException>(() => Store.Save([new Bookmark("No válido", "javascript:alert(1)")]));
        Assert.Throws<InvalidDataException>(() => Store.Save([new Bookmark("Huérfano", "https://example.com/", "No existe")]));
        Assert.Throws<InvalidDataException>(() => Store.Save([new Bookmark("A", "", IsFolder: true), new Bookmark("a", "", IsFolder: true)]));
        Assert.Equal(Nested, Store.Load());
    }

    [Fact]
    public void Invalid_files_are_reported_and_never_overwritten()
    {
        File.WriteAllText(FilePath, "[null]");
        Assert.Throws<InvalidDataException>(() => Store.Load());
        Assert.Equal("[null]", File.ReadAllText(FilePath));
        File.WriteAllText(FilePath, "{");
        Assert.Throws<JsonException>(() => Store.Load());
    }

    [Fact]
    public void Html_export_and_import_keep_hierarchy_and_special_characters()
    {
        Assert.Equal(Nested, BookmarkHtml.Parse(BookmarkHtml.Export(Nested)).Items);
        var special = new List<Bookmark> { new("A & B <prueba> \"uno\"", "https://example.com/?a=1&b=2") };
        Assert.Equal(special, BookmarkHtml.Parse(BookmarkHtml.Export(special)).Items);
    }

    [Fact]
    public void Html_import_skips_unsafe_links_and_resolves_duplicate_folders()
    {
        BookmarkImport foreign = BookmarkHtml.Parse("""
            <!DOCTYPE NETSCAPE-Bookmark-file-1><dl><p>
            <dt><h3>Trabajo</h3><dl><p><dt><a href='https://example.com'>Ejemplo &amp; prueba</a>
            <dt><a href="javascript:alert(1)">No</a><dt><a href="https://user:pass@example.com">No</a>
            </dl><dt><h3>Trabajo</h3><dl></dl></dl>
            """);
        Assert.Equal(2, foreign.Skipped);
        Assert.Equal(3, foreign.Items.Count);
        Assert.Equal("Trabajo (2)", foreign.Items[2].Title);
        Store.Save(BookmarkHtml.Merge(Nested, foreign.Items, "Importados"));
        Assert.Equal(Nested.Count + foreign.Items.Count + 1, Store.Load().Count);
    }

    [Theory]
    [InlineData("<html>no es un archivo de favoritos</html>")]
    [InlineData("<DL><H3>Incompleto</H3><DL>")]
    public void Foreign_or_incomplete_html_is_rejected(string html) =>
        Assert.Throws<InvalidDataException>(() => BookmarkHtml.Parse(html));
}

public class ProfileStorageTests : TempFolderTests
{
    [Fact]
    public void Settings_and_session_round_trip()
    {
        var preferences = new BrowserPreferences { VerticalTabs = true, Theme = "Dark", BlurRadius = 8, SearchEngine = "Bing" };
        ProfileStorage.Write("settings.json", preferences, Folder);
        Assert.Equal(preferences, ProfileStorage.Read("settings.json", new BrowserPreferences(), Folder));

        var tabs = new List<SavedTab> { new("https://example.com/", true, "Ejemplo"), new("browser:home", false) };
        ProfileStorage.Write("session.json", tabs, Folder);
        Assert.Equal(tabs, ProfileStorage.Read("session.json", new List<SavedTab>(), Folder));
    }

    [Fact]
    public void Sessions_saved_before_titles_existed_still_load()
    {
        File.WriteAllText(Path.Combine(Folder, "session.json"), """[{"Address":"https://example.com/","Pinned":true}]""");
        var tabs = ProfileStorage.Read("session.json", new List<SavedTab>(), Folder);
        Assert.Equal(new SavedTab("https://example.com/", true), Assert.Single(tabs));
    }

    [Fact]
    public void An_invalid_settings_file_is_not_replaced()
    {
        string path = Path.Combine(Folder, "settings.json");
        File.WriteAllText(path, "{");
        Assert.Throws<JsonException>(() => ProfileStorage.Read("settings.json", new BrowserPreferences(), Folder));
        Assert.Equal("{", File.ReadAllText(path));
    }

    [Fact]
    public void Deferred_writes_keep_the_last_value()
    {
        ProfileStorage.WriteDeferred("a.json", new List<string> { "v1" }, Folder);
        ProfileStorage.WriteDeferred("a.json", new List<string> { "v2" }, Folder);
        Assert.False(File.Exists(Path.Combine(Folder, "a.json")));
        ProfileStorage.Flush();
        Assert.Equal(["v2"], ProfileStorage.Read("a.json", new List<string>(), Folder));
    }

    [Fact]
    public void An_immediate_write_cancels_a_stale_deferred_one()
    {
        ProfileStorage.WriteDeferred("a.json", new List<string> { "stale" }, Folder);
        ProfileStorage.Write("a.json", new List<string> { "fresh" }, Folder);
        ProfileStorage.Flush();
        Assert.Equal(["fresh"], ProfileStorage.Read("a.json", new List<string>(), Folder));
    }

    [Fact]
    public void The_timer_writes_deferred_data_without_a_flush()
    {
        ProfileStorage.WriteDeferred("b.json", new List<string> { "timer" }, Folder);
        for (int i = 0; i < 50 && !File.Exists(Path.Combine(Folder, "b.json")); i++) Thread.Sleep(100);
        Assert.True(File.Exists(Path.Combine(Folder, "b.json")));
    }

    [Fact]
    public void Oversized_profile_files_are_rejected()
    {
        File.WriteAllText(Path.Combine(Folder, "c.json"), new string(' ', 33 * 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => ProfileStorage.Read("c.json", new List<string>(), Folder));
    }

    [Fact]
    public void The_profile_folder_can_be_overridden_with_an_absolute_path_only()
    {
        string? previous = Environment.GetEnvironmentVariable(ProfileStorage.ProfileVariable);
        try
        {
            Environment.SetEnvironmentVariable(ProfileStorage.ProfileVariable, Folder);
            Assert.Equal(Folder, ProfileStorage.DirectoryPath);
            Environment.SetEnvironmentVariable(ProfileStorage.ProfileVariable, "relativa");
            Assert.NotEqual("relativa", ProfileStorage.DirectoryPath);
        }
        finally { Environment.SetEnvironmentVariable(ProfileStorage.ProfileVariable, previous); }
    }
}

public class DefaultBrowserTests
{
    [Fact]
    public void Registration_round_trips_in_an_isolated_registry_root()
    {
        string root = @"Software\MyOwnBrowser.Tests-" + Guid.NewGuid().ToString("N");
        string exe = Environment.ProcessPath!;
        try
        {
            Assert.False(DefaultBrowser.IsRegistered(exe, root));
            DefaultBrowser.Register(exe, root);
            Assert.True(DefaultBrowser.IsRegistered(exe, root));
            Assert.False(DefaultBrowser.IsRegistered(Path.Combine(Path.GetTempPath(), "otra.exe"), root));

            using var capabilities = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{root}\Clients\StartMenuInternet\MyOwnBrowser\Capabilities\URLAssociations");
            Assert.NotNull(capabilities);
            Assert.Equal("MyOwnBrowserURL", capabilities!.GetValue("http"));
            Assert.Equal("MyOwnBrowserURL", capabilities.GetValue("https"));

            DefaultBrowser.Unregister(root);
            Assert.False(DefaultBrowser.IsRegistered(exe, root));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void A_missing_executable_cannot_be_registered() =>
        Assert.Throws<FileNotFoundException>(() => DefaultBrowser.Register(@"C:\no\existe\MyOwnBrowser.exe", @"Software\MyOwnBrowser.Tests-never"));
}
