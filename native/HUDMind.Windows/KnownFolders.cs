using System.Runtime.InteropServices;

namespace HUDMind.Windows;

/// <summary>
/// Resolves Windows Shell known folders (such as Downloads) cleanly even when redirected or localized.
/// </summary>
public static class KnownFolders
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void TrySetAppUserModelId(string appId = "HUDMind.Native.Desktop")
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(appId))
                _ = SetCurrentProcessExplicitAppUserModelID(appId);
        }
        catch
        {
            // Non-fatal in headless or restricted environments
        }
    }

    public static string GetDownloadsDirectory()
    {
        IntPtr pPath = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out pPath) == 0 && pPath != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(pPath);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    return path;
            }
        }
        catch
        {
            // Fall back to standard profile Downloads directory
        }
        finally
        {
            if (pPath != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pPath);
        }

        var profileDownloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (Directory.Exists(profileDownloads))
            return profileDownloads;

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
}
