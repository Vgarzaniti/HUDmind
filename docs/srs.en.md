> 🌐 **Idioma / Language:** [🇪🇸 Español](srs.es.md) | **[🇺🇸 English](srs.en.md)**

# Software Requirements Specification (SRS) — ISO/IEC/IEEE 29148

**Project:** HUDMind Native (.NET 9) — Real-Time Tactical Video Game Coach  
**Version:** 1.2.2 (`HUDMind.Native`)  
**Reference Standards:** ISO/IEC/IEEE 29148 (Requirements Engineering), ISO/IEC 25010 (Software Product Quality), ISO/IEC 27001 (Information Security), ISO/IEC/IEEE 42010 (Architecture Description), ISO/IEC/IEEE 29119 (Software Testing).

---

## 1. Purpose and Scope

**HUDMind Native** is a native Windows desktop application (.NET 9 + WPF + Win32, zero external NuGet dependencies, `$0` infrastructure cost) that analyzes live match state and coaches the player according to skill tier (`beginner`, `casual`, `intermediate`, `advanced`, `pro`) and rank label via:
- **Bidirectional Live Engine (WebSocket):** Gemini Live, OpenAI Realtime, and xAI Grok Realtime with native 16kHz→24kHz PCM audio, voice activity detection (VAD), *Push-to-Talk* or open mic, instant barge-in, and low-rate JPEG video streaming.
- **Classic Turn-Based Multi-AI Engine (HTTP/2):** Provider routing and speculative *Hedged Vision* racing across Groq, Gemini, OpenAI, xAI, Anthropic Claude, OpenRouter, Mistral, Pollinations, Airforce, and local Ollama (`http://localhost:11434/v1`), with a **Blind-Text Sensor Adapter** (`TextOnlySensorService`) whenever the active account does not support vision.

### System Boundaries (100% Anti-Cheat Safe)
- **Zero injection and intrusion:** Never injects DLLs, never reads or writes game memory, and never installs `WH_KEYBOARD_LL` / `WH_MOUSE_LL` global hooks.
- **0% GPU for HUDMind:** Enforces `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` in WPF and relies on passive GDI `StretchBlt` plus a click-through `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` overlay.
- **No temporary screenshot files:** Captured frames remain strictly in RAM (`byte[]` / `MemoryStream`). Only user config (`config/`) and secret-redacted Markdown post-match reviews (`logs/reviews/`) are written to disk.

---

## 2. Functional Requirements (FR)

