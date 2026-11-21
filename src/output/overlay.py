"""Transparent, always-on-top, click-through overlay window for Windows (Tkinter + Win32)."""

from __future__ import annotations

import contextlib
import ctypes
import gc
import logging
import os
import queue
import sys
import threading
from dataclasses import dataclass
from pathlib import Path

from src.config.settings import OverlayPosition
from src.pipeline.schemas import UrgencyLevel

logger = logging.getLogger(__name__)

# Win32 extended window style flags for non-intrusive click-through overlays
_GWL_EXSTYLE = -20
_WS_EX_LAYERED = 0x00080000
_WS_EX_TRANSPARENT = 0x00000020
_WS_EX_NOACTIVATE = 0x08000000
_WS_EX_TOOLWINDOW = 0x00000080
_REQUIRED_CLICKTHROUGH_MASK = _WS_EX_LAYERED | _WS_EX_TRANSPARENT

_URGENCY_STYLES: dict[UrgencyLevel, tuple[str, str]] = {
    "low": ("#38BDF8", "INFO"),
    "medium": ("#F59E0B", "TACTIC"),
    "high": ("#EF4444", "ALERT"),
}

_TRANSPARENT_KEY = "#010101"
_CARD_BG = "#0F172A"
_TEXT_PRIMARY = "#F8FAFC"
_TEXT_MUTED = "#94A3B8"


@dataclass(slots=True)
class OverlayMessage:
    """Payload rendered inside the overlay HUD card."""

    text: str
    urgency: UrgencyLevel = "medium"
    title: str = "AI COACH"
    duration_seconds: float | None = None


def enable_windows_dpi_awareness() -> None:
    """Enable Per-Monitor DPI Awareness v2 on Windows so MSS & overlay align with physical pixels."""
    if sys.platform != "win32":
        return
    # DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    with contextlib.suppress(Exception):
        user32 = ctypes.windll.user32
        user32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
        user32.SetProcessDpiAwarenessContext.restype = ctypes.c_bool
        if user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            return
    with contextlib.suppress(Exception):
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        return
    with contextlib.suppress(Exception):
        ctypes.windll.user32.SetProcessDPIAware()


def _ensure_tcl_tk_env() -> None:
    """Fix Windows Python 3.13+ virtualenv missing TCL_LIBRARY/TK_LIBRARY paths."""
    if sys.platform != "win32":
        return
    tcl_root = Path(sys.base_prefix) / "tcl"
    if not tcl_root.is_dir():
        return
    if not os.environ.get("TCL_LIBRARY"):
        tcl_candidates = sorted(tcl_root.glob("tcl8.*"))
        if tcl_candidates and tcl_candidates[0].is_dir():
            os.environ["TCL_LIBRARY"] = str(tcl_candidates[0])
    if not os.environ.get("TK_LIBRARY"):
        tk_candidates = sorted(tcl_root.glob("tk8.*"))
        if tk_candidates and tk_candidates[0].is_dir():
            os.environ["TK_LIBRARY"] = str(tk_candidates[0])


def _configure_win32_user32() -> ctypes.WinDLL | None:
    """Configure explicit 64-bit safe ctypes signatures for Win32 user32 calls."""
    if sys.platform != "win32":
        return None
    user32 = ctypes.windll.user32
    user32.GetParent.argtypes = [ctypes.c_void_p]
    user32.GetParent.restype = ctypes.c_void_p
    user32.GetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int]
    user32.GetWindowLongW.restype = ctypes.c_long
    user32.SetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_long]
    user32.SetWindowLongW.restype = ctypes.c_long
    return user32


