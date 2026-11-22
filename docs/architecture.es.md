> 🌐 **Idioma / Language:** **[🇪🇸 Español](architecture.es.md)** | [🇺🇸 English](architecture.en.md)

# Arquitectura Técnica — HUDMind Native v1.1.0 (ISO/IEC/IEEE 42010)

Este documento describe la arquitectura modular de **HUDMind Native** sobre **.NET 9**, su separación estricta entre lógica pura, capa Win32 y UI WPF, sus motores polimórficos y sus Registros de Decisión de Arquitectura (ADRs).

---

## 1. Principios de Diseño

1. **Separación de Responsabilidades y Portabilidad (`Core` vs `Windows` vs `App`):**
   - `HUDMind.Core` (`net9.0`, 100% multiplataforma): modelos, configuración atómica, seguridad SSRF/CWE-22/redacción, protocolo compacto, memoria anti-repetición, registro extensible de proveedores, failover router, servicio de actualizaciones (`GitHubReleaseUpdateService`) y ambos orquestadores (`CoachOrchestrator` y `RealtimeLiveCoachOrchestrator`).
   - `HUDMind.Windows` (`net9.0-windows10.0.19041.0`): captura GDI `StretchBlt`, detección de proceso/ventana cliente, WinRT OCR, Valve GSI TCP, Riot TLS client, tailers de logs, Win32 hotkeys/PTT, audio WinMM streaming, tonos `PlaySoundW` y resolución de carpetas conocidas (`KnownFolders`).
   - `HUDMind.App` (`net9.0-windows10.0.19041.0` WPF): `MainWindow` (con banner integrado de actualizaciones), `OverlayWindow`, `Win32TrayIcon` y `CoachEngineCoordinator`.
2. **0% GPU y Anticheat-Safe por construcción:**
   - Renderizado WPF forzado por CPU (`RenderMode.SoftwareOnly`).
   - Captura sin hooks invasivos ni inyección de procesos; detección por `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` cacheada por `(HWND, PID)` y PTT mediante consulta directa `GetAsyncKeyState`.
3. **Frescura sobre completitud:**
   - Colas acotadas *Drop-Oldest*, *Freshness Deadline* en consejos automáticos, supresión local de pantallas de menú/carga/replay y memoria semántica anti-repetición.

---

## 2. Diagrama de Flujo y Capas

```text
+-----------------------------------------------------------------------------------+
|                        HUDMind.App (WPF · RenderMode.SoftwareOnly)                |
|  MainWindow (4 pestañas + UpdateBanner) | OverlayWindow (WS_EX_TRANSPARENT) | Tray|
+-----------------------------------------+-----------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
|                    CoachEngineCoordinator (Services/CoachEngineCoordinator.cs)    |
|  - Selecciona polimórficamente ICoachEngine (Live WebSocket o Clásico HTTP/2)      |
|  - Administra LoopbackGsiService (:47315), Win32GlobalHotkeyManager y PTT/Cues     |
+-----------------------+-----------------------------------+-----------------------+
                        |                                   |
        [Live WebSocket ICoachEngine]         [Clásico Turnos ICoachEngine]
                        v                                   v
+---------------------------------------+   +---------------------------------------+
| RealtimeLiveCoachOrchestrator (Core)  |   | CoachOrchestrator (Core)              |
| - GeminiLiveBackend / OpenAIRealtime  |   | - Capture Channel (Bounded DropOldest)|
| - Mic PCM 16kHz + VAD + PTT + BargeIn |   | - ProviderFailoverRouter + Hedged     |
| - Video frames periódicos + F10/Chat  |   | - HybridVoiceService (Neural/OneCore) |
+-------------------+-------------------+   +-------------------+-------------------+
                    |                                           |
                    +---------------------+---------------------+
                                          v
+-----------------------------------------------------------------------------------+
|                         HUDMind.Windows (Sensores & Captura)                      |
|  FastScreenCapture (StretchBlt+dHash) | Win32WindowDetector | TextOnlySensorService|
|  ValveGsiTelemetrySensor | LeagueLiveClientSensor | RocketLeague/ValorantLogSensor |
+-----------------------------------------------------------------------------------+
```

---

## 3. Patrones de Diseño Aplicados

1. **Strategy / Polimorfismo (`ICoachEngine`, `IAIProvider`, `ILiveRealtimeBackend`, `IGameTelemetrySensor`):**
   - `ICoachEngine` unifica ciclo de vida, pausa, análisis forzado `F10`, chat de texto, cambios en vivo y resumen de partida entre el motor Live WebSocket y el motor Clásico por turnos.
   - `IGameTelemetrySensor` desacopla los sensores locales (`ValveGsiTelemetrySensor`, `LeagueLiveClientSensor`, `RocketLeagueLogSensor`, `ValorantLogSensor`), con jerarquía compartida `TailLogSensorBase` para tailing seguro UTF-8/UTF-16 LE.
2. **Extensible Registry (`ProviderRegistry`):**
   - Centraliza presets (`groq`, `gemini`, `openai`, `xai`, `claude`, `openrouter`, `mistral`, `pollinations`, `airforce`, `ollama`), URLs por defecto, capacidades (`SupportsLiveWebSocket`, `SupportsFastHedgedVision`, `IsKeylessAllowed`) y factorías de adaptadores HTTP/Live en un solo punto.
