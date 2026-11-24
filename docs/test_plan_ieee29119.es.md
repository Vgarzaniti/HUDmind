> 🌐 **Idioma / Language:** **[🇪🇸 Español](test_plan_ieee29119.es.md)** | [🇺🇸 English](test_plan_ieee29119.en.md)

# Plan y Reporte de Pruebas de Software — ISO/IEC/IEEE 29119

**Proyecto:** HUDMind Native v1.2.1 (`.NET 9`)  
**Suites Automatizadas:** `native/HUDMind.Tests` (49 casos `net9.0` multiplataforma) + `native/HUDMind.Windows.Tests` (14 casos `net9.0-windows10.0.19041.0`) + smoke WPF (`--smoke-startup`)  
**Integración Continua:** [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) (`native-dotnet9-tests` en `ubuntu-latest` + `native-windows-build` en `windows-latest`)

---

## 1. Estrategia y Niveles de Prueba

1. **Suite Multiplataforma de Lógica y Seguridad (`HUDMind.Tests` — 49 pruebas):**
   - Cubre protocolo compacto, hashing perceptual (`dHash` 64-bit), memoria anti-repetición y aislamiento de chat, redacción de secretos, guardrails extensibles Chain-of-Responsibility (`ICoachSafetyPipeline` / `CoachContentGuard`: cero insultos multi-idioma, bloqueo anti-NSFW/gemidos/fotos/ilegales/off-topic y protección de markdown), validación SSRF/HTTPS/CWE-22, failover router, *Hedged Vision*, *Freshness Deadline*, adaptadores Gemini/Anthropic/OpenAI-compatible, Live backends (Gemini Live / OpenAI / xAI Realtime), Push-to-Talk, memoria rodante de últimas `N` partidas + graduación de hábitos + `.md` enlazados (`coach_soul.md`, `player_profile.md`, `logs/memory/*.md`, `INDEX.md`, `CoachContextAssembler`), secuencias temporales estilo OBS 25s macro + ~10 FPS micro y energía visual de movimiento (`RecentFrameRingBuffer`), compilación de conocimiento por juego (`GameKnowledgeCompiler`), coaching predictivo, evaluador táctico determinista + detección Anti-Tilt (`TacticalSignalEvaluator`), resúmenes post-partida + foco entre sesiones, coordinación compartida `MatchSessionCoordinator`, vault cifrado AES-256-GCM (`HUDMind.Vault.bin` / `ProfileVaultCodec` / `ProfileVaultReader`) + cero recursos `.md`/`.json` en texto plano, diagnóstico de pantalla negra, extensibilidad de `ProviderRegistry`, contrato polimórfico `ICoachEngine`, `HudPhaseClassifier`, DSP PCM (Catmull-Rom + fades + escudo de ruido + barge-in sostenido) y `GitHubReleaseUpdateService`.
2. **Suite Nativa Windows / Win32 / Sensores / Audio (`HUDMind.Windows.Tests` — 14 pruebas):**
   - Cubre enumeración/resolución `Win32AudioDevices` (`winmm.dll`), `Win32StreamingPcmPlayer` (`PreOpenDevice()`, pre-buffer jitter 300ms, recuperación de corte mid-turn 200ms, lead-in silencioso 40ms, chunks estables 120ms / slicing DMA <=400ms, padding final y corte limpio), `Win32TemporalStoryboardComposer` (tira 2x1 y storyboard 2x2 CPU), receptor TCP loopback HTTP `LoopbackGsiService` (CS2 con countdown C4/kit/helmet + Dota 2 + limpieza al volver a menú + rechazo HTTP `413`/`400` chunked + cierre acotado con cliente colgado + validación anti-UNC/reparse), `TailLogSensorBase` (UTF-8, UTF-16 LE, cutoff de antigüedad y scope por juego), `LeagueLiveClientSensor` (pinning X.509/SAN/Riot Games/vigencia + `allgamedata` CS/Wards/DEAD), `TextOnlySensorService.ReadTelemetryAsync`, `WinRtNaturalVoiceService` / `HybridVoiceService` (WAV→24kHz PCM y tasas SAPI/WinRT), smoke integrado de `Win32DisplayHelper`, `Win32KeyStateProbe`, `Win32WindowDetector`, `FastScreenCapture` y `Win32AudioCue`, integridad runtime `RuntimeHardeningGuard` + vault AES-256-GCM, y resolución de carpeta Descargas via `KnownFolders.GetDownloadsDirectory()`.
