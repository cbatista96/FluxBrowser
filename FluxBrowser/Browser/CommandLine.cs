namespace FluxBrowser.Browser;

/// <summary>
/// Extracts web addresses from process arguments or a raw command-line string. Anything that is not a plain HTTP/HTTPS address
/// (other schemes, local files, switches) is ignored: arguments come from other programs and are never trusted.
/// </summary>
internal static class CommandLine
{
    public const int MaxAddresses = 20;

    public static List<string> Addresses(IEnumerable<string> arguments)
    {
        var result = new List<string>();
        foreach (string argument in arguments)
        {
            string candidate = argument.Trim().Trim('"');
            if (candidate.StartsWith('-') || candidate.Length > 8192) continue;
            if (AddressResolver.TryResolve(candidate, out string address, out _) && WebAddress.IsHttp(address) &&
                candidate.Contains("://", StringComparison.Ordinal) && !result.Contains(address))
                result.Add(address);
            if (result.Count >= MaxAddresses) break;
        }
        return result;
    }

    /// <summary>Splits a Windows command line into arguments, honouring double quotes.</summary>
    public static List<string> Split(string? line)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return parts;
        var current = new System.Text.StringBuilder();
        bool quoted = false, started = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) parts.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else { current.Append(c); started = true; }
        }
        if (started) parts.Add(current.ToString());
        return parts;
    }
}
