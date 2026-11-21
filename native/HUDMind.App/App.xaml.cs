using System.Diagnostics;
using System.Windows;

namespace HUDMind.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
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
