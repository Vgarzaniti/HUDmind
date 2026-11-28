using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace HUDMind.Windows;

/// <summary>
/// Launches a hidden, CWE-78-safe Win32 PowerShell worker that waits for the current
/// portable HUDMind process to exit, atomically swaps the staged binary into the exact
/// original .exe path, and restarts HUDMind in place with zero Explorer prompts.
/// </summary>
public static class InPlaceUpdaterLauncher
{
    private const string PortableSwapScript =
        "$ErrorActionPreference = 'Stop'; " +
        "$p = [int]$env:HUDMIND_UPD_PID; " +
        "for ($i = 0; $i -lt 120; $i++) { " +
        "  if (-not (Get-Process -Id $p -ErrorAction SilentlyContinue)) { break }; " +
        "  Start-Sleep -Milliseconds 150 " +
        "}; " +
        "Start-Sleep -Milliseconds 250; " +
        "$ok = $false; " +
        "for ($r = 0; $r -lt 25; $r++) { " +
        "  try { " +
        "    Move-Item -LiteralPath $env:HUDMIND_UPD_SRC -Destination $env:HUDMIND_UPD_DST -Force; " +
        "    $ok = $true; " +
        "    break " +
        "  } catch { Start-Sleep -Milliseconds 200 } " +
        "}; " +
        "if ($ok) { Start-Process -FilePath $env:HUDMIND_UPD_DST -WorkingDirectory $env:HUDMIND_UPD_DIR }";

    public static bool IsSafeLocalExePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        try
        {
            var trimmed = rawPath.Trim().Trim('"');
            if (!Path.IsPathRooted(trimmed) || trimmed.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.IsUnc)
                return false;
            if (trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
                return false;

            var full = Path.GetFullPath(trimmed);
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return false;

            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) &&
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryBuildPortableSwapStartInfo(
        int currentProcessId,
        string stagedExePath,
        string targetExePath,
        out ProcessStartInfo? startInfo,
        out string error)
    {
        startInfo = null;
        error = string.Empty;

        if (currentProcessId <= 0)
        {
            error = "Invalid process identifier.";
            return false;
        }

        if (!IsSafeLocalExePath(stagedExePath) || !IsSafeLocalExePath(targetExePath))
        {
            error = "Updater blocked an unsafe or missing executable path.";
            return false;
        }

        var fullStaged = Path.GetFullPath(stagedExePath);
        var fullTarget = Path.GetFullPath(targetExePath);
        if (string.Equals(fullStaged, fullTarget, StringComparison.OrdinalIgnoreCase))
        {
            error = "Staged executable cannot equal active target executable.";
            return false;
        }

        var sysPs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var psExe = File.Exists(sysPs) ? sysPs : "powershell.exe";

        var psi = new ProcessStartInfo(psExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(PortableSwapScript);

        psi.Environment["HUDMIND_UPD_PID"] = currentProcessId.ToString(CultureInfo.InvariantCulture);
        psi.Environment["HUDMIND_UPD_SRC"] = fullStaged;
        psi.Environment["HUDMIND_UPD_DST"] = fullTarget;
        psi.Environment["HUDMIND_UPD_DIR"] = Path.GetDirectoryName(fullTarget) ?? Environment.CurrentDirectory;

        startInfo = psi;
        return true;
    }

    public static bool TryLaunchPortableSwap(
        int currentProcessId,
        string stagedExePath,
        string targetExePath,
        out string error)
    {
        if (!TryBuildPortableSwapStartInfo(currentProcessId, stagedExePath, targetExePath, out var psi, out error) || psi is null)
            return false;

        try
        {
            return Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
