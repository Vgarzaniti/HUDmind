# Arquitectura Técnica — HUDMind (v0.4.0 — ISO/IEC/IEEE 42010)

Este documento describe la arquitectura **Event-Driven**, los patrones de diseño, el enrutador multi-proveedor IA, el modelo de concurrencia y los registros de decisión arquitectónica (ADRs) de **HUDMind**.

---

## 1. Principios de Diseño

1. **Impacto imperceptible en FPS del juego:** Proceso en prioridad Windows `BELOW_NORMAL`, compuerta pre-captura que omite `BitBlt` durante cooldowns o pérdida de foco, captura `MSS` zero-copy desde `raw.raw`, y compresión `JPEG` baseline en **~2.4 ms** (pipeline completo en **~13.7 ms** a 1360×768).
2. **Frescura sobre completitud (*Drop-Oldest*):** Las colas asíncronas (`maxsize=1`) descartan frames y audios obsoletos cerrando inmediatamente sus buffers `PIL.Image`.
3. **Economía extrema de tokens:**
   - **Gemini 2.5 Flash:** *Context Caching* + `ThinkingConfig(thinking_budget=0)` (ahorra 300–1,500 tokens ocultos de razonamiento por frame) + `_CoachWireSchema` compacto (-95 tokens/frame).
   - **OpenAI / Claude / Groq / OpenRouter / Ollama:** Prefijo estático unificado en `system` (`> 1,024` tokens) con `cache_control: {"type": "ephemeral"}` en Claude y compactación automática de markdown (-18% tokens).
   - **Modo `eco` (`896×504`):** Respeta la frontera vertical de tile `<= 512px` (reduciendo tiles `512×512` de 4 a 2) y activa `"detail": "low"` (85 tokens fijos) en proveedores compatibles.
4. **Alta disponibilidad por rotación de cuota (*Multi-Provider Failover*):** Si una cuenta agota tokens (`HTTP 429/402`) o falla, `ProviderFailoverRouter` conmuta en el mismo frame a la siguiente cuenta configurada (incluyendo motores locales gratuitos sin login como **Ollama**).

---

## 2. Diagrama de Flujo de Datos (ASCII)

```text
+-----------------------------------------------------------------------------------+
|                   HUDMindGUI (src/ui/gui.py + user_store.py)                      |
|  - Gestión Multi-Cuenta IA (config/accounts.json, 0o600, gitignored)              |
|  - Idioma Dual (Texto ES/EN | Voz ES/EN), Estilo Overlay + Preview Click-Through  |
|  - Lanza Coach en subproceso aislado (o modo "Iniciar y Cerrar GUI" = 0 MB RAM)   |
+-----------------------------------------+-----------------------------------------+
                                          |
                        +-----------------v---------------------+
                        |   GameProfileLoader (Process Watcher) |
                        |  psutil + pywin32 ((HWND, PID) cache) |
                        +-----------------+---------------------+
                                          |
+------------------------+     +----------v--------------+     +-----------------------------+
|    CaptureStrategy     |     |     ScreenObserver      |     |   Chain of Responsibility   |
| (MSS raw.raw /         +---->|  Pre-Capture Gate       +---->|     (CPU Pre-AI Filter)     |
|  VideoMock / DXCam)    |     |  + Queue(maxsize=1,     |     | 1. FocusFilter (O(1))       |
+------------------------+     |    Drop-Oldest + close) |     | 2. CooldownFilter (O(1))    |
                               +-------------------------+     | 3. PerceptualHash (BOX 32)  |
+------------------------+                  ^                  | 4. ImageOptimizer (JPEG/WebP|
|  GlobalHotkeyManager   |                  |                  |    720p o Eco 896x504)      |
|  Win32 RegisterHotKey  +------------------+                  +--------------+--------------+
|  F9 Pause | F10 Force  |                                                    |
+------------------------+                                       [Compressed Bytes + Memory]
                                                                              v
+--------------------------------------------------------------------------------------------+
|                      ProviderFailoverRouter (src/ai/providers.py)                          |
|  Prioridad #1 -> #2 -> #N (Rotación instantánea en HTTP 429 / 402 / 401 / 403 / 5xx)       |
|  +------------------------+  +---------------------------+  +---------------------------+  |
|  | GeminiProviderAdapter  |  | OpenAICompatibleProvider  |  | AnthropicClaudeProvider   |  |
|  | (Context Cache +       |  | (ChatGPT, Groq,           |  | (Ephemeral System Cache + |  |
|  |  thinking_budget=0 +   |  |  OpenRouter :free,        |  |  Base64 Vision)           |  |
|  |  _CoachWireSchema)     |  |  Pollinations, Ollama)    |  +---------------------------+  |
+--+------------------------+--+---------------------------+---------------------------------+
                                              |
                                       [CoachResponse]
                                              v
                     +-------------------------------------------------+
                     | InterventionStateMachine + InterventionMemory   |
                     | (IDLE -> ANALYZING -> DELIVERING -> COOLDOWN)   |
                     | (Rolling deque maxlen=3, TTL=90s anti-repeat)   |
                     +-----------+-------------------------+-----------+
                                 |                         |
                 +---------------+                         +---------------+
                 v                                                         v
   +---------------------------+                             +---------------------------+
   |   TransparentOverlay      |                             |       VoiceService        |
   |  Tkinter + Win32 64-bit   |                             |  TTSEngine Strategy       |
   |  WS_EX_TRANSPARENT        |                             |  (EdgeTTS / Pyttsx3 /     |
   |  5 Themes, DPI Aware v2   |                             |   Piper) + pygame.mixer   |
   +---------------------------+                             +---------------------------+
```

