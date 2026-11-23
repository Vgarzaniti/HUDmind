> 🌐 **Idioma / Language:** [🇪🇸 Guía completa en Español (`AGENTS.es.md`)](AGENTS.es.md) | [🇺🇸 Full English Guide (`AGENTS.en.md`)](AGENTS.en.md)

# AGENTS.md — HUDMind (.NET 9 Native)

This file provides immediate architectural, security, testing, and workflow context for any AI coding assistant working in **HUDMind**. Read [AGENTS.en.md](AGENTS.en.md) or [AGENTS.es.md](AGENTS.es.md) for the complete reference.

## Project Summary
- **What:** Real-time tactical eSports AI coach for Windows 10/11 (x64) built with **.NET 9 + WPF + pure Win32**, zero external NuGet dependencies, 0% GPU (`RenderMode.SoftwareOnly`), and 100% passive anti-cheat safe observation.
- **Layers (`native/HUDMind.sln`):**
  - `HUDMind.Core` (`net9.0` cross-platform): models, config/SSRF/redaction, embedded profiles, AI adapters/live backends, orchestrators, wire protocol, pipelines, and `GitHubReleaseUpdateService`.
  - `HUDMind.Windows` (`net9.0-windows10.0.19041.0`): GDI `StretchBlt` capture, Win32 foreground/hotkeys/PTT, WinMM streaming audio, WinRT TTS/OCR, passive game sensors (GSI TCP, Riot TLS, log tailers), and `KnownFolders`.
  - `HUDMind.App` (`net9.0-windows10.0.19041.0` WPF): `MainWindow` (with in-app GitHub Releases `UpdateBanner`), click-through `OverlayWindow`, `Win32TrayIcon`, and `CoachEngineCoordinator`.
  - `HUDMind.Tests` (`net9.0`, 42 tests) & `HUDMind.Windows.Tests` (`net9.0-windows`, 9 tests).
  - `profiles/`: 7 embedded game cartridges (`profiles/*/*.md` filenames are runtime resources—never rename them).
  - `docs/`: All developer/project documentation in bilingual pairs (`*.es.md` and `*.en.md`).

## Non-Negotiable Rules
1. **0% GPU & Zero NuGet:** Keep `RenderMode.SoftwareOnly`; never add external NuGet dependencies or WPF GPU effects.
2. **100% Passive / Anti-Cheat Safe:** Never inject DLLs, read/write game memory, or install `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks.
3. **Security Boundaries:** Preserve `BaseUrlSecurityValidator` (SSRF/HTTPS), `SecretRedactor`, `ConfigRepository` (CWE-22 + symlink checks + atomic writes), `CoachingPersonaBuilder.SanitizeField`, and `GitHubReleaseUpdateService` (GitHub HTTPS redirect allowlist + `SHA-256` verification against `SHA256SUMS.txt` when present and listing the asset).
4. **Keep `HUDMind.Core` Cross-Platform (`net9.0`):** Never reference Windows/WPF/WinForms inside `HUDMind.Core`. Do not edit `archive/legacy_python/`.
5. **Bilingual Docs (`*.es.md` + `*.en.md`):** Every documentation `.md` in root or `docs/` must maintain both Spanish and English versions with top cross-links.
6. **Never Commit Secrets or Build Outputs:** Never stage `.env`, `config/*.json`, `profiles/*/custom_rules.md`, `logs/`, `dist/`, `bin/`, or `obj/`.

## Verification Commands (Must Pass Before Any PR)
```powershell
dotnet build native/HUDMind.sln -c Release
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build
$p = Start-Process -FilePath ".\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe" -ArgumentList "--smoke-startup" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "WPF --smoke-startup failed with exit code $($p.ExitCode)" }
```

## Git & Pull Request Rules
- **`main` is protected:** Always work on a prefixed branch (`feat/`, `fix/`, `bug/`, `refactor/`, `perf/`, `security/`, `docs/`, `test/`, `build/`, `ci/`, `chore/`) and open a PR using [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md).
- **Required CI Checks:** `native-dotnet9-tests` (`ubuntu-latest`) and `native-windows-build` (`windows-latest`).
- **PR & SemVer Guides:** [docs/pull_request_guide.en.md](docs/pull_request_guide.en.md) ([ES](docs/pull_request_guide.es.md)) · [docs/versioning_and_releases.en.md](docs/versioning_and_releases.en.md) ([ES](docs/versioning_and_releases.es.md)).
