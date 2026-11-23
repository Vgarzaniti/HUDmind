> 🌐 **Idioma / Language:** **[🇪🇸 Español](AGENTS.es.md)** | [🇺🇸 English](AGENTS.en.md)

# Guía de Contexto para IAs — HUDMind Native

Leé este documento primero antes de modificar código, pruebas, documentación, workflows o abrir un PR en **HUDMind**.

---

## 1. Qué es HUDMind (Modelo Mental en 30 Segundos)

**HUDMind** es un copiloto y coach táctico de videojuegos en tiempo real para **Windows 10/11 (x64)** construido sobre **.NET 9 + WPF + Win32 puro**, distribuido como ejecutables single-file (`HUDMind.Native-Standalone-win-x64.exe` y `HUDMind.Native-win-x64.exe`) vía GitHub Releases (`Vgarzaniti/HUDmind`).

### Invariantes Arquitectónicos y de Seguridad (NO Violar Nunca)
1. **0% GPU para HUDMind:** `App.xaml.cs` fuerza `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly`. Nunca agregues shaders GPU en WPF (ej. `DropShadowEffect`) ni hooks DXGI/GPU.
2. **Cero Paquetes NuGet Externos:** Todos los `.csproj` usan únicamente la BCL de .NET 9, WinRT (`net9.0-windows10.0.19041.0`) y P/Invoke Win32 directo (`user32`, `gdi32`, `kernel32`, `winmm`, `shell32`). No agregues dependencias NuGet sin pedido explícito.
3. **100% Pasivo / Anti-Cheat Safe:**
   - Captura externa GDI `StretchBlt` sobre `GetClientRect` (`FastScreenCapture.cs`).
   - Detección de foco por `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` cacheada por `(HWND, PID)` (`Win32WindowDetector.cs`).
   - Atajos globales vía `RegisterHotKey` (`Win32GlobalHotkeyManager.cs`); Push-to-Talk por consulta directa `GetAsyncKeyState` (`Win32KeyStateProbe` en `Win32GlobalHotkeyManager.cs`).
   - **NUNCA** inyectes DLLs, leas/escribas memoria de juegos ni instales hooks globales (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`).
4. **Fronteras Estrictas de Seguridad:**
   - **SSRF / Texto claro (`BaseUrlSecurityValidator` y `SharedHttpFactory`):** Exigir HTTPS remoto (HTTP en loopback permitido solo para Ollama local), mantener `AllowAutoRedirect = false` y bloquear rangos privados/CGNAT/NAT64/Teredo/6to4/metadata.
   - **Actualizaciones GitHub (`GitHubReleaseUpdateService`):** Permitir redirecciones HTTPS (`<= 5` saltos) **solo** hacia `github.com` / `.githubusercontent.com`, limitar tamaño de respuestas y validar `SHA-256` contra `SHA256SUMS.txt` cuando el archivo de checksums existe, es legible y lista el binario descargado (fallando y borrando el archivo si no coincide). Si falta `SHA256SUMS.txt` o no lista el archivo, la descarga finaliza sin verificación y la opción para abrirlo sigue habilitada en `MainWindow`.
   - **Path Traversal (`ConfigRepository` y `LoopbackGsiService`):** Validar IDs (`^[a-z0-9_\-]{1,64}$`), bloquear `..`, rutas UNC y symlinks (`ReparsePoint`); escribir configuraciones de forma atómica (`AtomicWrite`).
   - **Secretos (`SecretRedactor`):** Enmascarar API keys/tokens en logs, errores, backups corruptos y resúmenes Markdown.
   - **Sanitización de prompts (`CoachingPersonaBuilder.SanitizeField`):** Limpiar delimitadores (`|`) y tags XML (`<...>`) provenientes de OCR o telemetría antes de inyectarlos al prompt.
5. **Separación Multiplataforma del Core:** Mantener `native/HUDMind.Core` estrictamente en `net9.0` (sin referencias a `Windows`, `WPF` ni `WinForms`) para que `HUDMind.Tests` corra tanto en `ubuntu-latest` como en `windows-latest`.
6. **Reglas de Archivos Legados y Perfiles Embebidos:**
   - **No** modifiques `archive/legacy_python/` ni reintroduzcas dependencias Python en runtime.
   - **No** renombres `profiles/*/*.md` (`system_prompt.md`, `knowledge_base.md`): son recursos embebidos cargados en runtime por `ConfigRepository`.

---

## 2. Mapa del Repositorio ("Qué es Cada Cosa")

| Ruta | Target / Rol | Tipos Clave y Responsabilidades |
| :--- | :--- | :--- |
| `native/HUDMind.Core/` | Núcleo `net9.0` multiplataforma | Modelos, config atómica, perfiles embebidos, enrutamiento IA, protocolo compacto, pipelines, actualizador |
| `native/HUDMind.Core/Models/DomainModels.cs` | Modelos de dominio | `UserConfig` (preserva `ExtraFields` Python), `ProviderAccount`, `GameProfile`, `GameManifest`, `CoachResponse`, `UrgencyLevel`, `HudSensorSnapshot`, `CoachRuntimeStats` |
| `native/HUDMind.Core/Config/ConfigRepository.cs` | Config y seguridad | `ConfigRepository`, `SecretRedactor`, `BaseUrlSecurityValidator`, `HotkeyParser`, `EnsureDefaultProfilesSeeded` |
| `native/HUDMind.Core/AI/` | Motores IA y adaptadores | `ProviderRegistry`, `ProviderFailoverRouter` (hedged vision + circuit breaker), `SharedHttpFactory`, `CompactWireProtocol` (`FASE\|QUE_VEO\|URG\|CONSEJO`), `CoachingPersonaBuilder`, `LiveCoachEngine` (`RealtimeLiveCoachOrchestrator`), `Adapters/*`, `LiveBackends/*` |
| `native/HUDMind.Core/Engine/` | Motor clásico por turnos | `Abstractions.cs` (`ICoachEngine`, `IScreenCaptureService`, `IForegroundWindowDetector`, `IVoiceOutputService`, `IOverlayOutputService`, `ITextOnlySensorService`), `CoachOrchestrator.cs` |
| `native/HUDMind.Core/Pipeline/` | Utilidades frame/audio | `PerceptualHasher` (dHash 64-bit), `InterventionMemory` (anti-repetición TTL + aislamiento de chat), `HudPhaseClassifier`, `AudioPcmUtilities`, `BlackScreenDiagnostic` |
| `native/HUDMind.Core/Updates/GitHubReleaseUpdateService.cs` | Verificador y descarga segura | `CheckForUpdatesAsync`, `ParseLatestReleaseJson`, `SelectPreferredAssets`, `DownloadAssetAsync` (allowlist GitHub + `SHA-256`) |
| `native/HUDMind.Windows/` | `net9.0-windows10.0.19041.0` | Captura GDI, detección Win32, hotkeys, audio WinMM, voz/OCR WinRT, sensores pasivos, `KnownFolders` |
| `native/HUDMind.Windows/Sensors/` | Sensores pasivos | `TailLogSensorBase`, `RocketLeagueLogSensor`, `ValorantLogSensor`, `ValveGsiTelemetrySensor`, `LeagueLiveClientSensor` |
| `native/HUDMind.Windows/Audio/` | Audio nativo WinMM / WinRT | `Win32AudioDevices`, `Win32StreamingMicCapture` (16kHz), `Win32StreamingPcmPlayer` (24kHz), `WinRtNaturalVoiceService`, `Win32AudioCue`, `AudioDiagnosticsService` |
| `native/HUDMind.App/` | UI WPF (`HUDMind.Native.exe`) | `App.xaml.cs` (SoftwareOnly + mutex + `ReleaseSingleInstanceMutex`), `MainWindow.xaml/.cs` (4 pestañas + `UpdateBanner`), `OverlayWindow.xaml/.cs` (click-through PerMonitorV2), `Services/CoachEngineCoordinator.cs`, `Win32TrayIcon.cs` |
| `native/HUDMind.Tests/` | Suite `net9.0` | 43 pruebas unitarias/integración multiplataforma (`Program.cs`) |
| `native/HUDMind.Windows.Tests/` | Suite `net9.0-windows` | 10 pruebas nativas de WinMM, GSI, TLS pinning, logs, captura, voz y `KnownFolders` (`Program.cs`) |
| `profiles/` | Cartuchos de juegos | 7 perfiles incluidos (`aoe2_de`, `cs2`, `dota2`, `league_of_legends`, `rob_run`, `rocket_league`, `valorant`) embebidos en `HUDMind.Core.csproj` |
| `docs/` | Documentación bilingüe | Todos los docs tienen par `.es.md` y `.en.md` (`architecture`, `srs`, `test_plan_ieee29119`, `game_profiles_guide`, `versioning_and_releases`, `pull_request_guide`) |
| `.github/` | CI/CD y gobernanza | `workflows/ci.yml`, `workflows/release.yml`, `PULL_REQUEST_TEMPLATE.md`, `ISSUE_TEMPLATE/*`, `dependabot.yml` |

---

## 3. Cómo Funciona en Tiempo de Ejecución

1. **Arranque y Persistencia:** `App.OnStartup` fija `RenderMode.SoftwareOnly`, protege instancia única con Mutex (`--smoke-startup` lo omite y cierra limpio en `MainWindow_Loaded`), inicializa `ConfigRepository` (`%LocalAppData%\HUDMind` cuando corre standalone, o `profiles/`+`config/` del repo en dev) y extrae los 7 perfiles embebidos si faltan.
2. **Actualizaciones In-App:** Con `CheckUpdatesOnStartup` activo, `MainWindow` consulta `https://api.github.com/repos/Vgarzaniti/HUDmind/releases/latest` al abrir y cada 60 min en reposo (`!_coordinator.IsRunning`). Si hay versión estable mayor, muestra `UpdateBanner` con **⬇ Descargar actualización**, elige `Standalone` o compacto según `IsRunningSelfContained()`, valida `SHA-256` cuando `SHA256SUMS.txt` existe y lista el archivo (si no, finaliza con `Sha256Verified = false`) y libera el mutex antes de lanzar el nuevo `.exe`.
3. **Selección Polimórfica de Motor (`CoachEngineCoordinator`):**
   - **Live Conversacional (`RealtimeLiveCoachOrchestrator`):** Envía audio de micrófono 16kHz (con supresión de eco o PTT) + frames GDI periódicos por WebSocket (`GeminiLiveBackend` / `OpenAIRealtimeBackend`) y reproduce voz PCM 24kHz + texto en overlay con *barge-in*.
   - **Turnos Multi-IA (`CoachOrchestrator`):** Captura vía `FastScreenCapture`, filtra estáticos por dHash 64-bit, alimenta un canal `DropOldest` de capacidad 1 (despertar inmediato con `F10`), enruta con `ProviderFailoverRouter` (*Hedged Vision* o `TextOnlySensorService` WinRT OCR + sensores pasivos en modelos sin visión), parsea `CompactWireProtocol`, evita repetidos en `InterventionMemory` y muestra en `OverlayWindow` + `HybridVoiceService`.
4. **Modos de Comunicación (`UserConfig.CommunicationMode`):** Separan en vivo `conversation` (mic + voz), `coach_voice` (solo voz del coach) y `text_only` (chat escrito + overlay, sin audio ni tonos).

---

## 4. Comandos de Compilación, Tests y Publicación

```powershell
# 1. Compilar solución completa (debe pasar con 0 warnings / 0 errors)
dotnet build native/HUDMind.sln -c Release

# 2. Correr suite Core multiplataforma (43 pruebas) y suite nativa Windows (10 pruebas)
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 3. Smoke test WPF real (debe salir con código 0; usar Start-Process -Wait -PassThru porque es WinExe)
$p = Start-Process -FilePath ".\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe" -ArgumentList "--smoke-startup" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "WPF --smoke-startup failed with exit code $($p.ExitCode)" }

# 4. Publicar binario single-file x64:
#    a) Dependiente de framework (requiere .NET 9 Desktop Runtime en la PC destino)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist/native
#    b) Portable standalone (no requiere instalar .NET 9 Desktop Runtime en la PC destino)
dotnet publish native/HUDMind.App/HUDMind.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist/native-standalone
```

---

## 5. Flujo Git, Reglas de PR y Política Documental

- **`main` está protegida:** **NUNCA** pushees directo a `main`. Creá una rama (`feat/`, `fix/`, `bug/`, `refactor/`, `perf/`, `security/`, `docs/`, `test/`, `build/`, `ci/`, `chore/`) y abrí un Pull Request.
- **Checks obligatorios en CI:** `native-dotnet9-tests` (`ubuntu-latest`) y `native-windows-build` (`windows-latest`).
- **Guía y Plantilla de PR:** Seguí [docs/pull_request_guide.es.md](docs/pull_request_guide.es.md) y completá [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md) (tipo de PR `feat`, `fix`/`bug`, `security`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`, `revert`, `BREAKING CHANGE`, impacto SemVer, evidencia de pruebas y checklist de seguridad).
- **Versionado (`vMAJOR.MINOR.PATCH`):** Seguí [docs/versioning_and_releases.es.md](docs/versioning_and_releases.es.md). Subir un tag `vX.Y.Z` dispara `.github/workflows/release.yml` automáticamente.
- **Documentación bilingüe obligatoria:** Todo archivo de documentación nuevo o editado en raíz o `docs/` **debe** mantener su par `<nombre>.es.md` y `<nombre>.en.md` con links cruzados en la cabecera (y verificar 0 links rotos). No renombres `profiles/*/*.md`.
- **NUNCA Commitear Privados ni Artefactos:** `.env`, `config/accounts.json`, `config/user_config.json`, `config/telemetry.json`, `profiles/*/custom_rules.md`, `logs/`, `dist/`, `bin/`, `obj/`.
