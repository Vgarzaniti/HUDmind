> 🌐 **Idioma / Language:** [🇪🇸 Español](test_plan_ieee29119.es.md) | **[🇺🇸 English](test_plan_ieee29119.en.md)**

# Software Test Plan and Report — ISO/IEC/IEEE 29119

**Project:** HUDMind Native v1.5.0 (`.NET 9`)  
**Automated Suites:** `native/HUDMind.Tests` (61 cross-platform `net9.0` cases) + `native/HUDMind.Windows.Tests` (20 `net9.0-windows10.0.19041.0` cases) + WPF smoke test (`--smoke-startup`)  
**Continuous Integration:** [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) (`native-dotnet9-tests` on `ubuntu-latest` + `native-windows-build` on `windows-latest`)

---

## 1. Test Strategy and Levels

1. **Cross-Platform Core Logic & Security Suite (`HUDMind.Tests` — 61 tests):**
   - Covers the compact wire protocol, 64-bit perceptual hashing (`dHash`) + `ComputeMeanAbsLumaDelta`, anti-repetition memory & chat isolation, secret redaction (including `rcon_password`), extensible Chain-of-Responsibility content/domain safety guardrails (`ICoachSafetyPipeline` / `CoachContentGuard`: zero multilingual profanity, NSFW/moans/photos/illegal & off-topic weather blocking, markdown rule filtering), SSRF/HTTPS/CWE-22 validation, DPAPI `ISecretProtector` sealing/migration/recovery, isolated personal profiles with PBKDF2-SHA256 PIN lock (`UserProfileStore`), portable AES-256-GCM `.hudmind` backups (`ProfileArchiveCodec`), serverless loopback OpenRouter OAuth PKCE (`OpenRouterOAuthPkceClient`), failover routing, *Hedged Vision*, *Freshness Deadline* (including post-AI latency/phase-switch discard), `event_first` IGL pacing, `<5 ms` deterministic local alerts (`InstantLocalAlert`), dead-time briefing cards (`BriefingNote`), attempt-based training cadence + role calibration (`PlayerRoleTag`), measurable Today-vs-Previous habit leak tracking (`PlayerLeakTracker`), Gemini/Anthropic/OpenAI-compatible adapters, Live backends (Gemini Live / OpenAI / xAI Realtime) with passive static frame dedup, Push-to-Talk, rolling `N`-match player memory + habit graduation + cross-linked `.md` context (`coach_soul.md`, `player_profile.md`, `logs/memory/*.md`, `INDEX.md`, `CoachContextAssembler`), OBS-style dual-tier 25s macro + ~10 FPS micro temporal ring buffer & visual motion-energy triggers (`RecentFrameRingBuffer`), non-overlapping `ConversationalSpeechCoordinator` + `NaturalConnectorResolver`, Discord-style translucent overlay feed state (`OverlayFeedBuffer`, Live stream coalescing, `Live->Final` promotion, per-item TTL & `overlay_style` persistence), embedded HTTP/SSE Companion Web HUD (`CompanionHudServer` CSP/XFO/textContent XSS-safe rendering, LAN session-token auth, transparent `?mode=obs` ticker mode & `CompositeOverlayService` fan-out), zero-NuGet ISO QR encoder (`QrMatrixEncoder`), generic loopback overlay bridge protocol (`InGameOverlayBridgeProtocol` CWE-77 toast arg sanitization, idempotent allowlist & `EnsureRconConfigEnabled`) + generic JSON telemetry normalizer (`ExternalTelemetryParser` supporting `hudmind:telemetry` and match relays), query-aware game knowledge compiler (`GameKnowledgeCompiler`), predictive coaching prompts, deterministic tactical evaluator (`LOCAL_WS_TELEMETRY`, GSI, logs) + Anti-Tilt tracking (`TacticalSignalEvaluator`), shared `MatchSessionCoordinator` epoch/state isolation, post-match reviews + cross-session focus, AES-256-GCM sealed asset vault (`HUDMind.Vault.bin` / `ProfileVaultCodec` / `ProfileVaultReader`) with zero plaintext `.md`/`.json` assembly resources, black-screen diagnostic, `ProviderRegistry` extensibility, `ICoachEngine` polymorphic contract, `HudPhaseClassifier`, PCM DSP (Catmull-Rom resample, edge fades, noise shield & sustained barge-in), and `GitHubReleaseUpdateService`.
