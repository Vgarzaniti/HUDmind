using System.Net;
using System.Net.Sockets;
using HUDMind.Core.Config;
using HUDMind.Core.Models;
using HUDMind.Core.Overlay;
using HUDMind.Core.Security;
using Windows.Media.Ocr;

namespace HUDMind.Windows;

/// <summary>
/// Read-only non-invasive health probe for Windows 10/11 desktop readiness:
/// checks GPU SoftwareOnly mode, sealed Vault integrity, monitor enumeration, WinRT OCR,
/// WinMM microphone/speaker devices, and loopback ports without mutating state or reading game memory.
/// </summary>
public static class SystemDiagnosticsProbe
{
    public static async Task<List<DiagnosticCheckItem>> RunChecksAsync(
        UserConfig config,
        ConfigRepository repo,
        string renderModeLabel,
        bool isCompanionActive,
        bool isGsiActive,
        bool probeMicSample = false)
    {
        var items = new List<DiagnosticCheckItem>(8);

        bool gpuZero = renderModeLabel.Contains("SoftwareOnly", StringComparison.OrdinalIgnoreCase);
        items.Add(new DiagnosticCheckItem(
            "Renderizado UI 0% GPU / Overlay",
            gpuZero,
            $"{renderModeLabel} (0% VRAM en GPU de juegos)"));

        bool vaultOk = ProfileVaultReader.IsVaultIntegrityHealthy;
        int sealedCount = ProfileVaultReader.GetDefaultProfileEntries().Count;
        items.Add(new DiagnosticCheckItem(
            "Bóveda cifrada (HUDMind.Vault.bin)",
            vaultOk && sealedCount > 0,
            vaultOk ? $"AES-256-GCM íntegra · {sealedCount} recursos sellados" : "Integridad de bóveda degradada o incompleta"));

        var screens = Win32DisplayHelper.GetAllWorkingAreas();
        var primary = screens.Length > 0 ? screens[0] : new MonitorBoundsRect(0, 0, 1920, 1080);
        items.Add(new DiagnosticCheckItem(
            "Captura GDI pasiva y Monitores",
            screens.Length > 0,
            $"{screens.Length} monitor(es) detectado(s) · Principal {primary.Width}x{primary.Height}px"));

        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages;
            var picked = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (langs.Count > 0 ? OcrEngine.TryCreateFromLanguage(langs[0]) : null);
            items.Add(new DiagnosticCheckItem(
                "Windows WinRT OCR (CPU local)",
                picked is not null,
                picked is not null
                    ? $"Disponible ({picked.RecognizerLanguage.LanguageTag}, {langs.Count} idioma(s))"
                    : "Sin paquetes OCR instalados en Windows (opcional para modelos sin visión)"));
        }
        catch (Exception ex)
        {
            items.Add(new DiagnosticCheckItem("Windows WinRT OCR (CPU local)", false, $"No disponible: {ex.Message}"));
        }

        var inputs = Win32AudioDevices.GetInputDeviceNames();
        var outputs = Win32AudioDevices.GetOutputDeviceNames();
        string micDetail = $"{inputs.Length} entrada(s) · {outputs.Length} salida(s)";
        bool micOk = inputs.Length > 0 && outputs.Length > 0;
        if (probeMicSample && inputs.Length > 0)
        {
            var res = await AudioDiagnosticsService.ProbeMicrophoneAsync(config.AudioInputDeviceName, durationMs: 450).ConfigureAwait(false);
            micOk = res.Opened && res.ChunksCaptured > 0 && outputs.Length > 0;
            micDetail = $"{micDetail} · {res.ChunksCaptured} tramas · Pico RMS {res.PeakRms:F0}";
        }
        items.Add(new DiagnosticCheckItem("Audio y Micrófono (WinMM)", micOk, micDetail));

        int companionPort = Math.Clamp(config.CompanionHudPort <= 0 ? 17890 : config.CompanionHudPort, 1024, 65535);
        int wsPort = Math.Clamp(
            config.ExternalTelemetryWsPort <= 0 ? InGameOverlayBridgeProtocol.DefaultTelemetryWsPort : config.ExternalTelemetryWsPort,
            1024,
            65535);
        items.Add(CheckLoopbackPort("Puerto Companion HUD", companionPort, activeInProcess: isCompanionActive));
        items.Add(CheckLoopbackPort("Puerto WS Telemetría Local", wsPort, activeInProcess: false));
        items.Add(CheckLoopbackPort("Puerto Valve GSI (CS2 / Dota 2)", 3000, activeInProcess: isGsiActive));

        bool workspaceOk = Directory.Exists(repo.WorkspaceRoot);
        items.Add(new DiagnosticCheckItem(
            "Workspace Personal DPAPI",
            workspaceOk,
            $"{repo.WorkspaceRoot} (claves protegidas por Windows CurrentUser DPAPI)"));

        return items;
    }

    private static DiagnosticCheckItem CheckLoopbackPort(string label, int port, bool activeInProcess)
    {
        if (activeInProcess)
            return new DiagnosticCheckItem(label, true, $"127.0.0.1:{port} activo en esta sesión");

        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return new DiagnosticCheckItem(label, true, $"127.0.0.1:{port} libre y listo");
        }
        catch (SocketException ex)
        {
            return new DiagnosticCheckItem(label, false, $"127.0.0.1:{port} ocupado ({ex.SocketErrorCode})");
        }
    }
}
