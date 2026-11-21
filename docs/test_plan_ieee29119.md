# Plan y Reporte de Pruebas de Software — ISO/IEC/IEEE 29119 / IEEE 829

**Proyecto:** HUDMind v0.4.0  
**Entorno de Ejecución Verificado:** Windows 11 (x64) · Python 3.13.0 · `.venv` aislado  
**Herramientas QA (IEEE 730):** `pytest 9.2`, `pytest-asyncio 1.5`, `mypy 2.4`, `ruff 0.17`

---

## 1. Estrategia de Verificación y Niveles de Prueba

1. **Análisis Estático y Seguridad (IEEE 730 / ISO 25010 Mantenibilidad y Seguridad):**
   - `ruff check src tests`: reglas `E`, `W`, `F`, `I`, `B`, `UP`, `SIM`, `S` (Bandit security checks).
   - `mypy src tests`: tipado estricto en los **30 archivos fuente y de prueba**.
2. **Pruebas Unitarias (`tests/`):**
   - `tests/test_schemas_and_settings.py`: `CoachResponse`, `InterventionStateMachine` thread-safe, `SecretRedactingFilter` multi-proveedor (incluyendo objetos `Exception` y actualización dinámica de filtros) y `RuntimeMetrics`.
   - `tests/test_profiles_and_filters.py`: Validación CWE-22 en perfiles, compactación markdown, cadena `Focus -> Cooldown -> pHash -> ImageOptimizer` y modo `eco` (`896×504` `<= 512px` tile boundary).
   - `tests/test_observer_and_capture.py`: `VideoMockCaptureStrategy`, política *Drop-Oldest*, `pause`/`resume` con `asyncio.Event` y compuerta pre-captura `should_capture`.
   - `tests/test_gemini_client.py`: Context Caching, fallback automático por mínimo de tokens, recuperación inline `HTTP 404` y Circuit Breaker ante `HTTP 401/403`.
   - `tests/test_providers_and_store.py`: `ProviderFailoverRouter` (rotación automática en `HTTP 429` hacia cuenta secundaria / `Ollama` sin login), extracción JSON no-greedy, protección anti-SSRF/HTTPS en `base_url`, envío de `x-goog-api-key` por header (nunca en URL) y persistencia atómica `0o600`.
   - `tests/test_tts_and_memory.py`: Fallback `EdgeTTSEngine -> Pyttsx3TTSEngine`, cola *Drop-Oldest* de `VoiceService`, `InterventionMemory` (TTL y `maxlen`), `GlobalHotkeyManager` (`F9`/`F10`) y carga multi-juego (`example_cs2` + `example_valorant`).
3. **Pruebas de Integración End-to-End:**
   - `tests/test_integration_orchestrator.py`: `GameCopilotOrchestrator` completo en `--dry-run` y con cliente IA mockeado validando cooldown, memoria anti-repetición e interrupción manual `F10`.

---

## 2. Matriz de Trazabilidad de Requisitos (RTM — ISO/IEC/IEEE 29148 ↔ 29119)

