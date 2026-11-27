> 🌐 **Idioma / Language:** [🇪🇸 Español](architecture.es.md) | **[🇺🇸 English](architecture.en.md)**

# Technical Architecture — HUDMind Native v1.6.0 (ISO/IEC/IEEE 42010)

This document describes the modular architecture of **HUDMind Native** on **.NET 9**, its strict separation between pure core logic, the Win32 layer, and the WPF UI, its polymorphic coaching engines, and its Architecture Decision Records (ADRs).

---

## 1. Design Principles

1. **Separation of Concerns & Portability (`Core` vs `Windows` vs `App`):**
   - `HUDMind.Core` (`net9.0`, 100% cross-platform): domain models, atomic config (`UserConfig` with window bounds, free overlay drag coords, and onboarding state) + multi-profile local store (`UserProfileStore` with PBKDF2-SHA256 PIN lock) + `ISecretProtector`, portable encrypted `.hudmind` backups (`ProfileArchiveCodec`), serverless OAuth PKCE flow (`OpenRouterOAuthPkceClient`), SSRF/CWE-22/CWE-77/secret-redaction security (`DiagnosticsFormatter`), AES-256-GCM vault (`HUDMind.Vault.bin`), compact wire protocol, event-first IGL pacing (`CoachingPacingMode="event_first"`), `<5 ms` local tactical alerts (`InstantLocalAlert`), dead-time briefings (`BriefingNote`), measurable habit leak counter (`PlayerLeakTracker`), active match roles (`PlayerRoleTag`), rolling `N`-match `.md` memory, HUD feed state (`OverlayFeedBuffer`), modular `Overlay/`, telemetry normalizer (`ExternalTelemetryParser`), failover router, OBS-style dual-tier frame buffer (`RecentFrameRingBuffer`), non-overlapping `ConversationalSpeechCoordinator`, update service (`GitHubReleaseUpdateService`), and both orchestrators (`CoachOrchestrator` and `RealtimeLiveCoachOrchestrator`).
   - `HUDMind.Windows` (`net9.0-windows10.0.19041.0`): GDI `StretchBlt` capture, foreground window detection, focus restore, active monitor resolution & multi-monitor clamping (`Win32DisplayHelper`), WinRT OCR (image-hash cached while polling live telemetry), Valve GSI TCP listener, Riot TLS client, configurable `LoopbackWebSocketTelemetrySensor` (`ws://127.0.0.1`), log tailers, passive `LoopbackRconToastBridge` / `InGameOverlayBridgeSetup`, current-user single-instance IPC (`SingleInstancePipeServer`), non-invasive health probe (`SystemDiagnosticsProbe`), Win32 global hotkeys (`F7`–`F10`) / PTT, WinMM streaming audio, passive WASAPI mixer monitor (`WasapiAudioSceneMonitor`), `PlaySoundW` audio cues, Windows DPAPI protection (`Win32DpapiSecretProtector`), opt-in HKCU startup (`WindowsStartupManager`), `RuntimeHardeningGuard`, and known-folder resolution (`KnownFolders`).
   - `HUDMind.App` (`net9.0-windows10.0.19041.0` WPF): `App.xaml.cs` (global secret-redacted crash handler writing `logs/crash-*.log` + named-pipe foreground wakeup), `MainWindow` (`MainWindow.DesktopFeatures.cs`: 5 modules incl. `📊 History`, 3-step onboarding wizard, `🩺 Diagnostics` health dialog, live ES/EN `ResourceDictionary` switching via `AppLocalizationService` + `Resources/Strings.*.xaml`, focus-scoped `HotkeyRecorderHelper` without global hooks, factory reset, non-blocking toasts, window geometry restore/save, local multi-profiles/PIN/backups `.hudmind`, 1-click OpenRouter PKCE, 2-click Gemini wizard, Today-vs-Previous CPU leak progress bars, pacing/role selectors, `UpdateBanner`, 0% GPU LAN QR & `?mode=obs`), `OverlayWindow` (`WS_EX_TOPMOST` + active monitor tracking + free drag `📌 Move Overlay` + `F7` Quick-Chat), `PersonalAccountDialogs`, `Win32TrayIcon`, and `CoachEngineCoordinator`.
2. **0% GPU & Anti-Cheat Safe by Construction:**
   - WPF rendering forced onto CPU (`RenderMode.SoftwareOnly`).
   - Capture uses zero invasive hooks or process injection; foreground detection uses `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` cached per `(HWND, PID)`, and Push-to-Talk polls directly via `GetAsyncKeyState`.
3. **Freshness Over Completeness:**
   - Bounded *Drop-Oldest* queues, *Freshness Deadline* enforcement for automatic advice, local suppression of menu/loading/replay screens, and semantic anti-repetition memory.