| ID | Requirement | Acceptance Criteria | Primary Module |
| :--- | :--- | :--- | :--- |
| **REQ-FUNC-01** | Automatic active-game detection. | Identifies foreground `.exe` via `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` cached by `(HWND, PID)`, crops to client area, and retains the last external game rect while interacting with HUDMind chat. | `native/HUDMind.Windows/Win32WindowDetector.cs` |
| **REQ-FUNC-02** | Embedded profile seeding & GUI CRUD. | Auto-seeds the 7 bundled profiles from the sealed `HUDMind.Vault.bin` embedded resource when `profiles/` is missing (`EnsureDefaultProfilesSeeded`), loads `manifest.json`/`system_prompt.md`/`custom_rules.md` + `universal`, and supports GUI create/edit/delete. | `native/HUDMind.Core/Config/ConfigRepository.cs` |
| **REQ-FUNC-03** | Low-impact GDI capture, dHash & black-screen alert. | `FastScreenCapture` scales via `StretchBlt` (`eco` `960×540`, `balanced` `1280×720`, `high` `1440×810`) into a reusable `Bitmap`, feeds a 25s macro + ~10 FPS OBS RAM ring buffer, skips static frames via dHash, and `BlackScreenDiagnostic` warns after `>=6` black frames (Exclusive Fullscreen). | `native/HUDMind.Windows/FastScreenCapture.cs`, `native/HUDMind.Core/Pipeline/RecentFrameRingBuffer.cs`, `native/HUDMind.Core/Pipeline/BlackScreenDiagnostic.cs` |
| **REQ-FUNC-04** | Independent communication channels. | Supports 3 live-switchable modes without restarting the engine (`CommunicationMode`): `conversation` (mic + voice), `coach_voice` (coach voice only, mic off), and `text_only` (silent text chat + overlay, no voice/mic/cues). | `native/HUDMind.Core/Models/DomainModels.cs`, `native/HUDMind.App/MainWindow.xaml.cs` |
| **REQ-FUNC-05** | Compact wire protocol & local noise suppression. | `CompactWireProtocol` parses `FASE\|QUE_VEO\|URG\|CONSEJO` and JSON; `HudPhaseClassifier` + `TextOnlySensorService` suppress analysis locally during `MENU` / `LOADING` / `REPLAY` screens. | `native/HUDMind.Core/AI/CompactWireProtocol.cs`, `native/HUDMind.Core/Pipeline/HudPhaseClassifier.cs` |
| **REQ-FUNC-06** | Extensible Multi-AI pool, *Hedged Vision* & Failover. | `ProviderRegistry` centralizes presets and HTTP/Live factories; `ProviderFailoverRouter` fails over on `HTTP 429/402/5xx`, runs *Hedged Vision* on primary delays, enforces *Freshness Deadline*, and triggers WinRT OCR + sensors for blind models. | `native/HUDMind.Core/AI/ProviderRegistry.cs`, `native/HUDMind.Core/AI/ProviderFailoverRouter.cs` |
| **REQ-FUNC-07** | Passive game-specific telemetry sensors. | Supports loopback Valve GSI TCP (`127.0.0.1:47315`) for CS2 & Dota 2, loopback Riot Live Client TLS (`127.0.0.1:2999`) for LoL, UTF-8/UTF-16 LE log tailers for Rocket League & Valorant, and in-RAM WinRT OCR. | `native/HUDMind.Windows/LoopbackGsiService.cs`, `native/HUDMind.Windows/Sensors/*.cs`, `native/HUDMind.Windows/TextOnlySensorService.cs` |
| **REQ-FUNC-08** | Calibration by rank tier, style & player rules. | `CoachingPersonaBuilder` calibrates tactical depth by tier (`beginner`, `casual`, `intermediate`, `advanced`, `pro`) and rank label, session mode (`auto`, `match`, `training`), style (`friendly`, `honest`, `tactical`, `strict`), and up to `600` chars of `custom_rules.md`. | `native/HUDMind.Core/AI/CoachingPersonaBuilder.cs` |
| **REQ-FUNC-09** | Click-through visual overlay & gap-aware continuous C# TTS. | `OverlayWindow` supports full card & compact pill, scale, opacity, 4 corners, and system tray (`Win32TrayIcon`); `HybridVoiceService` + `ConversationalSpeechCoordinator` + `WasapiAudioSceneMonitor` prevent mid-sentence cuts, chain follow-ups with natural connectors, and yield briefly to loud game audio bursts with zero Python. | `native/HUDMind.App/OverlayWindow.xaml.cs`, `native/HUDMind.Windows/HybridVoiceService.cs`, `native/HUDMind.Core/Pipeline/ConversationalSpeechPipeline.cs`, `native/HUDMind.Windows/Audio/WasapiAudioSceneMonitor.cs` |
| **REQ-FUNC-10** | In-match hotkeys, Push-to-Talk & optional audio cues. | Configurable Win32 `RegisterHotKey` (`F9` Pause, `F10` Analyze Now, `F8` Mic), hookless PTT probe (`GetAsyncKeyState` via `Win32KeyStateProbe`), and optional `Win32AudioCue` chimes (muted in `text_only`). | `native/HUDMind.Windows/Win32GlobalHotkeyManager.cs`, `native/HUDMind.Windows/Audio/Win32AudioCue.cs` |
| **REQ-FUNC-11** | Anti-repetition memory & cross-session continuity. | `InterventionMemory` blocks repeated callouts within TTL while isolating chat history; `SavePostMatchReview` writes redacted `.md` files to `logs/reviews/` and `LoadLatestPostMatchFocus` injects the last drill into the next match. | `native/HUDMind.Core/Pipeline/InterventionMemory.cs`, `native/HUDMind.Core/Config/ConfigRepository.cs` |
| **REQ-FUNC-12** | Native WPF graphical control center. | 4-tab UI with in-place account creation/editing (`BaseUrl`, `Vision Mode`), profile/`manifest.json` CRUD, Markdown review launcher, and `--smoke-startup` verification. | `native/HUDMind.App/MainWindow.xaml`, `native/HUDMind.App/MainWindow.xaml.cs` |
| **REQ-FUNC-13** | In-app GitHub Releases updater & assisted download. | Queries GitHub Releases on startup and every 60 min while idle (`!IsRunning`), ignores drafts/pre-releases, shows top `UpdateBanner` with **⬇ Descargar actualización**, matches `Standalone` vs compact `.exe`, saves to Downloads (`KnownFolders`), and cleanly launches the new `.exe`. | `native/HUDMind.Core/Updates/GitHubReleaseUpdateService.cs`, `native/HUDMind.Windows/KnownFolders.cs`, `native/HUDMind.App/MainWindow.xaml.cs` |

