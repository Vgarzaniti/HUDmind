> 🌐 **Idioma / Language:** [🇪🇸 Español](SECURITY.es.md) | **[🇺🇸 English](SECURITY.en.md)**

# Security Policy — HUDMind

## Anti-Cheat & Privacy Guarantee
- **100% Passive Observation:** HUDMind uses external GDI capture (`GetClientRect`), local in-RAM WinRT OCR, read-only official APIs/logs (`Rocket League`, `VALORANT`, `League of Legends` Live Client, `CS2` and `Dota 2` Valve GSI), and `RegisterHotKey`. It **never** injects DLLs, reads/writes game memory, or installs `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks.
- **Credential Protection:** User API keys are stored locally under `%LocalAppData%\HUDMind\config\` (or `config/` in repo mode), sealed at rest to the active Windows user via **CurrentUser DPAPI (`Win32DpapiSecretProtector` / `crypt32.dll`, `dpapi:` prefix)**, and masked automatically in logs, error messages, backups, and match reviews via `SecretRedactor`.
- **SSRF & Path Traversal Prevention:** `BaseUrlSecurityValidator` enforces remote HTTPS (loopback HTTP allowed solely for local Ollama) and blocks private/CGNAT/metadata IPv4/IPv6 ranges; `ConfigRepository` validates profile IDs and blocks `ReparsePoint` symlinks and `..` traversal.
- **Verified Updates:** The built-in updater only queries `https://api.github.com/repos/Vgarzaniti/HUDmind/releases/latest`, blocks redirects outside official GitHub domains (`github.com` / `.githubusercontent.com`), and verifies `SHA-256` against `SHA256SUMS.txt`.
- **Binary & Asset Hardening:** Bundled profiles and guarded prompt templates are sealed at build time inside an authenticated Brotli + AES-256-GCM binary container (`HUDMind.Vault.bin` via `HUDMind.Packer`), release executables compile with `ReadyToRun` and stripped debug symbols/metadata, and `RuntimeHardeningGuard` defends single-file packaged runtime loading cleanly.

## Reporting a Vulnerability
If you discover a security issue (SSRF, API key leakage, update validation bypass, or unsafe file/path handling), please **do not open a public issue with exploit details**. Report it privately via **GitHub Security Advisories** (`Security → Advisories → Report a vulnerability`).
