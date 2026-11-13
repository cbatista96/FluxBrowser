namespace FluxBrowser.Browser;

internal static class AddressResolver
{
    public static bool TryResolve(string input, out string address, out string error, string searchEngine = "DuckDuckGo")
    {
        input = input.Trim();
        address = string.Empty;
        error = string.Empty;

        if (input.Length == 0)
        {
            error = "Escribe una dirección o una búsqueda.";
            return false;
        }

        if (!input.Any(char.IsWhiteSpace) && Uri.TryCreate(input, UriKind.Absolute, out Uri? absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return AcceptHttpUri(absolute, out address, out error);
        }

        if (input.Contains("://", StringComparison.Ordinal))
        {
            error = input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "La dirección HTTP o HTTPS no es válida."
                : "Solo se admiten direcciones HTTP y HTTPS en este prototipo.";
            return false;
        }

        if (!input.Any(char.IsWhiteSpace) && LooksLikeHost(input))
        {
            string scheme = IsLoopbackHost(input) ? "http://" : "https://";
            if (Uri.TryCreate(scheme + input, UriKind.Absolute, out Uri? uri))
            {
                return AcceptHttpUri(uri, out address, out error);
            }

            error = "La dirección no es válida.";
            return false;
        }

        address = Search(input, searchEngine);
        return true;
    }

    public static string Search(string query, string searchEngine)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Escribe un texto para buscar.", nameof(query));
        string searchUrl = searchEngine switch
        {
            "DuckDuckGo" => "https://duckduckgo.com/?q=",
            "Bing" => "https://www.bing.com/search?q=",
            "Google" => "https://www.google.com/search?q=",
            _ => throw new ArgumentException("El buscador no está disponible.", nameof(searchEngine))
        };
        return searchUrl + Uri.EscapeDataString(query.Trim());
    }

    private static bool AcceptHttpUri(Uri uri, out string address, out string error)
    {
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            address = string.Empty;
            error = "La dirección no es válida o contiene credenciales.";
            return false;
        }

        address = uri.AbsoluteUri;
        error = string.Empty;
        return true;
    }

    private static bool LooksLikeHost(string input)
    {
        string host = input.Split('/', '?', '#')[0];
        return host.Contains('.') || host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("[::1]", StringComparison.Ordinal);
    }

    private static bool IsLoopbackHost(string input)
    {
        return Uri.TryCreate("http://" + input, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
    }
}
