using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HUDMind.Core.AI;

namespace HUDMind.Core.Updates;

public sealed record ReleaseAssetInfo(
    string Name,
    string DownloadUrl,
    long SizeBytes,
    string ContentType
);

public sealed record AppUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string ReleaseHtmlUrl,
    DateTimeOffset? PublishedAtUtc,
    ReleaseAssetInfo? PreferredAsset,
    ReleaseAssetInfo? ChecksumAsset
);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    AppUpdateInfo? Update,
    string StatusMessage,
    bool WasNotFound = false,
    bool WasRateLimited = false
);

public readonly record struct UpdateDownloadProgress(
    long BytesReceived,
    long? TotalBytes
);

public sealed record UpdateDownloadResult(
    bool Success,
    string FilePath,
    bool Sha256Verified,
    string? ExpectedSha256,
    string? ActualSha256,
    string Message
);

/// <summary>
/// Checks GitHub Releases for newer HUDMind binaries and downloads release assets
/// through an SSRF-hardened, redirect-validated, SHA-256-verifying pipeline.
/// </summary>
public sealed partial class GitHubReleaseUpdateService
{
    public const string DefaultOwner = "Vgarzaniti";
    public const string DefaultRepo = "HUDmind";

    private const int MaxApiBytes = 512 * 1024;
    private const int MaxChecksumBytes = 64 * 1024;
    private const long MaxAssetBytes = 250L * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly Lazy<HttpClient> SharedDownloadClient = new(() =>
        SharedHttpFactory.CreateSafeClient(allowLoopback: false, timeout: TimeSpan.FromMinutes(10)));

