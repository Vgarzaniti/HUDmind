using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HUDMind.Core.Config;
using HUDMind.Core.Security;

namespace HUDMind.Windows.Security;

/// <summary>
/// Zero-NuGet Windows CurrentUser DPAPI protector backed by native <c>crypt32.dll</c>.
/// Binds stored API keys in <c>accounts.json</c> strictly to the active Windows user profile.
/// </summary>
public sealed class Win32DpapiSecretProtector : ISecretProtector
{
    private const uint CryptprotectUiForbidden = 0x01;
    private const int MaxSecretBytes = 32 * 1024;
    private static readonly byte[] AppEntropy = "HUDMind.Accounts.v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public bool IsSupported => OperatingSystem.IsWindows();

    public string Protect(string plainSecret)
    {
        ArgumentNullException.ThrowIfNull(plainSecret);
        if (plainSecret.Length == 0)
            return string.Empty;
        if (!IsSupported)
            return plainSecret;

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainSecret);
        if (plainBytes.Length > MaxSecretBytes)
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            throw new ArgumentException("Secret exceeds maximum DPAPI payload size.", nameof(plainSecret));
        }

        IntPtr pPlain = IntPtr.Zero;
        IntPtr pEntropy = IntPtr.Zero;
        DATA_BLOB outBlob = default;
        try
        {
            pPlain = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, pPlain, plainBytes.Length);

            pEntropy = Marshal.AllocHGlobal(AppEntropy.Length);
            Marshal.Copy(AppEntropy, 0, pEntropy, AppEntropy.Length);

            var inBlob = new DATA_BLOB { cbData = plainBytes.Length, pbData = pPlain };
            var entropyBlob = new DATA_BLOB { cbData = AppEntropy.Length, pbData = pEntropy };

            if (!CryptProtectData(ref inBlob, "HUDMind", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI CryptProtectData failed.");

            byte[] cipherBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
            return $"{ConfigRepository.ProtectedSecretPrefix}{Convert.ToBase64String(cipherBytes)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            if (pPlain != IntPtr.Zero)
            {
                ZeroAndFreeHGlobal(pPlain, plainBytes.Length);
            }
            if (pEntropy != IntPtr.Zero)
                Marshal.FreeHGlobal(pEntropy);
            if (outBlob.pbData != IntPtr.Zero)
                _ = LocalFree(outBlob.pbData);
        }
    }

    public string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            return string.Empty;
        if (!protectedSecret.StartsWith(ConfigRepository.ProtectedSecretPrefix, StringComparison.Ordinal))
            return protectedSecret;
        if (!IsSupported)
            return protectedSecret;

        string b64 = protectedSecret[ConfigRepository.ProtectedSecretPrefix.Length..];
        byte[] cipherBytes = Convert.FromBase64String(b64);
        if (cipherBytes.Length == 0 || cipherBytes.Length > MaxSecretBytes * 2)
            throw new FormatException("Invalid DPAPI secret length.");

        IntPtr pCipher = IntPtr.Zero;
        IntPtr pEntropy = IntPtr.Zero;
        DATA_BLOB outBlob = default;
        try
        {
            pCipher = Marshal.AllocHGlobal(cipherBytes.Length);
            Marshal.Copy(cipherBytes, 0, pCipher, cipherBytes.Length);

            pEntropy = Marshal.AllocHGlobal(AppEntropy.Length);
            Marshal.Copy(AppEntropy, 0, pEntropy, AppEntropy.Length);

            var inBlob = new DATA_BLOB { cbData = cipherBytes.Length, pbData = pCipher };
            var entropyBlob = new DATA_BLOB { cbData = AppEntropy.Length, pbData = pEntropy };

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outBlob))
                throw new CryptographicException("DPAPI CryptUnprotectData failed for current Windows user.", new Win32Exception(Marshal.GetLastWin32Error()));

            if (outBlob.cbData <= 0 || outBlob.pbData == IntPtr.Zero)
                return string.Empty;

            byte[] plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            if (pCipher != IntPtr.Zero)
                Marshal.FreeHGlobal(pCipher);
            if (pEntropy != IntPtr.Zero)
                Marshal.FreeHGlobal(pEntropy);
            if (outBlob.pbData != IntPtr.Zero)
                ZeroAndFreeLocal(outBlob.pbData, outBlob.cbData);
        }
    }

    private static void ZeroAndFreeHGlobal(IntPtr ptr, int length)
    {
        if (length > 0)
            Marshal.Copy(new byte[length], 0, ptr, length);
        Marshal.FreeHGlobal(ptr);
    }

    private static void ZeroAndFreeLocal(IntPtr ptr, int length)
    {
        if (length > 0)
            Marshal.Copy(new byte[length], 0, ptr, length);
        _ = LocalFree(ptr);
    }
}
