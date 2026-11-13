using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using MyOwnBrowser.Browser;
using MyOwnBrowser.Diagnostics;
using Windows.ApplicationModel.Activation;

namespace MyOwnBrowser;

internal static class Program
{
    internal static bool HasPackageIdentity { get; } = CheckPackageIdentity();
    private static readonly object ActivationLock = new();
    private static DispatcherQueue? _dispatcher;
    private static bool _activationPending;
    private static readonly List<string> PendingAddresses = [];
    private static AppInstance? _instance;

    /// <summary>Set by <c>--restore-session</c> (restart after a graphics failure): reopen the saved tabs even if "restore session" is off.</summary>
    internal static bool RestoreRequested { get; private set; }

    /// <summary>Set by <c>--private</c>: the first window opens as a private window (no history, no saved session, separate cookies).</summary>
    internal static bool PrivateRequested { get; private set; }

    /// <summary>Milliseconds since the process was created (start-up measurements in the log).</summary>
    internal static long SinceStart()
    {
        try { using Process self = Process.GetCurrentProcess(); return (long)(DateTime.Now - self.StartTime).TotalMilliseconds; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return -1; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Excepción no controlada" + (e.IsTerminating ? " (fatal)" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Tarea con error no observado", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ProfileStorage.Flush();
        RestoreRequested = args.Contains("--restore-session");
        PrivateRequested = args.Contains("--private");
        if (HandleMaintenanceArguments(args, out int maintenanceResult)) return maintenanceResult;
        try
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // One instance per profile folder: a copy started with MYOWNBROWSER_PROFILE (tests, portable use) is independent.
            string instanceKey = Environment.GetEnvironmentVariable(ProfileStorage.ProfileVariable) is { Length: > 0 }
                ? "MyOwnBrowser.Main." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(ProfileStorage.DirectoryPath.ToLowerInvariant())))[..12]
                : "MyOwnBrowser.Main";
            _instance = AppInstance.FindOrRegisterForKey(instanceKey);
            if (!_instance.IsCurrent)
            {
                RedirectActivation(_instance, activation);
                return 0;
            }

