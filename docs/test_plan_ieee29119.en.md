> 🌐 **Idioma / Language:** [🇪🇸 Español](test_plan_ieee29119.es.md) | **[🇺🇸 English](test_plan_ieee29119.en.md)**

# Software Test Plan and Report — ISO/IEC/IEEE 29119

**Project:** HUDMind Native v1.2.2 (`.NET 9`)  
**Automated Suites:** `native/HUDMind.Tests` (52 cross-platform `net9.0` cases) + `native/HUDMind.Windows.Tests` (16 `net9.0-windows10.0.19041.0` cases) + WPF smoke test (`--smoke-startup`)  
**Continuous Integration:** [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) (`native-dotnet9-tests` on `ubuntu-latest` + `native-windows-build` on `windows-latest`)

---

## 1. Test Strategy and Levels

1. **Cross-Platform Core Logic & Security Suite (`HUDMind.Tests` — 52 tests):**
   - Covers the compact wire protocol, 64-bit perceptual hashing (`dHash`), anti-repetition memory & chat isolation, secret redaction, extensible Chain-of-Responsibility content/domain safety guardrails (`ICoachSafetyPipeline` / `CoachContentGuard`: zero multilingual profanity, NSFW/moans/photos/illegal & off-topic weather blocking, markdown rule filtering), SSRF/HTTPS/CWE-22 validation, DPAPI `ISecretProtector` sealing/migration/recovery, failover routing, *Hedged Vision*, *Freshness Deadline*, Gemini/Anthropic/OpenAI-compatible adapters, Live backends (Gemini Live / OpenAI / xAI Realtime), Push-to-Talk, rolling `N`-match player memory + habit graduation + cross-linked `.md` context (`coach_soul.md`, `player_profile.md`, `logs/memory/*.md`, `INDEX.md`, `CoachContextAssembler`), OBS-style dual-tier 25s macro + ~10 FPS micro temporal ring buffer & visual motion-energy triggers (`RecentFrameRingBuffer`), non-overlapping `ConversationalSpeechCoordinator` + `NaturalConnectorResolver`, Discord-style translucent overlay feed state (`OverlayFeedBuffer`, Live stream coalescing, `Live->Final` promotion, per-item TTL & `overlay_style` persistence), query-aware game knowledge compiler (`GameKnowledgeCompiler`), predictive coaching prompts, deterministic tactical evaluator + Anti-Tilt tracking (`TacticalSignalEvaluator`), shared `MatchSessionCoordinator` epoch/state isolation, post-match reviews + cross-session focus, AES-256-GCM sealed asset vault (`HUDMind.Vault.bin` / `ProfileVaultCodec` / `ProfileVaultReader`) with zero plaintext `.md`/`.json` assembly resources, black-screen diagnostic, `ProviderRegistry` extensibility, `ICoachEngine` polymorphic contract, `HudPhaseClassifier`, PCM DSP (Catmull-Rom resample, edge fades, noise shield & sustained barge-in), and `GitHubReleaseUpdateService`.
2. **Native Windows / Win32 / Sensors / Audio Suite (`HUDMind.Windows.Tests` — 16 tests):**
   - Covers `Win32AudioDevices` enumeration/resolution (`winmm.dll`), `Win32StreamingPcmPlayer` (DAC `PreOpenDevice()`, 300ms startup jitter pre-buffering, 100ms steady recovery/feed, 4ms tail-fade holdback, 40ms silent wake-up lead-in, <=400ms DMA slicing, leak-safe stale `WAVEHDR` reclaim & clean clear — verified both in deterministic virtual mode on headless CI and on live WinMM), `WasapiAudioSceneMonitor` (read-only COM mixer peak telemetry excluding self), `Win32TemporalStoryboardComposer` (CPU `2x1` strips, `2x2` storyboards & `3x2` 6-panel filmstrips), `LoopbackGsiService` TCP HTTP receiver (CS2 C4/kit/helmet tracking + Dota 2 + menu state clearing + `413`/`400` chunked rejection + bounded teardown on stalled sockets + anti-UNC/reparse path checks), `TailLogSensorBase` (UTF-8, UTF-16 LE, age cutoff, and per-game scoping), `LeagueLiveClientSensor` (X.509/SAN/`Riot Games`/validity pinning + `allgamedata` CS/Wards/DEAD), `TextOnlySensorService.ReadTelemetryAsync`, `WinRtNaturalVoiceService` / `InstantSapiVoiceService` / `HybridVoiceService` (WAV→24kHz PCM, continuation queueing & WinRT/SAPI rate parsing), integrated smoke testing of `Win32DisplayHelper`, `Win32KeyStateProbe`, `Win32WindowDetector` (+ external foreground focus restore), `FastScreenCapture`, and `Win32AudioCue`, CurrentUser Windows DPAPI sealing (`Win32DpapiSecretProtector` / `crypt32.dll`), opt-in `WindowsStartupManager` HKCU Run toggling (off by default), `RuntimeHardeningGuard` + AES-256-GCM vault integrity checks, plus Windows Downloads folder resolution via `KnownFolders.GetDownloadsDirectory()`.
