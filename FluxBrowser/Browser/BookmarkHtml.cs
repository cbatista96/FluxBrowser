using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FluxBrowser.Browser;

internal sealed record BookmarkImport(List<Bookmark> Items, int Skipped);

internal static class BookmarkHtml
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Tokens = new(@"<(?<end>/)?(?<tag>DL|H3|A)\b(?<attrs>[^>]*)>|(?<text>[^<]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Attributes = new("""(?<name>[\w-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s>]+))""",
        RegexOptions.CultureInvariant, Timeout);

    public static BookmarkImport Parse(string html)
    {
        if (html.Length > 10 * 1024 * 1024) throw new InvalidDataException("El archivo supera el límite de 10 MB.");
        if (!html.Contains("<DL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selecciona un archivo HTML de favoritos exportado por un navegador.");
        var result = new List<Bookmark>();
        var parents = new Stack<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string folder = "", pending = "", text = "", href = "", icon = "", capture = "";
        int skipped = 0, depth = 0;
        bool root = false;
        foreach (Match token in Tokens.Matches(html))
        {
            if (token.Groups["text"].Success)
            {
                if (capture.Length > 0) text += token.Groups["text"].Value;
                continue;
            }
            string tag = token.Groups["tag"].Value.ToUpperInvariant();
            bool end = token.Groups["end"].Success;
            if (tag == "DL")
            {
                if (!end)
                {
                    if (++depth > 64) throw new InvalidDataException("Hay demasiados niveles de carpetas.");
                    if (!root) root = true;
                    else
                    {
                        parents.Push(folder);
                        if (pending.Length > 0) folder = pending;
                    }
                    pending = "";
                }
                else
                {
                    if (depth <= 0) throw new InvalidDataException("La estructura de carpetas del archivo no es válida.");
                    depth--;
                    if (parents.Count > 0) folder = parents.Pop();
                }
                continue;
            }
            if (!end)
            {
                capture = tag;
                text = "";
                if (tag == "A")
                {
                    var attrs = Attributes.Matches(token.Groups["attrs"].Value).Cast<Match>()
                        .GroupBy(m => m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
                    href = attrs.GetValueOrDefault("HREF", "");
                    icon = attrs.GetValueOrDefault("ICON_URI", "");
                }
            }
            else if (capture == tag)
            {
                string title = WebUtility.HtmlDecode(text).Trim();
                if (tag == "H3")
                {
                    title = title.Replace('/', '-');
                    if (title.Length == 0) title = "Carpeta";
                    string name = title;
                    int n = 2;
                    string PathOf(string value) => folder.Length == 0 ? value : folder + "/" + value;
                    while (!folders.Add(PathOf(name))) name = $"{title} ({n++})";
                    result.Add(new(name, "", folder, true));
                    pending = PathOf(name);
                }
                else if (WebAddress.IsHttp(href))
                    result.Add(new(title.Length > 0 ? title : new Uri(href).Host, href, folder, Favicon: WebAddress.IsHttp(icon) ? icon : null));
                else skipped++;
                capture = "";
                if (result.Count > 20_000) throw new InvalidDataException("El archivo contiene más de 20.000 favoritos.");
            }
        }
        if (depth != 0 || capture.Length > 0) throw new InvalidDataException("El archivo de favoritos está incompleto.");
        return new(result, skipped);
    }

    public static string Export(IReadOnlyCollection<Bookmark> bookmarks)
    {
        var output = new StringBuilder("""
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
            <TITLE>Favoritos</TITLE>
            <H1>Favoritos</H1>
            <DL><p>

            """);
        void WriteFolder(string parent, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Hay demasiados niveles de carpetas.");
            foreach (Bookmark item in bookmarks.Where(b => b.Folder.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            {
                string title = WebUtility.HtmlEncode(item.Title);
                if (item.IsFolder)
                {
                    output.AppendLine($"<DT><H3>{title}</H3>\n<DL><p>");
                    WriteFolder(item.FolderPath, depth + 1);
                    output.AppendLine("</DL><p>");
                }
                else output.AppendLine($"<DT><A HREF=\"{WebUtility.HtmlEncode(item.Address)}\">{title}</A>");
            }
        }
        WriteFolder("", 0);
        output.AppendLine("</DL><p>");
        return output.ToString();
    }

    public static List<Bookmark> Merge(IReadOnlyCollection<Bookmark> current, IReadOnlyCollection<Bookmark> imported, string name)
    {
        var paths = current.Where(b => b.IsFolder).Select(b => b.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string root = name;
        for (int n = 2; paths.Contains(root); n++) root = $"{name} ({n})";
        var result = current.ToList();
        result.Add(new(root, "", IsFolder: true));
        result.AddRange(imported.Select(b => b with { Folder = b.Folder.Length == 0 ? root : root + "/" + b.Folder }));
        return result;
    }
}
