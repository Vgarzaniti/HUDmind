# Arquitectura Técnica — HUDMind

Este documento describe la arquitectura **Event-Driven**, los patrones de diseño obligatorios, el manejo de concurrencia y las estrategias de rendimiento de **HUDMind**.

---

## 1. Principios de Diseño

1. **Bajo impacto en FPS del juego:** Toda la captura, comparación perceptual (`pHash`) y compresión en memoria se ejecuta a baja frecuencia (1–2 FPS) y en memoria RAM (`BytesIO`), sin escribir capturas temporales en disco.
2. **Frescura sobre completitud (*Drop-Oldest*):** En un videojuego en tiempo real, analizar un frame de hace 5 segundos es inútil o contraproducente. Las colas asíncronas descartan frames obsoletos si la IA todavía está procesando una inferencia previa.
3. **Ahorro agresivo de tokens:** Un pipeline determinista en CPU filtra pantallas sin foco, escenas estáticas y períodos de enfriamiento antes de realizar cualquier llamada a Gemini.
4. **Cero latencia de búsqueda (*Context Caching*):** En lugar de utilizar RAG vectorial en tiempo de inferencia, el conocimiento completo del juego se precarga en la caché de contexto de Gemini al inicio de la sesión.
5. **Desacoplamiento por interfaces (SOLID):** Captura de pantalla, perfiles de juego, filtros y motores TTS se adhieren a contratos claros y son intercambiables sin modificar el orquestador.

---

## 2. Diagrama de Flujo de Datos (ASCII)

```text
                        +---------------------------------------+
                        |   GameProfileLoader (Process Watcher) |
                        |  psutil + pywin32 (Foreground .exe)   |
                        +-------------------+-------------------+
                                            |
                                    [ActiveProfile]
                                            v
+------------------------+     +-------------------------+     +-----------------------------+
|    CaptureStrategy     |     |     ScreenObserver      |     |   Chain of Responsibility   |
| (MSS / VideoMock /     +---->|   Async Pub/Sub Bus     +---->|     (CPU Pre-AI Filter)     |
|  DXCam-ready)          |     |  Queue(maxsize=1,       |     |                             |
+------------------------+     |        Drop-Oldest)     |     | 1. FocusFilter              |
                               +-------------------------+     | 2. PerceptualHashFilter     |
                                                               | 3. CooldownFilter           |
                                                               | 4. ImageOptimizer (720p/WebP|
                                                               +--------------+--------------+
                                                                              |
                                                                 [Optimized WebP Bytes]
                                                                              v
+--------------------------------------------------------------------------------------------+
|                         GeminiCoachClient (google-genai aio)                               |
|  - Session Start: client.aio.caches.create(system_prompt + knowledge_base, ttl=3600s)      |
|  - Auto-Fallback: si < min_tokens (HTTP 400), envía system_instruction + KB inline         |
|  - Inference: generate_content(cached_content=cache_name, response_schema=CoachResponse)   |
+---------------------------------------------+----------------------------------------------+
                                              |
                                       [CoachResponse]
                            (should_intervene, urgency, texts)
                                              v
                              +-------------------------------+
                              |  InterventionStateMachine     |
                              |  IDLE -> ANALYZING ->         |
                              |  DELIVERING -> COOLDOWN       |
                              +-------+---------------+-------+
                                      |               |
                 +--------------------+               +--------------------+
                 v                                                         v
   +---------------------------+                             +---------------------------+
   |   TransparentOverlay      |                             |       VoiceService        |
   |  Tkinter + Win32 API      |                             |  TTSEngine Strategy       |
   |  WS_EX_TRANSPARENT        |                             |  (EdgeTTS / Pyttsx3)      |
   |  WS_EX_LAYERED            |                             |  + pygame.mixer (async)   |
   |  Always-on-top, non-focus |                             +---------------------------+
   +---------------------------+
```

---

## 3. Patrones de Diseño Aplicados

### 3.1 Strategy Pattern
- **Captura (`src/capture/base.py`, `src/capture/mss_capture.py`):**
  - `CaptureStrategy` define el contrato `start()`, `capture_frame() -> CapturedFrame | None`, `stop()`.
  - `MSSCaptureStrategy` captura el monitor seleccionado directamente a un objeto `PIL.Image` en RAM.
  - `VideoMockCaptureStrategy` reproduce frames desde una carpeta de imágenes o un archivo de video para pruebas deterministas sin ejecutar un videojuego.
  - La interfaz está diseñada para enchufar fácilmente un futuro `DXCamCaptureStrategy` (DirectX Desktop Duplication) sin tocar el `ScreenObserver`.