---

## 3. Patrones de Diseño Aplicados

1. **Strategy Pattern:**
   - **Captura (`src/capture/base.py`, `src/capture/mss_capture.py`):** `MSSCaptureStrategy`, `VideoMockCaptureStrategy`, `DXCamCaptureStrategy`.
   - **Proveedores IA (`src/ai/providers.py`):** `BaseAIProvider` con adaptadores `GeminiProviderAdapter`, `OpenAICompatibleProvider` (OpenAI, Groq, OpenRouter, Pollinations, Ollama, LM Studio) y `AnthropicClaudeProvider`.
   - **Perfiles de Juego (`src/profiles/loader.py`):** Cartuchos declarativos en `profiles/<game_id>/` (`example_cs2`, `example_valorant`).
   - **Síntesis de Voz (`src/output/tts.py`):** `EdgeTTSEngine`, `Pyttsx3TTSEngine`, `PiperTTSEngine`.
2. **Observer / Pub-Sub Asíncrono con Compuerta Pre-Captura (`src/capture/observer.py`):**
   - Evalúa `should_capture()` antes de llamar a `capture_frame()` (evitando `BitBlt` en cooldown o fuera de foco) y publica en colas `maxsize=1` con política *Drop-Oldest* y cierre seguro de `PIL.Image`.
3. **Chain of Responsibility (`src/pipeline/filters.py`):**
   - Orden optimizado por costo computacional: `FocusFilter (O(1))` → `CooldownFilter (O(1))` → `PerceptualHashFilter (BOX 32×32)` → `ImageOptimizer (resize directo + JPEG/WebP)`.
4. **State Machine + Rolling Anti-Repetition Memory (`src/pipeline/schemas.py`):**
   - `InterventionStateMachine` thread-safe gobierna las transiciones y `InterventionMemory` (`maxlen=3`, `ttl=90s`) evita consejos repetidos.

---

## 4. Modelo de Concurrencia