2. **Native Windows / Win32 / Sensors / Audio Suite (`HUDMind.Windows.Tests` — 20 tests):**
   - Covers `Win32AudioDevices` enumeration/resolution (`winmm.dll`), `Win32StreamingPcmPlayer` (DAC `PreOpenDevice()`, 300ms startup jitter pre-buffering, 100ms steady recovery/feed, 4ms tail-fade holdback, 40ms silent wake-up lead-in, <=400ms DMA slicing, leak-safe stale `WAVEHDR` reclaim & clean clear — verified both in deterministic virtual mode on headless CI and on live WinMM), `WasapiAudioSceneMonitor` (read-only COM mixer peak telemetry excluding self), `Win32TemporalStoryboardComposer` (CPU `2x1` strips, `2x2` storyboards & `3x2` 6-panel filmstrips), `LoopbackGsiService` TCP HTTP receiver (CS2 C4/kit/helmet tracking + Dota 2 + menu state clearing + `413`/`400` chunked rejection + bounded teardown on stalled sockets + anti-UNC/reparse path checks), `TailLogSensorBase` (UTF-8, UTF-16 LE, age cutoff, and per-game scoping), `LeagueLiveClientSensor` (X.509/SAN/`Riot Games`/validity pinning + `allgamedata` CS/Wards/DEAD), configurable multi-game `LoopbackWebSocketTelemetrySensor` (`ws://127.0.0.1`), `InGameOverlayBridgeSetup.ConfigureDirectoryAsync` (safe `rcon_commands.cfg` + `rcon_enabled` setup), `TextOnlySensorService.ReadTelemetryAsync` + cached WinRT OCR zones preserving fresh live telemetry (`ExtractAsync`), `WinRtNaturalVoiceService` / `InstantSapiVoiceService` / `HybridVoiceService` (WAV→24kHz PCM, continuation queueing & WinRT/SAPI rate parsing), integrated smoke testing of `Win32DisplayHelper` (`TryGetMonitorIndexForWindow`), `Win32KeyStateProbe`, `Win32WindowDetector` (+ external foreground focus restore), `FastScreenCapture` (`SparseLumaDelta`), and `Win32AudioCue`, CurrentUser multi-profile Windows DPAPI sealing (`Win32DpapiSecretProtector` / `crypt32.dll` + `UserProfileStore`), opt-in `WindowsStartupManager` HKCU Run toggling (off by default), `RuntimeHardeningGuard` + AES-256-GCM vault integrity checks, plus Windows Downloads folder resolution via `KnownFolders.GetDownloadsDirectory()`.
3. **WPF Startup Verification (`--smoke-startup`):**
   - Instantiates `MainWindow` off-screen with `RenderMode.SoftwareOnly`, binds XAML, tray icon, and settings, and exits cleanly with code `0` on `windows-latest` (also verified against the published single-file executables in `release.yml`).

---

