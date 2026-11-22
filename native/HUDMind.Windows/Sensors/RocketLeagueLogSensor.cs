using System.IO;
using HUDMind.Core.Models;

namespace HUDMind.Windows;

public sealed class RocketLeagueLogSensor : TailLogSensorBase
{
    public override string SensorId => "rocket_league_log";
    protected override double MaxAgeMinutes => 120.0;
    protected override string LogPrefix => "RL_LOG";

    protected override IReadOnlyList<(string Marker, string TagMessage)> Rules { get; } =
    [
        ("Freeplay", "Freeplay activo"),
        ("GoalScored", "Gol anotado reciente"),
        ("MatchEnded", "Partido finalizado"),
        ("Menu_Main", "En menú principal"),
        ("LoadMap:", "{tail}")
    ];

    public override bool Supports(GameProfile profile) =>
        profile.Manifest.GameId.Equals("rocket_league", StringComparison.OrdinalIgnoreCase) ||
        profile.MatchesProcess("RocketLeague.exe");

    protected override string ResolveLogPath(GameProfile profile)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "My Games", "Rocket League", "TAGame", "Logs", "Launch.log");
    }
}
