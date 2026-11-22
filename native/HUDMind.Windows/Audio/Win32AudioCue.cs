using System.Runtime.InteropServices;

namespace HUDMind.Windows;

public enum AudioCueKind
{
    SoftOn,
    SoftOff
}

/// <summary>
/// Ultra-lightweight, non-blocking pinned RIFF WAV chime player via Win32 PlaySoundW (0-alloc per play, 0% GPU).
/// Gives subtle audible confirmation when toggling Pause (F9), Mic (F8), or starting Push-to-Talk while fullscreen.
/// </summary>
public static class Win32AudioCue
{
    private const uint SndSync = 0x0000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly byte[] SoftOnWav = BuildToneWav(fStartHz: 620.0, fEndHz: 860.0, durationMs: 55, amplitude: 0.14);
    private static readonly byte[] SoftOffWav = BuildToneWav(fStartHz: 680.0, fEndHz: 460.0, durationMs: 55, amplitude: 0.13);
    private static readonly GCHandle PinOn = GCHandle.Alloc(SoftOnWav, GCHandleType.Pinned);
    private static readonly GCHandle PinOff = GCHandle.Alloc(SoftOffWav, GCHandleType.Pinned);
    private static readonly IntPtr PtrOn = PinOn.AddrOfPinnedObject();
    private static readonly IntPtr PtrOff = PinOff.AddrOfPinnedObject();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    public static void Play(AudioCueKind kind)
    {
        try
        {
            IntPtr ptr = kind == AudioCueKind.SoftOn ? PtrOn : PtrOff;
            if (ptr != IntPtr.Zero)
                _ = PlaySoundW(ptr, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
        catch { /* Best-effort UI chime */ }
    }

    private static byte[] BuildToneWav(double fStartHz, double fEndHz, int durationMs, double amplitude)
    {
        const int sampleRate = 22050;
        int samples = (sampleRate * durationMs) / 1000;
        int dataBytes = samples * 2;
        var wav = new byte[44 + dataBytes];

        // Standard 44-byte RIFF/WAVE PCM16 mono header
        WriteAscii(wav, 0, "RIFF");
        WriteInt32(wav, 4, 36 + dataBytes);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt32(wav, 16, 16);
        WriteInt16(wav, 20, 1); // PCM
        WriteInt16(wav, 22, 1); // Mono
        WriteInt32(wav, 24, sampleRate);
        WriteInt32(wav, 28, sampleRate * 2);
        WriteInt16(wav, 32, 2);
        WriteInt16(wav, 34, 16);
        WriteAscii(wav, 36, "data");
        WriteInt32(wav, 40, dataBytes);

        double phase = 0;
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)samples;
            double freq = fStartHz + (fEndHz - fStartHz) * t;
            // Smooth Hann-like attack/decay envelope prevents speaker pops
            double env = Math.Sin(Math.PI * t);
            phase += (2.0 * Math.PI * freq) / sampleRate;
            short sample = (short)Math.Clamp(Math.Round(Math.Sin(phase) * env * amplitude * short.MaxValue), short.MinValue, short.MaxValue);
            WriteInt16(wav, 44 + i * 2, sample);
        }
        return wav;
    }

    private static void WriteAscii(byte[] b, int off, string s)
    {
        for (int i = 0; i < s.Length; i++) b[off + i] = (byte)s[i];
    }

    private static void WriteInt32(byte[] b, int off, int v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
        b[off + 2] = (byte)((v >> 16) & 0xFF);
        b[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] b, int off, short v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
    }
}
