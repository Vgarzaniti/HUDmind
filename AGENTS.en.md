> 🌐 **Idioma / Language:** [🇪🇸 Español](AGENTS.es.md) | **[🇺🇸 English](AGENTS.en.md)**

# AI Coding Agent Onboarding — HUDMind Native

Read this document first before modifying code, tests, docs, workflows, or PRs in **HUDMind**.

---

## 1. What HUDMind Is (30-Second Mental Model)

**HUDMind** is a real-time tactical eSports AI coach for **Windows 10/11 (x64)** built on **.NET 9 + WPF + pure Win32**, distributed as single-file Windows executables (`HUDMind.Native-Standalone-win-x64.exe` and `HUDMind.Native-win-x64.exe`) via GitHub Releases (`Vgarzaniti/HUDmind`).

### Non-Negotiable Architectural & Safety Invariants
1. **0% GPU for HUDMind:** `App.xaml.cs` enforces `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly`. Never add WPF GPU shader effects (e.g. `DropShadowEffect`) or DXGI/GPU hooks.
2. **Zero External NuGet Dependencies:** All `.csproj` files use only the .NET 9 BCL, WinRT (`net9.0-windows10.0.19041.0`), and direct Win32 P/Invoke (`user32`, `gdi32`, `kernel32`, `winmm`, `shell32`). Never add NuGet packages unless explicitly requested.
3. **100% Passive / Anti-Cheat Safe:**
   - Capture uses external GDI `StretchBlt` over `GetClientRect` (`FastScreenCapture.cs`).
   - Foreground detection uses `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` cached per `(HWND, PID)` (`Win32WindowDetector.cs`).
   - Global hotkeys use `RegisterHotKey` (`Win32GlobalHotkeyManager.cs`); Push-to-Talk polls `GetAsyncKeyState` (`Win32KeyStateProbe` in `Win32GlobalHotkeyManager.cs`).
   - **NEVER** inject DLLs, read/write game memory, or install global low-level hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`).
4. **Strict Security Boundaries:**
   - **SSRF / Cleartext (`BaseUrlSecurityValidator` & `SharedHttpFactory`):** Enforce HTTPS remotely (loopback HTTP allowed solely for local Ollama), disable `AllowAutoRedirect`, and block private/CGNAT/NAT64/Teredo/6to4/metadata IPs.
   - **GitHub Updates (`GitHubReleaseUpdateService`):** Allow HTTPS redirects (`<= 5` hops) **only** to trusted `github.com` / `.githubusercontent.com` hosts, cap response sizes, and verify `SHA-256` against `SHA256SUMS.txt` when that checksum asset exists, is readable, and lists the downloaded file (failing and deleting the file on mismatch). Otherwise the download completes without verification and launch remains available in `MainWindow`.
   - **Path Traversal (`ConfigRepository` & `LoopbackGsiService`):** Validate game IDs (`^[a-z0-9_\-]{1,64}$`), block `..`, UNC paths, and `ReparsePoint` symlinks; write configs atomically (`AtomicWrite`).
   - **Secrets (`SecretRedactor`):** Mask API keys/tokens in all logs, error messages, corrupt backups, and Markdown match reviews.
   - **Prompt Sanitization (`CoachingPersonaBuilder.SanitizeField`):** Strip protocol delimiters (`|`) and XML-like tags (`<...>`) from OCR/telemetry before prompt injection.
5. **Cross-Platform Core Split:** Keep `native/HUDMind.Core` strictly `net9.0` (no `Windows`, `WPF`, or `WinForms` imports) so `HUDMind.Tests` runs on both `ubuntu-latest` and `windows-latest`.
6. **Archive & Runtime Assets Rules:**
   - Do **not** modify `archive/legacy_python/` or reintroduce Python runtime dependencies.
   - Do **not** rename `profiles/*/*.md` (`system_prompt.md`, `knowledge_base.md`)—they are embedded assembly resources loaded by `ConfigRepository`.

---

## 2. Repository Map & Responsibilities

| Path | Target / Role | Key Types & Responsibilities |
| :--- | :--- | :--- |
| `native/HUDMind.Core/` | `net9.0` cross-platform core | Domain models, atomic config, embedded profiles, AI routing, wire protocol, pipelines, update checker |
| `native/HUDMind.Core/Models/DomainModels.cs` | Models | `UserConfig` (preserves Python `ExtraFields`), `ProviderAccount`, `GameProfile`, `GameManifest`, `CoachResponse`, `UrgencyLevel`, `HudSensorSnapshot`, `CoachRuntimeStats` |
| `native/HUDMind.Core/Config/ConfigRepository.cs` | Config & security | `ConfigRepository`, `SecretRedactor`, `BaseUrlSecurityValidator`, `HotkeyParser`, `EnsureDefaultProfilesSeeded` |
| `native/HUDMind.Core/AI/` | AI routing & backends | `ProviderRegistry`, `ProviderFailoverRouter` (hedged vision + circuit breaker), `SharedHttpFactory`, `CompactWireProtocol` (`FASE\|QUE_VEO\|URG\|CONSEJO`), `CoachingPersonaBuilder`, `LiveCoachEngine` (`RealtimeLiveCoachOrchestrator`), `Adapters/*`, `LiveBackends/*` |
| `native/HUDMind.Core/Engine/` | Classic engine | `Abstractions.cs` (`ICoachEngine`, `IScreenCaptureService`, `IForegroundWindowDetector`, `IVoiceOutputService`, `IOverlayOutputService`, `ITextOnlySensorService`), `CoachOrchestrator.cs` |
| `native/HUDMind.Core/Pipeline/` | Frame/audio utilities | `PerceptualHasher` (64-bit dHash), `InterventionMemory` (TTL anti-repeat + chat isolation), `HudPhaseClassifier`, `AudioPcmUtilities`, `BlackScreenDiagnostic` |
| `native/HUDMind.Core/Updates/GitHubReleaseUpdateService.cs` | Self-update check & download | `CheckForUpdatesAsync`, `ParseLatestReleaseJson`, `SelectPreferredAssets`, `DownloadAssetAsync` (GitHub redirect allowlist + `SHA-256`) |
| `native/HUDMind.Windows/` | `net9.0-windows10.0.19041.0` | GDI capture, Win32 detection, hotkeys, WinMM audio, WinRT TTS/OCR, passive game sensors, `KnownFolders` |
| `native/HUDMind.Windows/Sensors/` | Passive sensors | `TailLogSensorBase`, `RocketLeagueLogSensor`, `ValorantLogSensor`, `ValveGsiTelemetrySensor`, `LeagueLiveClientSensor` |
| `native/HUDMind.Windows/Audio/` | Pure WinMM / WinRT audio | `Win32AudioDevices`, `Win32StreamingMicCapture` (16kHz), `Win32StreamingPcmPlayer` (24kHz), `WinRtNaturalVoiceService`, `Win32AudioCue`, `AudioDiagnosticsService` |
| `native/HUDMind.App/` | WPF UI (`HUDMind.Native.exe`) | `App.xaml.cs` (SoftwareOnly + mutex + `ReleaseSingleInstanceMutex`), `MainWindow.xaml/.cs` (4 tabs + `UpdateBanner`), `OverlayWindow.xaml/.cs` (click-through PerMonitorV2), `Services/CoachEngineCoordinator.cs`, `Win32TrayIcon.cs` |
| `native/HUDMind.Tests/` | `net9.0` test suite | 42 cross-platform unit/integration tests (`Program.cs`) |
| `native/HUDMind.Windows.Tests/` | `net9.0-windows` test suite | 9 Windows-native tests for WinMM, GSI, TLS pinning, logs, capture, voice, and `KnownFolders` (`Program.cs`) |
| `profiles/` | Game cartridges | 7 bundled profiles (`aoe2_de`, `cs2`, `dota2`, `league_of_legends`, `rob_run`, `rocket_league`, `valorant`) embedded in `HUDMind.Core.csproj` |
| `docs/` | Bilingual technical docs | All docs have `.es.md` & `.en.md` pairs (`architecture`, `srs`, `test_plan_ieee29119`, `game_profiles_guide`, `versioning_and_releases`, `pull_request_guide`) |
| `.github/` | CI/CD & governance | `workflows/ci.yml`, `workflows/release.yml`, `PULL_REQUEST_TEMPLATE.md`, `ISSUE_TEMPLATE/*`, `dependabot.yml` |

---

## 3. How Runtime Execution Works

1. **Startup & Persistence:** `App.OnStartup` sets `RenderMode.SoftwareOnly`, guards single-instance mutex (`--smoke-startup` bypasses mutex and closes cleanly in `MainWindow_Loaded`), initializes `ConfigRepository` (`%LocalAppData%\HUDMind` when standalone, or local repo `profiles/`+`config/` in dev), and seeds the 7 embedded profiles if missing.
2. **In-App Update Flow:** When `CheckUpdatesOnStartup` is enabled, `MainWindow` queries `https://api.github.com/repos/Vgarzaniti/HUDmind/releases/latest` at startup and every 60 minutes while idle (`!_coordinator.IsRunning`). Newer stable releases display `UpdateBanner` with **⬇ Descargar actualización**, choosing `Standalone` vs compact `.exe` via `IsRunningSelfContained()`, verifying `SHA-256` when `SHA256SUMS.txt` is available and lists the asset (otherwise completing with `Sha256Verified = false`), and cleanly releasing the mutex before launching the downloaded binary.
3. **Live Engine Selection (`CoachEngineCoordinator`):**
   - **Live Conversational (`RealtimeLiveCoachOrchestrator`):** Streams 16kHz mic audio (with acoustic echo suppression and optional PTT) + low-FPS GDI JPEGs over WebSocket (`GeminiLiveBackend` / `OpenAIRealtimeBackend`), receiving 24kHz PCM voice and overlay text with barge-in.
   - **Classic Turn Engine (`CoachOrchestrator`):** Captures frames via `FastScreenCapture`, filters unchanged screens via 64-bit dHash, feeds a capacity-1 `DropOldest` channel (instant wakeup on `F10`), routes through `ProviderFailoverRouter` (hedged vision race or lazy `TextOnlySensorService` WinRT OCR + passive sensors for blind models), parses `CompactWireProtocol`, filters duplicates in `InterventionMemory`, and outputs to `OverlayWindow` + `HybridVoiceService`.
4. **Communication Modes (`UserConfig.CommunicationMode`):** Independently controls `conversation` (mic + voice), `coach_voice` (coach speaks, mic off), and `text_only` (silent text chat + overlay, audio cues suppressed).

---

## 4. Build, Test, and Publish Commands

```powershell
# 1. Build entire solution (must pass with 0 warnings / 0 errors)
dotnet build native/HUDMind.sln -c Release

# 2. Run cross-platform Core suite (42 tests) & Windows native suite (9 tests)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 3. Run WPF smoke test (must exit 0; use Start-Process -Wait -PassThru because WinExe returns immediately otherwise)
$p = Start-Process -FilePath ".\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe" -ArgumentList "--smoke-startup" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "WPF --smoke-startup failed with exit code $($p.ExitCode)" }

# 4. Publish single-file x64 executable:
#    a) Framework-dependent (requires .NET 9 Desktop Runtime on target PC)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
#    b) Portable standalone (no .NET 9 Desktop Runtime required on target PC)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist/native-standalone
```

---

## 5. Git Workflow, PR Rules & Documentation Policy

- **`main` is protected:** **NEVER** push directly to `main`. Create a feature branch (`feat/`, `fix/`, `bug/`, `refactor/`, `perf/`, `security/`, `docs/`, `test/`, `build/`, `ci/`, `chore/`) and open a Pull Request.
- **Required CI checks on PRs:** `native-dotnet9-tests` (`ubuntu-latest`) and `native-windows-build` (`windows-latest`).
- **PR Checklist & Types:** Follow [docs/pull_request_guide.en.md](docs/pull_request_guide.en.md) and fill out [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md) (explicit PR type `feat`, `fix`/`bug`, `security`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`, `revert`, `BREAKING CHANGE`, SemVer impact, test evidence, and security checklist).
- **Versioning (`vMAJOR.MINOR.PATCH`):** Follow [docs/versioning_and_releases.en.md](docs/versioning_and_releases.en.md). Pushing tag `vX.Y.Z` triggers `.github/workflows/release.yml` automatically.
- **Bilingual Documentation Rule:** Every human/developer documentation file in root or `docs/` **must** exist in both `<name>.es.md` and `<name>.en.md` with language-switcher links at the top (verify zero broken links before committing). Never change `profiles/*/*.md` filenames.
- **NEVER Commit Private or Generated Files:** `.env`, `config/accounts.json`, `config/user_config.json`, `config/telemetry.json`, `profiles/*/custom_rules.md`, `logs/`, `dist/`, `bin/`, `obj/`.