| Componente | Modelo de Ejecución | Justificación |
| :--- | :--- | :--- |
| Orquestador, Observer, Failover Router | `asyncio` (hilo principal) | I/O de red no bloqueante (`google-genai aio` y `aiohttp` con `DummyCookieJar`). |
| Captura `mss`, `pHash`, compresión Pillow | `asyncio.to_thread` | Evita bloquear el *event loop* en operaciones CPU/GDI síncronas. |
| Overlay `Tkinter` + Win32 | Hilo dedicado (`daemon=True`) | Aislado en `_run_tk_session()` + `gc.collect()` en el propio hilo Tk para evitar `Tcl_AsyncDelete`. |
| Hotkeys Globales `F9` / `F10` | Hilo dedicado (`win32-hotkey-thread`) | Bloquea en `GetMessageW` (0% CPU) con `RegisterHotKey` y despacha a `asyncio` vía `call_soon_threadsafe`. |
| Síntesis y Audio TTS | `asyncio` + `asyncio.to_thread` | Protegido con `_MIXER_LOCK` global, inicialización COM (`CoInitialize`) en `pyttsx3` y `music.unload()`. |
| Panel de Configuración (`HUDMindGUI`) | Proceso principal o lanzamiento desacoplado | Lanza el coach en un subproceso con `CREATE_NEW_PROCESS_GROUP` y parada limpia vía `CTRL_BREAK_EVENT`. |

---

## 5. Registro de Decisiones de Arquitectura (ADRs — ISO/IEC/IEEE 42010)

| ADR | Decisión | Alternativa Descartada | Justificación |
| :--- | :--- | :--- | :--- |
| **ADR-001** | `Context Caching` en Gemini + `ThinkingConfig(thinking_budget=0)` + `_CoachWireSchema` | RAG vectorial local o thinking por defecto | Elimina 300–1,500 tokens ocultos de razonamiento por frame, ahorra ~95 tokens de esquema y reduce la latencia a sub-segundo. |
| **ADR-002** | `asyncio.Queue(maxsize=1)` con *Drop-Oldest* + compuerta `should_capture` | Cola FIFO o captura continua en cooldown | Evita *backpressure* y elimina ~75–85% de llamadas `BitBlt` a GPU/DWM durante períodos de enfriamiento. |
| **ADR-003** | `Tkinter` + Win32 `WS_EX_TRANSPARENT` + Per-Monitor DPI Awareness v2 | `PyQt6` / Electron | Cero dependencias pesadas, 5 temas visuales y *click-through* verificado en Windows 64-bit. |
| **ADR-004** | `pHash` `BOX 32×32` + JPEG baseline (~2.4 ms) + modo `eco` `896×504` | WebP `method=4` (~287 ms) o `960×540` (>512px) | `896×504` no cruza la frontera vertical de tile de `512px` (mitad de tiles de visión) y JPEG reduce la carga CPU 18×. |
| **ADR-005** | `SecretStr` + `SecretRedactingFilter` multi-patrón + `base_url` anti-SSRF/HTTPS | Strings planos y URLs sin validar | Cumple ISO/IEC 27001 y OWASP (CWE-319, CWE-532, CWE-598, CWE-918), redactando incluso objetos `Exception`. |
| **ADR-006** | `ProviderFailoverRouter` multi-cuenta con Prefix/Ephemeral Caching | Cliente único acoplado a un solo proveedor | Permite apilar cuentas gratuitas/pagas y rotar en el mismo frame ante `HTTP 429/402`, con soporte offline sin login vía `Ollama`. |
| **ADR-007** | Win32 `RegisterHotKey` (`F9`/`F10`) sin hooks globales | Librería `keyboard` (`WH_KEYBOARD_LL`) | Los hooks de bajo nivel son marcados por motores anti-cheat competitivos (Vanguard/FACEIT); `RegisterHotKey` es 100% seguro. |
| **ADR-008** | `HUDMindGUI` en `tkinter/ttk` con imports diferidos y subproceso desacoplado | GUI pesada corriendo en el mismo loop que el overlay | Abre en ~52 MB RSS y permite cerrar la GUI al iniciar partida (**0 MB RAM extra** mientras jugás). |
