"""Asynchronous ScreenObserver implementing Observer / Pub-Sub with Drop-Oldest queues."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
from collections.abc import Callable

from src.capture.base import CapturedFrame, CaptureStrategy

logger = logging.getLogger(__name__)


class ScreenObserver:
    """Publishes captured frames to async subscribers without backpressure.

    Each subscriber receives an `asyncio.Queue(maxsize=1)`. When a new frame is
    produced while a subscriber's queue is still full, the stale frame is dropped
    immediately (`Drop-Oldest`) so downstream AI consumers always inspect the
    freshest visual state of the game.
    """

    def __init__(
        self,
        strategy: CaptureStrategy,
        target_fps: float = 1.0,
        should_capture: Callable[[], bool] | None = None,
        on_capture_ms: Callable[[float], None] | None = None,
    ) -> None:
        self._strategy = strategy
        self._target_fps = max(0.1, target_fps)
        self._should_capture = should_capture
        self._on_capture_ms = on_capture_ms
        self._subscribers: dict[str, asyncio.Queue[CapturedFrame]] = {}
        self._task: asyncio.Task[None] | None = None
        self._running = False
        self._paused = False
        self._active_event = asyncio.Event()
        self._active_event.set()
        self._published_count = 0
        self._dropped_count = 0
        self._skipped_by_gate_count = 0

    @property
    def target_fps(self) -> float:
        return self._target_fps

    @property
    def is_paused(self) -> bool:
        return self._paused

    @property
    def published_count(self) -> int:
        return self._published_count

    @property
    def dropped_count(self) -> int:
        return self._dropped_count

    @property
    def skipped_by_gate_count(self) -> int:
        return self._skipped_by_gate_count

    def update_fps(self, fps: float) -> None:
        """Dynamically adjust capture cadence when switching active game profiles."""
        new_fps = max(0.1, min(30.0, fps))
        if new_fps != self._target_fps:
            logger.info("ScreenObserver FPS actualizado: %.2f -> %.2f", self._target_fps, new_fps)
            self._target_fps = new_fps

    def pause(self) -> None:
        """Suspend frame grabbing with zero idle polling while no game profile is active."""
        self._paused = True
        self._active_event.clear()

    def resume(self) -> None:
        """Resume frame grabbing immediately."""
        self._paused = False
        self._active_event.set()

    def subscribe(self, subscriber_name: str) -> asyncio.Queue[CapturedFrame]:
        """Register a subscriber with a Drop-Oldest queue (maxsize=1)."""
        if subscriber_name in self._subscribers:
            return self._subscribers[subscriber_name]

        queue: asyncio.Queue[CapturedFrame] = asyncio.Queue(maxsize=1)
        self._subscribers[subscriber_name] = queue
        logger.debug("Suscriptor registrado en ScreenObserver: %s", subscriber_name)
        return queue

    def unsubscribe(self, subscriber_name: str) -> None:
        """Remove a subscriber from the event bus."""
        self._subscribers.pop(subscriber_name, None)

    def publish(self, frame: CapturedFrame) -> None:
        """Push a frame to all subscribers using Drop-Oldest semantics."""
        multi_sub = len(self._subscribers) > 1
        for idx, (name, queue) in enumerate(self._subscribers.items()):
            if queue.full():
                try:
                    stale = queue.get_nowait()
                    with contextlib.suppress(Exception):
                        stale.image.close()
                    self._dropped_count += 1
                    logger.debug(
                        "Drop-Oldest: frame obsoleto descartado para suscriptor '%s'",
                        name,
                    )
                except asyncio.QueueEmpty:
                    pass
            item = (
                CapturedFrame(
                    image=frame.image.copy(),
                    timestamp=frame.timestamp,
                    wall_time=frame.wall_time,
                    frame_id=frame.frame_id,
                    source=frame.source,
                )
                if (multi_sub and idx > 0)
                else frame
            )
            try:
                queue.put_nowait(item)
            except asyncio.QueueFull:
                with contextlib.suppress(Exception):
                    item.image.close()
                self._dropped_count += 1

        self._published_count += 1

    async def start(self) -> None:
        """Initialize strategy and spawn the async capture loop."""
        if self._running:
            return
        self._strategy.start()
        self._running = True
        self._task = asyncio.create_task(self._capture_loop(), name="screen-observer-loop")
        logger.info(
            "ScreenObserver iniciado | estrategia=%s | fps=%.2f",
            self._strategy.name,
            self._target_fps,
        )

    async def stop(self) -> None:
        """Stop the capture loop cleanly and release strategy resources."""
        self._running = False
        self._paused = False
        self._active_event.set()
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None

        self._strategy.stop()
        logger.info(
            "ScreenObserver detenido | publicados=%d | omitidos(cooldown/gate)=%d | descartados(Drop-Oldest)=%d",
            self._published_count,
            self._skipped_by_gate_count,
            self._dropped_count,
        )

    async def _capture_loop(self) -> None:
        """Continuously grab frames off the event loop thread at `target_fps`."""
        while self._running:
            if self._paused:
                await self._active_event.wait()
                continue

            loop_start = time.monotonic()

            if self._subscribers:
                if self._should_capture is not None and not self._should_capture():
                    # Skip BitBlt / GPU-DWM capture & 6MB RAM allocation while in cooldown/busy
                    self._skipped_by_gate_count += 1
                else:
                    try:
                        t_cap = time.perf_counter()
                        frame = await asyncio.to_thread(self._strategy.capture_frame)
                        cap_ms = (time.perf_counter() - t_cap) * 1000.0
                        if frame is not None:
                            if self._on_capture_ms is not None:
                                self._on_capture_ms(cap_ms)
                            self.publish(frame)
                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:
                        logger.error("Error inesperado en ciclo de ScreenObserver: %s", exc)

            interval = 1.0 / self._target_fps
            elapsed = time.monotonic() - loop_start
            sleep_time = max(0.01, interval - elapsed)
            await asyncio.sleep(sleep_time)