3. **Verificación de Arranque WPF (`--smoke-startup`):**
   - Instancia `MainWindow` fuera de pantalla con `RenderMode.SoftwareOnly`, enlaza XAML, bandeja y configuraciones, y cierra limpiamente con código `0` en `windows-latest` (validado también sobre el binario single-file publicado en `release.yml`).

---

## 2. Matriz de Trazabilidad de Requisitos (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requisito | Descripción Resumida | Casos de Prueba Verificados |
| :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Perfiles embebidos, reglas custom, manifest y creador/borrado CWE-22 | `HUDMind.Tests` (`06`, `24`, `30`, `32`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-03** | Captura GDI `StretchBlt`, pHash, ring buffer OBS dual macro (25s) + ráfaga (~10 FPS), disparo por pico de movimiento, multi-cuadro HD / storyboard `Win32` y pantalla negra | `HUDMind.Tests` (`02`, `34`, `44`, `46`, `48`, `49`), `HUDMind.Windows.Tests` (`W6`, `W11`) |
| **REQ-FUNC-04** | Modos `conversation`, `coach_voice` (con auto-cadencia proactiva) y `text_only` | `HUDMind.Tests` (`16`, `23`, `48`, `49`) |
| **REQ-FUNC-05** | Protocolo `FASE\|QUE_VEO\|URG\|CONSEJO`, predictivo 1–3s, conocimiento por juego y supresión menú/replay | `HUDMind.Tests` (`01`, `13`, `29`, `44`, `46`) |
| **REQ-FUNC-06** | `ProviderRegistry`, failover `429/422`, *Hedged Vision* y *Freshness* | `HUDMind.Tests` (`07`, `08`, `09`, `10`, `11`, `20`, `21`, `28`) |
| **REQ-FUNC-07** | Sensores pasivos GSI TCP, Riot Live TLS, logs UE/Valorant, evaluador táctico, Anti-Tilt y OCR | `HUDMind.Tests` (`13`, `39`, `41`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W4`, `W5`, `W9`) |
| **REQ-FUNC-08 / 11** | Persona/`coach_soul.md`, perfil global, memoria rodante `N` partidas, `.md` enlazados, aislamiento `MatchSessionCoordinator` y post-partida | `HUDMind.Tests` (`03`, `15`, `18`, `19`, `27`, `31`, `33`, `37`, `38`, `40`, `41`, `42`, `47`) |
| **REQ-FUNC-09 / 10** | Live WebSocket, PTT, hotkeys, WinRT TTS/OneCore, audio WinMM (pre-apertura DAC, prime 300ms, recuperación 200ms, lead-in, slicing DMA <=400ms, escudo de ruido y barge-in) y tonos sutiles | `HUDMind.Tests` (`12`, `14`, `22`, `23`, `24`, `25`, `26`, `29`, `31`, `43`, `49`), `HUDMind.Windows.Tests` (`W1`, `W6`, `W7`, `W10`, `W12`, `W14`), `--smoke-startup` |
| **REQ-FUNC-13 / SEC-06** | Verificador in-app GitHub Releases, redirecciones seguras, carpeta Descargas y `SHA-256` | `HUDMind.Tests` (`35`, `36`), `HUDMind.Windows.Tests` (`W8`) |
| **REQ-SEC-01..07** | Redacción, SSRF, CWE-22, UNC, TLS pinning, `ICoachSafetyPipeline`, vault AES-256-GCM (`HUDMind.Vault.bin`) y `RuntimeHardeningGuard` | `HUDMind.Tests` (`04`, `05`, `06`, `17`, `24`, `30`, `31`, `32`, `33`, `38`, `45`, `47`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W5`, `W13`) |

---

## 3. Comandos de Ejecución Local y CI

```powershell
# 1. Compilar toda la solución (0 warnings, 0 errors esperado)
dotnet build native/HUDMind.sln -c Release

# 2. Ejecutar suite multiplataforma Core (49 pruebas)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Ejecutar suite nativa Windows / Win32 / Sensores / Audio (14 pruebas)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Smoke test real de arranque WPF (--smoke-startup)
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 5. Publicar binario single-file win-x64 (o con --self-contained true si falta .NET 9 Runtime)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
```
