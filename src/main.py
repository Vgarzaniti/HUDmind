"""Main entrypoint, health diagnostics, and asyncio orchestrator for Game AI Copilot."""

from __future__ import annotations

import argparse
import asyncio
import logging
import platform
import sys
import time
from pathlib import Path

# Support both `python -m src.main` and `python src/main.py`
if __package__ is None or __package__ == "":
    _project_root = Path(__file__).resolve().parent.parent
    if str(_project_root) not in sys.path:
        sys.path.insert(0, str(_project_root))

from src.ai.gemini_client import GeminiCoachClient
from src.capture.base import CapturedFrame, CaptureStrategy
from src.capture.mss_capture import MSSCaptureStrategy, VideoMockCaptureStrategy
from src.capture.observer import ScreenObserver
from src.config.hotkeys import GlobalHotkeyManager
from src.config.settings import AppSettings, RuntimeMetrics, configure_logging
from src.output.overlay import TransparentOverlay, enable_windows_dpi_awareness
from src.output.tts import AudioPlayer, VoiceService
from src.pipeline.filters import FilterPipeline, FrameContext
from src.pipeline.schemas import CoachResponse, InterventionMemory, InterventionStateMachine
from src.profiles.loader import GameProfile, GameProfileLoader

logger = logging.getLogger("copilot.main")


