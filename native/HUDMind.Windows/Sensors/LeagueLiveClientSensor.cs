using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Windows;

/// <summary>
/// Read-only Riot Live Client Data API sensor (https://127.0.0.1:2999) with strict X.509/SAN/process pinning.
/// </summary>
public sealed class LeagueLiveClientSensor : IGameTelemetrySensor
{
    private static readonly HttpClient LocalApiHttp = CreateLoopbackApiClient();

    public string SensorId => "league_live_client_api";

    public bool Supports(GameProfile profile)
    {
        var gid = profile.Manifest.GameId.Trim().ToLowerInvariant();
        return gid is "league_of_legends" or "league" or "lol" ||
               gid.StartsWith("league_of_legends_", StringComparison.Ordinal) ||
               profile.MatchesProcess("League of Legends.exe");
    }

    public async Task<string> ReadAsync(GameProfile profile, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(260));

            using var respAll = await LocalApiHttp
                .GetAsync("https://127.0.0.1:2999/liveclientdata/allgamedata", timeoutCts.Token)
                .ConfigureAwait(false);
            if (respAll.IsSuccessStatusCode)
            {
                await using var sAll = await respAll.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                using var docAll = await JsonDocument.ParseAsync(sAll, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
                var parsed = ParseAllGameData(docAll.RootElement);
                if (!string.IsNullOrEmpty(parsed))
                    return parsed;
            }
        }
        catch
        {
            // Fall through to compact activeplayer + gamestats endpoints
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(260));

            var tPlayer = LocalApiHttp.GetAsync("https://127.0.0.1:2999/liveclientdata/activeplayer", timeoutCts.Token);
            var tStats = LocalApiHttp.GetAsync("https://127.0.0.1:2999/liveclientdata/gamestats", timeoutCts.Token);
            await Task.WhenAll(tPlayer, tStats).ConfigureAwait(false);

            using var respPlayer = await tPlayer.ConfigureAwait(false);
            using var respStats = await tStats.ConfigureAwait(false);
            if (!respPlayer.IsSuccessStatusCode || !respStats.IsSuccessStatusCode)
                return string.Empty;

            await using var sPlayer = await respPlayer.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using var sStats = await respStats.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var docPlayer = await JsonDocument.ParseAsync(sPlayer, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            using var docStats = await JsonDocument.ParseAsync(sStats, cancellationToken: timeoutCts.Token).ConfigureAwait(false);

            double gameTime = docStats.RootElement.TryGetProperty("gameTime", out var gt) ? gt.GetDouble() : 0;
            return FormatActiveSummary(docPlayer.RootElement, gameTime, cs: -1, wards: -1, isDead: false);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string ParseAllGameData(JsonElement root)
    {
        if (!root.TryGetProperty("activePlayer", out var ap))
            return string.Empty;

        double gameTime = 0;
        if (root.TryGetProperty("gameData", out var gd) && gd.TryGetProperty("gameTime", out var gt))
            gameTime = gt.GetDouble();

        string? riotId = ap.TryGetProperty("riotId", out var rid) ? rid.GetString() : null;
        string? summoner = ap.TryGetProperty("summonerName", out var sn) ? sn.GetString() : null;

        int cs = -1, wards = -1;
        bool isDead = false;
        if (root.TryGetProperty("allPlayers", out var players) && players.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in players.EnumerateArray())
            {
                string? pRiot = p.TryGetProperty("riotId", out var pr) ? pr.GetString() : null;
                string? pName = p.TryGetProperty("summonerName", out var ps) ? ps.GetString() : null;
                bool matched = (!string.IsNullOrWhiteSpace(riotId) && string.Equals(riotId, pRiot, StringComparison.OrdinalIgnoreCase)) ||
                               (!string.IsNullOrWhiteSpace(summoner) && string.Equals(summoner, pName, StringComparison.OrdinalIgnoreCase));
                if (!matched) continue;

                isDead = p.TryGetProperty("isDead", out var dd) && dd.ValueKind == JsonValueKind.True;
                if (p.TryGetProperty("scores", out var sc))
                {
                    if (sc.TryGetProperty("creepScore", out var cEl)) cs = cEl.GetInt32();
                    if (sc.TryGetProperty("wardScore", out var wEl)) wards = (int)Math.Round(wEl.GetDouble());
                }
                break;
            }
        }

        return FormatActiveSummary(ap, gameTime, cs, wards, isDead);
    }

    private static string FormatActiveSummary(JsonElement activePlayer, double gameTime, int cs, int wards, bool isDead)
    {
        int lvl = activePlayer.TryGetProperty("level", out var lv) ? lv.GetInt32() : 1;
        double gold = activePlayer.TryGetProperty("currentGold", out var cg) ? cg.GetDouble() : 0;

        double hp = 1, maxHp = 1;
        if (activePlayer.TryGetProperty("championStats", out var st))
        {
            if (st.TryGetProperty("currentHealth", out var ch)) hp = ch.GetDouble();
            if (st.TryGetProperty("maxHealth", out var mh)) maxHp = Math.Max(1.0, mh.GetDouble());
        }

        int mins = (int)(gameTime / 60);
        int secs = (int)(gameTime % 60);
        string csTag = cs >= 0 ? $", CS {cs}" : string.Empty;
        string wardTag = wards >= 0 ? $", Wards {wards}" : string.Empty;
        string deadTag = isDead ? ", DEAD" : string.Empty;
        return $"LoL_API: Tiempo {mins:D2}:{secs:D2}, Nivel {lvl}, Oro {gold:F0}, Vida {(hp * 100.0 / maxHp):F0}%{csTag}{wardTag}{deadTag}";
    }

    private static HttpClient CreateLoopbackApiClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            ServerCertificateCustomValidationCallback = ValidateLeagueLoopbackCert
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(350),
            MaxResponseContentBufferSize = 512 * 1024
        };
    }

    internal static bool ValidateLeagueLoopbackCert(
        HttpRequestMessage req,
        X509Certificate2? cert,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (req.RequestUri is not { Scheme: "https", Host: "127.0.0.1", Port: 2999 })
            return false;
        if (errors == SslPolicyErrors.None)
            return true;
        if (cert is null || errors != SslPolicyErrors.RemoteCertificateChainErrors)
            return false;

        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
            return false;

        int keyBits = cert.GetRSAPublicKey()?.KeySize ?? cert.GetECDsaPublicKey()?.KeySize ?? 0;
        bool isEcdsa = cert.GetECDsaPublicKey() is not null;
        if (keyBits < (isEcdsa ? 256 : 2048))
            return false;

        bool sanMatches = false;
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value == "2.5.29.17")
            {
                var sanText = ext.Format(multiLine: false);
                if (sanText.Contains("127.0.0.1", StringComparison.Ordinal) ||
                    sanText.Contains("riotgames", StringComparison.OrdinalIgnoreCase))
                {
                    sanMatches = true;
                    break;
                }
            }
        }
        if (!sanMatches)
            return false;

        bool hasRiotIdentity =
            cert.Subject.Contains("Riot Games", StringComparison.OrdinalIgnoreCase) ||
            cert.Issuer.Contains("Riot Games", StringComparison.OrdinalIgnoreCase);

        if (!hasRiotIdentity)
            return false;

        try
        {
            return Process.GetProcessesByName("League of Legends").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
