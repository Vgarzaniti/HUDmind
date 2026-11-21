"""Native Windows global hotkeys using Win32 RegisterHotKey (no low-level keyboard hooks).

Using `RegisterHotKey` + a dedicated message-loop thread consumes ~0% CPU while idle
and avoids `WH_KEYBOARD_LL` hooks that competitive anti-cheat engines may flag.
"""

from __future__ import annotations

import asyncio
import contextlib
import ctypes
import logging
import sys
import threading
from collections.abc import Callable
from ctypes import wintypes

logger = logging.getLogger(__name__)

# Win32 constants
_WM_HOTKEY = 0x0312
_WM_QUIT = 0x0012
_MOD_NOREPEAT = 0x4000

# Virtual-Key codes
VK_F9 = 0x78
VK_F10 = 0x79

_HOTKEY_ID_TOGGLE_PAUSE = 1
_HOTKEY_ID_ANALYZE_NOW = 2


class GlobalHotkeyManager:
    """Registers non-intrusive global hotkeys on Windows and dispatches callbacks to asyncio."""

    def __init__(
        self,
        *,
        enabled: bool = True,
        on_toggle_pause: Callable[[], None] | None = None,
        on_analyze_now: Callable[[], None] | None = None,
        loop: asyncio.AbstractEventLoop | None = None,
    ) -> None:
        self._enabled = enabled and (sys.platform == "win32")
        self._on_toggle_pause = on_toggle_pause
        self._on_analyze_now = on_analyze_now
        self._loop = loop

        self._thread: threading.Thread | None = None
        self._thread_id: int = 0
        self._ready_event = threading.Event()
        self._registered_ids: list[int] = []
        self._running = False

    @property
    def is_running(self) -> bool:
        return self._running

    def start(self, loop: asyncio.AbstractEventLoop | None = None) -> None:
        """Start the Win32 hotkey message-loop thread."""
        if not self._enabled or self._running:
            return
        if loop is not None:
            self._loop = loop

        self._ready_event.clear()
        self._thread = threading.Thread(
            target=self._run_message_loop,
            name="win32-hotkey-thread",
            daemon=True,
        )
        self._thread.start()
        self._ready_event.wait(timeout=2.0)

    def stop(self) -> None:
        """Post WM_QUIT to the Win32 hotkey thread and unregister hotkeys cleanly."""
        if not self._running:
            return
        self._running = False
        if sys.platform == "win32" and self._thread_id:
            with contextlib.suppress(Exception):
                ctypes.windll.user32.PostThreadMessageW(self._thread_id, _WM_QUIT, 0, 0)
        if self._thread is not None and self._thread.is_alive():
            self._thread.join(timeout=2.0)
        self._thread = None
        self._thread_id = 0

    def dispatch_action(self, hotkey_id: int) -> None:
        """Invoke the callback bound to `hotkey_id` safely on the asyncio event loop."""
        callback: Callable[[], None] | None = None
        if hotkey_id == _HOTKEY_ID_TOGGLE_PAUSE:
            callback = self._on_toggle_pause
        elif hotkey_id == _HOTKEY_ID_ANALYZE_NOW:
            callback = self._on_analyze_now

        if callback is None:
            return

        if self._loop is not None and self._loop.is_running():
            self._loop.call_soon_threadsafe(callback)
        else:
            callback()

    def _run_message_loop(self) -> None:
        if sys.platform != "win32":
            self._ready_event.set()
            return

        user32 = ctypes.windll.user32
        kernel32 = ctypes.windll.kernel32
        self._thread_id = int(kernel32.GetCurrentThreadId())
        self._registered_ids.clear()

        bindings = [
            (_HOTKEY_ID_TOGGLE_PAUSE, VK_F9, "F9 (Pausar/Reanudar)"),
            (_HOTKEY_ID_ANALYZE_NOW, VK_F10, "F10 (Analizar ahora)"),
        ]

        active_labels: list[str] = []
        for hk_id, vk_code, label in bindings:
            if user32.RegisterHotKey(None, hk_id, _MOD_NOREPEAT, vk_code):
                self._registered_ids.append(hk_id)
                active_labels.append(label)
            else:
                logger.warning("No se pudo registrar hotkey %s (puede estar en uso).", label)

        self._running = bool(self._registered_ids)
        if active_labels:
            logger.info("Hotkeys globales activos: %s", " | ".join(active_labels))
        self._ready_event.set()

        if not self._running:
            return

        msg = wintypes.MSG()
        try:
            while self._running:
                ret = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
                if ret <= 0:
                    break
                if msg.message == _WM_HOTKEY:
                    self.dispatch_action(int(msg.wParam))
        except Exception as exc:
            logger.debug("Bucle de hotkeys finalizado con excepción: %s", exc)
        finally:
            for hk_id in self._registered_ids:
                user32.UnregisterHotKey(None, hk_id)
            self._registered_ids.clear()
            self._running = False
