using System.Diagnostics;
using System.Text;
using MyOwnBrowser.Browser;

namespace MyOwnBrowser.Diagnostics;

/// <summary>
/// Minimal rolling log in the profile folder (<c>logs\app.log</c>). Never throws: logging must not be able to crash the app.
/// Messages must not contain page content or credentials; only addresses already shown in the UI are acceptable.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(ProfileStorage.DirectoryPath, "logs", "app.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" +
            (exception is null ? "" : Environment.NewLine + exception) + Environment.NewLine;
        Debug.Write(line);
        try
        {
            lock (Gate)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".1.log"), true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"No se pudo escribir el registro: {e.Message}");
        }
    }
}
