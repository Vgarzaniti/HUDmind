> 🌐 **Idioma / Language:** **[🇪🇸 Español](test_plan_ieee29119.es.md)** | [🇺🇸 English](test_plan_ieee29119.en.md)

# Plan y Reporte de Pruebas de Software — ISO/IEC/IEEE 29119

**Proyecto:** HUDMind Native v1.6.0 (`.NET 9`)  
**Suites Automatizadas:** `native/HUDMind.Tests` (63 casos `net9.0` multiplataforma) + `native/HUDMind.Windows.Tests` (21 casos `net9.0-windows10.0.19041.0`) + smoke WPF (`--smoke-startup`)  
**Integración Continua:** [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) (`native-dotnet9-tests` en `ubuntu-latest` + `native-windows-build` en `windows-latest`)

---

## 1. Estrategia y Niveles de Prueba

1. **Suite Multiplataforma de Lógica y Seguridad (`HUDMind.Tests` — 63 pruebas):**
   - Cubre protocolo compacto, hashing perceptual (`dHash` 64-bit y `ComputeMeanAbsLumaDelta`), memoria anti-repetición y aislamiento de chat, redacción de secretos (incluyendo `rcon_password` y reportes redactados de crash/diagnóstico `DiagnosticsFormatter`), guardrails extensibles Chain-of-Responsibility (`ICoachSafetyPipeline` / `CoachContentGuard`: cero insultos multi-idioma, bloqueo anti-NSFW/gemidos/fotos/ilegales/off-topic y protección de markdown), validación SSRF/HTTPS/CWE-22 (incluyendo lectura/borrado seguro de resúmenes post-partida), protección DPAPI (`ISecretProtector` + migración + recuperación), reset de preferencias sin pérdida de cuentas (`ResetPreferencesToDefaults`) y persistencia de geometría/onboarding, cuentas locales multi-perfil con PIN PBKDF2-SHA256 (`UserProfileStore`), backups portables `.hudmind` AES-256-GCM (`ProfileArchiveCodec`), flujo OpenRouter OAuth PKCE loopback (`OpenRouterOAuthPkceClient`), failover router, *Hedged Vision*, *Freshness Deadline* (incluyendo descarte post-IA por latencia/cambio de fase), pacing por eventos (`event_first`), alertas locales determinísticas `<5 ms` (`InstantLocalAlert`), briefings en tiempo muerto (`BriefingNote`), entrenamiento por intentos + roles (`PlayerRoleTag`) y contador de fugas `Hoy vs Anterior` (`PlayerLeakTracker`), adaptadores Gemini/Anthropic/OpenAI-compatible, Live backends (Gemini Live / OpenAI / xAI Realtime) con deduplicación de frames estáticos pasivos, Push-to-Talk, memoria rodante de últimas `N` partidas + graduación de hábitos + `.md` enlazados (`coach_soul.md`, `player_profile.md`, `logs/memory/*.md`, `INDEX.md`, `CoachContextAssembler`), secuencias temporales estilo OBS 25s macro + ~10 FPS micro y energía visual de movimiento (`RecentFrameRingBuffer`), cola conversacional sin pisarse ni esquipearse con conectores naturales (`ConversationalSpeechCoordinator` / `NaturalConnectorResolver`), mini-feed overlay translúcido estilo Discord (`OverlayFeedBuffer`, coalescing de transcripción Live, promoción `Live->Final`, TTL por mensaje y round-trip `overlay_style`), servidor web embebido SSE `CompanionHudServer` (CSP, `X-Frame-Options`, XSS `textContent`, auth LAN por token, modo transparente `?mode=obs` y fan-out `CompositeOverlayService`), codificador QR ISO 0% GPU (`QrMatrixEncoder`), protocolo loopback `InGameOverlayBridgeProtocol` (sanitización anti CWE-77 de comandos `toast`, idempotencia de allowlist y `EnsureRconConfigEnabled`) + normalizador genérico `ExternalTelemetryParser` (`hudmind:telemetry` + relays de partida), compilación de conocimiento por juego (`GameKnowledgeCompiler`), coaching predictivo, evaluador táctico determinista (`LOCAL_WS_TELEMETRY`, GSI, logs) + detección Anti-Tilt (`TacticalSignalEvaluator`), resúmenes post-partida + foco entre sesiones, coordinación compartida `MatchSessionCoordinator`, vault cifrado AES-256-GCM (`HUDMind.Vault.bin` / `ProfileVaultCodec` / `ProfileVaultReader`) + cero recursos `.md`/`.json` en texto plano, diagnóstico de pantalla negra, extensibilidad de `ProviderRegistry`, contrato polimórfico `ICoachEngine`, `HudPhaseClassifier`, DSP PCM (Catmull-Rom + fades + escudo de ruido + cola VAD 760ms + barge-in sostenido) y `GitHubReleaseUpdateService`.
