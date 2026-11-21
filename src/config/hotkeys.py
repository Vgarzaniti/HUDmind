"""Native Windows global hotkeys using Win32 RegisterHotKey (no low-level keyboard hooks).

Using `RegisterHotKey` + a dedicated message-loop thread consumes ~0% CPU while idle,
supports user-configurable keys and modifiers (e.g. `F9`, `F10`, `Alt+F10`, `Ctrl+Shift+C`),
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

# Win32 message & modifier constants
_WM_HOTKEY = 0x0312
_WM_QUIT = 0x0012
_MOD_ALT = 0x0001
_MOD_CONTROL = 0x0002
_MOD_SHIFT = 0x0004
_MOD_NOREPEAT = 0x4000

_HOTKEY_ID_TOGGLE_PAUSE = 1
_HOTKEY_ID_ANALYZE_NOW = 2

# Standard Virtual-Key codes supported for configurable bindings
_VK_MAP: dict[str, int] = {
    **{f"F{i}": 0x6F + i for i in range(1, 13)},  # F1 (0x70) .. F12 (0x7B)
    **{chr(c): c for c in range(ord("A"), ord("Z") + 1)},
    **{str(d): 0x30 + d for d in range(10)},
    **{f"NUM{d}": 0x60 + d for d in range(10)},
    "TAB": 0x09,
    "SPACE": 0x20,
    "PRIOR": 0x21,
    "PAGEUP": 0x21,
    "NEXT": 0x22,
    "PAGEDOWN": 0x22,
    "END": 0x23,
    "HOME": 0x24,
    "INSERT": 0x2D,
    "DELETE": 0x2E,
    "PAUSE": 0x13,
    "ADD": 0x6B,
    "SUBTRACT": 0x6D,
    "MULTIPLY": 0x6A,
    "DIVIDE": 0x6F,
}

_MODIFIER_MAP: dict[str, tuple[int, str]] = {
    "CTRL": (_MOD_CONTROL, "Ctrl"),
    "CONTROL": (_MOD_CONTROL, "Ctrl"),
    "ALT": (_MOD_ALT, "Alt"),
    "SHIFT": (_MOD_SHIFT, "Shift"),
}

SUPPORTED_HOTKEY_PRESETS: list[str] = [
    "F6",
    "F7",
    "F8",
    "F9",
    "F10",
    "F11",
    "F12",
    "INSERT",
    "HOME",
    "END",
    "PAGEUP",
    "PAGEDOWN",
    "PAUSE",
    "Alt+F9",
    "Alt+F10",
    "Ctrl+F9",
    "Ctrl+F10",
    "Alt+C",
    "Alt+X",
    "Ctrl+Shift+A",
]


def parse_hotkey_string(hotkey_str: str) -> tuple[int, int, str]:
    """Parse a hotkey specification like `'F9'` or `'Ctrl+Alt+F10'`.

    Returns:
        `(win32_modifiers, vk_code, normalized_label)`
    Raises:
        `ValueError` if the string is empty or contains an unknown key token.
    """
    raw = hotkey_str.strip()
    if not raw:
        raise ValueError("El atajo de teclado no puede estar vacío.")

    parts = [p.strip().upper() for p in raw.split("+") if p.strip()]
    if not parts:
        raise ValueError(f"Atajo de teclado inválido: {hotkey_str!r}")

    mod_mask = 0
    key_token: str | None = None

    for token in parts:
        if token in _MODIFIER_MAP:
            bit, _ = _MODIFIER_MAP[token]
            mod_mask |= bit
        elif key_token is None and token in _VK_MAP:
            key_token = token
        else:
            raise ValueError(
                f"Tecla no reconocida o inválida en atajo '{hotkey_str}': '{token}'. "
                "Ejemplos válidos: F9, F10, Alt+F10, Ctrl+F8, INSERT, HOME."
            )

    if key_token is None:
        raise ValueError(
            f"El atajo '{hotkey_str}' debe incluir una tecla principal (ej. F9, F10, C, INSERT)."
        )

    ordered_mods: list[str] = []
    if mod_mask & _MOD_CONTROL:
        ordered_mods.append("Ctrl")
    if mod_mask & _MOD_ALT:
        ordered_mods.append("Alt")
    if mod_mask & _MOD_SHIFT:
        ordered_mods.append("Shift")

    vk_code = _VK_MAP[key_token]
    canonical = "+".join([*ordered_mods, key_token])
    return mod_mask, vk_code, canonical


class GlobalHotkeyManager:
    """Registers non-intrusive global hotkeys on Windows and dispatches callbacks to asyncio."""

    def __init__(
        self,
        *,
        enabled: bool = True,
        pause_hotkey: str = "F9",
        analyze_hotkey: str = "F10",
        on_toggle_pause: Callable[[], None] | None = None,
        on_analyze_now: Callable[[], None] | None = None,
        loop: asyncio.AbstractEventLoop | None = None,
    ) -> None:
        self._enabled = enabled and (sys.platform == "win32")
        try:
            _, _, self._pause_hotkey = parse_hotkey_string(pause_hotkey.strip() or "F9")
        except ValueError:
            self._pause_hotkey = "F9"
        try:
            _, _, self._analyze_hotkey = parse_hotkey_string(analyze_hotkey.strip() or "F10")
        except ValueError:
            self._analyze_hotkey = "F10"
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

    @property
    def pause_hotkey(self) -> str:
        return self._pause_hotkey

    @property
    def analyze_hotkey(self) -> str:
        return self._analyze_hotkey

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

        raw_specs = [
            (_HOTKEY_ID_TOGGLE_PAUSE, self._pause_hotkey, "Pausar/Reanudar"),
            (_HOTKEY_ID_ANALYZE_NOW, self._analyze_hotkey, "Analizar ahora"),
        ]

        active_labels: list[str] = []
        for hk_id, spec_str, action_desc in raw_specs:
            try:
                mod_mask, vk_code, canonical = parse_hotkey_string(spec_str)
            except ValueError as exc:
                logger.warning("Atajo inválido '%s' (%s); usando default.", spec_str, exc)
                fallback_key = "F9" if hk_id == _HOTKEY_ID_TOGGLE_PAUSE else "F10"
                mod_mask, vk_code, canonical = parse_hotkey_string(fallback_key)

            if user32.RegisterHotKey(None, hk_id, mod_mask | _MOD_NOREPEAT, vk_code):
                self._registered_ids.append(hk_id)
                active_labels.append(f"{canonical} ({action_desc})")
            else:
                logger.warning(
                    "No se pudo registrar hotkey %s (%s) — puede estar en uso por otra app.",
                    canonical,
                    action_desc,
                )

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
