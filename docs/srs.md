# Especificación de Requisitos de Software (SRS) — ISO/IEC/IEEE 29148

**Proyecto:** HUDMind Native (.NET 9) — Coach Táctico de Videojuegos en Tiempo Real  
**Versión:** 1.1.0 (`HUDMind.Native`)  
**Estándares de Referencia:** ISO/IEC/IEEE 29148 (Ingeniería de Requisitos), ISO/IEC 25010 (Calidad de Producto de Software), ISO/IEC 27001 (Seguridad de la Información), ISO/IEC/IEEE 42010 (Descripción de Arquitectura), ISO/IEC/IEEE 29119 (Pruebas de Software).

---

## 1. Propósito y Alcance

**HUDMind Native** es una aplicación nativa para Windows (.NET 9 + WPF + Win32, sin dependencias NuGet externas y con costo `$0` de infraestructura) que analiza el estado de la partida en tiempo real y orienta al jugador según su nivel (`beginner`, `intermediate`, `advanced`, `pro`) y rango, mediante:
- **Motor Live Bidireccional (WebSocket):** Gemini Live, OpenAI Realtime y xAI Grok Realtime con audio nativo 16kHz→24kHz, detección de actividad de voz (VAD), *Push-to-Talk* o micrófono abierto, interrupción inmediata (*barge-in*) y envío de video JPEG a baja frecuencia.
- **Motor Clásico Multi-IA por Turnos (HTTP/2):** Enrutamiento y carrera especulativa (*Hedged Vision*) sobre Groq, Gemini, OpenAI, xAI, Anthropic Claude, OpenRouter, Mistral, Pollinations, Airforce y Ollama local (`http://localhost:11434/v1`), con adaptador de **Sensores Ciego-Texto** (`TextOnlySensorService`) cuando la cuenta activa no soporta imágenes.

### Límites del Sistema (100% Anti-Cheat Safe)
- **Cero inyección e intrusión:** No inyecta DLLs, no lee/escribe memoria del juego ni instala hooks `WH_KEYBOARD_LL` / `WH_MOUSE_LL`.
- **0% GPU para HUDMind:** Fuerza `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` en WPF y utiliza captura pasiva GDI `StretchBlt` + overlay transparente `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`.
- **Sin almacenamiento temporal de capturas:** Las imágenes viven únicamente en RAM (`byte[]` / `MemoryStream`). En disco solo se guardan configuraciones (`config/`) y resúmenes Markdown redactados (`logs/reviews/`) cuando el usuario solicita el resumen post-partida.

---

## 2. Requisitos Funcionales (RF)

