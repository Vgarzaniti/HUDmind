# Plan y Reporte de Pruebas de Software — ISO/IEC/IEEE 29119

**Proyecto:** HUDMind Native v1.1.0 (`.NET 9`)  
**Suites Automatizadas:** `native/HUDMind.Tests` (34 casos `net9.0` multiplataforma) + `native/HUDMind.Windows.Tests` (7 casos `net9.0-windows10.0.19041.0`) + smoke WPF (`--smoke-startup`)  
**Integración Continua:** [`.github/workflows/ci.yml`](file:///c:/Users/Hogar/Desktop/CouchIA/.github/workflows/ci.yml) (`native-dotnet9-tests` en `ubuntu-latest` + `native-windows-build` en `windows-latest`)

---

## 1. Estrategia y Niveles de Prueba

1. **Suite Multiplataforma de Lógica y Seguridad (`HUDMind.Tests` — 34 pruebas):**
   - Cubre protocolo compacto, hashing perceptual (`dHash` 64-bit), memoria anti-repetición y aislamiento de chat, redacción de secretos, validación SSRF/HTTPS/CWE-22, failover router, *Hedged Vision*, *Freshness Deadline*, adaptadores Gemini/Anthropic/OpenAI-compatible, Live backends (Gemini Live / OpenAI / xAI Realtime), Push-to-Talk, resúmenes post-partida + foco entre sesiones, perfiles embebidos + actualización/borrado seguro, diagnóstico de pantalla negra, extensibilidad de `ProviderRegistry`, contrato polimórfico `ICoachEngine`, `HudPhaseClassifier` y DSP PCM.
2. **Suite Nativa Windows / Win32 / Sensores / Audio (`HUDMind.Windows.Tests` — 7 pruebas):**
   - Cubre enumeración/resolución `Win32AudioDevices` (`winmm.dll`), receptor TCP loopback HTTP `LoopbackGsiService` (CS2 + Dota 2 + limpieza al volver a menú + rechazo HTTP `413`/`400` chunked + cierre acotado con cliente colgado + validación anti-UNC/reparse), `TailLogSensorBase` (UTF-8, UTF-16 LE, cutoff de antigüedad y scope por juego), `LeagueLiveClientSensor` (pinning X.509/SAN/Riot Games/vigencia), `WinRtNaturalVoiceService` / `HybridVoiceService` (WAV→24kHz PCM y tasa), y smoke integrado de `Win32DisplayHelper`, `Win32KeyStateProbe`, `Win32WindowDetector`, `FastScreenCapture` y `Win32AudioCue`.
3. **Verificación de Arranque WPF (`--smoke-startup`):**
   - Instancia `MainWindow` fuera de pantalla con `RenderMode.SoftwareOnly`, enlaza XAML, bandeja y configuraciones, y cierra limpiamente con código `0` en `windows-latest`.

---

## 2. Matriz de Trazabilidad de Requisitos (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requisito | Descripción Resumida | Casos de Prueba Verificados |
| :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Perfiles embebidos, reglas custom, manifest y creador/borrado CWE-22 | `HUDMind.Tests` (`06`, `24`, `30`, `32`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-03** | Captura GDI `StretchBlt`, pHash y diagnóstico de pantalla negra | `HUDMind.Tests` (`02`, `34`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-04** | Modos `conversation`, `coach_voice` y `text_only` | `HUDMind.Tests` (`16`, `23`) |
| **REQ-FUNC-05** | Protocolo `FASE\|QUE_VEO\|URG\|CONSEJO` y supresión menú/replay | `HUDMind.Tests` (`01`, `13`, `29`) |
| **REQ-FUNC-06** | `ProviderRegistry`, failover `429/422`, *Hedged Vision* y *Freshness* | `HUDMind.Tests` (`07`, `08`, `09`, `10`, `11`, `20`, `21`, `28`) |
| **REQ-FUNC-07** | Sensores pasivos GSI TCP, Riot Live TLS, logs UE/Valorant y OCR | `HUDMind.Tests` (`13`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W4`, `W5`) |
| **REQ-FUNC-08 / 11** | Persona por rango/tier, memoria anti-repetición, resumen post-partida y foco entre sesiones | `HUDMind.Tests` (`03`, `15`, `18`, `19`, `27`, `31`, `33`) |
| **REQ-FUNC-09 / 10** | Live WebSocket, PTT, hotkeys, WinRT TTS/OneCore, audio WinMM y tonos sutiles | `HUDMind.Tests` (`12`, `14`, `22`, `23`, `24`, `25`, `26`, `29`, `31`), `HUDMind.Windows.Tests` (`W1`, `W6`, `W7`), `--smoke-startup` |
| **REQ-SEC-01..05** | Redacción, SSRF, CWE-22, UNC, HTTP bounds y TLS loopback pinning | `HUDMind.Tests` (`04`, `05`, `06`, `17`, `24`, `30`, `31`, `32`, `33`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W5`) |

---

## 3. Comandos de Ejecución Local y CI

```powershell
# 1. Compilar toda la solución (0 warnings, 0 errors esperado)
dotnet build native/HUDMind.sln -c Release

# 2. Ejecutar suite multiplataforma Core (34 pruebas)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Ejecutar suite nativa Windows / Win32 / Sensores / Audio (7 pruebas)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Smoke test real de arranque WPF (--smoke-startup)
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 5. Publicar binario single-file win-x64 (o con --self-contained true si falta .NET 9 Runtime)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
```
