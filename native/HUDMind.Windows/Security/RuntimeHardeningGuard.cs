using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace HUDMind.Windows.Security;

/// <summary>
/// Documented Win32 runtime integrity guard (`IsDebuggerPresent` / `CheckRemoteDebuggerPresent`)
/// scoped strictly to single-file production launches so reverse-engineering debuggers cannot
/// unlock encrypted profile/prompt vault secrets in memory while leaving unit tests, IDE debug
/// sessions, and <c>--smoke-startup</c> completely unaffected.
/// </summary>
public static class RuntimeHardeningGuard
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Intentional runtime check for single-file packaged execution.")]
    public static bool IsPackagedSingleFileRuntime() =>
        string.IsNullOrEmpty(typeof(RuntimeHardeningGuard).Assembly.Location);

    public static bool IsExternalDebuggerAttached(bool isSmokeMode = false)
    {
        if (isSmokeMode || !IsPackagedSingleFileRuntime())
            return false;

        try
        {
            if (IsDebuggerPresent())
                return true;

            if (CheckRemoteDebuggerPresent(GetCurrentProcess(), out bool remote) && remote)
                return true;
        }
        catch
        {
            // Never disrupt normal user execution on probe failure
        }
        return false;
    }
}