## 2. Requirements Traceability Matrix (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requirement | Summary Description | Verified Test Cases |
| :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Embedded profiles, custom rules, manifest & CWE-22 profile CRUD | `HUDMind.Tests` (`06`, `24`, `30`, `32`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-03** | GDI `StretchBlt` capture, pHash + luma delta (`MeanAbsLumaDelta`), OBS dual-tier macro (~25s) + ~10 FPS burst ring buffer, motion-energy trigger, passive WS frame dedup, multi-frame HD / `Win32` filmstrip & black-screen diagnostic | `HUDMind.Tests` (`02`, `34`, `44`, `46`, `48`, `49`, `50`, `55`), `HUDMind.Windows.Tests` (`W6`, `W11`, `W17`) |
| **REQ-FUNC-04** | `conversation`, `coach_voice` (with auto proactive cadence), and `text_only` communication modes | `HUDMind.Tests` (`16`, `23`, `48`, `49`, `50`) |
| **REQ-FUNC-05** | `FASE\|QUE_VEO\|URG\|CONSEJO` protocol, 1-3s predictive cues, post-AI staleness/phase-switch discard, game knowledge & menu/replay suppression | `HUDMind.Tests` (`01`, `13`, `29`, `44`, `46`, `55`) |
| **REQ-FUNC-06** | `ProviderRegistry`, `429/422` failover, *Hedged Vision* & *Freshness* | `HUDMind.Tests` (`07`, `08`, `09`, `10`, `11`, `20`, `21`, `28`) |
| **REQ-FUNC-07** | Passive sensors: GSI TCP, Riot Live TLS, configurable loopback WebSocket telemetry (`LoopbackWebSocketTelemetrySensor` / `ws://127.0.0.1` + `hudmind:telemetry`), local game logs (PII-stripped), tactical evaluator, Anti-Tilt & cached OCR with fresh telemetry | `HUDMind.Tests` (`13`, `39`, `41`, `56`, `58`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W4`, `W5`, `W9`, `W17`, `W18`, `W19`) |
| **REQ-FUNC-08 / 11** | Rank persona/`coach_soul.md`, active role tags, `event_first` IGL pacing, attempt-based training cadence, Today-vs-Previous leak tracking, rolling `N`-match `.md` memory & post-match review | `HUDMind.Tests` (`03`, `15`, `18`, `19`, `27`, `31`, `33`, `37`, `38`, `40`, `41`, `42`, `47`, `61`) |
| **REQ-FUNC-09 / 10** | Live WebSocket, PTT, hotkeys (`F7` Quick-Chat), WinRT TTS/OneCore, 0% GPU translucent overlay (`OverlayFeedBuffer` + `<5 ms` local tactical alerts / briefings + auto-monitor), Companion Web HUD HTTP SSE (`CompanionHudServer` + `?mode=obs` + 0% GPU `QrMatrixEncoder` + `CompositeOverlayService`), assisted loopback RCON Toast bridge (`LoopbackRconToastBridge` / `InGameOverlayBridgeSetup`), Live `NONE` suppression & `<3.5s` freshness, opt-in HKCU startup, WASAPI gap yield & cues | `HUDMind.Tests` (`12`, `14`, `22`, `23`, `24`, `25`, `26`, `29`, `31`, `43`, `49`, `50`, `51`, `52`, `53`, `54`, `56`, `57`, `58`, `61`), `HUDMind.Windows.Tests` (`W1`, `W6`, `W7`, `W10`, `W12`, `W14`, `W15`, `W16`, `W17`, `W18`, `W19`), `--smoke-startup` |
| **REQ-FUNC-13 / SEC-06** | In-app GitHub Releases updater, 1-click Setup, safe redirects, Downloads & `SHA-256` | `HUDMind.Tests` (`35`, `36`), `HUDMind.Windows.Tests` (`W8`) |
| **REQ-FUNC-14 / SEC-01..07** | Personal profiles `UserProfileStore` (PBKDF2-SHA256 PIN), portable `.hudmind` AES-256-GCM backups (`ProfileArchiveCodec`), OpenRouter OAuth PKCE (`OpenRouterOAuthPkceClient`), multi-profile DPAPI `accounts.json` (`crypt32.dll`), secret redaction, SSRF, CWE-22, CWE-77, UNC/ReparsePoint, loopback TLS pinning, Companion CSP/XSS + LAN token auth, `ICoachSafetyPipeline`, AES-256-GCM vault & `RuntimeHardeningGuard` | `HUDMind.Tests` (`04`, `05`, `06`, `17`, `24`, `30`, `31`, `32`, `33`, `38`, `45`, `47`, `52`, `54`, `56`, `57`, `58`, `59`, `60`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W5`, `W13`, `W16`, `W18`, `W19`, `W20`) |

---

## 3. Local & CI Execution Commands

```powershell
# 1. Build entire solution (expected: 0 warnings, 0 errors)
dotnet build native/HUDMind.sln -c Release

# 2. Run cross-platform Core test suite (61 tests)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Run native Windows / Win32 / Sensors / Audio suite (20 tests)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Run live WPF startup smoke test (--smoke-startup)
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 5. Publish single-file win-x64 binary (or pass --self-contained true if .NET 9 Runtime is absent)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
```