| ID | Requisito | Criterio de Aceptación | Módulo Principal |
| :--- | :--- | :--- | :--- |
| **REQ-FUNC-01** | Detección automática del juego activo. | Identifica el `.exe` en primer plano vía `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` con caché por par `(HWND, PID)`, recorte al área cliente útil y retención de última ventana externa al interactuar con el chat de HUDMind. | `native/HUDMind.Windows/Win32WindowDetector.cs` |
| **REQ-FUNC-02** | Carga dinámica de perfiles de juego ("cartuchos") y creación en GUI. | Carga perfiles desde `profiles/<game_id>/` (`manifest.json`, `system_prompt.md`, `knowledge_base.md`, `custom_rules.md`), más perfil dinámico `universal`. Soporta creación directa desde la pestaña `Reglas por Juego` vía `ConfigRepository.CreateNewGameProfile`. | `native/HUDMind.Core/Config/ConfigRepository.cs` |
| **REQ-FUNC-03** | Captura GDI de bajo impacto y deduplicación perceptual. | `FastScreenCapture` escala vía `StretchBlt` (`HALFTONE` + `SetBrushOrgEx`) en un único `Bitmap` reutilizable según modo (`eco` `800×450`, `balanced` `960×540`, `high` `1280×720`) y omite frames estáticos mediante `PerceptualHasher` (dHash 64-bit). | `native/HUDMind.Windows/FastScreenCapture.cs`, `native/HUDMind.Core/Pipeline/PerceptualHasher.cs` |
| **REQ-FUNC-04** | Canales de comunicación independientes. | Soporta 3 modos en vivo sin reiniciar motor (`CommunicationMode`): `conversation` (Charla: mic + voz), `coach_voice` (Solo voz del coach, mic apagado) y `text_only` (Solo texto + chat + overlay, sin voz ni mic ni tonos). | `native/HUDMind.Core/Models/DomainModels.cs`, `native/HUDMind.App/MainWindow.xaml.cs` |
| **REQ-FUNC-05** | Protocolo de cable compacto y supresión local de ruido. | `CompactWireProtocol` parsea `FASE\|QUE_VEO\|URG\|CONSEJO` y JSON; `HudPhaseClassifier` + `TextOnlySensorService` bloquean localmente análisis en pantallas `MENU` / `LOADING` / `REPLAY`. | `native/HUDMind.Core/AI/CompactWireProtocol.cs`, `native/HUDMind.Core/Pipeline/HudPhaseClassifier.cs` |
| **REQ-FUNC-06** | Pool Multi-IA extensible, *Hedged Vision* y Failover. | `ProviderRegistry` centraliza presets y factorías HTTP/Live; `ProviderFailoverRouter` rota ante `HTTP 429/402/5xx`, ejecuta *Hedged Vision* ante demoras primarias, descarta consejos vencidos (`Freshness Deadline`) y activa WinRT OCR + sensores cuando hay cuentas sin visión. | `native/HUDMind.Core/AI/ProviderRegistry.cs`, `native/HUDMind.Core/AI/ProviderFailoverRouter.cs` |
| **REQ-FUNC-07** | Sensores de telemetría pasiva específicos por juego. | Soporta receptor TCP loopback Valve GSI (`127.0.0.1:47315`) para CS2 y Dota 2, cliente TLS loopback Riot Live Client (`127.0.0.1:2999`) para LoL, tailers de logs UTF-8/UTF-16 LE para Rocket League y Valorant, y WinRT OCR en RAM. | `native/HUDMind.Windows/LoopbackGsiService.cs`, `native/HUDMind.Windows/Sensors/*.cs`, `native/HUDMind.Windows/TextOnlySensorService.cs` |
| **REQ-FUNC-08** | Personalización por nivel, estilo y reglas del jugador. | `CoachingPersonaBuilder` calibra profundidad táctica por tier (`beginner`, `intermediate`, `advanced`, `pro`) y rango, modo (`normal`, `training`), estilo (`friendly`, `direct`, `analytical`, `calm`) y hasta `600` caracteres de `custom_rules.md`. | `native/HUDMind.Core/AI/CoachingPersonaBuilder.cs` |
| **REQ-FUNC-09** | Salida visual *click-through* y síntesis de voz instantánea. | `OverlayWindow` soporta modo completo y pill compacto, escala, opacidad, 4 esquinas y bandeja del sistema (`Win32TrayIcon`); `HybridVoiceService` prioriza voz neuronal local o fallback instantáneo Windows OneCore/SAPI. | `native/HUDMind.App/OverlayWindow.xaml.cs`, `native/HUDMind.Windows/HybridVoiceService.cs` |
| **REQ-FUNC-10** | Controles durante partida, Push-to-Talk y tonos sutiles opcionales. | Atajos Win32 `RegisterHotKey` configurables (`F9` Pausa, `F10` Analizar Ahora, `F8` Mic), sonda PTT sin hooks (`GetAsyncKeyState` via `Win32KeyStateProbe`) y confirmación opcional vía `Win32AudioCue` (silenciada en `text_only`). | `native/HUDMind.Windows/Win32GlobalHotkeyManager.cs`, `native/HUDMind.Windows/Audio/Win32AudioCue.cs` |
| **REQ-FUNC-11** | Memoria anti-repetición y resumen post-partida persistente. | `InterventionMemory` evita consejos repetidos dentro de ventana/TTL manteniendo aislamiento del chat; `GeneratePostMatchReviewAsync` y `SavePostMatchReview` generan y guardan reportes `.md` en `logs/reviews/`. | `native/HUDMind.Core/Pipeline/InterventionMemory.cs`, `native/HUDMind.App/Services/CoachEngineCoordinator.cs` |
| **REQ-FUNC-12** | Centro de control gráfico nativo (WPF). | UI de 4 pestañas (`Control y En Vivo`, `Cuentas IA ($0 Gratis)` con `BaseUrl` opcional y override de visión, `Reglas por Juego` con creador de perfiles y `Coach, Rango y Voz`) con `PasswordBox` seguro para streaming. | `native/HUDMind.App/MainWindow.xaml`, `native/HUDMind.App/MainWindow.xaml.cs` |