    private static readonly HashSet<string> ExactTrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "codeload.github.com"
    };

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,100}$", RegexOptions.Compiled)]
    private static partial Regex SafeRepoSegmentRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256HexRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownLinkRegex();

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubReleaseUpdateService(string owner = DefaultOwner, string repo = DefaultRepo)
        : this(SharedDownloadClient.Value, owner, repo)
    {
    }

    public GitHubReleaseUpdateService(HttpClient httpClient, string owner = DefaultOwner, string repo = DefaultRepo)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!SafeRepoSegmentRegex().IsMatch(owner))
            throw new ArgumentException("Invalid GitHub repository owner.", nameof(owner));
        if (!SafeRepoSegmentRegex().IsMatch(repo))
            throw new ArgumentException("Invalid GitHub repository name.", nameof(repo));
        _owner = owner;
        _repo = repo;
    }

    public static (Version Version, bool IsPrerelease) GetCurrentAppVersion(Assembly? entryAssembly = null)
    {
        var asm = entryAssembly ?? Assembly.GetEntryAssembly() ?? typeof(GitHubReleaseUpdateService).Assembly;
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (infoAttr is not null && TryParseReleaseVersion(infoAttr.InformationalVersion, out var parsedInfo, out bool isPre))
            return (parsedInfo, isPre);

        var asmVer = asm.GetName().Version ?? new Version(1, 0, 0, 0);
        return (NormalizeVersion(asmVer), false);
    }

    public static bool IsRunningSelfContained(Assembly? entryAssembly = null)
    {
        var asm = entryAssembly ?? Assembly.GetEntryAssembly();
        if (asm is not null)
        {
            var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(m => string.Equals(m.Key, "IsSelfContained", StringComparison.OrdinalIgnoreCase));
            if (meta is not null && bool.TryParse(meta.Value, out bool isSelf))
                return isSelf;
        }

        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(runtimeDir))
            return true;

        return !runtimeDir.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstalledSetupDirectory(string? baseDirectory = null)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "unins000.exe"));
        }
        catch
        {
            return false;
        }
    }

    public static Version NormalizeVersion(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static string FormatVersion(Version v)
    {
        var n = NormalizeVersion(v);
        return n.Revision > 0 ? $"{n.Major}.{n.Minor}.{n.Build}.{n.Revision}" : $"{n.Major}.{n.Minor}.{n.Build}";
    }

    public static bool TryParseReleaseVersion(string? rawVersion, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0, 0);
        isPrerelease = false;
        if (string.IsNullOrWhiteSpace(rawVersion))
            return false;

        var s = rawVersion.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        int plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
            s = s[..plusIdx];

        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            isPrerelease = true;
            s = s[..dashIdx];
        }

        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        Span<int> nums = [0, 0, 0, 0];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int val) || val < 0)
                return false;
            nums[i] = val;
        }

        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    public static bool IsNewerVersion(Version current, bool currentIsPrerelease, Version latest, bool latestIsPrerelease)
    {
        if (latestIsPrerelease)
            return false;

        var curNorm = NormalizeVersion(current);
        var latNorm = NormalizeVersion(latest);
        int cmp = latNorm.CompareTo(curNorm);
        if (cmp > 0)
            return true;
        return cmp == 0 && currentIsPrerelease;
    }

    public static bool IsTrustedGitHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
            return false;

        if (ExactTrustedHosts.Contains(host))
            return true;

        return host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
               !host.StartsWith('.');
    }

    public static (ReleaseAssetInfo? PreferredAsset, ReleaseAssetInfo? ChecksumAsset) SelectPreferredAssets(
        IReadOnlyList<ReleaseAssetInfo> assets,
        bool preferSelfContained,
        bool preferInstaller = false)
    {
        var trusted = assets.Where(a => !string.IsNullOrWhiteSpace(a.Name) && IsTrustedGitHubUrl(a.DownloadUrl)).ToList();
        var exes = trusted.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();

        ReleaseAssetInfo? preferred = null;
        if (exes.Count > 0)
        {
            static bool IsStandaloneName(string n) =>
                n.Contains("standalone", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("self-contained", StringComparison.OrdinalIgnoreCase);

            static bool IsSetupName(string n) =>
                n.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("installer", StringComparison.OrdinalIgnoreCase);

            if (preferInstaller)
            {
                preferred = exes.FirstOrDefault(a => IsSetupName(a.Name) && a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                  ?? exes.FirstOrDefault(a => IsSetupName(a.Name));
            }

            preferred ??= preferSelfContained
                ? exes.FirstOrDefault(a => IsStandaloneName(a.Name) && !IsSetupName(a.Name))
                  ?? exes.FirstOrDefault(a => !IsSetupName(a.Name) && a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                  ?? exes[0]
                : exes.FirstOrDefault(a => !IsStandaloneName(a.Name) && !IsSetupName(a.Name) && a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                  ?? exes.FirstOrDefault(a => !IsStandaloneName(a.Name) && !IsSetupName(a.Name))
                  ?? exes[0];
        }

        var checksum = trusted.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (checksum is null && preferred is not null)
        {
            checksum = trusted.FirstOrDefault(a =>
                a.Name.Equals($"{preferred.Name}.sha256", StringComparison.OrdinalIgnoreCase));
        }

        return (preferred, checksum);
    }

    public static string? ParseSha256FromChecksums(string checksumsText, string targetAssetName)
    {
        if (string.IsNullOrWhiteSpace(checksumsText) || string.IsNullOrWhiteSpace(targetAssetName))
            return null;

        var targetFile = Path.GetFileName(targetAssetName.Trim());
        string? singleHashFallback = null;
        int lineCount = 0;

        foreach (var rawLine in checksumsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            lineCount++;

            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !Sha256HexRegex().IsMatch(tokens[0]))
                continue;

            var hash = tokens[0].ToLowerInvariant();
            if (tokens.Length == 1)
            {
                singleHashFallback ??= hash;
                continue;
            }

            var fileToken = tokens[^1].TrimStart('*');
            if (string.Equals(Path.GetFileName(fileToken), targetFile, StringComparison.OrdinalIgnoreCase))
                return hash;
        }

        return lineCount == 1 ? singleHashFallback : null;
    }

    public static string SummarizeReleaseNotes(string? rawBody, int maxChars = 260)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return string.Empty;

        var lines = new List<string>();
        foreach (var rawLine in rawBody.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var noCtrl = new string(rawLine.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (noCtrl.Length == 0 || noCtrl.All(c => c is '-' or '*' or '_'))
                continue;

            var clean = MarkdownLinkRegex().Replace(noCtrl, "$1")
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .TrimStart('#', '*', '-', ' ')
                .Trim();

            if (clean.Length == 0 ||
                clean.StartsWith("Descargas Windows", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("HUDMind-Setup", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("HUDMind.Native", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("Full Changelog:", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("What's Changed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(clean);
            if (lines.Count >= 3)
                break;
        }

        var joined = string.Join(" · ", lines);
        return joined.Length > maxChars ? joined[..(maxChars - 1)] + "…" : joined;
    }

    public UpdateCheckResult ParseLatestReleaseJson(
        string json,
        Version currentVersion,
        bool currentIsPrerelease = false,
        bool preferSelfContained = false,
        bool preferInstaller = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool isDraft = root.TryGetProperty("draft", out var dEl) && dEl.ValueKind == JsonValueKind.True;
        bool isPrerelease = root.TryGetProperty("prerelease", out var pEl) && pEl.ValueKind == JsonValueKind.True;
        if (isDraft || isPrerelease)
            return new UpdateCheckResult(false, null, "No hay una release estable disponible todavía.");

        var tagName = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? string.Empty).Trim() : string.Empty;
        if (!TryParseReleaseVersion(tagName, out var latestVer, out bool tagPrerelease))
            return new UpdateCheckResult(false, null, $"Versión de release no reconocida: '{tagName}'.");

        if (!IsNewerVersion(currentVersion, currentIsPrerelease, latestVer, tagPrerelease))
            return new UpdateCheckResult(false, null, $"Ya tenés la última versión (v{FormatVersion(currentVersion)}).");

        var htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
        if (!IsTrustedGitHubUrl(htmlUrl))
            htmlUrl = $"https://github.com/{_owner}/{_repo}/releases";

        var releaseName = root.TryGetProperty("name", out var nameEl) && !string.IsNullOrWhiteSpace(nameEl.GetString())
            ? nameEl.GetString()!.Trim()
            : tagName;
        var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var pubEl) &&
            pubEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(pubEl.GetString(), out var dt))
        {
            publishedAt = dt;
        }

        var assets = new List<ReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsEl.EnumerateArray())
            {
                var aName = item.TryGetProperty("name", out var an) ? (an.GetString() ?? string.Empty).Trim() : string.Empty;
                var aUrl = item.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? string.Empty).Trim() : string.Empty;
                long aSize = item.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long sVal) ? sVal : 0;
                var aType = item.TryGetProperty("content_type", out var ct) ? (ct.GetString() ?? string.Empty) : string.Empty;
                if (!string.IsNullOrEmpty(aName) && IsTrustedGitHubUrl(aUrl))
                    assets.Add(new ReleaseAssetInfo(aName, aUrl, aSize, aType));
            }
        }

        var (preferred, checksum) = SelectPreferredAssets(assets, preferSelfContained, preferInstaller);
        var info = new AppUpdateInfo(
            CurrentVersion: NormalizeVersion(currentVersion),
            LatestVersion: latestVer,
            TagName: tagName,
            ReleaseName: releaseName,
            ReleaseNotes: body,
            ReleaseHtmlUrl: htmlUrl!,
            PublishedAtUtc: publishedAt,
            PreferredAsset: preferred,
            ChecksumAsset: checksum);

        return new UpdateCheckResult(true, info, $"Nueva actualización disponible: {tagName}");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        Version? currentVersion = null,
        bool? currentIsPrerelease = null,
        bool? preferSelfContained = null,
        bool? preferInstaller = null,
        CancellationToken ct = default)
    {
        var (curVer, curPre) = currentVersion is null
            ? GetCurrentAppVersion()
            : (NormalizeVersion(currentVersion), currentIsPrerelease ?? false);
        bool selfContained = preferSelfContained ?? IsRunningSelfContained();
        bool useInstaller = preferInstaller ?? IsInstalledSetupDirectory();

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        using var req = CreateGitHubRequest(url, acceptJson: true);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(false, null, "Todavía no hay releases publicadas en GitHub.", WasNotFound: true);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return new UpdateCheckResult(false, null, "Límite temporal de GitHub API alcanzado. Probá en unos minutos.", WasRateLimited: true);

        if (!resp.IsSuccessStatusCode)
            return new UpdateCheckResult(false, null, $"No se pudo consultar actualizaciones (HTTP {(int)resp.StatusCode}).");

        var json = await ReadBoundedStringAsync(resp, MaxApiBytes, timeoutCts.Token);
        return ParseLatestReleaseJson(json, curVer, curPre, selfContained, useInstaller);
    }

    public async Task<UpdateDownloadResult> DownloadAssetAsync(
        ReleaseAssetInfo asset,
        string destinationPath,
        ReleaseAssetInfo? checksumAsset = null,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsTrustedGitHubUrl(asset.DownloadUrl))
            return new UpdateDownloadResult(false, destinationPath, false, null, null, "URL de descarga rechazada por seguridad.");

        var fullDest = Path.GetFullPath(destinationPath);
        var destDir = Path.GetDirectoryName(fullDest);
        if (string.IsNullOrEmpty(destDir))
            return new UpdateDownloadResult(false, destinationPath, false, null, null, "Ruta de destino inválida.");

        Directory.CreateDirectory(destDir);
        var tmpPath = $"{fullDest}.download.{Environment.ProcessId}.tmp";

        try
        {
            using var resp = await SendWithValidatedRedirectsAsync(asset.DownloadUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return new UpdateDownloadResult(false, fullDest, false, null, null, $"Error HTTP {(int)resp.StatusCode} descargando {asset.Name}.");

            long? totalBytes = resp.Content.Headers.ContentLength ?? (asset.SizeBytes > 0 ? asset.SizeBytes : null);
            if (totalBytes is > MaxAssetBytes)
                return new UpdateDownloadResult(false, fullDest, false, null, null, "El archivo supera el tamaño máximo permitido (250 MB).");

            string actualSha256;
            long bytesReadTotal = 0;

            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                progress?.Report(new UpdateDownloadProgress(0, totalBytes));

                while (true)
                {
                    int read = await remoteStream.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;

                    bytesReadTotal += read;
                    if (bytesReadTotal > MaxAssetBytes)
                        throw new InvalidOperationException("La descarga excedió el límite de 250 MB.");

                    hasher.AppendData(buffer, 0, read);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(new UpdateDownloadProgress(bytesReadTotal, totalBytes));
                }

                await fileStream.FlushAsync(ct);
                actualSha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (bytesReadTotal == 0)
                return new UpdateDownloadResult(false, fullDest, false, null, actualSha256, "El archivo descargado está vacío.");

            string? expectedSha256 = null;
            bool shaVerified = false;
            if (checksumAsset is not null && IsTrustedGitHubUrl(checksumAsset.DownloadUrl))
            {
                using var sumResp = await SendWithValidatedRedirectsAsync(checksumAsset.DownloadUrl, ct);
                if (sumResp.IsSuccessStatusCode)
                {
                    var sumText = await ReadBoundedStringAsync(sumResp, MaxChecksumBytes, ct);
                    expectedSha256 = ParseSha256FromChecksums(sumText, asset.Name);
                    if (expectedSha256 is not null)
                    {
                        if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            return new UpdateDownloadResult(
                                false,
                                fullDest,
                                false,
                                expectedSha256,
                                actualSha256,
                                "Error de integridad SHA-256: el archivo descargado no coincide con la firma de la release.");
                        }
                        shaVerified = true;
                    }
                }
            }

            File.Move(tmpPath, fullDest, overwrite: true);
            return new UpdateDownloadResult(
                true,
                fullDest,
                shaVerified,
                expectedSha256,
                actualSha256,
                shaVerified
                    ? $"Descarga completa y SHA-256 verificado: {Path.GetFileName(fullDest)}"
                    : $"Descarga completa: {Path.GetFileName(fullDest)}");
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // Ignore cleanup failure for temporary download file
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(string url, CancellationToken ct)
    {
        var currentUri = new Uri(url, UriKind.Absolute);
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsTrustedGitHubUrl(currentUri.AbsoluteUri))
                throw new InvalidOperationException($"URL bloqueada por seguridad: {currentUri.Host}");

            using var req = CreateGitHubRequest(currentUri.AbsoluteUri, acceptJson: false);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is not null)
            {
                var nextUri = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(currentUri, resp.Headers.Location);
                resp.Dispose();

                if (!IsTrustedGitHubUrl(nextUri.AbsoluteUri))
                    throw new InvalidOperationException($"Redirección bloqueada por seguridad hacia dominio no confiable: {nextUri.Host}");

                currentUri = nextUri;
                continue;
            }

            return resp;
        }

        throw new InvalidOperationException("Demasiadas redirecciones al descargar la actualización.");
    }

    private HttpRequestMessage CreateGitHubRequest(string url, bool acceptJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptJson)
        {
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            req.Headers.UserAgent.ParseAdd("HUDMind-Native/1.3.0");
        return req;
    }

    private static async Task<string> ReadBoundedStringAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is long len && len > maxBytes)
            throw new InvalidOperationException("La respuesta supera el tamaño máximo permitido.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException("La respuesta excedió el límite de lectura.");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
