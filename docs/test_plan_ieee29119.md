# Plan y Reporte de Pruebas de Software — ISO/IEC/IEEE 29119

**Proyecto:** HUDMind Native v1.1.0 (`.NET 9`)  
**Suites Automatizadas:** `native/HUDMind.Tests` (31 casos `net9.0` multiplataforma) + `native/HUDMind.Windows.Tests` (6 casos `net9.0-windows10.0.19041.0`)  
**Integración Continua:** [`.github/workflows/ci.yml`](file:///c:/Users/Hogar/Desktop/CouchIA/.github/workflows/ci.yml) (`native-dotnet9-tests` en `ubuntu-latest` + `native-windows-build` en `windows-latest`)

---

## 1. Estrategia y Niveles de Prueba

1. **Suite Multiplataforma de Lógica y Seguridad (`HUDMind.Tests` — 31 pruebas):**
   - Cubre protocolo compacto, hashing perceptual (`dHash` 64-bit), memoria anti-repetición y aislamiento de chat, redacción de secretos, validación SSRF/HTTPS/CWE-22, failover router, *Hedged Vision*, *Freshness Deadline*, adaptadores Gemini/Anthropic/OpenAI-compatible, Live backends (Gemini Live / OpenAI / xAI Realtime), Push-to-Talk, resúmenes post-partida, extensibilidad de `ProviderRegistry`, contrato polimórfico `ICoachEngine`, `HudPhaseClassifier`, DSP PCM y creación persistente de perfiles/resúmenes.
2. **Suite Nativa Windows / Win32 / Sensores / Audio (`HUDMind.Windows.Tests` — 6 pruebas):**
   - Cubre enumeración/resolución `Win32AudioDevices` (`winmm.dll`), receptor TCP loopback HTTP `LoopbackGsiService` (CS2 + Dota 2 + limpieza al volver a menú + rechazo HTTP `413`/`400` chunked + cierre acotado con cliente colgado + validación anti-UNC/reparse), `TailLogSensorBase` (UTF-8, UTF-16 LE, cutoff de antigüedad y scope por juego), `LeagueLiveClientSensor` (pinning X.509/SAN/Riot Games/vigencia), y prueba integrada de `Win32DisplayHelper`, `Win32KeyStateProbe`, `Win32WindowDetector`, `FastScreenCapture` y `Win32AudioCue`.

---

## 2. Matriz de Trazabilidad de Requisitos (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requisito | Descripción Resumida | Casos de Prueba Verificados |
| :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Perfiles, reglas custom, creador CWE-22 y `.exe` | `HUDMind.Tests` (`06`, `24`, `30`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-03** | Captura GDI `StretchBlt` + pHash | `HUDMind.Tests` (`02`), `HUDMind.Windows.Tests` (`W6`) |
| **REQ-FUNC-04** | Modos `conversation`, `coach_voice` y `text_only` | `HUDMind.Tests` (`16`, `23`) |
| **REQ-FUNC-05** | Protocolo `FASE\|QUE_VEO\|URG\|CONSEJO` y supresión menú/replay | `HUDMind.Tests` (`01`, `13`, `29`) |
| **REQ-FUNC-06** | `ProviderRegistry`, failover `429/422`, *Hedged Vision* y *Freshness* | `HUDMind.Tests` (`07`, `08`, `09`, `10`, `11`, `20`, `21`, `28`) |
| **REQ-FUNC-07** | Sensores pasivos GSI TCP, Riot Live TLS, logs UE/Valorant y OCR | `HUDMind.Tests` (`13`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W4`, `W5`) |
| **REQ-FUNC-08 / 11** | Persona por rango/tier, memoria anti-repetición y resumen post-partida | `HUDMind.Tests` (`03`, `15`, `18`, `19`, `27`, `31`) |
| **REQ-FUNC-09 / 10** | Live WebSocket, PTT, hotkeys, audio WinMM y tonos sutiles | `HUDMind.Tests` (`12`, `14`, `22`, `23`, `24`, `25`, `26`, `29`, `31`), `HUDMind.Windows.Tests` (`W1`, `W6`) |
| **REQ-SEC-01..05** | Redacción, SSRF, CWE-22, UNC, HTTP bounds y TLS loopback pinning | `HUDMind.Tests` (`04`, `05`, `06`, `17`, `24`, `30`, `31`), `HUDMind.Windows.Tests` (`W2`, `W3`, `W5`) |

---

## 3. Comandos de Ejecución Local y CI

```powershell
# 1. Compilar toda la solución (0 warnings, 0 errors esperado)
dotnet build native/HUDMind.sln -c Release

# 2. Ejecutar suite multiplataforma Core (31 pruebas)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Ejecutar suite nativa Windows / Win32 / Sensores / Audio (6 pruebas)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Publicar binario single-file win-x64 (dependiente de runtime .NET 9 Desktop)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
```