2. **Suite Nativa Windows / Win32 / Sensores / Audio (`HUDMind.Windows.Tests` — 21 pruebas):**
   - Cubre enumeración/resolución `Win32AudioDevices` (`winmm.dll`), `Win32StreamingPcmPlayer` (`PreOpenDevice()`, pre-buffer jitter 300ms, recuperación estable 100ms, retención tail-fade 4ms, lead-in silencioso 40ms, slicing DMA <=400ms, liberación segura de `WAVEHDR` y corte limpio — verificado en modo virtual determinista sobre CI headless y en hardware WinMM), `WasapiAudioSceneMonitor` (medidor COM pasivo de picos de audio externos), `Win32TemporalStoryboardComposer` (tiras `2x1`, storyboards `2x2` y filmstrips 6-paneles `3x2` CPU), receptor TCP loopback HTTP `LoopbackGsiService` (CS2 con countdown C4/kit/helmet + Dota 2 + limpieza al volver a menú + rechazo HTTP `413`/`400` chunked + cierre acotado con cliente colgado + validación anti-UNC/reparse), `TailLogSensorBase` (UTF-8, UTF-16 LE, cutoff de antigüedad y scope por juego), `LeagueLiveClientSensor` (pinning X.509/SAN/Riot Games/vigencia + `allgamedata` CS/Wards/DEAD), `LoopbackWebSocketTelemetrySensor` multi-juego con puerto configurable, `InGameOverlayBridgeSetup.ConfigureDirectoryAsync` (`rcon_commands.cfg` + `rcon_enabled` seguro), `TextOnlySensorService.ReadTelemetryAsync` + caché de zonas OCR sin dejar logs obsoletos (`ExtractAsync`), `WinRtNaturalVoiceService` / `InstantSapiVoiceService` / `HybridVoiceService` (WAV→24kHz PCM, encolado continuo sin corte y tasas SAPI/WinRT), smoke integrado de `Win32DisplayHelper` (`TryGetMonitorIndexForWindow` + `ClampPixelRectToWorkingArea`), `SingleInstancePipeServer` IPC de instancia única por usuario Windows, `SystemDiagnosticsProbe`, `Win32KeyStateProbe`, `Win32WindowDetector` (+ restauración de foco), `FastScreenCapture` (`SparseLumaDelta`) y `Win32AudioCue`, cifrado nativo multi-perfil `Win32DpapiSecretProtector` (`crypt32.dll` + `UserProfileStore`), inicio opcional `WindowsStartupManager` en `HKCU` desactivado por defecto, integridad runtime `RuntimeHardeningGuard` + vault AES-256-GCM, y resolución de carpeta Descargas via `KnownFolders.GetDownloadsDirectory()`.
3. **Verificación de Arranque WPF (`--smoke-startup`):**
   - Instancia `MainWindow` fuera de pantalla con `RenderMode.SoftwareOnly`, enlaza XAML, bandeja y configuraciones, y cierra limpiamente con código `0` en `windows-latest` (validado también sobre el binario single-file publicado en `release.yml`).

---

