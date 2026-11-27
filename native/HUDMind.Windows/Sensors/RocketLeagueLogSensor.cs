using System.IO;
using HUDMind.Core.Models;

namespace HUDMind.Windows;

public sealed class RocketLeagueLogSensor : TailLogSensorBase
{
    private readonly string? _overrideLogPath;

    public RocketLeagueLogSensor(string? overrideLogPath = null)
    {
        _overrideLogPath = overrideLogPath;
    }

    public override string SensorId => "rocket_league_log";
    protected override double MaxAgeMinutes => 120.0;
    protected override string LogPrefix => "RL_LOG";

    protected override IReadOnlyList<(string Marker, string TagMessage)> Rules { get; } =
    [
        ("GoalScored", "Gol anotado reciente"),
        ("MatchEnded", "Partido finalizado"),
        ("Menu_Main", "En menú principal"),
        ("GameTags=Training", "Entrenamiento activo"),
        ("GameTags=Freeplay", "Freeplay activo"),
        ("GameEvent_Soccar_TA", "Partido Soccar activo"),
        ("GameInfo_Soccar_TA", "Partido Soccar activo"),
        ("LoadMap:", "Cargando mapa")
    ];

    public override bool Supports(GameProfile profile) =>
        profile.Manifest.GameId.Equals("rocket_league", StringComparison.OrdinalIgnoreCase) ||
        profile.MatchesProcess("RocketLeague.exe");

    protected override string ResolveLogPath(GameProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(_overrideLogPath))
            return _overrideLogPath;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "My Games", "Rocket League", "TAGame", "Logs", "Launch.log");
    }
}