---

## 2. Layer & Data-Flow Diagram

```text
+-----------------------------------------------------------------------------------+
|                        HUDMind.App (WPF · RenderMode.SoftwareOnly)                |
|  MainWindow (5 tabs + Toast/Wizard)      | OverlayWindow (WS_EX_TRANSPARENT/Drag)| WinTray |
+-----------------------------------------+-----------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
|                    CoachEngineCoordinator (Services/CoachEngineCoordinator.cs)    |
|  - Polymorphically selects ICoachEngine (Live WebSocket or Classic HTTP/2)        |
|  - Manages LoopbackGsiService (:47315), Win32GlobalHotkeyManager & PTT/AudioCues  |
+-----------------------+-----------------------------------+-----------------------+
                        |                                   |
        [Live WebSocket ICoachEngine]         [Classic Turn-Based ICoachEngine]
                        v                                   v
+---------------------------------------+   +---------------------------------------+
| RealtimeLiveCoachOrchestrator (Core)  |   | CoachOrchestrator (Core)              |
| - GeminiLiveBackend / OpenAIRealtime  |   | - Capture Channel (Bounded DropOldest)|
| - Mic PCM 16kHz + VAD + PTT + BargeIn |   | - ProviderFailoverRouter + Hedged     |
| - 25s/~10 FPS OBS Ring + Follow-Ups   |   | - ConversationalSpeechCoordinator     |
+-------------------+-------------------+   +-------------------+-------------------+
                    |                                           |
                    +---------------------+---------------------+
                                          v
+-----------------------------------------------------------------------------------+
|                    HUDMind.Windows (Sensors, Audio & Capture)                     |
|  FastScreenCapture (StretchBlt+dHash) | Win32WindowDetector | TextOnlySensorService|
|  Win32StreamingPcmPlayer | WinRt/Sapi | WasapiAudioSceneMonitor (Read-Only COM)    |
|  ValveGsiTelemetrySensor | LeagueLiveClientSensor | RocketLeague/ValorantLogSensor |
+-----------------------------------------------------------------------------------+
```

---

## 3. Applied Design Patterns

1. **Strategy / Polymorphism (`ICoachEngine`, `IAIProvider`, `ILiveRealtimeBackend`, `IGameTelemetrySensor`):**
   - `ICoachEngine` unifies lifecycle, pause state, forced `F10` analysis, text chat, live settings updates, and post-match reviews across both the Live WebSocket engine and the Classic turn-based engine.
   - `IGameTelemetrySensor` decouples passive local sensors (`ValveGsiTelemetrySensor`, `LeagueLiveClientSensor`, `RocketLeagueLogSensor`, `ValorantLogSensor`), sharing a common `TailLogSensorBase` hierarchy for safe UTF-8/UTF-16 LE log tailing.
2. **Extensible Registry (`ProviderRegistry`):**
   - Centralizes presets (`groq`, `gemini`, `openai`, `xai`, `claude`, `openrouter`, `mistral`, `pollinations`, `airforce`, `ollama`), default URLs, capabilities (`SupportsLiveWebSocket`, `SupportsFastHedgedVision`, `IsKeylessAllowed`), and HTTP/Live adapter factories in one place.
3. **Failover + Speculative Hedging (`ProviderFailoverRouter`):**
   - Automatically rotates accounts on failures or quota limits (`429`/`402`/`5xx`). For providers with `SupportsFastHedgedVision`, triggers a speculative hedged race when the primary exceeds the latency threshold, preventing unobserved task exceptions and dropping stale automatic advice past its freshness deadline.
4. **Lazy Blind-Text Sensors (`TextOnlySensorService` + `HudPhaseClassifier`):**
   - When the active account supports vision, OCR cost is `0 ms`. Only for text-only accounts (`SupportsVision == false`) or Live local phase gating does it run zoned WinRT OCR plus passive game sensors to describe match state without sending pixels.
5. **Chain of Responsibility (`ICoachSafetyPipeline` / `CompositeContentSafetyPipeline`) & Shared Coordinator (`MatchSessionCoordinator`):**
   - `ICoachSafetyPipeline` composes ordered safety/domain rules cleanly without modifying orchestrators; `MatchSessionCoordinator` unifies cross-game epoch fencing, tactical state, and `RecentFrameRingBuffer` lifecycle across both engines.

---

## 4. Concurrency Model

