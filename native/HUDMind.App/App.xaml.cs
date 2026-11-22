using System.Diagnostics;
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

        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Optional OS priority hint
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
