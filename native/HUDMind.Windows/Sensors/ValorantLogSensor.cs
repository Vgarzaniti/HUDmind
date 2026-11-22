using System.IO;
using HUDMind.Core.Models;

namespace HUDMind.Windows;

public sealed class ValorantLogSensor : TailLogSensorBase
{
    public override string SensorId => "valorant_log";
    protected override double MaxAgeMinutes => 120.0;
    protected override string LogPrefix => "VAL_LOG";

    protected override IReadOnlyList<(string Marker, string TagMessage)> Rules { get; } =
    [
        ("MENU", "Menu / Lobby"),
        ("PREGAME", "Selección de agentes"),
        ("INGAME", "Partida en curso")
    ];

    public override bool Supports(GameProfile profile) =>
        profile.Manifest.GameId.Contains("valorant", StringComparison.OrdinalIgnoreCase) ||
        profile.MatchesProcess("VALORANT-Win64-Shipping.exe");

    protected override string ResolveLogPath(GameProfile profile)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApp, "VALORANT", "Saved", "Logs", "ShooterGame.log");
    }
}
