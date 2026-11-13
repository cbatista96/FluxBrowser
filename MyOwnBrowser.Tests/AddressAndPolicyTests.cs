using MyOwnBrowser.Browser;
using Xunit;

// ProfileStorage and the process environment are shared state: run everything serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MyOwnBrowser.Tests;

public class AddressAndPolicyTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("localhost:8080/test", "http://localhost:8080/test")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("[::1]:8080", "http://[::1]:8080/")]
    [InlineData("localhostevil.com", "https://localhostevil.com/")]
    [InlineData("dos palabras", "https://duckduckgo.com/?q=dos%20palabras")]
    public void Input_is_resolved_to_an_address_or_search(string input, string expected)
    {
        Assert.True(AddressResolver.TryResolve(input, out string address, out _));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("http://bad host")]
    public void Invalid_input_is_rejected_with_a_message(string input)
    {
        Assert.False(AddressResolver.TryResolve(input, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("Bing", "https://www.bing.com/search?q=dos%20palabras")]
    [InlineData("Google", "https://www.google.com/search?q=dos%20palabras")]
    public void Search_engine_is_applied(string engine, string expected)
    {
        Assert.True(AddressResolver.TryResolve("dos palabras", out string address, out _, engine));
        Assert.Equal(expected, address);
    }

    [Fact]
    public void Unknown_search_engine_is_rejected() =>
        Assert.Throws<ArgumentException>(() => AddressResolver.TryResolve("dos palabras", out _, out _, "Unknown"));

    [Fact]
    public void Selected_text_is_searched_not_interpreted_as_an_address() =>
        Assert.Equal("https://www.bing.com/search?q=example.com", AddressResolver.Search("example.com", "Bing"));

    [Fact]
    public void Only_http_and_https_are_web_addresses()
    {
        Assert.True(WebAddress.IsHttp("https://example.com/"));
        Assert.False(WebAddress.IsHttp("javascript:alert(1)"));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("foo.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("router")]
    [InlineData("printer.local")]
    [InlineData("nas.lan")]
    public void Local_and_private_hosts_are_recognised(string host) => Assert.True(WebAddress.IsLocalOrPrivateHost(host));

    [Theory]
    [InlineData("example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("sub.example.co.uk")]
    public void Public_hosts_are_not_flagged(string host) => Assert.False(WebAddress.IsLocalOrPrivateHost(host));
}

public class CommandLineTests
{
    [Fact]
    public void Quoted_arguments_are_split_correctly() =>
        Assert.Equal(["C:/Program Files/app.exe", "https://example.com", "https://example.org/a b"],
            CommandLine.Split("\"C:/Program Files/app.exe\" https://example.com \"https://example.org/a b\""));

    [Fact]
    public void Only_explicit_http_urls_are_accepted()
    {
        var accepted = CommandLine.Addresses(["--flag", "C:/app.exe", "file:///C:/secret.txt", "javascript:alert(1)", "dos palabras",
            "example.com", "https://example.com", "https://example.com", "https://user:pw@example.com"]);
        Assert.Equal(["https://example.com/"], accepted);
    }

    [Fact]
    public void The_number_of_addresses_is_limited() =>
        Assert.Equal(CommandLine.MaxAddresses, CommandLine.Addresses(Enumerable.Range(0, 100).Select(i => $"https://example.com/{i}")).Count);
}

public class ViewportLayoutTests
{
    [Fact]
    public void Page_start_sits_below_the_bars_and_is_revealed_by_scrolling()
    {
        Assert.Equal(108, ViewportLayout.TopInset(108, 800, 0, 3000));
        Assert.Equal(58, ViewportLayout.TopInset(108, 800, 50, 3000));
        Assert.Equal(0, ViewportLayout.TopInset(108, 800, 500, 3000));
        Assert.Equal(78, ViewportLayout.TopInset(78, 800, 0, 3000));
    }

    [Fact]
    public void Short_pages_do_not_oscillate()
    {
        Assert.Equal(108, ViewportLayout.TopInset(108, 800, 0, 692));
        Assert.Equal(83, ViewportLayout.TopInset(108, 800, 50, 742));
    }

    [Fact]
    public void Reveal_inset_follows_the_scroll_without_jumps()
    {
        Assert.Equal(108, ViewportLayout.RevealInset(108, 0));
        Assert.Equal(58, ViewportLayout.RevealInset(108, 50));
        Assert.Equal(0, ViewportLayout.RevealInset(108, 500));
    }

    [Fact]
    public void Only_scrollable_pages_use_the_full_viewport()
    {
        Assert.True(ViewportLayout.UsesFullViewport(108, 800, 3000, false));
        Assert.False(ViewportLayout.UsesFullViewport(108, 800, 700, true));
    }

    [Fact]
    public void Gap_model_fills_the_window_only_for_content_taller_than_it()
    {
        // 800 high window, 108 toolbar: a page that starts below the bars needs a full extra toolbar of content to be worth sliding under them.
        Assert.True(ViewportLayout.UsesFullViewportWithGap(108, 800, 3000, false));
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, 850, false));
        Assert.True(ViewportLayout.UsesFullViewportWithGap(108, 800, 908, false));
        // Once full, it stays full until the content no longer exceeds the window.
        Assert.True(ViewportLayout.UsesFullViewportWithGap(108, 800, 850, true));
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, 800, true));
    }

    [Fact]
    public void Gap_model_does_not_flip_pages_that_size_themselves_to_the_viewport()
    {
        // An app that fills its viewport measures H - toolbar when full and H when below the bars; neither may flip the other way.
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, 692, true));
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, 692, false));
        // min-height:100vh pages: H when full (-> below the bars), H - toolbar there (stays).
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, 800, true));
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, 692, false));
    }

    [Fact]
    public void Gap_model_keeps_the_current_mode_when_the_height_is_unknown()
    {
        Assert.True(ViewportLayout.UsesFullViewportWithGap(108, 800, 0, true));
        Assert.False(ViewportLayout.UsesFullViewportWithGap(108, 800, double.NaN, false));
    }

    [Fact]
    public void Non_finite_metrics_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewportLayout.TopInset(108, 800, double.NaN, 3000));

    [Fact]
    public void Settings_is_an_internal_page()
    {
        Assert.True(ViewportLayout.IsInternal("browser:settings"));
        Assert.False(ViewportLayout.IsInternal("https://example.com/"));
    }
}