class GameCopilotOrchestrator:
    """Event-driven asyncio orchestrator connecting capture, filters, Gemini, and outputs."""

    def __init__(
        self,
        settings: AppSettings,
        *,
        test_mode: bool = False,
        forced_profile_id: str | None = None,
        mock_source: str | None = None,
        use_synthetic_mock: bool = False,
        dry_run: bool = False,
        enable_voice: bool | None = None,
        enable_overlay: bool | None = None,
        enable_hotkeys: bool | None = None,
        ai_client: GeminiCoachClient | None = None,
    ) -> None:
        self._settings = settings
        self._test_mode = test_mode
        self._forced_profile_id = forced_profile_id
        self._dry_run = dry_run
        self.metrics = RuntimeMetrics()

        voice_enabled = settings.enable_voice if enable_voice is None else enable_voice
        overlay_enabled = settings.enable_overlay if enable_overlay is None else enable_overlay
        hotkeys_enabled = settings.enable_hotkeys if enable_hotkeys is None else enable_hotkeys

        # 1. Profile Loader & Windows Process Detector
        self._profile_loader = GameProfileLoader(settings.profiles_dir)

        # 2. Capture Strategy (Strategy Pattern)
        self._capture_strategy: CaptureStrategy
        if mock_source is not None:
            self._capture_strategy = VideoMockCaptureStrategy(source_path=mock_source)
        elif use_synthetic_mock:
            self._capture_strategy = VideoMockCaptureStrategy(source_path=None)
        else:
            self._capture_strategy = MSSCaptureStrategy(monitor_index=settings.capture_monitor)

        # 3. Screen Observer (Async Pub-Sub with Drop-Oldest)
        self._observer = ScreenObserver(strategy=self._capture_strategy, target_fps=1.0)

        # 4. Intervention State Machine + Anti-Repetition Memory + Filter Pipeline
        self._state_machine = InterventionStateMachine()
        self._memory = InterventionMemory(max_items=3, ttl_seconds=90.0)
        self._filter_pipeline = FilterPipeline(
            detector=self._profile_loader.detector,
            state_machine=self._state_machine,
            min_inference_interval=settings.min_inference_interval_seconds,
            compression_quality=settings.image_compression_quality,
            image_format=settings.image_format,
        )

        # 5. Gemini Client (Context Caching + Circuit Breaker + Structured Outputs)
        self._ai_client: GeminiCoachClient | None
        if ai_client is not None:
            self._ai_client = ai_client
        elif dry_run:
            self._ai_client = None
        else:
            self._ai_client = GeminiCoachClient(settings)

        # 6. Output Channels & Non-Intrusive Windows Global Hotkeys
        self._overlay = TransparentOverlay(
            enabled=overlay_enabled,
            position=settings.overlay_position,
            width=settings.overlay_width,
            opacity=settings.overlay_opacity,
            default_duration_seconds=settings.overlay_duration_seconds,
        )
        self._voice = VoiceService(settings=settings, enabled=voice_enabled)
        self._hotkeys = GlobalHotkeyManager(
            enabled=hotkeys_enabled,
            on_toggle_pause=self.toggle_user_pause,
            on_analyze_now=self.trigger_on_demand_analysis,
        )

        self._active_profile: GameProfile | None = None
        self._profile_lock = asyncio.Lock()
        self._stop_event = asyncio.Event()
        self._user_paused = False
        self._force_analysis_pending = False

    async def run(self, max_runtime_seconds: float | None = None) -> None:
        """Start all subsystems and run until interrupted or optional timeout expires."""
        logger.info("=" * 68)
        logger.info(
            "HUDMind iniciado | modo=%s | dry_run=%s | modelo=%s",
            "TEST-MODE" if self._test_mode else "AUTO-DETECT",
            self._dry_run,
            self._settings.default_model,
        )
        logger.info("=" * 68)

        self._overlay.start()
        await self._voice.start()
        self._hotkeys.start(loop=asyncio.get_running_loop())

        frame_queue = self._observer.subscribe("ai-pipeline")

        if self._test_mode:
            profile = self._resolve_test_profile()
            if profile is None:
                logger.error(
                    "No se encontró un perfil válido en '%s'. Verifica la carpeta profiles/.",
                    self._settings.profiles_dir.resolve(),
                )
                await self.shutdown()
                return
            await self._activate_profile(profile)
        else:
            self._observer.pause()
            logger.info(
                "Esperando que abras un juego registrado (%s)...",
                ", ".join(
                    proc
                    for p in self._profile_loader.list_profiles()
                    for proc in p.manifest.process_names
                )
                or "ningún perfil cargado",
            )

        await self._observer.start()

        tasks: list[asyncio.Task[None]] = [
            asyncio.create_task(
                self._frame_consumer_loop(frame_queue),
                name="frame-consumer-loop",
            )
        ]
        if not self._test_mode:
            tasks.append(
                asyncio.create_task(
                    self._profile_watcher_loop(),
                    name="profile-watcher-loop",
                )
            )

        try:
            if max_runtime_seconds is not None and max_runtime_seconds > 0:
                try:
                    await asyncio.wait_for(self._stop_event.wait(), timeout=max_runtime_seconds)
                except TimeoutError:
                    logger.info("Tiempo máximo de ejecución alcanzado (%.1fs).", max_runtime_seconds)
            else:
                await self._stop_event.wait()
        except asyncio.CancelledError:
            pass
        finally:
            for task in tasks:
                task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
            await self.shutdown()

    def request_stop(self) -> None:
        """Signal the orchestrator to begin graceful shutdown."""
        self._stop_event.set()

    def toggle_user_pause(self) -> None:
        """Toggle manual pause/mute state (bound to F9 hotkey)."""
        self._user_paused = not self._user_paused
        title = self._active_profile.manifest.display_name if self._active_profile else "HUDMind"
        if self._user_paused:
            self._observer.pause()
            logger.info("Coach PAUSADO por el usuario (F9 para reanudar).")
            self._overlay.show_message(
                text="Coach PAUSADO — presioná F9 para reanudar",
                urgency="low",
                title=title,
                duration_seconds=2.5,
            )
        else:
            if self._active_profile is not None:
                self._observer.resume()
            logger.info("Coach REANUDADO por el usuario.")
            self._overlay.show_message(
                text="Coach ACTIVO — observando partida",
                urgency="low",
                title=title,
                duration_seconds=2.5,
            )

    def trigger_on_demand_analysis(self) -> None:
        """Request immediate tactical analysis on next frame bypassing pHash & cooldown (F10)."""
        if self._active_profile is None:
            logger.info("Solicitud manual (F10) ignorada: no hay perfil de juego activo.")
            return
        self._force_analysis_pending = True
        if self._user_paused:
            self._user_paused = False
            self._observer.resume()
        logger.info("Análisis táctico bajo demanda solicitado (F10).")
        self._overlay.show_message(
            text="Analizando situación actual...",
            urgency="low",
            title=self._active_profile.manifest.display_name,
            duration_seconds=2.0,
        )

    def _resolve_test_profile(self) -> GameProfile | None:
        """Select forced profile for --test-mode (defaults to example_cs2 or first profile)."""
        if self._forced_profile_id:
            profile = self._profile_loader.get_by_id(self._forced_profile_id)
            if profile is None:
                logger.error("Perfil solicitado '%s' no existe.", self._forced_profile_id)
            return profile

        default_profile = self._profile_loader.get_by_id("example_cs2")
        if default_profile is not None:
            return default_profile

        profiles = self._profile_loader.list_profiles()
        return profiles[0] if profiles else None

    async def _activate_profile(self, profile: GameProfile) -> None:
        """Switch active game cartridge, reconfigure observer/filters, and warm Context Cache."""
        async with self._profile_lock:
            if (
                self._active_profile is not None
                and self._active_profile.manifest.game_id == profile.manifest.game_id
            ):
                return

            self._active_profile = profile
            self._filter_pipeline.reset()
            self._state_machine.reset()
            self._memory.clear()
            self._observer.update_fps(profile.manifest.capture_fps)
            if not self._user_paused:
                self._observer.resume()

            logger.info(
                "Perfil activado: %s (%s) | FPS=%.1f | pHash>=%d | cooldown=%.1fs",
                profile.manifest.display_name,
                profile.manifest.game_id,
                profile.manifest.capture_fps,
                profile.manifest.phash_threshold,
                profile.manifest.cooldown_seconds,
            )

            cache_status = "DRY-RUN"
            if self._ai_client is not None:
                cached = await self._ai_client.prepare_session(profile)
                cache_status = "CONTEXT-CACHE" if cached else "INLINE-FALLBACK"

            self._overlay.show_message(
                text=f"Coach activo ({cache_status}) — observando partida",
                urgency="low",
                title=profile.manifest.display_name,
                duration_seconds=4.0,
            )

    async def _deactivate_profile(self) -> None:
        """Pause capture when no supported game is focused."""
        async with self._profile_lock:
            if self._active_profile is None:
                return
            logger.info(
                "Juego '%s' ya no está en primer plano; pausando captura.",
                self._active_profile.manifest.display_name,
            )
            self._observer.pause()

    async def _profile_watcher_loop(self) -> None:
        """Poll foreground Windows executable to auto-load matching game profiles."""
        poll_interval = self._settings.process_poll_interval_seconds
        while not self._stop_event.is_set():
            try:
                _, detected = await asyncio.to_thread(self._profile_loader.detect_active_profile)
                if detected is not None:
                    if (
                        self._active_profile is None
                        or self._active_profile.manifest.game_id != detected.manifest.game_id
                    ):
                        await self._activate_profile(detected)
                    elif not self._user_paused:
                        self._observer.resume()
                elif self._active_profile is not None:
                    await self._deactivate_profile()
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                logger.error("Error en detector de procesos activos: %s", exc)

            await asyncio.sleep(poll_interval)

    async def _frame_consumer_loop(self, frame_queue: asyncio.Queue[CapturedFrame]) -> None:
        """Consume freshest frames, execute CPU filter pipeline, call Gemini, and dispatch outputs."""
        while not self._stop_event.is_set():
            try:
                frame = await frame_queue.get()
            except asyncio.CancelledError:
                raise

            profile = self._active_profile
            if profile is None or self._user_paused:
                continue

            force_now = self._force_analysis_pending
            if force_now:
                self._force_analysis_pending = False

            if force_now:
                if self._state_machine.is_busy:
                    continue
                t_filter_start = time.perf_counter()
                raw_ctx = FrameContext(frame=frame, profile=profile, test_mode=True)
                filtered = await asyncio.to_thread(
                    self._filter_pipeline.image_optimizer.handle,
                    raw_ctx,
                )
                filter_ms = (time.perf_counter() - t_filter_start) * 1000.0
            else:
                # Fast O(1) short-circuit in event loop: skip thread-pool handoff & pHash
                # while Gemini is busy, in intervention cooldown, or within min interval.
                can_run, _ = self._state_machine.can_analyze(
                    cooldown_seconds=profile.manifest.cooldown_seconds,
                    min_inference_interval=self._settings.min_inference_interval_seconds,
                )
                if not can_run:
                    self.metrics.record_filter(0.0, passed=False)
                    continue

                # Run deterministic CPU Chain of Responsibility off the event loop
                t_filter_start = time.perf_counter()
                filtered = await asyncio.to_thread(
                    self._filter_pipeline.run,
                    frame,
                    profile,
                    self._test_mode,
                )
                filter_ms = (time.perf_counter() - t_filter_start) * 1000.0

            if filtered is None or filtered.encoded_bytes is None:
                self.metrics.record_filter(filter_ms, passed=False)
                continue

            # Frame passed pipeline (or manual F10 request)
            if filtered.phash is not None:
                self._filter_pipeline.commit_analyzed_frame(filtered)
            size_kb = len(filtered.encoded_bytes) / 1024.0
            self.metrics.record_filter(filter_ms, passed=True, payload_kb=size_kb)

            if self._dry_run or self._ai_client is None:
                self._state_machine.begin_analysis()
                logger.info(
                    "[DRY-RUN] Frame #%d aprobó pipeline%s | filter=%.1fms | pHash_dist=%s | payload=%.1f KB (%s)",
                    frame.frame_id,
                    " [F10]" if force_now else "",
                    filter_ms,
                    filtered.phash_distance,
                    size_kb,
                    filtered.mime_type,
                )
                self._state_machine.finish_analysis(None)
                continue

            self._state_machine.begin_analysis()
            logger.debug(
                "Enviando Frame #%d a Gemini | filter=%.1fms | pHash_dist=%s | payload=%.1f KB (%s)",
                frame.frame_id,
                filter_ms,
                filtered.phash_distance,
                size_kb,
                filtered.mime_type,
            )

            recent_history = self._memory.format_prompt_context()
            t_inf_start = time.perf_counter()
            try:
                response = await self._ai_client.analyze_frame(
                    image_bytes=filtered.encoded_bytes,
                    mime_type=filtered.mime_type,
                    profile=profile,
                    recent_history=recent_history,
                    force_advice=force_now,
                )
            except asyncio.CancelledError:
                self._state_machine.abort_analysis()
                raise
            except Exception as exc:
                inf_ms = (time.perf_counter() - t_inf_start) * 1000.0
                self.metrics.record_inference(inf_ms, intervened=False, failed=True)
                logger.error("Fallo inesperado procesando Frame #%d: %s", frame.frame_id, exc)
                self._state_machine.abort_analysis()
                continue

            inf_ms = (time.perf_counter() - t_inf_start) * 1000.0
            intervened = await self._handle_coach_response(response, profile)
            self.metrics.record_inference(
                inf_ms,
                intervened=intervened,
                failed=(response is None),
            )

    async def _handle_coach_response(
        self,
        response: CoachResponse | None,
        profile: GameProfile,
    ) -> bool:
        """Apply InterventionStateMachine rules and dispatch overlay + voice outputs."""
        should_deliver = self._state_machine.finish_analysis(response)
        if not should_deliver or response is None:
            logger.debug("Gemini decidió silencio (should_intervene=False).")
            return False

        logger.info(
            "COACH [%s] | Overlay: %r | Voz: %r",
            response.urgency.upper(),
            response.overlay_text,
            response.voice_line,
        )

        if response.overlay_text.strip():
            self._overlay.show_message(
                text=response.overlay_text,
                urgency=response.urgency,
                title=profile.manifest.display_name,
            )

        if response.voice_line.strip():
            self._voice.enqueue(response.voice_line)

        self._memory.record(response)
        self._state_machine.complete_delivery()
        return True

    async def shutdown(self) -> None:
        """Release all active resources cleanly and emit ISO/IEC 25010 telemetry summary."""
        logger.info("Deteniendo HUDMind...")
        self._hotkeys.stop()
        await self._observer.stop()
        await self._voice.stop()
        if self._ai_client is not None:
            await self._ai_client.close_session()
        self._overlay.close()
        snap = self.metrics.snapshot()
        logger.info(
            "Telemetría final | filtrados=%d (descarte=%.1f%%, avg=%.1fms) | "
            "payload_avg=%.1fKB | IA=%d (avg=%.0fms, errores=%d) | intervenciones=%d",
            snap["frames_filtered"],
            snap["filter_drop_rate_pct"],
            snap["avg_filter_ms"],
            snap["avg_payload_kb"],
            snap["frames_sent_to_ai"],
            snap["avg_inference_ms"],
            snap["api_errors"],
            snap["interventions_delivered"],
        )


