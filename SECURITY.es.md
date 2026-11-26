> 🌐 **Idioma / Language:** **[🇪🇸 Español](SECURITY.es.md)** | [🇺🇸 English](SECURITY.en.md)

# Política de Seguridad — HUDMind

## Garantía Anti-Cheat y Privacidad
- **100% Pasivo:** HUDMind utiliza captura GDI externa (`GetClientRect`), WinRT OCR local, APIs/logs oficiales de solo lectura (`Rocket League`, `VALORANT`, `League of Legends` Live Client, `CS2` y `Dota 2` Valve GSI) y `RegisterHotKey`. **Nunca** inyecta DLLs ni lee o escribe memoria de procesos de juegos ni instala hooks `WH_KEYBOARD_LL` / `WH_MOUSE_LL`.
- **Protección de credenciales:** las API keys del usuario viven localmente en `%LocalAppData%\HUDMind\config\` (o `config/` en modo repo) cifradas en disco por usuario de Windows mediante **DPAPI (`Win32DpapiSecretProtector` / `crypt32.dll`, prefijo `dpapi:`)** y se redactan automáticamente en logs, backups y errores (`SecretRedactor`).
- **Prevención SSRF & Path Traversal:** `BaseUrlSecurityValidator` exige HTTPS remoto (HTTP solo en loopback para Ollama local) y bloquea rangos privados/CGNAT/metadata; `ConfigRepository` valida IDs y bloquea symlinks (`ReparsePoint`) y `..`.
- **Actualizaciones verificadas:** el verificador integrado solo consulta `https://api.github.com/repos/Vgarzaniti/HUDmind/releases/latest`, bloquea redirecciones fuera de dominios oficiales de GitHub (`github.com` / `.githubusercontent.com`) y valida firmas `SHA-256` contra `SHA256SUMS.txt`.
- **Protección de activos y binarios:** los perfiles incluidos y reglas de seguridad se sellan en tiempo de compilación en un contenedor autenticado (`HUDMind.Vault.bin`, Brotli + AES-256-GCM vía `HUDMind.Packer`), los ejecutables de release compilan a `ReadyToRun` con símbolos/soporte de depuración removidos y protegen la carga en single-file mediante `RuntimeHardeningGuard`.

## Reportar una Vulnerabilidad
Si encontrás un fallo de seguridad (SSRF, fuga de API keys, validación de actualizaciones o ejecución insegura), por favor **no abras un issue público con el exploit**. Reportalo de forma privada a través de **GitHub Security Advisories** en el repositorio (`Security → Advisories → Report a vulnerability`).