---

## 3. Requisitos No Funcionales (ISO/IEC 25010)

| ID | Característica ISO 25010 | Requisito | Mecanismo / Verificación |
| :--- | :--- | :--- | :--- |
| **REQ-NFR-01** | Eficiencia de Desempeño | 0% carga en GPU dedicada para la app HUDMind; captura en `StretchBlt` con buffer reutilizable y detección de frame estático vía dHash antes de codificar o invocar red. | `RenderMode.SoftwareOnly`, `FastScreenCapture` y pruebas `W6` |
| **REQ-NFR-02** | Baja Latencia / Frescura | Descarte de frames obsoletos mediante `Channel` acotado (`DropOldest`), *Freshness Deadline* en modo automático y priorización de `F10` sin esperar ciclo. | Pruebas Core `07`, `09`, `12` |
| **REQ-NFR-03** | Fiabilidad y Tolerancia a Fallos | Recuperación atómica de `accounts.json` corrupto (con backup redactado), *Circuit Breaker* por cuenta y rotación transparente multi-proveedor o Live→Clásico. | Pruebas Core `06`, `08`, `20`, `21`, `24` |
| **REQ-NFR-04** | Portabilidad / Zero-Bloat | Cero paquetes NuGet externos; binario distribuible *single-file* `win-x64` sobre .NET 9 Desktop Runtime. | `HUDMind.App.csproj`, `dotnet publish` |
| **REQ-NFR-05** | Mantenibilidad y Modularidad | Separación estricta en `HUDMind.Core` (multiplataforma `net9.0`), `HUDMind.Windows` (Win32/Sensores/Audio) y `HUDMind.App` (WPF). Validación continua por CI en Linux + Windows. | `.github/workflows/ci.yml`, `HUDMind.Tests`, `HUDMind.Windows.Tests` |

---

## 4. Requisitos de Seguridad y Privacidad (ISO/IEC 27001 / OWASP)

| ID | Requisito | Implementación Verificada |
| :--- | :--- | :--- |
| **REQ-SEC-01** | Protección de credenciales y logs limpios (CWE-532 / CWE-598). | Claves en `config/accounts.json` (excluido de Git) enviadas por headers HTTP/WebSocket y nunca en query string. `SecretRedactor` enmascara claves, `Bearer`, `Basic`, JSON sensibles y tokens en logs, errores, backups y resúmenes `.md`. |
| **REQ-SEC-02** | Prevención de SSRF y tráfico plano (CWE-918 / CWE-319). | `BaseUrlSecurityValidator` exige HTTPS remoto (HTTP permitido solo en loopback para Ollama local) y bloquea IPv4/IPv6 privadas, CGNAT, metadata (`169.254.169.254`), NAT64, Teredo, 6to4 y credenciales embebidas en URI. |
| **REQ-SEC-03** | Prevención de Path Traversal y symlinks (CWE-22 / CWE-59). | `ConfigRepository` valida `SafeGameIdRegex`, bloquea `..` y `ReparsePoint` en `profiles/`, `logs/` y `logs/reviews/`; `LoopbackGsiService` valida rutas locales antes de leer `libraryfolders.vdf`. |
| **REQ-SEC-04** | Validación estricta de TLS loopback Riot e HTTP GSI. | `LeagueLiveClientSensor` verifica host `127.0.0.1:2999`, vigencia, RSA>=2048/ECDSA>=256, SAN loopback, identidad `Riot Games` y proceso activo. `LoopbackGsiService` limita headers (`8KB`), body (`64KB`, `413`), rechaza `chunked` (`400`) y limita conexiones concurrentes. |
| **REQ-SEC-05** | Neutralización de inyección de prompts en OCR/telemetría. | `CoachingPersonaBuilder.SanitizeField` limpia separadores de protocolo (`\|`), tags XML/prompt (`<...>`) y trunca longitud antes de inyectar contexto en prompts del modelo. |
