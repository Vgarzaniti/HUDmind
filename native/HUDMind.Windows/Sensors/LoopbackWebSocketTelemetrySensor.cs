using System.IO;
using System.Net.WebSockets;
using System.Text;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Overlay;
using HUDMind.Core.Pipeline;

namespace HUDMind.Windows;

/// <summary>
/// Passive read-only loopback WebSocket telemetry sensor (ws://127.0.0.1:49122) for local broadcast/relay
/// streams when present; fails silently in background with backoff when absent so overhead stays 0ms.
/// </summary>
public sealed class LoopbackWebSocketTelemetrySensor : IGameTelemetrySensor, IDisposable
{
    private readonly int _port;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private string _latestSummary = string.Empty;
    private long _latestTick;
    private long _nextConnectAttemptTick;
    private bool _disposed;

    public string SensorId => "loopback_ws_telemetry";

    public LoopbackWebSocketTelemetrySensor(int port = InGameOverlayBridgeProtocol.DefaultTelemetryWsPort)
    {
        _port = Math.Clamp(port, 1024, 65535);
    }

    public bool Supports(GameProfile profile) =>
        profile.Manifest.GameId.Equals("rocket_league", StringComparison.OrdinalIgnoreCase) ||
        profile.MatchesProcess("RocketLeague.exe");

    public Task<string> ReadAsync(GameProfile profile, CancellationToken ct = default)
    {
        if (_disposed || !Supports(profile))
            return Task.FromResult(string.Empty);

        EnsureBackgroundListenerStarted();

        lock (_sync)
        {
            if (_latestTick > 0 &&
                System.Diagnostics.Stopwatch.GetElapsedTime(_latestTick).TotalSeconds <= 4.0)
            {
                return Task.FromResult(_latestSummary);
            }
        }
        return Task.FromResult(string.Empty);
    }

    private void EnsureBackgroundListenerStarted()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_listenerTask is { IsCompleted: false })
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_nextConnectAttemptTick > 0 && now < _nextConnectAttemptTick)
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _listenerTask = Task.Run(() => ListenLoopAsync(token), token);
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connCts.CancelAfter(TimeSpan.FromMilliseconds(900));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/"), connCts.Token).ConfigureAwait(false);

            var buf = new byte[16384];
            var msg = new MemoryStream(16384);
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                msg.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close)
                        return;
                    msg.Write(buf, 0, res.Count);
                    if (msg.Length > 262144)
                        return;
                }
                while (!res.EndOfMessage);

                if (res.MessageType == WebSocketMessageType.Text &&
                    ExternalTelemetryParser.TryParseStateEventJson(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length), out var summary))
                {
                    lock (_sync)
                    {
                        _latestSummary = summary;
                        _latestTick = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
            }
        }
        catch { /* Graceful no-op when local telemetry relay is not running */ }
        finally
        {
            lock (_sync)
            {
                _nextConnectAttemptTick = System.Diagnostics.Stopwatch.GetTimestamp() +
                                          System.Diagnostics.Stopwatch.Frequency * 12;
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch { }
        try { _listenerTask?.Wait(TimeSpan.FromMilliseconds(400)); } catch { }
        cts?.Dispose();
    }
}
