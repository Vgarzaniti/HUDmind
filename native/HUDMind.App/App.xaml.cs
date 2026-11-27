using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private string? _customDataDir;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Guarantee 0.0% GPU and 0 MB VRAM usage on GTX 1050 Ti / gaming GPUs!
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        KnownFolders.TrySetAppUserModelId("HUDMind.Native.Desktop");

        bool smokeMode = e.Args.Any(a => string.Equals(a, "--smoke-startup", StringComparison.OrdinalIgnoreCase));
        bool startMinimized = !smokeMode && e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        bool disableUpdateCheck = !smokeMode && e.Args.Any(a => string.Equals(a, "--no-update-check", StringComparison.OrdinalIgnoreCase));
        _customDataDir = TryExtractSafeDataDirArg(e.Args);
        string? pendingArchivePath = smokeMode ? null : TryExtractBackupArchiveArg(e.Args);
        ProfileVaultReader.RegisterRuntimeIntegrityProbe(() => RuntimeHardeningGuard.IsExternalDebuggerAttached(smokeMode));

        if (!smokeMode)
        {
            var (mutexName, pipeName) = ResolveSingleInstanceChannels(_customDataDir);
            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                string pipeCmd = pendingArchivePath is not null
                    ? $"IMPORT:{pendingArchivePath}"
                    : (startMinimized ? "PING" : "SHOW");
                bool signaled = SingleInstancePipeServer.TrySignalExistingInstance(
                    pipeName,
                    pipeCmd,
                    timeoutMs: 650);

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
                pipeName,
                command => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.HandlePipeCommand(command)));
        }

        // NOTE: Keep process PriorityClass Normal so Win32 audio/mic/hotkey threads never stutter in CPU-bound games!
        base.OnStartup(e);

        try
        {
            var win = new MainWindow(smokeMode, startMinimized, pendingArchivePath, _customDataDir, disableUpdateCheck);
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
            var (report, logPath) = WriteRedactedCrashReport(ex, "Startup", _customDataDir);
            PersonalAccountDialogs.ShowCrashReportDialog(null, report, logPath);
            Shutdown(1);
        }
    }

    private void RegisterGlobalCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var (report, logPath) = WriteRedactedCrashReport(args.Exception, "UI.Dispatcher", _customDataDir);
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
                _ = WriteRedactedCrashReport(ex, args.IsTerminating ? "AppDomain.Fatal" : "AppDomain.Unhandled", _customDataDir);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _ = WriteRedactedCrashReport(args.Exception, "TaskScheduler.Unobserved", _customDataDir);
            args.SetObserved();
        };
    }

    private static (string Report, string? LogPath) WriteRedactedCrashReport(Exception? ex, string source, string? customDataDir = null)
    {
        var (ver, _) = GitHubReleaseUpdateService.GetCurrentAppVersion();
        var verStr = GitHubReleaseUpdateService.FormatVersion(ver);
        string report = DiagnosticsFormatter.FormatCrashReport(ex, verStr, source);
        string? writtenPath = null;
        try
        {
            var root = !string.IsNullOrWhiteSpace(customDataDir)
                ? customDataDir
                : ConfigRepository.DiscoverWorkspaceRoot();
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

    private static (string MutexName, string PipeName) ResolveSingleInstanceChannels(string? customDataDir)
    {
        if (string.IsNullOrWhiteSpace(customDataDir))
            return ("HUDMind_Native_SingleInstance_Mutex", SingleInstancePipeServer.DefaultPipeName);

        var normalized = customDataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        return ($"HUDMind_Native_SingleInstance_Mutex_{hash}", $"{SingleInstancePipeServer.DefaultPipeName}_{hash}");
    }

    private static string? TryExtractSafeDataDirArg(string[] args)
    {
        string? candidate = null;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                candidate = args[i + 1];
                break;
            }
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                candidate = arg["--data-dir=".Length..];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        try
        {
            var cleaned = candidate.Trim().Trim('"');
            if (!Path.IsPathRooted(cleaned) || cleaned.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) || uri.IsUnc)
                return null;
            if (cleaned.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
                return null;

            var full = Path.GetFullPath(cleaned);
            Directory.CreateDirectory(full);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return null;

            var profilesDir = Path.Combine(full, "profiles");
            var configDir = Path.Combine(full, "config");
            Directory.CreateDirectory(profilesDir);
            Directory.CreateDirectory(configDir);
            if ((File.GetAttributes(profilesDir) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(configDir) & FileAttributes.ReparsePoint) != 0)
                return null;

            return full;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractBackupArchiveArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (string.Equals(raw, "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (raw.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (!AppReleaseHighlights.IsValidBackupArchivePath(raw))
                continue;
            try
            {
                var full = Path.GetFullPath(raw.Trim().Trim('"'));
                if (!File.Exists(full))
                    continue;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var info = new FileInfo(full);
                if (info.Length is > 0 and <= AppReleaseHighlights.MaxArchiveBytes)
                    return full;
            }
            catch
            {
                // Ignore invalid shell paths
            }
        }
        return null;
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