- **Perfiles de Juego (`src/profiles/loader.py`):**
  - Cada carpeta en `profiles/<juego>/` funciona como un "cartucho" autocontenido (`manifest.json`, `system_prompt.md`, `knowledge_base.md`).
- **Síntesis de Voz (`src/output/tts.py`):**
  - `TTSEngine` desacopla la generación de audio (`EdgeTTSEngine`, `Pyttsx3TTSEngine`, y punto de extensión para `PiperTTSEngine`) de la cola de reproducción no bloqueante (`VoiceService`).

### 3.2 Observer / Pub-Sub Asíncrono con *Drop-Oldest*
- Implementado en `src/capture/observer.py` (`ScreenObserver`).
- Desacopla el productor de frames (bucle de captura a `capture_fps`) de uno o más consumidores asíncronos (el pipeline de análisis IA, telemetría o debuggers).
- Cada suscriptor posee su propia `asyncio.Queue(maxsize=1)`. Cuando llega un frame nuevo y la cola está llena, el observador descarta el elemento viejo (`get_nowait()`) e inserta el más reciente (`put_nowait()`). Esto elimina por completo el *backpressure* y la deriva temporal.

### 3.3 Chain of Responsibility (Pipeline Pre-IA)
- Implementado en `src/pipeline/filters.py`.
- Cada filtro recibe un `FrameContext` mutable y retorna el contexto para continuar la cadena o `None` para abortar inmediatamente:
  1. **`FocusFilter`**: Verifica si el `.exe` en primer plano coincide con `manifest.process_names` (se omite en `--test-mode`).
  2. **`PerceptualHashFilter`**: Calcula `imagehash.phash()` sobre el frame y mide la distancia de Hamming respecto al último frame aprobado. Si es inferior a `phash_threshold`, descarta la imagen (ej. pantalla de carga, menú estático, jugador campeando sin cambios visuales relevantes).
  3. **`CooldownFilter`**: Garantiza que haya transcurrido el tiempo mínimo desde la última intervención del coach (`cooldown_seconds`) y un intervalo mínimo de seguridad entre llamadas a la API (`min_inference_interval_seconds`).
  4. **`ImageOptimizer`**: Redimensiona preservando la relación de aspecto (por defecto `<= 1280x720`) con filtro Bilinear/Lanczos y comprime a WebP en un buffer `io.BytesIO` en memoria.

### 3.4 Structured Outputs + Máquina de Estados de Intervención
- Implementado en `src/pipeline/schemas.py`.
- **`CoachResponse`** fuerza mediante JSON Schema nativo de Gemini exactamente cuatro campos:
  - `should_intervene: bool`
  - `urgency: Literal["low", "medium", "high"]`
  - `overlay_text: str` (texto ultra breve para lectura periférica)
  - `voice_line: str` (frase natural corta para locución)
- **`InterventionStateMachine`** gobierna las transiciones `IDLE -> ANALYZING -> DELIVERING -> COOLDOWN -> IDLE`. Si `should_intervene` es `False`, transita directamente de `ANALYZING` a `IDLE` sin disparar overlay ni voz ni activar el cooldown largo de intervención.

---

## 4. Context Caching y Gestión de Memoria

En `src/ai/gemini_client.py`:
- Al activar un perfil de juego, `prepare_session(profile)` invoca `client.aio.caches.create(...)` subiendo `system_prompt.md` como `system_instruction` y `knowledge_base.md` como contenido cacheado con un TTL configurable (`CACHE_TTL_SECONDS`).
- Todas las inferencias posteriores envían únicamente el frame WebP comprimido + un micro-prompt situacional referenciando `cached_content=cache_name`.
- **Fallback automático:** Gemini exige un tamaño mínimo de tokens para crear un caché explícito (típico: 1,024–4,096 tokens según modelo). Si `caches.create` devuelve un error `400 INVALID_ARGUMENT` (o el contenido es demasiado pequeño), el cliente cambia de forma transparente al modo `STANDARD`, enviando `system_instruction` + `knowledge_base` inline en cada petición sin interrumpir al usuario.
- **Limpieza:** Al cambiar de juego o cerrar la aplicación, el caché explícito se elimina (`client.aio.caches.delete`) para evitar costos residuales de almacenamiento.

---

## 5. Modelo de Concurrencia

Python y Windows imponen restricciones específicas que esta arquitectura resuelve de forma limpia:

