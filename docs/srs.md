# Especificación de Requisitos de Software (SRS) — ISO/IEC/IEEE 29148

**Proyecto:** HUDMind — Coach Táctico de Videojuegos en Tiempo Real  
**Versión:** 0.4.0  
**Estándares de Referencia:** ISO/IEC/IEEE 29148 (Ingeniería de Requisitos), ISO/IEC 25010 (Calidad de Producto de Software), ISO/IEC 27001 (Seguridad de la Información), ISO/IEC/IEEE 42010 (Descripción de Arquitectura), ISO/IEC/IEEE 29119 / IEEE 829 (Pruebas de Software).

---

## 1. Propósito y Alcance

**HUDMind** es una aplicación de escritorio para Windows que captura frames de videojuegos a baja frecuencia, aplica una compuerta pre-captura y filtrado determinista en CPU, consulta un pool multi-proveedor de modelos multimodales (**Google Gemini** con *Context Caching*, **OpenAI / ChatGPT**, **Anthropic Claude** con *Ephemeral Prompt Caching*, **Groq**, **OpenRouter `:free`**, **Pollinations** y proveedores locales 100% gratuitos sin login como **Ollama** / **LM Studio**) con rotación automática ante agotamiento de cuota, y entrega recomendaciones tácticas breves mediante un overlay transparente *click-through* y síntesis de voz asíncrona (TTS) con configuración independiente de idioma (`es`/`en`).

### Límites del Sistema (Out of Scope)
- No inyecta DLLs, no instala hooks `WH_KEYBOARD_LL` ni modifica memoria o archivos del proceso del videojuego (100% pasivo y compatible con motores anti-cheat).
- No almacena capturas de pantalla ni audio temporal en disco (`BytesIO` puro en RAM).
- No realiza acciones de entrada (mouse/teclado) en lugar del jugador.

---

## 2. Requisitos Funcionales (RF)

