using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Windows;

/// <summary>
/// Pluggable Valve Game State Integration (GSI) sensor bridging CS2 &amp; Dota 2 snapshots in ~0ms.
/// </summary>
public sealed class ValveGsiTelemetrySensor(LoopbackGsiService gsiService) : IGameTelemetrySensor
{
    public string SensorId => "valve_gsi_loopback";

    public bool Supports(GameProfile profile)
    {
        var gid = profile.Manifest.GameId.ToLowerInvariant();
        return gid.Contains("cs2") || profile.MatchesProcess("cs2.exe") ||
               gid.Contains("dota") || profile.MatchesProcess("dota2.exe");
    }

    public Task<string> ReadAsync(GameProfile profile, CancellationToken ct = default)
    {
        var gid = profile.Manifest.GameId.ToLowerInvariant();
        string kind = (gid.Contains("dota") || profile.MatchesProcess("dota2.exe")) ? "dota2" : "cs2";
        return Task.FromResult(gsiService.GetRecentSummary(kind));
    }
}
