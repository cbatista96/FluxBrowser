using Microsoft.Win32;

namespace MyOwnBrowser.Browser;

/// <summary>
/// Per-user (HKCU, no administrator rights) registration that makes the app appear in Windows' "Default apps" as a web browser.
/// Windows does not let an app make itself the default: this only makes it selectable; the user still confirms the choice.
/// </summary>
internal static class DefaultBrowser
{
    public const string DisplayName = "My Own Browser";
    private const string ProgId = "MyOwnBrowserURL";
    private const string ClientKey = "MyOwnBrowser";

    private static string Quoted(string exe) => "\"" + exe + "\"";

    /// <param name="exePath">Absolute path of the executable that handles the links.</param>
    /// <param name="softwareRoot">Registry root under HKCU; only tests pass something other than <c>Software</c>.</param>
    public static void Register(string exePath, string softwareRoot = "Software")
    {
        if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("No se encontró el ejecutable que debe registrarse.", exePath);
        string command = Quoted(exePath) + " \"%1\"";
        string icon = Quoted(exePath) + ",0";
        string capabilities = $@"{softwareRoot}\Clients\StartMenuInternet\{ClientKey}\Capabilities";

        Set($@"{softwareRoot}\Classes\{ProgId}", null, DisplayName + " URL");
        Set($@"{softwareRoot}\Classes\{ProgId}", "URL Protocol", "");
        Set($@"{softwareRoot}\Classes\{ProgId}\DefaultIcon", null, icon);
        Set($@"{softwareRoot}\Classes\{ProgId}\shell\open\command", null, command);

        Set($@"{softwareRoot}\Clients\StartMenuInternet\{ClientKey}", null, DisplayName);
        Set($@"{softwareRoot}\Clients\StartMenuInternet\{ClientKey}\DefaultIcon", null, icon);
        Set($@"{softwareRoot}\Clients\StartMenuInternet\{ClientKey}\shell\open\command", null, Quoted(exePath));
        Set(capabilities, "ApplicationName", DisplayName);
        Set(capabilities, "ApplicationDescription", "Navegador con barras de cristal translúcido sobre Chromium.");
        Set(capabilities, "ApplicationIcon", icon);
        Set(capabilities + @"\URLAssociations", "http", ProgId);
        Set(capabilities + @"\URLAssociations", "https", ProgId);
        Set(capabilities + @"\StartMenu", "StartMenuInternet", DisplayName);
        Set($@"{softwareRoot}\RegisteredApplications", DisplayName, capabilities);
    }

    public static void Unregister(string softwareRoot = "Software")
    {
        using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey($@"{softwareRoot}\RegisteredApplications", writable: true))
            registered?.DeleteValue(DisplayName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{softwareRoot}\Clients\StartMenuInternet\{ClientKey}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{softwareRoot}\Classes\{ProgId}", throwOnMissingSubKey: false);
    }

    /// <summary>True when the registration exists and points at <paramref name="exePath"/> (a moved or deleted copy counts as not registered).</summary>
    public static bool IsRegistered(string exePath, string softwareRoot = "Software")
    {
        using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{softwareRoot}\Classes\{ProgId}\shell\open\command");
        using RegistryKey? registered = Registry.CurrentUser.OpenSubKey($@"{softwareRoot}\RegisteredApplications");
        return command?.GetValue(null) is string value && value.StartsWith(Quoted(exePath), StringComparison.OrdinalIgnoreCase) &&
            registered?.GetValue(DisplayName) is string;
    }

    private static void Set(string path, string? name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }
}