| Subsystem | Primitive / Thread | Primary Guarantee |
| :--- | :--- | :--- |
| Main UI & WPF Overlay | UI Thread (`Dispatcher`) | Non-blocking updates via `Dispatcher.BeginInvoke`; click-through overlay never steals game focus. |
| Capture & HTTP Pipeline | `Channel<CapturedFrameData>` + async `Task` | `BoundedChannelFullMode.DropOldest` capacity `1` with epoch tag; `F10` wakes immediately. |
| Speech Queue & Audio Gap | `ConversationalSpeechCoordinator` + `WasapiAudioSceneMonitor` | Priority capacity-2 FIFO queue chains rapid tips with natural connectors, never chops active speech, and yields briefly to external audio bursts. |
| Live Audio (Mic + Speaker) | WinMM Threads / PCM Queue (`Win32StreamingMicCapture` & `Win32StreamingPcmPlayer`) | In-memory 16kHz mic capture (760ms digital silence tail) and 24kHz playback (`PreOpenDevice`, 300ms prime, 4ms tail fade, leak-safe reclaim, AEC barge-in). |
| Global Hotkeys | Dedicated Win32 Thread (`GetMessage`) | `RegisterHotKey` + `PostThreadMessage(WM_QUIT)` on clean shutdown; `0%` idle CPU. |
| CS2/Dota2 GSI Listener | Loopback `TcpListener` + `SemaphoreSlim(4)` | Max 4 concurrent sockets with bounded `CancellationTokenSource` and clean teardown on `Stop()`. |
| Update Checker | Async `Task` + `DispatcherTimer` (60 min when idle) | Runs only at startup and while idle (`!IsRunning`); validates HTTPS GitHub redirects and streaming SHA-256. |

---

## 5. Architecture Decision Records (ADRs — ISO/IEC/IEEE 42010)

| ADR | Decision | Rejected Alternative | Rationale |
| :--- | :--- | :--- | :--- |
| **ADR-001** | Migrate from Python to **Native .NET 9 with zero external NuGet packages** | Python packaging (`PyInstaller`) or heavy external dependencies | Eliminates interpreter overhead, lowers RAM and startup latency, compiles to single-file `HUDMind.Native.exe`, and avoids NuGet supply-chain risk. |
| **ADR-002** | `RenderMode.SoftwareOnly` + GDI `StretchBlt` into a single reusable `Bitmap` | WPF Hardware D3D or heavy DXGI capture hooks | Keeps **100% of GPU resources free** for the game and avoids repeated per-frame GC allocations. |
| **ADR-003** | Strict split: `HUDMind.Core` (`net9.0`) vs `HUDMind.Windows` (`net9.0-windows`) | Monolithic project coupled to WinForms/WPF | Enables running `HUDMind.Tests` on both Linux (`ubuntu-latest`) and Windows in CI/CD. |
| **ADR-004** | `ProviderRegistry` + decoupled adapters guarded by `BaseUrlSecurityValidator` | Scattered `switch` blocks across UI and router | Lets new providers or custom HTTPS endpoints be added at runtime without duplicating SSRF checks. |
| **ADR-005** | Lazy WinRT OCR and sensors only for blind-text accounts or Live local gating | Running OCR on every frame regardless of model | Saves ~50–150 ms CPU when the active model already sees pixels, while keeping coaching functional on free text-only LLMs. |
| **ADR-006** | Compact `FASE\|QUE_VEO\|URG\|CONSEJO` protocol + `HudPhaseClassifier` | Unstructured verbose responses | Minimizes output tokens (`<= 160`), enables deterministic parsing without partial-JSON failures, and suppresses menus/replays locally. |
| **ADR-007** | Pinned embedded WAV audio cues (`Win32AudioCue` + `PlaySoundW`) | Runtime TTS synthesis or external WAV files on disk | Microsecond trigger latency, zero disk I/O, and guaranteed silence under `text_only` or `EnableAudioCues=false`. |
| **ADR-008** | 100% C# Classic voice (`WinRtNaturalVoiceService` + `InstantSapiVoiceService`) & embedded profiles | Archived Python subprocess or mandatory external `profiles/` folder | Zero Python runtime processes, direct respect for `AudioOutputDeviceName`, and plug-and-play portability on any Windows PC. |
| **ADR-009** | Assisted in-app GitHub Releases updater with `SHA-256` verification without in-place self-patching | `.bat`/PowerShell scripts overwriting the running `.exe` | Avoids heuristic antivirus/SmartScreen false positives, restricts redirects to trusted GitHub CDN hosts, and cleanly downloads and launches the new binary. |
| **ADR-010** | Build-time AES-256-GCM + Brotli asset vault (`HUDMind.Packer` → `HUDMind.Vault.bin`) + `ReadyToRun` + compressed single-file + release CLI `Obfuscar` | Plaintext embedded `.md`/`.json` resources or heavyweight NuGet ILCompiler packages | Prevents casual string/resource extraction from shipped `.exe` binaries while keeping zero `.csproj` NuGet dependencies and gating runtime inspection via `RuntimeHardeningGuard`. |