3. **Failover + Speculative Hedging (`ProviderFailoverRouter`):**
   - Si una cuenta falla o limita cuota (`429`/`402`/`5xx`), rota automáticamente. En proveedores con `SupportsFastHedgedVision`, dispara una carrera especulativa si el primario supera el umbral de latencia, evitando errores no observados y descartando respuestas tardías si expiró la frescura automática.
4. **Sensores Perezosos Ciego-Texto (`TextOnlySensorService` + `HudPhaseClassifier`):**
   - Cuando la cuenta activa soporta visión, el costo OCR es `0 ms`. Solo ante cuentas solo-texto (`SupportsVision == false`) o evaluación de compuerta local en Live ejecuta WinRT OCR por zonas + sensores de juego para describir la partida sin enviar píxeles.

---

## 4. Modelo de Concurrencia

| Subsistema | Primitiva / Hilo | Garantía Principal |
| :--- | :--- | :--- |
| UI Principal & Overlay WPF | Hilo UI (`Dispatcher`) | Actualizaciones no bloqueantes vía `Dispatcher.BeginInvoke`; layout click-through sin robar foco al juego. |
| Captura & Pipeline HTTP | `Channel<CapturedFrameData>` + `Task` async | Política `BoundedChannelFullMode.DropOldest` con capacidad `1`; `F10` despierta inmediatamente. |
| Audio Live (Mic + Speaker) | Threads WinMM / Cola PCM (`Win32StreamingMicCapture` & `Win32StreamingPcmPlayer`) | Captura 16kHz y reproducción 24kHz en memoria; supresión de eco e interrupción instantánea (*barge-in*). |
| Hotkeys Globales | Hilo dedicado Win32 (`GetMessage`) | `RegisterHotKey` + `PostThreadMessage(WM_QUIT)` en cierre ordenado; `0%` CPU en espera. |
| Receptor GSI CS2/Dota2 | `TcpListener` loopback + `SemaphoreSlim(4)` | Máximo 4 sockets simultáneos con `CancellationTokenSource` acotado y limpieza en `Stop()`. |
| Verificador de Updates | `Task` async + `DispatcherTimer` (60 min en reposo) | Solo consulta en arranque y en reposo (`!IsRunning`); valida redirecciones HTTPS a GitHub y SHA-256 en streaming. |

---

## 5. Registro de Decisiones de Arquitectura (ADRs — ISO/IEC/IEEE 42010)

| ADR | Decisión | Alternativa Descartada | Justificación |
| :--- | :--- | :--- | :--- |
| **ADR-001** | Migración de Python a **.NET 9 Nativo sin NuGet externos** | Empaquetado Python (`PyInstaller`) o dependencias pesadas | Elimina overhead de intérprete, reduce uso de RAM/arranque, compila a binario único (`HUDMind.Native.exe`) y elimina riesgo de cadena de suministro NuGet. |
| **ADR-002** | `RenderMode.SoftwareOnly` + GDI `StretchBlt` hacia `Bitmap` único reutilizable | WPF Hardware D3D o capturas DXGI pesadas | Deja el **100% de la GPU libre** para el videojuego y evita asignaciones GC repetidas por frame. |
| **ADR-003** | División estricta `HUDMind.Core` (`net9.0`) vs `HUDMind.Windows` (`net9.0-windows`) | Proyecto monolítico acoplado a WinForms/WPF | Permite correr la suite `HUDMind.Tests` tanto en Linux (`ubuntu-latest`) como en Windows en CI/CD. |
| **ADR-004** | `ProviderRegistry` + adaptadores desacoplados con `BaseUrlSecurityValidator` | `switch` dispersos por UI y router | Permite sumar nuevos proveedores o endpoints privados HTTPS en runtime sin duplicar validaciones SSRF. |
| **ADR-005** | Activación perezosa de WinRT OCR y sensores solo en cuentas sin visión o gate Live | Ejecutar OCR en cada frame sin importar el modelo | Ahorra ~50-150 ms de CPU cuando el modelo activo ya ve la pantalla, manteniendo coaching funcional incluso en LLMs solo-texto gratuitos. |
| **ADR-006** | Protocolo `FASE\|QUE_VEO\|URG\|CONSEJO` + `HudPhaseClassifier` | Respuestas verbosas sin estructura | Minimiza tokens de salida (`<= 160`), facilita parseo instantáneo sin fallos de JSON parcial y filtra menús/replays de forma determinista. |
| **ADR-007** | Confirmación sonora vía WAV embebido *pinned* (`Win32AudioCue` + `PlaySoundW`) | Síntesis TTS o archivos WAV externos en disco | Latencia de disparo en microsegundos, cero I/O de disco y silencio garantizado cuando `text_only` o `EnableAudioCues=false`. |
| **ADR-008** | Voz clásica 100% C# (`WinRtNaturalVoiceService` + `InstantSapiVoiceService`) y perfiles embebidos | Subproceso Python archivado o dependencia de carpeta externa obligatoria | Cero procesos Python en runtime, respeto directo al dispositivo `AudioOutputDeviceName` y portabilidad plug-and-play en cualquier PC Windows. |
| **ADR-009** | Actualizador asistido in-app vía GitHub Releases con verificación `SHA-256` sin auto-parcheo en caliente | Scripts `.bat`/PowerShell auto-reemplazando el `.exe` en ejecución | Evita falsos positivos heurísticos de antivirus/SmartScreen, valida redirecciones únicamente a GitHub CDN y permite descargar y lanzar limpiamente el nuevo binario. |
