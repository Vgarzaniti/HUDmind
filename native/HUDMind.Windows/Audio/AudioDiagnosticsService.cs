using HUDMind.Core.Pipeline;

namespace HUDMind.Windows;

public sealed record MicProbeResult(bool Opened, int ChunksCaptured, double PeakRms, string SummaryMessage);

/// <summary>
/// Non-blocking 1-second hardware microphone probe sharing Core DSP utilities.
/// </summary>
public static class AudioDiagnosticsService
{
    public static async Task<MicProbeResult> ProbeMicrophoneAsync(string? deviceName, int durationMs = 1000, CancellationToken ct = default)
    {
        double peakRms = 0;
        int chunks = 0;
        string? errSeen = null;

        using var testMic = new Win32StreamingMicCapture();
        testMic.SetDevice(deviceName);
        bool started = testMic.TryStart(
            chunk =>
            {
                Interlocked.Increment(ref chunks);
                double rms = AudioPcmUtilities.ComputeRms16(chunk);
                double cur = Volatile.Read(ref peakRms);
                if (rms > cur) Volatile.Write(ref peakRms, rms);
            },
            err => errSeen = err);

        if (!started)
        {
            return new MicProbeResult(
                false,
                0,
                0,
                errSeen ?? "No se pudo abrir el micrófono seleccionado.");
        }

        await Task.Delay(Math.Clamp(durationMs, 300, 3000), ct).ConfigureAwait(false);
        testMic.Stop();

        string msg = peakRms >= 220
            ? $"✅ Micrófono funcionando perfecto ({chunks} tramas, pico RMS {peakRms:F0})."
            : $"ℹ️ Micrófono conectado ({chunks} tramas), pero nivel muy bajo (RMS {peakRms:F0}). Probá acercarte o elegir otro dispositivo.";

        return new MicProbeResult(true, chunks, peakRms, msg);
    }
}