| Requisito | Descripción Resumida | Caso(s) de Prueba | Estado |
| :--- | :--- | :--- | :--- |
| **REQ-FUNC-01 / 02** | Detección `.exe`, carga multi-perfil y compactación | `test_profile_loader_loads_example_and_blocks_traversal`, `test_multi_game_profiles_load_both_cs2_and_valorant` | PASS |
| **REQ-FUNC-03** | Estrategias intercambiables de captura | `test_video_mock_directory_sequence` + `--health-check` | PASS |
| **REQ-FUNC-04** | Pub/Sub *Drop-Oldest* + compuerta pre-captura | `test_screen_observer_drop_oldest_and_pause`, `test_screen_observer_pre_capture_gate_skips_grab_during_cooldown` | PASS |
| **REQ-FUNC-05** | Chain of Responsibility Pre-IA + modo `eco` `896×504` | `test_filter_chain_focus_phash_cooldown_and_resize` | PASS |
| **REQ-FUNC-06** | Context Caching + fallback inline automático | `test_context_cache_success_and_clean_close`, `test_context_cache_min_tokens_automatic_fallback` | PASS |
| **REQ-FUNC-07** | Structured Outputs + State Machine thread-safe | `test_coach_response_actionable_logic`, `test_state_machine_full_lifecycle` | PASS |
| **REQ-FUNC-08** | Overlay transparente *click-through* + 5 temas | `--health-check` (`Win32 WS_EX_TRANSPARENT verificado=True`) + smoke test GUI | PASS |
| **REQ-FUNC-09** | Motor TTS asíncrono con fallback offline y *Drop-Oldest* | `test_edge_tts_falls_back_to_offline_engine_on_network_error`, `test_voice_service_drop_oldest_never_queues_stale_lines` | PASS |
| **REQ-FUNC-10** | Modos CLI (`--test-mode`, `--dry-run`, `--health-check`, `--gui`) | `test_orchestrator_dry_run_end_to_end` | PASS |
| **REQ-FUNC-11 / 12** | Hotkeys `F9`/`F10` + Memoria anti-repetición | `test_hotkey_manager_dispatches_callbacks_and_respects_disabled`, `test_intervention_memory_maxlen_ttl_and_prompt_formatting`, `test_orchestrator_mocked_ai_intervention_cooldown_and_f10_on_demand` | PASS |
| **REQ-FUNC-13 / 14** | Multi-Proveedor IA con rotación en `HTTP 429` + Store GUI | `test_failover_router_rotates_on_quota_429_to_second_account`, `test_user_store_saves_and_reloads_accounts_and_preferences` | PASS |
| **REQ-FUNC-15** | Idiomas independientes de texto y voz (`es`/`en`) | `test_extract_json_handles_markdown_fences_and_language_directive`, `test_failover_router_rotates_on_quota_429_to_second_account` | PASS |
| **REQ-NFR-01 / 02** | Latencia CPU `< 60 ms`, ahorro de tokens y RAM | Benchmarks reales Windows 11 (`13.74 ms/frame` JPEG, GUI `52.2 MB` RSS) | PASS |
| **REQ-NFR-03 / 04** | Recuperación `HTTP 404`, Circuit Breaker y cierre limpio | `test_cache_expiration_404_recovers_inline_and_circuit_breaker` | PASS |
| **REQ-SEC-01 / 02** | Cabecera `x-goog-api-key` y redacción multi-proveedor en logs | `test_gemini_test_connection_uses_header_and_never_exposes_key_in_url`, `test_secret_redacting_filter_iso27001`, `test_settings_placeholder_key_rejected` | PASS |
| **REQ-SEC-03 / 05** | Prevención Path Traversal (CWE-22) y SSRF/HTTPS (CWE-918/319) | `test_manifest_security_and_bounds_validation`, `test_provider_account_ssrf_and_https_validation` | PASS |

---

## 3. Resultados Reales de Ejecución y Benchmarks en Windows 11

| Gate / Prueba | Resultado | Métrica Verificada |
| :--- | :---: | :--- |
| `ruff check src tests` | **PASS** | **0 errores** (incluyendo reglas de seguridad Bandit `S`) |
| `mypy src tests` | **PASS** | **0 errores** en **30 archivos** analizados |
| `pytest` | **PASS** | **28/28 pruebas aprobadas** |
| Overlay Windows 11 (`--health-check`) | **PASS** | `WS_EX_TRANSPARENT \| WS_EX_LAYERED` verificado con `GetWindowLongW` |
| Pipeline CPU (`pHash + 720p + JPEG`, 1360×768) | **PASS** | **13.74 ms/frame** · pico RAM Python `97 KB` · payload `25.6 KB` |
| Pipeline CPU (`pHash + 720p + JPEG`, 1920×1080) | **PASS** | **19.85 ms/frame** · pico RAM Python `96 KB` · payload `25.6 KB` |
| Huella RAM GUI (`HUDMindGUI` con lazy imports) | **PASS** | **52.2 MB RSS** abierta · **0 MB** en modo *"Iniciar y Cerrar GUI"* |

---

## 4. Comandos de Reproducción

```powershell
.\.venv\Scripts\ruff.exe check src tests
.\.venv\Scripts\mypy.exe src tests
.\.venv\Scripts\pytest.exe
.\.venv\Scripts\hudmind.exe --health-check
```
