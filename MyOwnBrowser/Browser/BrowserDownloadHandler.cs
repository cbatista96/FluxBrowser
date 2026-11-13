using CefSharp;
using CefSharp.Handler;

namespace MyOwnBrowser.Browser;

internal sealed record DownloadProgress(int Id, string Name, string Path, string Address,
    long Received, long Total, int Percent, string State, bool InProgress);

internal sealed class BrowserDownloadHandler(Func<string> directory, Func<bool> askLocation, Action<DownloadProgress> update,
    Action<string, string> report) : DownloadHandler, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, IDownloadItemCallback> _callbacks = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) =>
        WebAddress.IsHttp(url) || (url.StartsWith("blob:", StringComparison.Ordinal) && WebAddress.IsHttp(url[5..])) ||
        url.StartsWith("data:", StringComparison.Ordinal);

    protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem item, IBeforeDownloadCallback callback)
    {
        using (callback)
        {
            try
            {
                string folder = directory();
                Directory.CreateDirectory(folder);
                string name = Path.GetFileName(item.SuggestedFileName);
                name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
                if (string.IsNullOrWhiteSpace(name)) name = "descarga";
                string path;
                lock (_gate)
                {
                    if (_disposed) return true;
                    path = Path.Combine(folder, name);
                    for (int n = 1; File.Exists(path) || _paths.Contains(path); n++)
                        path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
                    _paths.Add(path);
                }
                callback.Continue(path, showDialog: askLocation());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                report("No se pudo iniciar la descarga", exception.Message);
            }
        }
        return true;
    }

    protected override void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem item, IDownloadItemCallback callback)
    {
        lock (_gate)
        {
            if (_disposed) { callback.Dispose(); return; }
            if (_callbacks.Remove(item.Id, out IDownloadItemCallback? previous)) previous.Dispose();
            if (item.IsInProgress) _callbacks[item.Id] = callback;
            else callback.Dispose();
        }
        string state = item.IsComplete ? "Completada" : item.IsCancelled ? "Cancelada" :
            item.IsInterrupted ? $"Interrumpida: {item.InterruptReason}" : item.IsPaused ? "Pausada" : "Descargando";
        update(new DownloadProgress(item.Id, Path.GetFileName(item.FullPath), item.FullPath, item.Url,
            item.ReceivedBytes, item.TotalBytes, item.PercentComplete, state, item.IsInProgress));
        if (item.IsInterrupted) report("Descarga interrumpida", $"{item.SuggestedFileName}: {item.InterruptReason}");
    }

    public void Cancel(int id)
    {
        lock (_gate)
        {
            if (_callbacks.TryGetValue(id, out IDownloadItemCallback? callback) && !callback.IsDisposed) callback.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (IDownloadItemCallback callback in _callbacks.Values) callback.Dispose();
            _callbacks.Clear();
        }
    }
}