def run_health_check(settings: AppSettings) -> int:
    """Execute a comprehensive local environment & hardware readiness diagnostic."""
    logger.info("=== HUDMind — Health Check (ISO/IEC 25010 Operability) ===")
    all_ok = True

    logger.info("OS: %s %s | Python: %s", platform.system(), platform.release(), platform.python_version())

    # 1. Profiles
    loader = GameProfileLoader(settings.profiles_dir)
    profiles = loader.list_profiles()
    if not profiles:
        logger.error("[FAIL] No se encontraron perfiles válidos en %s", settings.profiles_dir)
        all_ok = False
    else:
        logger.info("[OK] Perfiles válidos: %s", ", ".join(p.manifest.game_id for p in profiles))

    # 2. Foreground Process Detection
    if loader.detector.is_supported:
        exe = loader.detector.get_foreground_executable()
        logger.info("[OK] Detector de ventana activa (pywin32+psutil) | actual=%s", exe)
    else:
        logger.warning("[WARN] Detector Win32 no disponible en esta plataforma.")

    # 3. Screen Capture & CPU Filter Pipeline Benchmark
    strategy = MSSCaptureStrategy(monitor_index=settings.capture_monitor)
    t0 = time.perf_counter()
    frame = strategy.capture_frame()
    cap_ms = (time.perf_counter() - t0) * 1000.0
    strategy.stop()

    if frame is None:
        logger.warning(
            "[WARN] MSSCaptureStrategy no obtuvo imagen en monitor %d "
            "(sesión bloqueada/no interactiva o UAC). Evaluando pipeline con frame sintético.",
            settings.capture_monitor,
        )
        mock = VideoMockCaptureStrategy(source_path=None)
        mock.start()
        frame = mock.capture_frame()
        mock.stop()
    else:
        logger.info(
            "[OK] Captura MSS: %dx%d en %.1f ms",
            frame.width,
            frame.height,
            cap_ms,
        )

    if frame is not None and profiles:
        sm = InterventionStateMachine()
        pipeline = FilterPipeline(
            detector=loader.detector,
            state_machine=sm,
            compression_quality=settings.image_compression_quality,
            image_format=settings.image_format,
        )
        tf0 = time.perf_counter()
        ctx = pipeline.run(frame, profiles[0], test_mode=True)
        filt_ms = (time.perf_counter() - tf0) * 1000.0
        if ctx is not None and ctx.encoded_bytes is not None:
            logger.info(
                "[OK] Pipeline CPU (pHash+720p+%s): %.1f ms | payload=%.1f KB",
                ctx.mime_type,
                filt_ms,
                len(ctx.encoded_bytes) / 1024.0,
            )
        else:
            logger.error("[FAIL] Pipeline CPU falló al procesar frame de diagnóstico.")
            all_ok = False

    # 4. Transparent Click-Through Overlay
    overlay = TransparentOverlay(
        enabled=True,
        position=settings.overlay_position,
        width=settings.overlay_width,
        opacity=settings.overlay_opacity,
    )
    overlay.start()
    if overlay.is_running:
        logger.info(
            "[OK] Overlay Tkinter activo | Win32 WS_EX_TRANSPARENT verificado=%s",
            overlay.click_through_verified,
        )
    else:
        logger.warning("[WARN] Overlay Tkinter no pudo iniciarse (entorno headless o sin Tk).")
    overlay.close()

    # 5. Audio Subsystem
    player = AudioPlayer(volume=settings.tts_volume)
    audio_ready = player._ensure_mixer()
    player.close()
    if audio_ready:
        logger.info("[OK] Audio pygame.mixer inicializado | motor TTS=%s", settings.tts_engine)
    else:
        logger.warning("[WARN] pygame.mixer no disponible; TTS usará fallback pyttsx3.")

    # 6. Gemini API Key Check (never prints secret)
    if settings.has_valid_api_key():
        logger.info("[OK] GEMINI_API_KEY configurada | modelo=%s", settings.default_model)
    else:
        logger.warning(
            "[WARN] GEMINI_API_KEY no configurada en .env (requerida fuera de --dry-run)."
        )

    logger.info("=== Health Check finalizado: %s ===", "PASS" if all_ok else "WITH ERRORS")
    return 0 if all_ok else 1


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    """Parse CLI arguments."""
    parser = argparse.ArgumentParser(
        prog="hudmind",
        description="HUDMind — Copiloto / Coach de Videojuegos en Tiempo Real con Gemini API.",
    )
    parser.add_argument(
        "--test-mode",
        action="store_true",
        help="Permite probar el pipeline sobre cualquier ventana abierta (omite FocusFilter).",
    )
    parser.add_argument(
        "--profile",
        type=str,
        default=None,
        help="ID de perfil a forzar en --test-mode (por defecto: example_cs2).",
    )
    parser.add_argument(
        "--mock-source",
        type=str,
        default=None,
        help="Ruta a carpeta de capturas o video para usar VideoMockCaptureStrategy.",
    )
    parser.add_argument(
        "--mock-synthetic",
        action="store_true",
        help="Usa VideoMockCaptureStrategy con frames sintéticos en memoria.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Ejecuta captura y filtros CPU sin enviar peticiones a Gemini API.",
    )
    parser.add_argument(
        "--max-runtime",
        type=float,
        default=None,
        help="Tiempo máximo de ejecución en segundos (útil para pruebas automatizadas).",
    )
    parser.add_argument(
        "--health-check",
        action="store_true",
        help="Ejecuta diagnóstico de captura, pipeline, overlay, audio y perfiles y sale.",
    )
    parser.add_argument(
        "--no-voice",
        action="store_true",
        help="Desactiva la salida de voz (TTS) para esta ejecución.",
    )
    parser.add_argument(
        "--no-overlay",
        action="store_true",
        help="Desactiva el overlay transparente para esta ejecución.",
    )
    parser.add_argument(
        "--no-hotkeys",
        action="store_true",
        help="Desactiva los atajos de teclado globales (F9/F10) para esta ejecución.",
    )
    parser.add_argument(
        "--log-level",
        type=str,
        default=None,
        help="Sobrescribe el nivel de logging (DEBUG, INFO, WARNING, ERROR).",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    """CLI entrypoint."""
    enable_windows_dpi_awareness()
    args = parse_args(argv)
    settings = AppSettings()
    raw_secret = settings.get_api_key_value()
    configure_logging(
        args.log_level or settings.log_level,
        secrets=(raw_secret,) if raw_secret else (),
        enable_file_logging=settings.enable_file_logging,
        log_file=settings.log_file,
    )

    if args.health_check:
        return run_health_check(settings)

    if not args.dry_run and not settings.has_valid_api_key():
        logger.error(
            "GEMINI_API_KEY no configurada o conserva el valor de ejemplo. "
            "Copia '.env.example' a '.env', coloca tu API key real, o ejecuta con '--dry-run'."
        )
        return 1

    orchestrator = GameCopilotOrchestrator(
        settings=settings,
        test_mode=args.test_mode,
        forced_profile_id=args.profile,
        mock_source=args.mock_source,
        use_synthetic_mock=args.mock_synthetic,
        dry_run=args.dry_run,
        enable_voice=False if args.no_voice else None,
        enable_overlay=False if args.no_overlay else None,
        enable_hotkeys=False if args.no_hotkeys else None,
    )

    try:
        asyncio.run(orchestrator.run(max_runtime_seconds=args.max_runtime))
        return 0
    except KeyboardInterrupt:
        logger.info("Interrupción por usuario (Ctrl+C). Cerrando...")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