public class PreferencesTests
{
    private static readonly Dictionary<string, BrowserPreferences> InvalidCases = new()
    {
        { "blur NaN", new BrowserPreferences { BlurRadius = double.NaN } },
        { "glass 0", new BrowserPreferences { GlassOpacity = 0 } },
        { "menu 1", new BrowserPreferences { MenuOpacity = 1 } },
        { "sidebar 100", new BrowserPreferences { SidebarWidth = 100 } },
        { "engine", new BrowserPreferences { SearchEngine = "Unknown" } },
        { "theme", new BrowserPreferences { Theme = "Unknown" } },
        { "fps", new BrowserPreferences { FrameRate = 1000 } },
        { "zoom", new BrowserPreferences { DefaultZoom = double.PositiveInfinity } },
        { "file home", new BrowserPreferences { HomePage = "file:///C:/example" } },
        { "js new tab", new BrowserPreferences { NewTabPage = "javascript:alert(1)" } },
        { "relative downloads", new BrowserPreferences { DownloadDirectory = "..\\Downloads" } },
        { "empty profile", new BrowserPreferences { ProfileName = "" } },
        { "empty language", new BrowserPreferences { PreferredLanguages = "es,,en" } },
        { "bad language", new BrowserPreferences { PreferredLanguages = "no-es-un-idioma" } },
        { "font", new BrowserPreferences { WebFontSize = 100 } },
        { "min font", new BrowserPreferences { MinimumWebFontSize = -1 } },
        { "page theme", new BrowserPreferences { PageTheme = "Unknown" } },
        { "custom pages empty", new BrowserPreferences { StartupUseCustomPages = true } },
        { "custom pages js", new BrowserPreferences { StartupUseCustomPages = true, StartupPages = "javascript:alert(1)" } }
    };

    public static TheoryData<string> InvalidNames => new(InvalidCases.Keys);

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Invalid_settings_are_rejected(string name) =>
        Assert.Throws<InvalidDataException>(InvalidCases[name].Validate);

    [Fact]
    public void Valid_settings_are_accepted()
    {
        new BrowserPreferences().Validate();
        new BrowserPreferences { FrameRate = 144 }.Validate();
        new BrowserPreferences { FrameRate = 240 }.Validate();
        new BrowserPreferences { StartupUseCustomPages = true, StartupPages = "https://example.com/\r\nhttps://www.bing.com/" }.Validate();
        new BrowserPreferences { PreferredLanguages = "es-ES,en-US", AnimateInterface = false, WebFontSize = 24 }.Validate();
        new BrowserPreferences { PersistSessions = true }.Validate();
    }

    [Fact]
    public void Persistent_sessions_are_off_by_default() => Assert.False(new BrowserPreferences().PersistSessions);
}