| Componente | Modelo de Ejecución | Justificación |
| :--- | :--- | :--- |
| Orquestador, Observer, Gemini API | `asyncio` (hilo principal) | I/O de red no bloqueante con `client.aio` y coordinación limpia mediante tareas cancelables. |
| Captura `mss`, `pHash`, compresión Pillow | `asyncio.to_thread` | Evita bloquear el *event loop* durante operaciones CPU/GDI síncronas. |
| Overlay `Tkinter` + Win32 | Hilo dedicado (`daemon=True`) | `Tkinter` exige poseer su propio *mainloop* en un único hilo; se comunica desde `asyncio` mediante `queue.Queue` *thread-safe* sondeada con `root.after()`. |
| Síntesis `edge-tts` / `pyttsx3` + `pygame.mixer` | `asyncio` + `asyncio.to_thread` | `edge-tts` hace streaming asíncrono a `BytesIO`; la reproducción de audio corre sin bloquear la captura ni el overlay. |

---

## 6. Resiliencia, Seguridad y Manejo de Errores (ISO/IEC 25010 & 27001)

- **Rate Limits (`429`) y errores transitorios (`5xx` / timeouts):** Reintentos acotados con *exponential backoff* + *jitter*. Si el error persiste en un frame, se descarta ese frame y el sistema continúa operando con el siguiente.
- **Circuit Breaker en `GeminiCoachClient`:** Tras `CIRCUIT_BREAKER_THRESHOLD` fallos consecutivos (defecto: `5`), el circuito se abre temporalmente (`30s`) evitando tormentas de peticiones. Ante errores de credenciales (`HTTP 401/403`), el circuito se abre permanentemente con diagnóstico claro.
- **Expiración del caché en sesiones largas:** Si una llamada falla indicando caché expirado o inexistente (`404`/`400`), el cliente conmuta a fallback inline y reintenta sin perder la sesión.
- **Recuperación GDI en Windows (`BitBlt`):** Ante bloqueos de sesión (`Win+L`), avisos UAC o cambios de resolución, `MSSCaptureStrategy` reinicia el contexto GDI *thread-local* y evita saturar los logs.
- **Seguridad y Privacidad:**
  - `SecretRedactingFilter` redacta automáticamente cualquier clave de API (`AIza...` o el valor configurado en `GEMINI_API_KEY`) antes de emitir logs a consola o a `logs/copilot.log`.
  - Validación anti *Path Traversal* (CWE-22) en `GameManifest.game_id` y `GameProfileLoader`.
  - Cero escritura de frames o audio temporal en disco (`BytesIO` puro).
- **Cierre ordenado (`SIGINT` / `SIGTERM`):** Cancela tareas asíncronas, destruye el overlay de Tkinter y ejecuta `gc.collect()` en el propio hilo de Tk (evitando `Tcl_AsyncDelete`), detiene el mixer de audio, elimina el caché activo en Gemini y emite un resumen cuantitativo de `RuntimeMetrics`.

---

## 7. Registro de Decisiones de Arquitectura (ADRs — ISO/IEC/IEEE 42010)

| ADR | Decisión | Alternativa Descartada | Justificación |
| :--- | :--- | :--- | :--- |
| **ADR-001** | `Context Caching` con fallback inline automático | RAG con base vectorial local (FAISS/Chroma) | Elimina latencia de embeddings/búsqueda en cada frame y aprovecha el caché nativo de Gemini con menor costo de tokens. |
| **ADR-002** | `asyncio.Queue(maxsize=1)` con *Drop-Oldest* | Cola FIFO ilimitada | En videojuegos en tiempo real, procesar frames atrasados degrada la utilidad del coach y genera *backpressure*. |
| **ADR-003** | `Tkinter` + Win32 `WS_EX_TRANSPARENT` en hilo dedicado | `PyQt6` | Cero dependencias pesadas de Qt (~100 MB menos), bajo consumo de RAM y soporte nativo de *click-through* en Windows 64-bit. |
| **ADR-004** | `pHash` sobre `BOX 32×32` + resize directo + JPEG baseline (o WebP `method=0` configurable) | Envío directo de PNG 1080p o WebP `method=4` | Reduce el pipeline CPU completo de ~95–300 ms a **13.7 ms** (1360×768) / **19.8 ms** (1080p) con **~25 KB** por frame e idéntico costo en tokens de Gemini. |
| **ADR-005** | `SecretStr` + `SecretRedactingFilter` | Strings planos en variables globales | Cumple principios ISO/IEC 27001 evitando fugas accidentales de API keys en tracebacks o logs compartidos. |

