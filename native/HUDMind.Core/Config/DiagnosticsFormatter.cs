using System.Runtime.InteropServices;
using System.Text;
using HUDMind.Core.Security;

namespace HUDMind.Core.Config;

public sealed record DiagnosticCheckItem(string Name, bool Passed, string Detail);

/// <summary>
/// Cross-platform formatter for secret-redacted crash logs and system health reports.
/// Ensures no API keys, bearer tokens, or private passphrases ever leak into crash files or clipboard copies.
/// </summary>
public static class DiagnosticsFormatter
{
    public static string FormatCrashReport(
        Exception? exception,
        string appVersion,
        string source,
        IEnumerable<string>? extraSecrets = null,
        DateTimeOffset? timestampUtc = null)
    {
        var now = timestampUtc ?? DateTimeOffset.UtcNow;
        var secrets = extraSecrets?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];
        var rawException = exception?.ToString() ?? "(No exception instance attached)";
        var redactedException = SecretRedactor.Redact(rawException, secrets);
        var safeSource = SecretRedactor.Redact(string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim(), secrets);
        var safeVersion = string.IsNullOrWhiteSpace(appVersion) ? "0.0.0" : appVersion.Trim();

        var sb = new StringBuilder();
        sb.AppendLine("=== HUDMind Crash & Diagnostics Report ===");
        sb.AppendLine($"TimestampUTC: {now:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"AppVersion: {safeVersion}");
        sb.AppendLine($"Source: {safeSource}");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine("------------------------------------------");
        sb.AppendLine(redactedException);
        return sb.ToString();
    }

    public static string FormatHealthReport(
        string appVersion,
        IReadOnlyList<DiagnosticCheckItem> checks,
        IEnumerable<string>? extraSecrets = null,
        DateTimeOffset? timestampUtc = null)
    {
        var now = timestampUtc ?? DateTimeOffset.UtcNow;
        var secrets = extraSecrets?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];
        var sb = new StringBuilder();
        sb.AppendLine("=== HUDMind System Health Check ===");
        sb.AppendLine($"TimestampUTC: {now:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"Version: {(string.IsNullOrWhiteSpace(appVersion) ? "0.0.0" : appVersion.Trim())}");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription} | OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine("-----------------------------------");
        foreach (var c in checks)
        {
            var icon = c.Passed ? "OK" : "WARN";
            var safeDetail = SecretRedactor.Redact(c.Detail ?? string.Empty, secrets);
            sb.AppendLine($"[{icon}] {c.Name}: {safeDetail}");
        }
        return sb.ToString();
    }
}
