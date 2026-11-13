using CefSharp.Handler;

namespace FluxBrowser.Browser;

internal sealed class PrototypeBrowserProcessHandler(Action activateWindow) : BrowserProcessHandler
{
    protected override bool OnAlreadyRunningAppRelaunch(
        IReadOnlyDictionary<string, string> commandLine, string currentDirectory)
    {
        activateWindow();
        return true;
    }
}