| ID | Requisito | Criterio de Aceptación | Módulo |
| :--- | :--- | :--- | :--- |
| **REQ-FUNC-01** | Detección automática del juego en primer plano. | Identifica el `.exe` de la ventana activa en Windows (`pywin32` + `psutil`) con caché inmutable por par `(HWND, PID)` y activa el perfil correspondiente. | `src/profiles/loader.py` |
| **REQ-FUNC-02** | Carga dinámica y compactación de "cartuchos" de juego. | Carga perfiles desde `profiles/<game_id>/` (`manifest.json`, `system_prompt.md`, `knowledge_base.md`) y precalcula versiones compactadas sin reglas decorativas (`example_cs2`, `example_valorant`). | `src/profiles/loader.py` |
| **REQ-FUNC-03** | Captura de pantalla desacoplada (Strategy) sin copia intermedia. | Soporta `MSSCaptureStrategy` (acceso directo a `raw.raw` bytearray), `VideoMockCaptureStrategy` (imágenes/video/sintético) y adaptador `DXCamCaptureStrategy`. | `src/capture/base.py`, `src/capture/mss_capture.py` |
| **REQ-FUNC-04** | Bus de eventos asíncrono con política *Drop-Oldest* y compuerta pre-captura. | `ScreenObserver` evalúa `should_capture` antes de ejecutar `BitBlt` y distribuye frames en colas `maxsize=1` liberando buffers `PIL.Image` obsoletos. | `src/capture/observer.py` |
| **REQ-FUNC-05** | Filtrado determinista Pre-IA (Chain of Responsibility). | Todo frame atraviesa `FocusFilter -> CooldownFilter -> PerceptualHashFilter -> ImageOptimizer` (`JPEG` rápido por defecto o `WebP`, con modo `eco` `896×504`). | `src/pipeline/filters.py` |
| **REQ-FUNC-06** | Context Caching con fallback automático y esquema compacto. | En Gemini crea `CachedContent` cuando `>= 1,024` tokens, desactiva *thinking tokens* ocultos en modelos 2.5 (`ThinkingConfig(thinking_budget=0)`), usa `_CoachWireSchema` compacto y conmuta a modo inline ante `HTTP 400/404`. | `src/ai/gemini_client.py` |
| **REQ-FUNC-07** | Salida estructurada estricta + Máquina de Estados thread-safe. | Fuerza `CoachResponse` (`should_intervene`, `urgency`, `overlay_text`, `voice_line`). Si `should_intervene == false`, no emite voz ni overlay. | `src/pipeline/schemas.py` |
| **REQ-FUNC-08** | Overlay visual transparente *click-through* personalizable. | Ventana sin bordes, *always-on-top*, `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, 5 temas visuales, opacidad, fuente y posición configurables. | `src/output/overlay.py` |
| **REQ-FUNC-09** | Salida de voz asíncrona desacoplada (TTS). | Estrategias intercambiables (`EdgeTTSEngine`, `Pyttsx3TTSEngine`, `PiperTTSEngine`) con lock global de mixer, descarga explícita `unload()` en RAM y cola *Drop-Oldest*. | `src/output/tts.py` |
| **REQ-FUNC-10** | Modos de operación CLI y diagnóstico de salud. | Soporta `--gui`, `--test-mode`, `--dry-run`, `--health-check`, `--profile`, `--mock-source` y `--mock-synthetic`. | `src/main.py` |
| **REQ-FUNC-11** | Hotkeys globales Win32 no intrusivos (`F9` / `F10`). | `GlobalHotkeyManager` registra `F9` (pausar/reanudar) y `F10` (análisis inmediato bajo demanda respetando `FocusFilter` y actualizando `pHash`) vía `RegisterHotKey`. | `src/config/hotkeys.py`, `src/main.py` |
| **REQ-FUNC-12** | Memoria anti-repetición de corto plazo. | `InterventionMemory` conserva las últimas 3 intervenciones (TTL 90s) e inyecta contexto anti-repetición en cada consulta. | `src/pipeline/schemas.py` |
| **REQ-FUNC-13** | Enrutador Multi-Proveedor IA con rotación automática por cuota. | `ProviderFailoverRouter` coordina múltiples cuentas (`gemini`, `openai`, `claude`, `groq`, `openrouter`, `pollinations`, `ollama`, `custom`) y rota inmediatamente en el mismo frame ante `HTTP 429/402/401/403/5xx`. | `src/ai/providers.py`, `src/config/user_store.py` |
| **REQ-FUNC-14** | Centro de Control Gráfico Ligero (GUI). | `HUDMindGUI` (`--gui` / `HUDMind.bat`) gestiona cuentas, prioridades, pruebas de conexión, idiomas, estilos con vista previa *click-through* y modo **"Iniciar y Cerrar GUI (0 MB RAM)"**. | `src/ui/gui.py` |
| **REQ-FUNC-15** | Configuración independiente de idioma para texto y audio. | `text_language` y `voice_language` (`es`/`en`) se configuran de forma independiente e inyectan directivas bilingües mediante `build_language_instruction`. | `src/config/settings.py`, `src/ai/gemini_client.py` |

---

## 3. Requisitos No Funcionales (ISO/IEC 25010)

| ID | Característica ISO 25010 | Requisito Cuantificable | Verificación |
| :--- | :--- | :--- | :--- |
| **REQ-NFR-01** | Eficiencia de Desempeño | El pipeline de CPU (`pHash` + resize + JPEG/WebP) debe ejecutarse en `< 60 ms` en régimen estable (~13.7 ms en 1360×768 JPEG), con prioridad Windows `BELOW_NORMAL` y compuerta pre-captura que evita `BitBlt` durante cooldowns. | `--health-check` y `RuntimeMetrics` |
| **REQ-NFR-02** | Eficiencia de Recursos y Tokens | Cero archivos temporales de imagen/audio en disco; modo `eco` (`896×504`, `<= 512px` boundary + `detail: low`), `ThinkingConfig(thinking_budget=0)` en Gemini 2.5 y *Prefix/Ephemeral Prompt Caching* en OpenAI/Claude/Ollama. | `tests/test_profiles_and_filters.py`, `src/ai/providers.py` |
| **REQ-NFR-03** | Fiabilidad / Tolerancia a Fallos | Reintentos acotados, fallback inline inmediato ante `HTTP 400/404` de caché (incluso con `max_retries=0`), *Circuit Breaker*, rotación multi-cuenta y recuperación GDI `BitBlt`. | `tests/test_gemini_client.py`, `tests/test_providers_and_store.py` |
| **REQ-NFR-04** | Recuperabilidad | Cierre ordenado ante `SIGINT`/`SIGTERM`/`SIGBREAK`: parada limpia desde GUI (`CTRL_BREAK_EVENT`), `gc.collect()` en hilo Tk sin `Tcl_AsyncDelete` y eliminación de caché remoto. | `tests/test_integration_orchestrator.py` |
| **REQ-NFR-05** | Mantenibilidad | Tipado estricto verificable con `mypy` (0 errores en 30 archivos), análisis estático/seguridad con `ruff` (0 errores) y suite automatizada con `pytest`. | `pyproject.toml`, `.github/workflows/ci.yml` |
| **REQ-NFR-06** | Operabilidad | Diagnóstico en un comando (`hudmind --health-check`), lanzador `HUDMind.bat` y telemetría cuantitativa al finalizar sesión. | `src/main.py`, `HUDMind.bat` |

---

## 4. Requisitos de Seguridad y Privacidad (ISO/IEC 27001 / OWASP)

| ID | Requisito | Implementación |
| :--- | :--- | :--- |
| **REQ-SEC-01** | Gestión segura de credenciales y almacenamiento local. | Las claves se cargan desde `.env` (`SecretStr`) o `config/accounts.json` (gitignored, escritura atómica con permisos `0o600` propietario) y se envían sólo por cabeceras HTTP (`x-goog-api-key`, `Authorization: Bearer`, `x-api-key`), jamás en query strings `?key=`. |
| **REQ-SEC-02** | Redacción multi-proveedor en logs y diálogos GUI. | `SecretRedactingFilter` y `redact_secrets_in_text` redactan secretos explícitos (`>= 4` chars), objetos `Exception` en `record.args` y patrones `AIza...`, `sk-...`, `gsk_...`, `Bearer ...` y `?key=...`. |
| **REQ-SEC-03** | Prevención de Path Traversal (CWE-22). | `GameManifest.game_id` y `GameProfileLoader.get_by_id()` rechazan separadores de ruta, `..` y symlinks fuera de `profiles/`. |
| **REQ-SEC-04** | Privacidad de pantalla y protección de foco. | Los frames viven solo en RAM y se cierran con `frame.image.close()` en `finally:`; tanto el ciclo automático como `F10` validan `FocusFilter` para no capturar ventanas privadas fuera del juego. |
| **REQ-SEC-05** | Prevención de SSRF y transmisión sin cifrar (CWE-918 / CWE-319). | `ProviderAccount.validate_safe_base_url` exige `https://` para todo host remoto (permitiendo `http://` únicamente en loopback `localhost`/`127.0.0.1`/`::1`) y bloquea IPs de metadata/link-local (`169.254.*`, `0.0.0.0`). |