## 2. Matriz de Trazabilidad de Requisitos (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requisito | Descripción Resumida | Casos de Prueba Verificados |
| :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Perfiles embebidos, reglas custom, manifest y creador/borrado CWE-22 | `HUDMind.Tests` (`06`, `24`, `30`, `32`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-03** | Captura GDI `StretchBlt`, pHash + delta luma (`MeanAbsLumaDelta`), ring buffer OBS dual macro (25s) + ráfaga (~10 FPS), disparo por pico de movimiento, deduplicación WS pasiva, multi-cuadro HD / storyboard `Win32` y pantalla negra | `HUDMind.Tests` (`02`, `34`, `44`, `46`, `48`, `49`, `50`, `55`), `HUDMind.Windows.Tests` (`W6`, `W11`, `W17`) |
| **REQ-FUNC-04** | Modos `conversation`, `coach_voice` (con auto-cadencia proactiva) y `text_only` | `HUDMind.Tests` (`16`, `23`, `48`, `49`, `50`) |
| **REQ-FUNC-05** | Protocolo `FASE\|QUE_VEO\|URG\|CONSEJO`, predictivo 1–3s, conocimiento por juego, descarte post-IA por latencia/fase y supresión menú/replay | `HUDMind.Tests` (`01`, `13`, `29`, `44`, `46`, `55`) |
| **REQ-FUNC-06** | `ProviderRegistry`, failover `429/422`, *Hedged Vision* y *Freshness* | `HUDMind.Tests` (`07`, `08`, `09`, `10`, `11`, `20`, `21`, `28`) |
| **REQ-FUNC-07** | Sensores pasivos GSI TCP, Riot Live TLS, telemetría WebSocket loopback configurable (`LoopbackWebSocketTelemetrySensor` / `ws://127.0.0.1` + `hudmind:telemetry`), logs locales (sin PII), evaluador táctico, Anti-Tilt y OCR con caché manteniendo telemetría fresca | `HUDMind.Tests` (`13`, `39`, `41`, `56`, `58`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W4`, `W5`, `W9`, `W17`, `W18`, `W19`) |
| **REQ-FUNC-08 / 11** | Persona/`coach_soul.md`, roles en partida, pacing `event_first`, entrenamiento por intentos, tracker de fugas `Hoy vs Anterior` (con agregado `universal`), memoria rodante `N` partidas, `.md` enlazados y post-partida | `HUDMind.Tests` (`03`, `15`, `18`, `19`, `27`, `31`, `33`, `37`, `38`, `40`, `41`, `42`, `47`, `61`, `62`) |
| **REQ-FUNC-09 / 10 / 12** | Live WebSocket, PTT, hotkeys (`F7` Quick-Chat), WinRT TTS/OneCore, overlay translúcido 0% GPU (`OverlayFeedBuffer`, alertas `<5 ms` + arrastre libre personalizado + clamping multi-monitor), Companion Web HUD HTTP SSE (`CompanionHudServer` + `?mode=obs` + `QrMatrixEncoder` + `CompositeOverlayService`), puente Toast RCON loopback asistido (`LoopbackRconToastBridge`), supresión Live `NONE` y frescura `<3.5s`, inicio HKCU opcional, IPC instancia única (`SingleInstancePipeServer`), diagnóstico (`SystemDiagnosticsProbe`), espera WASAPI, DMA WinMM/barge-in y tonos | `HUDMind.Tests` (`12`, `14`, `22`, `23`, `24`, `25`, `26`, `29`, `31`, `43`, `49`, `50`, `51`, `52`, `53`, `54`, `56`, `57`, `58`, `61`, `62`, `63`), `HUDMind.Windows.Tests` (`W1`, `W6`, `W7`, `W10`, `W12`, `W14`, `W15`, `W16`, `W17`, `W18`, `W19`, `W21`), `--smoke-startup` |
| **REQ-FUNC-13 / SEC-06** | Verificador in-app GitHub Releases, Setup 1-clic, redirecciones seguras, Descargas y `SHA-256` | `HUDMind.Tests` (`35`, `36`), `HUDMind.Windows.Tests` (`W8`) |
| **REQ-FUNC-14 / SEC-01..07** | Perfiles personales `UserProfileStore` (PIN PBKDF2-SHA256), backups portables `.hudmind` AES-256-GCM con metadatos y cuentas opcionales (`ProfileArchiveCodec`), OpenRouter OAuth PKCE (`OpenRouterOAuthPkceClient`), DPAPI multi-perfil `accounts.json` (`crypt32.dll`), redacción (incl. reportes `DiagnosticsFormatter`), SSRF, CWE-22 (incl. resúmenes post-partida), CWE-77, UNC/ReparsePoint, TLS pinning, Companion CSP/XSS + token LAN, `ICoachSafetyPipeline`, vault AES-256-GCM (`HUDMind.Vault.bin`) y `RuntimeHardeningGuard` | `HUDMind.Tests` (`04`, `05`, `06`, `17`, `24`, `30`, `31`, `32`, `33`, `38`, `45`, `47`, `52`, `54`, `56`, `57`, `58`, `59`, `60`, `62`, `63`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W5`, `W13`, `W16`, `W18`, `W19`, `W20`, `W21`) |

---

## 3. Comandos de Ejecución Local y CI

```powershell
# 1. Compilar toda la solución (0 warnings, 0 errors esperado)
dotnet build native/HUDMind.sln -c Release

# 2. Ejecutar suite multiplataforma Core (63 pruebas)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Ejecutar suite nativa Windows / Win32 / Sensores / Audio (21 pruebas)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Smoke test real de arranque WPF (--smoke-startup)
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 5. Publicar binario single-file win-x64 (o con --self-contained true si falta .NET 9 Runtime)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
```