            lock (ActivationLock) PendingAddresses.AddRange(activation.Kind == ExtendedActivationKind.Protocol
                ? ActivationAddresses(activation)
                : CommandLine.Addresses(args));
            _instance.Activated += Instance_Activated;
            Application.Start(p =>
            {
                DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new GuardedSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcher), App.ReportAsyncFault));
                SetDispatcher(dispatcher);
                _ = new App();
            });
            return 0;
        }
        catch (Exception exception)
        {
            AppLog.Error("No se pudo iniciar My Own Browser", exception);
            MessageBox(IntPtr.Zero, $"No se pudo iniciar My Own Browser.\n\n{exception.Message}",
                "My Own Browser", 0x10);
            return 1;
        }
    }

    /// <summary>
    /// <c>--register</c> / <c>--unregister</c> (used by the installer scripts) change the per-user browser registration and exit
    /// without opening a window. <c>--restart-after=PID</c> waits for the previous process to finish, so a restarted copy does not
    /// get redirected into the one that is still closing.
    /// </summary>
    private static bool HandleMaintenanceArguments(string[] args, out int exitCode)
    {
        exitCode = 0;
        foreach (string argument in args)
        {
            if (argument.StartsWith("--restart-after=", StringComparison.Ordinal) &&
                int.TryParse(argument["--restart-after=".Length..], out int pid) && pid > 0)
            {
                try { using Process previous = Process.GetProcessById(pid); previous.WaitForExit(15000); }
                catch (ArgumentException) { /* already gone */ }
                catch (InvalidOperationException) { /* already gone */ }
            }
        }
        bool register = args.Contains("--register"), unregister = args.Contains("--unregister");
        if (!register && !unregister) return false;
        if (HasPackageIdentity) return true;
        try
        {
            string self = Environment.ProcessPath ?? throw new InvalidOperationException("No se conoce la ruta del ejecutable.");
            if (register) DefaultBrowser.Register(self);
            // Only remove a registration that belongs to this copy; another installed copy keeps its own.
            else if (DefaultBrowser.IsRegistered(self)) DefaultBrowser.Unregister();
            AppLog.Info(register ? "Registrado como navegador en Windows." : "Registro de navegador eliminado.");
        }
        catch (Exception exception)
        {
            AppLog.Error("No se pudo actualizar el registro del navegador", exception);
            exitCode = 1;
        }
        return true;
    }

    /// <summary>Addresses passed on the command line before the first window exists; consumed once by the first window.</summary>
    internal static List<string> TakePendingAddresses()
    {
        lock (ActivationLock)
        {
            var taken = PendingAddresses.ToList();
            PendingAddresses.Clear();
            return taken;
        }
    }

    internal static void QueueAddresses(IEnumerable<string> addresses)
    {
        lock (ActivationLock) PendingAddresses.AddRange(addresses);
    }

    private static void Instance_Activated(object? sender, AppActivationArguments args)
    {
        // Raised on a thread-pool thread by the WinAppSDK; an exception escaping here fails the whole process fast.
        try
        {
            AppLog.Info($"Activación recibida ({args.Kind}).");
            List<string> addresses = ActivationAddresses(args);
            DispatcherQueue? dispatcher;
            lock (ActivationLock)
            {
                dispatcher = _dispatcher;
                if (dispatcher is null)
                {
                    _activationPending = true;
                    PendingAddresses.AddRange(addresses);
                    return;
                }
            }
            QueueActivation(dispatcher, addresses);
        }
        catch (Exception exception)
        {
            AppLog.Error("No se pudo procesar la activación de otra instancia", exception);
        }
    }

    private static List<string> ActivationAddresses(AppActivationArguments args) => args.Data switch
    {
        IProtocolActivatedEventArgs protocol => CommandLine.Addresses([protocol.Uri.AbsoluteUri]),
        ILaunchActivatedEventArgs launch => CommandLine.Addresses(CommandLine.Split(launch.Arguments)),
        _ => []
    };

    private static bool CheckPackageIdentity()
    {
        uint length = 0;
        // ERROR_INSUFFICIENT_BUFFER means a package name is available.
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == 122;
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    private static void SetDispatcher(DispatcherQueue dispatcher)
    {
        bool pending;
        lock (ActivationLock)
        {
            _dispatcher = dispatcher;
            pending = _activationPending;
            _activationPending = false;
        }
        if (pending) QueueActivation(dispatcher, []);
    }

    private static void QueueActivation(DispatcherQueue dispatcher, List<string> addresses)
    {
        if (!dispatcher.TryEnqueue(() =>
        {
            try { (Application.Current as App)?.ActivateExistingWindow(addresses); }
            catch (Exception exception) { AppLog.Error("No se pudo activar la ventana existente", exception); }
        }))
            Debug.WriteLine("No se pudo activar la ventana existente porque la interfaz se está cerrando.");
    }

    private static void RedirectActivation(AppInstance instance, AppActivationArguments activation)
    {
        var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        Task redirect = Task.Run(async () => await instance.RedirectActivationToAsync(activation));
        Task notifier = redirect.ContinueWith(_ => completed.Set(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            int result = CoWaitForMultipleObjects(0, 15000, 1, [completed.SafeWaitHandle.DangerousGetHandle()], out _);
            if (result != 0)
                throw new COMException("No se pudo contactar con la ventana del navegador que ya está abierta.", result);
            redirect.GetAwaiter().GetResult();
        }
        finally
        {
            if (notifier.IsCompleted) completed.Dispose();
            else _ = notifier.ContinueWith(_ => completed.Dispose(), TaskScheduler.Default);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint milliseconds, uint count,
        IntPtr[] handles, out uint index);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
