using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HUDMind.App.Controls;
using HUDMind.Core.AI;
using HUDMind.Core.Config;
using HUDMind.Core.Security;
using HUDMind.Core.Updates;
using HUDMind.Windows;
using HUDMind.Windows.Security;

namespace HUDMind.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private SingleInstancePipeServer? _singleInstancePipe;
    private int _crashDialogShowing;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Guarantee 0.0% GPU and 0 MB VRAM usage on GTX 1050 Ti / gaming GPUs!
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        KnownFolders.TrySetAppUserModelId("HUDMind.Native.Desktop");

        bool smokeMode = e.Args.Any(a => string.Equals(a, "--smoke-startup", StringComparison.OrdinalIgnoreCase));
        bool startMinimized = !smokeMode && e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        ProfileVaultReader.RegisterRuntimeIntegrityProbe(() => RuntimeHardeningGuard.IsExternalDebuggerAttached(smokeMode));

        if (!smokeMode)
        {
            _singleInstanceMutex = new Mutex(true, "HUDMind_Native_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)
            {
                bool signaled = SingleInstancePipeServer.TrySignalExistingInstance(
                    SingleInstancePipeServer.DefaultPipeName,
                    startMinimized ? "PING" : "SHOW",
                    timeoutMs: 500);

                if (!startMinimized && !signaled)
                {
                    System.Windows.MessageBox.Show(
                        "HUDMind Native ya está abierto en segundo plano.",
                        "HUDMind",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }

            RegisterGlobalCrashHandlers();
            _singleInstancePipe = new SingleInstancePipeServer(
                SingleInstancePipeServer.DefaultPipeName,
                command =>
                {
                    if (string.Equals(command, "SHOW", StringComparison.OrdinalIgnoreCase))
                        Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFrontFromPipe());
                });
        }

        // NOTE: Keep process PriorityClass Normal so Win32 audio/mic/hotkey threads never stutter in CPU-bound games!
        base.OnStartup(e);

        try
        {
            var win = new MainWindow(smokeMode, startMinimized);
            MainWindow = win;
            win.Show();
        }
        catch (Exception ex)
        {
            if (smokeMode)
            {
                Console.Error.WriteLine($"SMOKE_STARTUP_FAILED: {ex}");
                Shutdown(1);
                return;
            }
            var (report, logPath) = WriteRedactedCrashReport(ex, "Startup");
            PersonalAccountDialogs.ShowCrashReportDialog(null, report, logPath);
            Shutdown(1);
        }
    }

    private void RegisterGlobalCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var (report, logPath) = WriteRedactedCrashReport(args.Exception, "UI.Dispatcher");
            args.Handled = true;
            if (Interlocked.CompareExchange(ref _crashDialogShowing, 1, 0) == 0)
            {
                try
                {
                    PersonalAccountDialogs.ShowCrashReportDialog(MainWindow, report, logPath);
                }
                finally
                {
                    Interlocked.Exchange(ref _crashDialogShowing, 0);
                }
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _ = WriteRedactedCrashReport(ex, args.IsTerminating ? "AppDomain.Fatal" : "AppDomain.Unhandled");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _ = WriteRedactedCrashReport(args.Exception, "TaskScheduler.Unobserved");
            args.SetObserved();
        };
    }

    private static (string Report, string? LogPath) WriteRedactedCrashReport(Exception? ex, string source)
    {
        var (ver, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
        var verStr = GitHubReleaseUpdateService.FormatVersion(ver);
        string report = DiagnosticsFormatter.FormatCrashReport(ex, verStr, source);
        string? writtenPath = null;
        try
        {
            var root = ConfigRepository.DiscoverWorkspaceRoot();
            var logsDir = Path.GetFullPath(Path.Combine(root, "logs"));
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0 &&
                (!Directory.Exists(logsDir) || (File.GetAttributes(logsDir) & FileAttributes.ReparsePoint) == 0))
            {
                Directory.CreateDirectory(logsDir);
                var fileName = $"crash-{DateTime.Now:yyyyMMdd_HHmmss}.log";
                var dest = Path.GetFullPath(Path.Combine(logsDir, fileName));
                if (dest.StartsWith(logsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(dest, report);
                    writtenPath = dest;
                }
            }
        }
        catch
        {
            // Keep UI recovery functional even if logs directory is read-only
        }
        return (report, writtenPath);
    }

    public void ReleaseSingleInstanceMutex()
    {
        if (_singleInstancePipe is not null)
        {
            try { _singleInstancePipe.Dispose(); } catch { }
            _singleInstancePipe = null;
        }
        if (_singleInstanceMutex is null)
            return;
        try { _singleInstanceMutex.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex.Dispose(); } catch { }
        _singleInstanceMutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }
}