3. **WPF Startup Verification (`--smoke-startup`):**
   - Instantiates `MainWindow` off-screen with `RenderMode.SoftwareOnly`, binds XAML, tray icon, and settings, and exits cleanly with code `0` on `windows-latest` (also verified against the published single-file executables in `release.yml`).

---

## 2. Requirements Traceability Matrix (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requirement | Summary Description | Verified Test Cases |
| :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Embedded profiles, custom rules, manifest & CWE-22 profile CRUD | `HUDMind.Tests` (`06`, `24`, `30`, `32`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-03** | GDI `StretchBlt` capture, pHash, OBS dual-tier macro (~25s) + ~10 FPS burst ring buffer, motion-energy trigger, multi-frame HD / `Win32` filmstrip & black-screen diagnostic | `HUDMind.Tests` (`02`, `34`, `44`, `46`, `48`, `49`, `50`), `HUDMind.Windows.Tests` (`W6`, `W11`) |
| **REQ-FUNC-04** | `conversation`, `coach_voice` (with auto proactive cadence), and `text_only` communication modes | `HUDMind.Tests` (`16`, `23`, `48`, `49`, `50`) |
| **REQ-FUNC-05** | `FASE\|QUE_VEO\|URG\|CONSEJO` protocol, 1-3s predictive cues, game knowledge & menu/replay suppression | `HUDMind.Tests` (`01`, `13`, `29`, `44`, `46`) |
| **REQ-FUNC-06** | `ProviderRegistry`, `429/422` failover, *Hedged Vision* & *Freshness* | `HUDMind.Tests` (`07`, `08`, `09`, `10`, `11`, `20`, `21`, `28`) |
| **REQ-FUNC-07** | Passive sensors: GSI TCP, Riot Live TLS, UE/Valorant logs, tactical evaluator, Anti-Tilt & OCR | `HUDMind.Tests` (`13`, `39`, `41`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W4`, `W5`, `W9`) |
| **REQ-FUNC-08 / 11** | Rank persona/`coach_soul.md`, global profile, rolling `N`-match `.md` memory, `MatchSessionCoordinator` game-switch isolation & post-match review | `HUDMind.Tests` (`03`, `15`, `18`, `19`, `27`, `31`, `33`, `37`, `38`, `40`, `41`, `42`, `47`) |
| **REQ-FUNC-09 / 10** | Live WebSocket, PTT, hotkeys (`F7` Quick-Chat), WinRT TTS/OneCore, 0% GPU translucent overlay (`OverlayFeedBuffer`), opt-in HKCU startup, WASAPI gap yield & cues | `HUDMind.Tests` (`12`, `14`, `22`, `23`, `24`, `25`, `26`, `29`, `31`, `43`, `49`, `50`, `51`, `52`), `HUDMind.Windows.Tests` (`W1`, `W6`, `W7`, `W10`, `W12`, `W14`, `W15`, `W16`), `--smoke-startup` |
| **REQ-FUNC-13 / SEC-06** | In-app GitHub Releases updater, 1-click Setup, safe redirects, Downloads & `SHA-256` | `HUDMind.Tests` (`35`, `36`), `HUDMind.Windows.Tests` (`W8`) |
| **REQ-SEC-01..07** | DPAPI `accounts.json` (`crypt32.dll`), secret redaction, SSRF, CWE-22, UNC, loopback TLS pinning, `ICoachSafetyPipeline`, AES-256-GCM vault & `RuntimeHardeningGuard` | `HUDMind.Tests` (`04`, `05`, `06`, `17`, `24`, `30`, `31`, `32`, `33`, `38`, `45`, `47`, `52`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W5`, `W13`, `W16`) |

---

## 3. Local & CI Execution Commands

```powershell
# 1. Build entire solution (expected: 0 warnings, 0 errors)
dotnet build native/HUDMind.sln -c Release

# 2. Run cross-platform Core test suite (52 tests)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Run native Windows / Win32 / Sensors / Audio suite (16 tests)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Run live WPF startup smoke test (--smoke-startup)
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 5. Publish single-file win-x64 binary (or pass --self-contained true if .NET 9 Runtime is absent)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
```