---

## 3. Non-Functional Requirements (ISO/IEC 25010)

| ID | ISO 25010 Characteristic | Requirement | Mechanism / Verification |
| :--- | :--- | :--- | :--- |
| **REQ-NFR-01** | Performance Efficiency | 0% dedicated GPU load for HUDMind; `StretchBlt` capture into reusable buffer and dHash frame deduplication before encoding or network calls. | `RenderMode.SoftwareOnly`, `FastScreenCapture`, and `W6` tests |
| **REQ-NFR-02** | Low Latency / Freshness | Drop stale frames via bounded `Channel` (`DropOldest`), enforce automatic *Freshness Deadline*, and wake immediately on `F10`. | Core tests `07`, `09`, `12` |
| **REQ-NFR-03** | Reliability & Fault Tolerance | Atomic recovery from corrupt `accounts.json` (with redacted backup), per-account *Circuit Breaker*, and transparent multi-provider or Live→Classic fallback. | Core tests `06`, `08`, `20`, `21`, `24` |
| **REQ-NFR-04** | Portability / Zero-Bloat | Zero external NuGet packages; distributable single-file `win-x64` binaries (`Standalone` and framework-dependent) published via GitHub Releases. | `HUDMind.App.csproj`, `.github/workflows/release.yml` |
| **REQ-NFR-05** | Maintainability & Modularity | Strict split into `HUDMind.Core` (cross-platform `net9.0`), `HUDMind.Windows` (Win32/Sensors/Audio), and `HUDMind.App` (WPF), continuously validated on Linux + Windows CI. | `.github/workflows/ci.yml`, `HUDMind.Tests`, `HUDMind.Windows.Tests` |

---

## 4. Security & Privacy Requirements (ISO/IEC 27001 / OWASP)

| ID | Requirement | Verified Implementation |
| :--- | :--- | :--- |
| **REQ-SEC-01** | Credential protection & clean logs (CWE-532 / CWE-598). | API keys in `config/accounts.json` (excluded from Git) are sent via HTTP/WebSocket headers, never query strings. `SecretRedactor` masks keys, `Bearer`, `Basic`, sensitive JSON fields, and tokens in logs, errors, backups, and `.md` reviews. |
| **REQ-SEC-02** | SSRF & cleartext prevention (CWE-918 / CWE-319). | `BaseUrlSecurityValidator` enforces remote HTTPS (loopback HTTP allowed only for local Ollama) and blocks private IPv4/IPv6 ranges, CGNAT, cloud metadata (`169.254.169.254`), NAT64, Teredo, 6to4, and embedded URI userinfo. |
| **REQ-SEC-03** | Path Traversal & symlink protection (CWE-22 / CWE-59). | `ConfigRepository` validates `SafeGameIdRegex`, blocks `..` and `ReparsePoint` in `profiles/`, `logs/`, and `logs/reviews/`; `LoopbackGsiService` validates local paths before reading `libraryfolders.vdf`. |
| **REQ-SEC-04** | Strict Riot loopback TLS & GSI HTTP bounds. | `LeagueLiveClientSensor` enforces `127.0.0.1:2999`, validity window, RSA>=2048/ECDSA>=256, loopback SAN, `Riot Games` identity, and active process. `LoopbackGsiService` bounds headers (`8KB`), body (`64KB`, `413`), rejects `chunked` (`400`), and caps concurrency. |
| **REQ-SEC-05** | Prompt-injection sanitization in OCR/telemetry. | `CoachingPersonaBuilder.SanitizeField` strips protocol delimiters (`\|`), XML/prompt tags (`<...>`) and truncates length before injecting context into prompts. |
| **REQ-SEC-06** | Update integrity & trusted download origin. | `GitHubReleaseUpdateService` allows HTTPS only to `github.com` / `.githubusercontent.com` initially and across up to 5 redirects, bounds reads (`512KB` API / `250MB` binary), and verifies `SHA-256` against `SHA256SUMS.txt`, deleting mismatched files. |
| **REQ-SEC-07** | Extensible safety chain & anti-extraction binary hardening. | `ICoachSafetyPipeline` / `CoachContentGuard` filter profanity and off-topic/NSFW prompts in real time; `HUDMind.Packer` seals default profiles and sensitive rules into `HUDMind.Vault.bin` (Brotli + AES-256-GCM) and `release.yml` packages hardened `ReadyToRun` single-file binaries guarded by `RuntimeHardeningGuard`. |
