using System.Buffers;
using System.IO;
using System.Text;
using HUDMind.Core.AI;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;

namespace HUDMind.Windows;

/// <summary>
/// Shared Anti-Cheat safe tail log sensor base: validates ReparsePoint/age cutoffs, handles UE3 UTF-16 LE &amp; UTF-8 logs,
/// and evaluates declarative marker rules without loading entire log files into memory.
/// </summary>
public abstract class TailLogSensorBase : IGameTelemetrySensor
{
    public abstract string SensorId { get; }
    protected abstract double MaxAgeMinutes { get; }
    protected abstract string LogPrefix { get; }
    protected abstract IReadOnlyList<(string Marker, string TagMessage)> Rules { get; }

    public abstract bool Supports(GameProfile profile);
    protected abstract string ResolveLogPath(GameProfile profile);

    public Task<string> ReadAsync(GameProfile profile, CancellationToken ct = default) =>
        Task.Run(() => ReadLatestRuleMatch(profile), ct);

    private string ReadLatestRuleMatch(GameProfile profile)
    {
        try
        {
            var path = ResolveLogPath(profile);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            var tail = ReadTailTextSafe(path, MaxAgeMinutes);
            if (string.IsNullOrEmpty(tail))
                return string.Empty;

            var lines = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = CoachingPersonaBuilder.SanitizeField(lines[i], 120);
                foreach (var (marker, tagMessage) in Rules)
                {
                    if (!line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var resolved = tagMessage.Contains("{tail}", StringComparison.Ordinal)
                        ? tagMessage.Replace("{tail}", line[^Math.Min(60, line.Length)..], StringComparison.Ordinal)
                        : tagMessage;
                    return $"{LogPrefix}: {resolved}";
                }
            }
        }
        catch { /* Resilient best-effort log inspection */ }
        return string.Empty;
    }

    internal static string ReadTailTextSafe(string path, double maxAgeMinutes)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return string.Empty;

        // Generous cutoff because NTFS defers LastWriteTime updates while games hold logs open
        if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalMinutes > maxAgeMinutes)
            return string.Empty;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - 4096);
        start &= ~1L; // Preserve even alignment for UTF-16 LE files
        fs.Seek(start, SeekOrigin.Begin);
        int toRead = (int)Math.Min(4096, fs.Length - start);
        if (toRead <= 0) return string.Empty;

        byte[] buf = ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            int read = fs.Read(buf, 0, toRead);
            if (read <= 0) return string.Empty;

            bool looksUtf16Le = (read >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) ||
                                (read >= 6 && buf[1] == 0 && buf[3] == 0 && buf[5] == 0);
            return looksUtf16Le
                ? Encoding.Unicode.GetString(buf, 0, read & ~1)
                : Encoding.UTF8.GetString(buf, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