class TransparentOverlay:
    """Thread-safe, click-through transparent notification overlay.

    Runs Tkinter's event loop on a dedicated daemon thread so it never blocks
    Python's `asyncio` loop. On Windows, applies `WS_EX_TRANSPARENT`,
    `WS_EX_LAYERED`, `WS_EX_NOACTIVATE`, and `WS_EX_TOOLWINDOW` so mouse clicks
    pass directly through to the underlying game and focus is never stolen.
    """

    def __init__(
        self,
        enabled: bool = True,
        position: OverlayPosition = "top-right",
        width: int = 440,
        opacity: float = 0.90,
        default_duration_seconds: float = 6.0,
    ) -> None:
        self._enabled = enabled
        self._position = position
        self._width = width
        self._opacity = opacity
        self._default_duration = default_duration_seconds

        self._cmd_queue: queue.Queue[OverlayMessage | None] = queue.Queue(maxsize=8)
        self._thread: threading.Thread | None = None
        self._ready_event = threading.Event()
        self._started = False
        self._failed = False
        self._click_through_verified = False

    @property
    def is_running(self) -> bool:
        return self._started and not self._failed

    @property
    def click_through_verified(self) -> bool:
        """True once Win32 GetWindowLongW confirms WS_EX_LAYERED | WS_EX_TRANSPARENT."""
        return self._click_through_verified

    def start(self) -> None:
        """Spawn the dedicated Tkinter overlay thread."""
        if not self._enabled or self._started:
            return

        self._thread = threading.Thread(
            target=self._run_tk_loop,
            name="overlay-tk-thread",
            daemon=True,
        )
        self._thread.start()
        self._ready_event.wait(timeout=3.0)
        if not self._failed:
            self._started = True
            logger.info(
                "Overlay transparente iniciado (posición=%s, click-through=%s)",
                self._position,
                self._click_through_verified if sys.platform == "win32" else False,
            )

    def show_message(
        self,
        text: str,
        urgency: UrgencyLevel = "medium",
        title: str = "AI COACH",
        duration_seconds: float | None = None,
    ) -> None:
        """Schedule a discrete notification to appear on screen (non-blocking)."""
        if not self._enabled or not self._started or self._failed:
            return

        clean_text = text.strip()
        if not clean_text:
            return

        msg = OverlayMessage(
            text=clean_text,
            urgency=urgency,
            title=title,
            duration_seconds=duration_seconds or self._default_duration,
        )

        # Drop oldest queued message if the UI queue is saturated
        if self._cmd_queue.full():
            with contextlib.suppress(queue.Empty):
                self._cmd_queue.get_nowait()
        with contextlib.suppress(queue.Full):
            self._cmd_queue.put_nowait(msg)

    def close(self) -> None:
        """Gracefully shut down the Tkinter thread."""
        if not self._started:
            return
        with contextlib.suppress(queue.Full):
            self._cmd_queue.put_nowait(None)
        if self._thread is not None and self._thread.is_alive():
            self._thread.join(timeout=2.0)
        self._started = False
        logger.info("Overlay transparente detenido.")

    def _run_tk_loop(self) -> None:
        try:
            self._run_tk_session()
        except Exception as exc:
            logger.warning("Overlay Tkinter falló al inicializar: %s", exc)
            self._failed = True
            self._ready_event.set()
        finally:
            # Ensure all Tcl/Tk widget wrappers are collected ON the Tk thread
            # to prevent CPython `Tcl_AsyncDelete` on main-thread shutdown.
            gc.collect()

    def _run_tk_session(self) -> None:
        _ensure_tcl_tk_env()
        import tkinter as tk

        user32 = _configure_win32_user32()
        root = tk.Tk()
        root.withdraw()
        root.overrideredirect(True)
        root.attributes("-topmost", True)
        root.attributes("-alpha", self._opacity)
        root.configure(bg=_TRANSPARENT_KEY)

        if sys.platform == "win32":
            root.attributes("-transparentcolor", _TRANSPARENT_KEY)

        # Build sleek dark HUD notification card (WCAG AA+ contrast on dark background)
        card = tk.Frame(
            root,
            bg=_CARD_BG,
            bd=0,
            highlightthickness=1,
            highlightbackground="#334155",
        )
        card.pack(fill="both", expand=True, padx=4, pady=4)

        accent_bar = tk.Frame(card, bg="#38BDF8", width=6)
        accent_bar.pack(side="left", fill="y")

        content_frame = tk.Frame(card, bg=_CARD_BG, padx=14, pady=10)
        content_frame.pack(side="left", fill="both", expand=True)

        header_frame = tk.Frame(content_frame, bg=_CARD_BG)
        header_frame.pack(fill="x", anchor="w")

        badge_label = tk.Label(
            header_frame,
            text="TACTIC",
            bg="#38BDF8",
            fg="#0B1120",
            font=("Segoe UI", 8, "bold"),
            padx=6,
            pady=1,
        )
        badge_label.pack(side="left")

        title_label = tk.Label(
            header_frame,
            text="AI COACH",
            bg=_CARD_BG,
            fg=_TEXT_MUTED,
            font=("Segoe UI", 9, "bold"),
            padx=8,
        )
        title_label.pack(side="left")

        body_label = tk.Label(
            content_frame,
            text="",
            bg=_CARD_BG,
            fg=_TEXT_PRIMARY,
            font=("Segoe UI", 12, "bold"),
            justify="left",
            anchor="w",
            wraplength=max(200, self._width - 48),
        )
        body_label.pack(fill="x", pady=(6, 0), anchor="w")

        hide_after_id: list[str | None] = [None]

        def apply_win32_click_through() -> None:
            if user32 is None:
                return
            try:
                root.update_idletasks()
                raw_hwnd = root.winfo_id()
                parent_hwnd = user32.GetParent(raw_hwnd)
                hwnd = parent_hwnd if parent_hwnd else raw_hwnd

                style = user32.GetWindowLongW(hwnd, _GWL_EXSTYLE)
                new_style = (
                    style
                    | _WS_EX_LAYERED
                    | _WS_EX_TRANSPARENT
                    | _WS_EX_NOACTIVATE
                    | _WS_EX_TOOLWINDOW
                )
                user32.SetWindowLongW(hwnd, _GWL_EXSTYLE, new_style)
                verified_style = user32.GetWindowLongW(hwnd, _GWL_EXSTYLE)
                self._click_through_verified = (
                    verified_style & _REQUIRED_CLICKTHROUGH_MASK
                ) == _REQUIRED_CLICKTHROUGH_MASK
            except Exception as win_exc:
                logger.debug("No se pudo aplicar WS_EX_TRANSPARENT: %s", win_exc)

        def position_window() -> None:
            root.update_idletasks()
            screen_w = root.winfo_screenwidth()
            screen_h = root.winfo_screenheight()
            req_h = max(72, root.winfo_reqheight())
            win_w = self._width
            margin_x = 24
            margin_y = 28

            if self._position == "top-right":
                x = screen_w - win_w - margin_x
                y = margin_y
            elif self._position == "top-left":
                x = margin_x
                y = margin_y
            elif self._position == "bottom-right":
                x = screen_w - win_w - margin_x
                y = screen_h - req_h - margin_y - 36
            elif self._position == "bottom-left":
                x = margin_x
                y = screen_h - req_h - margin_y - 36
            else:  # top-center
                x = (screen_w - win_w) // 2
                y = margin_y

            root.geometry(f"{win_w}x{req_h}+{max(0, x)}+{max(0, y)}")

        def hide_card() -> None:
            hide_after_id[0] = None
            root.withdraw()

        def display_card(msg: OverlayMessage) -> None:
            color, badge = _URGENCY_STYLES.get(msg.urgency, ("#38BDF8", "INFO"))
            accent_bar.configure(bg=color)
            badge_label.configure(text=badge, bg=color)
            title_label.configure(text=msg.title.upper())
            body_label.configure(text=msg.text)

            if hide_after_id[0] is not None:
                root.after_cancel(hide_after_id[0])

            root.deiconify()
            root.attributes("-topmost", True)
            position_window()
            apply_win32_click_through()

            duration_ms = int((msg.duration_seconds or self._default_duration) * 1000)
            hide_after_id[0] = root.after(duration_ms, hide_card)

        def poll_queue() -> None:
            try:
                while True:
                    item = self._cmd_queue.get_nowait()
                    if item is None:
                        if hide_after_id[0] is not None:
                            root.after_cancel(hide_after_id[0])
                            hide_after_id[0] = None
                        root.quit()
                        return
                    display_card(item)
            except queue.Empty:
                pass
            # Adaptive polling: 120ms while hidden (low wakeups), 50ms while visible
            next_delay = 50 if hide_after_id[0] is not None else 120
            root.after(next_delay, poll_queue)

        # Apply click-through immediately upon Tk window creation
        apply_win32_click_through()
        self._ready_event.set()
        root.after(120, poll_queue)
        root.mainloop()
        with contextlib.suppress(Exception):
            root.destroy()
