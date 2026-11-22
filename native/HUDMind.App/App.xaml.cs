using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HUDMind.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Guarantee 0.0% GPU and 0 MB VRAM usage on GTX 1050 Ti / gaming GPUs!
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        bool smokeMode = e.Args.Any(a => string.Equals(a, "--smoke-startup", StringComparison.OrdinalIgnoreCase));
        if (!smokeMode)
        {
            _singleInstanceMutex = new Mutex(true, "HUDMind_Native_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "HUDMind Native ya está abierto en segundo plano.",
                    "HUDMind",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        // NOTE: Keep process PriorityClass Normal so Win32 audio/mic/hotkey threads never stutter in CPU-bound games!
        base.OnStartup(e);

        try
        {
            var win = new MainWindow(smokeMode);
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
            throw;
        }
    }

    public void ReleaseSingleInstanceMutex()
    {
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
