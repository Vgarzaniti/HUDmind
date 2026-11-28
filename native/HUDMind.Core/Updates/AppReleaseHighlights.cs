namespace HUDMind.Core.Updates;

/// <summary>
/// Cross-platform release changelog highlights, upgrade detection, and backup archive CLI argument validation.
/// Zero UI dependencies so all checks remain testable in Linux CI.
/// </summary>
public static class AppReleaseHighlights
{
    public const long MaxArchiveBytes = 10 * 1024 * 1024;

    public static bool ShouldShowWhatsNew(string? lastSeenVersion, Version currentVersion)
    {
        if (currentVersion is null || string.IsNullOrWhiteSpace(lastSeenVersion))
            return false;

        if (!Version.TryParse(lastSeenVersion.Trim().TrimStart('v', 'V'), out var prev))
            return false;

        var normPrev = Normalize(prev);
        var normCurr = Normalize(currentVersion);
        return normPrev > new Version(0, 0, 0) && normCurr > normPrev;
    }

    public static (string Title, string Body) GetReleaseNotes(Version currentVersion, bool isEnglish)
    {
        var verText = GitHubReleaseUpdateService.FormatVersion(currentVersion);
        if (isEnglish)
        {
            return (
                $"✨ What's New in HUDMind v{verText}",
                "• 🎯 Continuous High-Signal Watch (Silence-First Default): HUDMind observes passively in background and speaks ONLY when a genuine mistake, danger, or key timing needs fixing—never filling silence by timer.\n" +
                "• ⚡ True In-Place Auto-Update (Zero File Dialogs): updates download directly where HUDMind already runs (`SHA-256` verified) and restart cleanly in place.\n" +
                "• 📦 Double-Click & Drag-Drop `.hudmind`: open or drop encrypted profile backups anywhere in HUDMind to restore settings & memory safely.\n" +
                "• 🌐 Complete Live ES/EN Interface & WinGet Ready: every tab, selector, modal dialog, Active Banner badge, and in-game HUD overlay switches cleanly in real time, plus official `winget` manifests."
            );
        }

        return (
            $"✨ Qué hay de nuevo en HUDMind v{verText}",
            "• 🎯 Escucha Continua · Solo Cuando Hace Falta (Por Defecto): HUDMind observa pasivamente en segundo plano y habla ÚNICAMENTE ante un error real, peligro o ventana clara—sin tirar tips por reloj ni relleno.\n" +
            "• ⚡ Actualización Directa en la Misma Carpeta (Sin Abrir Explorador): baja directo donde ya está HUDMind con verificación `SHA-256` y reinicia listo.\n" +
            "• 📦 Doble clic y Arrastrar `.hudmind`: abrí o soltá tu backup cifrado desde cualquier carpeta de Windows para restaurar perfil, reglas y memoria al instante.\n" +
            "• 🌐 Interfaz 100% Bilingüe ES/EN en Vivo y WinGet: todas las pestañas, listas, diálogos, tarjeta de perfil y overlay cambian al instante sin reiniciar, con manifiestos listos para `winget`."
        );
    }

    public static bool IsValidBackupArchivePath(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        string trimmed = candidatePath.Trim().Trim('"');
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.Contains('\0'))
        {
            return false;
        }

        return trimmed.EndsWith(".hudmind", StringComparison.OrdinalIgnoreCase);
    }

    private static Version Normalize(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
