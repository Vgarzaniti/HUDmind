# Especificación de Requisitos de Software (SRS) — ISO/IEC/IEEE 29148

**Proyecto:** HUDMind — Coach Táctico de Videojuegos en Tiempo Real  
**Versión:** 0.2.0  
**Estándares de Referencia:** ISO/IEC/IEEE 29148 (Ingeniería de Requisitos), ISO/IEC 25010 (Calidad de Producto de Software), ISO/IEC 27001 (Seguridad de la Información), ISO/IEC/IEEE 42010 (Descripción de Arquitectura), ISO/IEC/IEEE 29119 / IEEE 829 (Pruebas de Software).

---

## 1. Propósito y Alcance

**Game AI Copilot** es una aplicación de escritorio para Windows que captura frames de videojuegos a baja frecuencia, filtra escenas redundantes en CPU, consulta un modelo multimodal de Google Gemini con conocimiento precargado mediante *Context Caching*, y entrega recomendaciones tácticas breves a través de un overlay transparente *click-through* y síntesis de voz asíncrona (TTS).

### Límites del Sistema (Out of Scope)
- No inyecta DLLs ni modifica memoria o archivos del proceso del videojuego (100% pasivo / compatible con políticas anti-cheat).
- No almacena capturas de pantalla en disco ni graba video continuo del usuario.
- No realiza acciones de entrada (mouse/teclado) en lugar del jugador.

---

## 2. Requisitos Funcionales (RF)

| ID | Requisito | Criterio de Aceptación | Módulo |
| :--- | :--- | :--- | :--- |
| **REQ-FUNC-01** | Detección automática del juego en primer plano. | El sistema identifica el `.exe` de la ventana activa en Windows (`pywin32` + `psutil`) y carga el perfil correspondiente sin intervención manual. | `src/profiles/loader.py` |
| **REQ-FUNC-02** | Carga dinámica de "cartuchos" de juego. | Cada carpeta en `profiles/<game_id>/` con `manifest.json`, `system_prompt.md` y `knowledge_base.md` válidos se carga automáticamente y puede extenderse sin modificar código. | `src/profiles/loader.py` |
| **REQ-FUNC-03** | Captura de pantalla desacoplada (Strategy). | Soporta captura real (`MSSCaptureStrategy`), pruebas sin juego (`VideoMockCaptureStrategy` con imágenes/video/sintético) y contrato listo para `DXCamCaptureStrategy`. | `src/capture/base.py`, `src/capture/mss_capture.py` |
| **REQ-FUNC-04** | Bus de eventos asíncrono sin *backpressure*. | `ScreenObserver` distribuye frames a suscriptores mediante colas `maxsize=1` con política *Drop-Oldest*. | `src/capture/observer.py` |
| **REQ-FUNC-05** | Filtrado determinista Pre-IA (Chain of Responsibility). | Todo frame atraviesa `FocusFilter -> PerceptualHashFilter -> CooldownFilter -> ImageOptimizer` antes de invocar la API. | `src/pipeline/filters.py` |
| **REQ-FUNC-06** | Context Caching con fallback automático. | Al activar un juego se sube `system_prompt` + `knowledge_base` una única vez a `client.aio.caches.create`; si la API rechaza por mínimo de tokens (`HTTP 400`), el cliente conmuta automáticamente a modo inline. | `src/ai/gemini_client.py` |
| **REQ-FUNC-07** | Salida estructurada estricta + Máquina de Estados. | Gemini devuelve obligatoriamente `CoachResponse` (`should_intervene`, `urgency`, `overlay_text`, `voice_line`). Si `should_intervene == false`, no se emite voz ni overlay. | `src/pipeline/schemas.py` |
| **REQ-FUNC-08** | Overlay visual transparente *click-through*. | Ventana sin bordes, siempre visible (*always-on-top*), no roba foco (`WS_EX_NOACTIVATE`), deja pasar clics (`WS_EX_TRANSPARENT`) y se auto-oculta tras `OVERLAY_DURATION_SECONDS`. | `src/output/overlay.py` |
| **REQ-FUNC-09** | Salida de voz asíncrona desacoplada (TTS). | Estrategias intercambiables (`EdgeTTSEngine`, `Pyttsx3TTSEngine`, `PiperTTSEngine`) con reproducción no bloqueante en memoria (`pygame.mixer`) y cola *Drop-Oldest*. | `src/output/tts.py` |
| **REQ-FUNC-10** | Modo de prueba y diagnóstico de salud. | CLI soporta `--test-mode` (omite `FocusFilter`), `--dry-run` (sin llamadas a Gemini) y `--health-check` (diagnóstico integral de entorno). | `src/main.py` |

---

## 3. Requisitos No Funcionales (ISO/IEC 25010)

| ID | Característica ISO 25010 | Requisito Cuantificable | Verificación |
| :--- | :--- | :--- | :--- |
| **REQ-NFR-01** | Eficiencia de Desempeño | El pipeline de CPU (`pHash` + resize 720p + WebP) debe ejecutarse en `< 60 ms` en régimen estable y comprimir frames a `< 120 KB` promedio en RAM. | `--health-check` y telemetría `RuntimeMetrics` |
| **REQ-NFR-02** | Eficiencia de Recursos | Cero archivos temporales de imagen o audio escritos en disco durante la ejecución normal (`BytesIO` puro). | Revisión de código + tests |
| **REQ-NFR-03** | Fiabilidad / Tolerancia a Fallos | Reintentos acotados con *exponential backoff* ante `HTTP 429/5xx`, *Circuit Breaker* tras `N` fallos consecutivos, detención inmediata ante `HTTP 401/403` y recuperación automática ante errores transitorios `BitBlt` (Win+L / UAC). | `tests/test_gemini_client.py`, `src/capture/mss_capture.py` |
| **REQ-NFR-04** | Recuperabilidad | Cierre ordenado ante `SIGINT`/`KeyboardInterrupt`: cancelación de tareas `asyncio`, cierre de Tk sin `Tcl_AsyncDelete`, detención de mixer y eliminación de caché remoto en Gemini. | `tests/test_integration_orchestrator.py` |
| **REQ-NFR-05** | Mantenibilidad | Tipado estricto verificable con `mypy`, linting y análisis de seguridad estático con `ruff`, y cobertura de pruebas automatizadas (`pytest`). | `pyproject.toml`, `tests/` |
| **REQ-NFR-06** | Operabilidad | Diagnóstico ejecutable en un comando (`--health-check`) y resumen de telemetría al cerrar sesión. | `src/main.py` |

---

## 4. Requisitos de Seguridad y Privacidad (ISO/IEC 27001 / ISO 25010)

| ID | Requisito | Implementación |
| :--- | :--- | :--- |
| **REQ-SEC-01** | Gestión segura de credenciales. | `GEMINI_API_KEY` se carga exclusivamente desde `.env` mediante `pydantic.SecretStr`; `.env` figura en `.gitignore` y nunca se hardcodean claves. |
| **REQ-SEC-02** | Redacción de secretos en logs. | `SecretRedactingFilter` intercepta y reemplaza tanto secretos configurados como patrones `AIza...` antes de imprimir en consola o escribir en `logs/copilot.log`. |
| **REQ-SEC-03** | Prevención de Path Traversal (CWE-22). | `GameManifest.game_id` y `GameProfileLoader.get_by_id()` rechazan separadores de ruta, `..` y symlinks fuera de `profiles/`. |
| **REQ-SEC-04** | Privacidad de pantalla. | Los frames existen solo en RAM y se descartan inmediatamente tras el análisis; no se persisten capturas en almacenamiento local. |
