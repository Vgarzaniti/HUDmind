# Plan y Reporte de Pruebas de Software — ISO/IEC/IEEE 29119 / IEEE 829

**Proyecto:** HUDMind v0.2.0  
**Entorno de Ejecución Verificado:** Windows 11 (x64) · Python 3.13.0 · `.venv` aislado  
**Herramientas QA (IEEE 730):** `pytest 9.2`, `pytest-asyncio 1.5`, `mypy 2.4`, `ruff 0.17`

---

## 1. Estrategia de Verificación y Niveles de Prueba

1. **Análisis Estático y Seguridad (IEEE 730 / ISO 25010 Mantenibilidad):**
   - `ruff check src tests`: reglas `E`, `W`, `F`, `I`, `B`, `UP`, `SIM`, `S` (Bandit security checks).
   - `mypy src tests`: tipado estricto (`disallow_untyped_defs`, `check_untyped_defs`, `warn_return_any`).
2. **Pruebas Unitarias (`tests/`):**
   - Esquemas Pydantic, Máquina de Estados, redacción de secretos en logs y telemetría (`tests/test_schemas_and_settings.py`).
   - Seguridad CWE-22 en perfiles y cadena de filtros Pre-IA (`tests/test_profiles_and_filters.py`).
   - Estrategias de captura mock y bus `ScreenObserver` con política *Drop-Oldest* (`tests/test_observer_and_capture.py`).
   - Cliente Gemini: Context Caching, fallback automático ante mínimo de tokens, recuperación HTTP 404 y Circuit Breaker ante HTTP 401/403 (`tests/test_gemini_client.py`).
3. **Pruebas de Integración End-to-End:**
   - Orquestador completo `GameCopilotOrchestrator` en `--dry-run` y con cliente IA mockeado validando cooldown e intervenciones (`tests/test_integration_orchestrator.py`).
4. **Pruebas de Sistema / Diagnóstico Real en Windows 11:**
   - Ejecución real de `python -m src.main --health-check`.
   - Verificación de flags Win32 `WS_EX_LAYERED | WS_EX_TRANSPARENT` en el overlay real.
   - Benchmark en régimen estable de `pHash + WebP` y síntesis `edge-tts` en memoria (`BytesIO`).

---

## 2. Matriz de Trazabilidad de Requisitos (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requisito | Descripción Resumida | Caso(s) de Prueba | Estado |
| :--- | :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Detección `.exe` y carga dinámica de perfiles | `test_profile_loader_loads_example_and_blocks_traversal` | PASS |
| **REQ-FUNC-03** | Estrategias intercambiables de captura | `test_video_mock_directory_sequence` + `--health-check` | PASS |
| **REQ-FUNC-04** | Pub/Sub asíncrono con política *Drop-Oldest* | `test_screen_observer_drop_oldest_and_pause` | PASS |
| **REQ-FUNC-05** | Chain of Responsibility Pre-IA | `test_filter_chain_focus_phash_cooldown_and_resize` | PASS |
| **REQ-FUNC-06** | Context Caching + fallback automático | `test_context_cache_success_and_clean_close`, `test_context_cache_min_tokens_automatic_fallback` | PASS |
| **REQ-FUNC-07** | Structured Outputs + State Machine | `test_coach_response_actionable_logic`, `test_state_machine_full_lifecycle` | PASS |
| **REQ-FUNC-08** | Overlay transparente *click-through* Windows | `--health-check` (`Win32 WS_EX_TRANSPARENT verificado=True`) | PASS |
| **REQ-FUNC-09** | Motor TTS asíncrono desacoplado en RAM | `--health-check` (`pygame.mixer OK`) + benchmark `edge-tts` | PASS |
| **REQ-FUNC-10** | `--test-mode`, `--dry-run`, `--health-check` | `test_orchestrator_dry_run_end_to_end`, `test_orchestrator_mocked_ai_intervention_and_cooldown` | PASS |
| **REQ-NFR-01** | Latencia CPU `< 60 ms` en régimen estable | Benchmark real Windows 11: **13.74 ms** (1360×768 JPEG) / **19.85 ms** (1080p JPEG) | PASS |
| **REQ-NFR-03** | Tolerancia a fallos y Circuit Breaker | `test_cache_expiration_404_recovers_inline_and_circuit_breaker` | PASS |
| **REQ-SEC-01 / 02** | Protección y redacción de secretos en logs | `test_secret_redacting_filter_iso27001`, `test_settings_placeholder_key_rejected` | PASS |
| **REQ-SEC-03** | Prevención Path Traversal (CWE-22) | `test_manifest_security_and_bounds_validation`, `test_profile_loader_loads_example_and_blocks_traversal` | PASS |

---

## 3. Resultados Reales de Ejecución y Ajustes Realizados en esta PC

| Gate / Prueba | Resultado | Métrica / Hallazgo |
| :--- | :--- | :--- |
| `ruff check src tests` | **PASS** | 0 errores (incluyendo reglas de seguridad Bandit `S`) |
| `mypy src tests` | **PASS** | 0 errores en 23 archivos analizados |
| `pytest` | **PASS** | 15/15 pruebas aprobadas |
| Overlay Windows 11 (`--health-check`) | **PASS** | `WS_EX_TRANSPARENT \| WS_EX_LAYERED` verificado con `GetWindowLongW` |
| Pipeline CPU (`pHash + 720p + JPEG`, 1360×768) | **PASS** | **13.74 ms/frame** · pico RAM Python `97 KB` · payload `25.6 KB` |
| Pipeline CPU (`pHash + 720p + JPEG`, 1920×1080) | **PASS** | **19.85 ms/frame** · pico RAM Python `96 KB` · payload `25.6 KB` |
| Pipeline CPU (`pHash + 720p + WebP m=0`, 1360×768) | **PASS** | **35.05 ms/frame** · pico RAM Python `78 KB` · payload `9.2 KB` |
| Síntesis `edge-tts` (`es-AR-TomasNeural`) | **PASS** | 16.2 KB MP3 generados en RAM (`BytesIO`) sin archivos en disco |

### Defectos Reales Detectados y Corregidos Durante las Pruebas en Windows 11
1. **Python 3.13 `.venv` sin `init.tcl` (`Tkinter`):** Se implementó auto-resolución de `TCL_LIBRARY` y `TK_LIBRARY` desde `sys.base_prefix/tcl/` en `src/output/overlay.py`.
2. **Error `Tcl_AsyncDelete` al cerrar el overlay en hilo dedicado:** Se aisló el ciclo de vida de widgets dentro de `_run_tk_session()` seguido de `gc.collect()` en el hilo de Tkinter, logrando salida limpia (`exit code 0`).
3. **Advertencia de deprecación en `mss 10.2` y tolerancia a `BitBlt` en escritorio bloqueado/UAC:** Se migró a `mss.MSS` con reinicio automático del handle GDI y throttling de warnings.
4. **Latencia inicial de compresión WebP (`method=4` → `95 ms`):** Se optimizó a `method=1` + pre-escalado `BILINEAR 64x64` para `pHash`, reduciendo el costo de CPU a **35.37 ms** promedio.

---

## 4. Comandos de Reproducción

```powershell
# Análisis estático y tipado estricto
.\.venv\Scripts\ruff.exe check src tests
.\.venv\Scripts\mypy.exe src tests

# Suite completa de pruebas unitarias e integración
.\.venv\Scripts\pytest.exe

# Diagnóstico de hardware y entorno local
.\.venv\Scripts\python.exe -m src.main --health-check
```
