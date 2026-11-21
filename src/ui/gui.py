"""HUDMind Control Center — Ultra-lightweight native Tkinter/ttk GUI.

Designed for minimal memory/CPU footprint (~15 MB while open, 0 MB when closed
using detached launch mode). Uses only Python stdlib `tkinter`/`ttk` (zero
Electron/Qt overhead) and runs the active coach in an isolated child process so
Tkinter never collides with the click-through overlay's Tk thread.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import queue
import re
import signal
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import messagebox, ttk
from typing import Any

from pydantic import ValidationError

from src.config.hotkeys import SUPPORTED_HOTKEY_PRESETS, parse_hotkey_string
from src.config.settings import (
    AppSettings,
    OverlayPosition,
    OverlayTheme,
    redact_secrets_in_text,
)
from src.config.telemetry import PlaystyleMetrics, TelemetryStore
from src.config.user_store import (
    PROVIDER_PRESETS,
    ProviderAccount,
    ProviderKind,
    load_accounts,
    load_effective_settings,
    save_accounts,
    save_user_settings,
)
from src.output.overlay import (
    OVERLAY_THEME_PALETTES,
    _configure_win32_user32,
    _ensure_tcl_tk_env,
    enable_windows_dpi_awareness,
)
from src.profiles.loader import MAX_CUSTOM_RULES_CHARS, GameProfileLoader

logger = logging.getLogger("hudmind.gui")

# Dark theme palette
_BG_ROOT = "#0B1120"
_BG_CARD = "#0F172A"
_BG_INPUT = "#1E293B"
_BORDER = "#334155"
_FG_TEXT = "#F8FAFC"
_FG_MUTED = "#94A3B8"
_ACCENT = "#38BDF8"
_SUCCESS = "#22C55E"
_DANGER = "#EF4444"

_VOICE_PRESETS: dict[str, list[str]] = {
    "es": [
        "es-AR-TomasNeural",
        "es-AR-ElenaNeural",
        "es-ES-AlvaroNeural",
        "es-ES-ElviraNeural",
        "es-MX-JorgeNeural",
        "es-MX-DaliaNeural",
    ],
    "en": [
        "en-US-ChristopherNeural",
        "en-US-AriaNeural",
        "en-GB-RyanNeural",
        "en-GB-SoniaNeural",
    ],
}

_FONT_FAMILIES = ["Segoe UI", "Consolas", "Arial", "Calibri"]
_POSITIONS: list[OverlayPosition] = [
    "top-right",
    "top-left",
    "top-center",
    "bottom-right",
    "bottom-left",
]
_THEMES: list[OverlayTheme] = [
    "cyber-dark",
    "minimal-glass",
    "high-contrast",
    "matrix-green",
    "tactical-amber",
]


class CircularGauge(tk.Canvas):
    """Lightweight native Tkinter Canvas circular gauge (0 to 100 / 0 to 100%)."""

    def __init__(
        self,
        parent: tk.Widget,
        *,
        title: str,
        subtitle: str = "",
        diameter: int = 86,
        ring_width: int = 8,
        is_percent: bool = False,
        fixed_color: str | None = None,
    ) -> None:
        self._title = title
        self._subtitle = subtitle
        self._diameter = diameter
        self._ring_width = ring_width
        self._is_percent = is_percent
        self._fixed_color = fixed_color

        width = diameter + 26
        height = diameter + 44
        super().__init__(
            parent,
            width=width,
            height=height,
            bg=_BG_CARD,
            highlightthickness=0,
            bd=0,
        )
        self._canvas_w = width
        self._canvas_h = height
        self.set_value(0, subtitle)

    @staticmethod
    def _grade_for_score(val: int) -> str:
        if val <= 0:
            return "—"
        if val >= 88:
            return "Rango S"
        if val >= 75:
            return "Rango A"
        if val >= 55:
            return "Rango B"
        return "Rango C"

    def set_value(self, value: int, subtitle: str | None = None) -> None:
        val = max(0, min(100, int(value)))
        if subtitle is not None:
            self._subtitle = subtitle

        self.delete("all")
        if self._fixed_color is not None and val > 0:
            color = self._fixed_color
        elif val == 0:
            color = "#475569"
        elif val >= 75:
            color = _SUCCESS
        elif val >= 52:
            color = _ACCENT
        elif val >= 35:
            color = "#F59E0B"
        else:
            color = _DANGER

        cx = self._canvas_w // 2
        self.create_text(
            cx,
            11,
            text=self._title,
            fill=_FG_TEXT,
            font=("Segoe UI", 8, "bold"),
        )

        x0 = (self._canvas_w - self._diameter) // 2
        y0 = 23
        x1 = x0 + self._diameter
        y1 = y0 + self._diameter
        cy = (y0 + y1) // 2

        # Background dark track ring
        self.create_oval(
            x0,
            y0,
            x1,
            y1,
            outline=_BG_INPUT,
            width=self._ring_width,
        )

        # Foreground progress arc (0 to 100 mapped clockwise from 12 o'clock)
        if val > 0:
            extent = -min(359.6, max(4.0, val * 3.6))
            self.create_arc(
                x0,
                y0,
                x1,
                y1,
                start=90,
                extent=extent,
                style="arc",
                outline=color,
                width=self._ring_width,
            )

        main_label = f"{val}%" if self._is_percent else f"{val}"
        self.create_text(
            cx,
            cy - 5,
            text=main_label,
            fill=color if val > 0 else _FG_MUTED,
            font=("Segoe UI", 13 if self._diameter >= 84 else 11, "bold"),
        )

        badge_text = self._grade_for_score(val) if self._is_percent else "/ 100"
        self.create_text(
            cx,
            cy + 12,
            text=badge_text,
            fill=_FG_MUTED,
            font=("Segoe UI", 7, "bold"),
        )

        if self._subtitle:
            self.create_text(
                cx,
                y1 + 11,
                text=self._subtitle[:24],
                fill=_FG_MUTED,
                font=("Segoe UI", 7),
            )


class AccountEditorDialog(tk.Toplevel):
    """Modal dialog for creating or editing a multi-provider AI account."""

    def __init__(
        self,
        parent: tk.Tk,
        account: ProviderAccount | None = None,
        default_priority: int = 10,
    ) -> None:
        super().__init__(parent)
        self.title("Editar Cuenta IA" if account else "Agregar Cuenta IA")
        self.configure(bg=_BG_ROOT)
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()

        self.result: ProviderAccount | None = None
        self._existing_id = account.id if account else None

        initial_provider: ProviderKind = account.provider if account else "gemini"
        preset = PROVIDER_PRESETS[initial_provider]

        self._var_provider = tk.StringVar(value=initial_provider)
        self._var_label = tk.StringVar(value=account.label if account else preset.display_name)
        self._var_model = tk.StringVar(value=account.model if account else preset.default_model)
        self._var_api_key = tk.StringVar(value=account.api_key if account else "")
        self._var_base_url = tk.StringVar(
            value=account.base_url if account else preset.default_base_url
        )
        self._var_priority = tk.IntVar(value=account.priority if account else default_priority)
        self._var_enabled = tk.BooleanVar(value=account.enabled if account else True)
        self._var_show_key = tk.BooleanVar(value=False)

        self._build_ui()
        self._on_provider_changed(reset_fields=(account is None))
        self.wait_visibility()
        self.focus_set()

    def _build_ui(self) -> None:
        pad: dict[str, Any] = {"padx": 12, "pady": 6}
        container = tk.Frame(self, bg=_BG_CARD, padx=16, pady=14)
        container.pack(fill="both", expand=True, padx=10, pady=10)

        # Provider selector
        tk.Label(container, text="Proveedor:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=0, column=0, sticky="w", **pad
        )
        provider_keys: list[str] = [str(k) for k in PROVIDER_PRESETS]
        self._combo_provider = ttk.Combobox(
            container,
            textvariable=self._var_provider,
            values=provider_keys,
            state="readonly",
            width=36,
        )
        self._combo_provider.grid(row=0, column=1, sticky="ew", **pad)
        self._combo_provider.bind("<<ComboboxSelected>>", lambda _: self._on_provider_changed(True))

        # Hint label
        self._lbl_hint = tk.Label(
            container,
            text="",
            bg=_BG_CARD,
            fg=_ACCENT,
            wraplength=390,
            justify="left",
            font=("Segoe UI", 8),
        )
        self._lbl_hint.grid(row=1, column=0, columnspan=2, sticky="w", padx=12, pady=(0, 8))

        # Account label
        tk.Label(container, text="Nombre / Alias:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=2, column=0, sticky="w", **pad
        )
        ttk.Entry(container, textvariable=self._var_label, width=38).grid(
            row=2, column=1, sticky="ew", **pad
        )

        # Model combobox (editable)
        tk.Label(container, text="Modelo (Visión):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=3, column=0, sticky="w", **pad
        )
        self._combo_model = ttk.Combobox(container, textvariable=self._var_model, width=36)
        self._combo_model.grid(row=3, column=1, sticky="ew", **pad)

        # API Key
        tk.Label(container, text="API Key:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=4, column=0, sticky="w", **pad
        )
        key_row = tk.Frame(container, bg=_BG_CARD)
        key_row.grid(row=4, column=1, sticky="ew", **pad)
        self._entry_key = ttk.Entry(key_row, textvariable=self._var_api_key, show="*", width=30)
        self._entry_key.pack(side="left", fill="x", expand=True)
        ttk.Checkbutton(
            key_row,
            text="Ver",
            variable=self._var_show_key,
            command=self._toggle_key_visibility,
        ).pack(side="left", padx=(6, 0))

        # Base URL
        tk.Label(container, text="Base URL (opcional):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=5, column=0, sticky="w", **pad
        )
        ttk.Entry(container, textvariable=self._var_base_url, width=38).grid(
            row=5, column=1, sticky="ew", **pad
        )

        # Priority & Enabled
        tk.Label(container, text="Prioridad (1=primero):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=6, column=0, sticky="w", **pad
        )
        prio_row = tk.Frame(container, bg=_BG_CARD)
        prio_row.grid(row=6, column=1, sticky="w", **pad)
        ttk.Spinbox(prio_row, from_=1, to=99, textvariable=self._var_priority, width=6).pack(
            side="left"
        )
        ttk.Checkbutton(prio_row, text="Cuenta habilitada", variable=self._var_enabled).pack(
            side="left", padx=(14, 0)
        )

        # Action buttons
        btn_row = tk.Frame(container, bg=_BG_CARD)
        btn_row.grid(row=7, column=0, columnspan=2, sticky="e", pady=(14, 2), padx=12)
        ttk.Button(btn_row, text="Cancelar", command=self.destroy).pack(side="right", padx=(8, 0))
        ttk.Button(btn_row, text="Guardar Cuenta", command=self._on_save).pack(side="right")

    def _toggle_key_visibility(self) -> None:
        self._entry_key.configure(show="" if self._var_show_key.get() else "*")

    def _on_provider_changed(self, reset_fields: bool) -> None:
        kind_raw = self._var_provider.get()
        kind: ProviderKind = kind_raw if kind_raw in PROVIDER_PRESETS else "gemini"  # type: ignore[assignment]
        preset = PROVIDER_PRESETS[kind]
        self._lbl_hint.configure(text=preset.hint)
        self._combo_model.configure(values=preset.suggested_models)
        if reset_fields:
            self._var_label.set(preset.display_name)
            self._var_model.set(preset.default_model)
            self._var_base_url.set(preset.default_base_url)

    def _on_save(self) -> None:
        label = self._var_label.get().strip()
        model = self._var_model.get().strip()
        if not label or not model:
            messagebox.showwarning("Campos requeridos", "Completá Nombre y Modelo.", parent=self)
            return

        try:
            prio = max(1, min(999, int(self._var_priority.get())))
        except (tk.TclError, ValueError):
            prio = 10

        kind_raw = self._var_provider.get()
        kind: ProviderKind = kind_raw if kind_raw in PROVIDER_PRESETS else "gemini"  # type: ignore[assignment]
        kwargs: dict[str, Any] = {
            "label": label,
            "provider": kind,
            "api_key": self._var_api_key.get().strip(),
            "model": model,
            "base_url": self._var_base_url.get().strip(),
            "priority": prio,
            "enabled": bool(self._var_enabled.get()),
        }
        if self._existing_id:
            kwargs["id"] = self._existing_id

        try:
            self.result = ProviderAccount(**kwargs)
        except ValidationError as exc:
            messagebox.showerror("Configuración inválida", str(exc), parent=self)
            return
        self.destroy()


class NewProfileDialog(tk.Toplevel):
    """Modal wizard to create a new game profile cartridge from active Windows processes."""

    def __init__(self, parent: tk.Tk, loader: GameProfileLoader) -> None:
        super().__init__(parent)
        self.title("Crear Nuevo Perfil de Juego")
        self.configure(bg=_BG_ROOT)
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()

        self._loader = loader
        self.created_game_id: str | None = None

        self._var_display_name = tk.StringVar(value="")
        self._var_game_id = tk.StringVar(value="")
        self._var_process = tk.StringVar(value="")
        self._var_lang = tk.StringVar(value="es")

        self._build_ui()
        self._refresh_processes()
        self.wait_visibility()
        self.focus_set()

    def _build_ui(self) -> None:
        pad: dict[str, Any] = {"padx": 12, "pady": 7}
        box = tk.Frame(self, bg=_BG_CARD, padx=16, pady=14)
        box.pack(fill="both", expand=True, padx=10, pady=10)

        tk.Label(
            box,
            text="Creá un cartucho para cualquier juego eligiendo su ejecutable (.exe):",
            bg=_BG_CARD,
            fg=_ACCENT,
            font=("Segoe UI", 9, "bold"),
        ).grid(row=0, column=0, columnspan=2, sticky="w", padx=12, pady=(0, 8))

        tk.Label(box, text="Ejecutable (.exe):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=1, column=0, sticky="w", **pad
        )
        proc_row = tk.Frame(box, bg=_BG_CARD)
        proc_row.grid(row=1, column=1, sticky="ew", **pad)
        self._combo_proc = ttk.Combobox(proc_row, textvariable=self._var_process, width=28)
        self._combo_proc.pack(side="left", fill="x", expand=True)
        self._combo_proc.bind("<<ComboboxSelected>>", lambda _: self._auto_fill_from_exe())
        ttk.Button(proc_row, text="↻", width=3, command=self._refresh_processes).pack(
            side="left", padx=(6, 0)
        )

        tk.Label(box, text="Nombre del Juego:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=2, column=0, sticky="w", **pad
        )
        entry_name = ttk.Entry(box, textvariable=self._var_display_name, width=36)
        entry_name.grid(row=2, column=1, sticky="ew", **pad)
        entry_name.bind("<KeyRelease>", lambda _: self._auto_slug())

        tk.Label(box, text="ID del Perfil (carpeta):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=3, column=0, sticky="w", **pad
        )
        ttk.Entry(box, textvariable=self._var_game_id, width=36).grid(
            row=3, column=1, sticky="ew", **pad
        )

        tk.Label(box, text="Idioma base:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=4, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            box, textvariable=self._var_lang, values=["es", "en"], state="readonly", width=12
        ).grid(row=4, column=1, sticky="w", **pad)

        btn_row = tk.Frame(box, bg=_BG_CARD)
        btn_row.grid(row=5, column=0, columnspan=2, sticky="e", pady=(14, 2), padx=12)
        ttk.Button(btn_row, text="Cancelar", command=self.destroy).pack(side="right", padx=(8, 0))
        ttk.Button(btn_row, text="Crear Perfil", command=self._on_create).pack(side="right")

    def _refresh_processes(self) -> None:
        exes = self._loader.detector.list_running_executables()
        self._combo_proc.configure(values=exes)

    def _auto_fill_from_exe(self) -> None:
        exe = self._var_process.get().strip()
        if not exe or self._var_display_name.get().strip():
            return
        stem = exe[:-4] if exe.lower().endswith(".exe") else exe
        clean_title = re.sub(r"[-_]+", " ", stem).strip().title()
        self._var_display_name.set(clean_title)
        self._auto_slug()

    def _auto_slug(self) -> None:
        raw = self._var_display_name.get().strip().lower()
        slug = re.sub(r"[^a-z0-9]+", "_", raw).strip("_")
        if slug:
            self._var_game_id.set(slug[:48])

    def _on_create(self) -> None:
        try:
            profile = self._loader.create_profile_scaffold(
                game_id=self._var_game_id.get(),
                display_name=self._var_display_name.get(),
                process_name=self._var_process.get(),
                language=self._var_lang.get(),
            )
            self.created_game_id = profile.manifest.game_id
            self.destroy()
        except Exception as exc:
            messagebox.showerror("No se pudo crear el perfil", str(exc), parent=self)


class HUDMindGUI:
    """Main lightweight Tkinter Control Center for HUDMind."""

    def __init__(self) -> None:
        enable_windows_dpi_awareness()
        _ensure_tcl_tk_env()

        self._settings: AppSettings = load_effective_settings()
        self._accounts: list[ProviderAccount] = load_accounts(self._settings)
        self._profile_loader = GameProfileLoader(self._settings.profiles_dir)
        self._profiles = self._profile_loader.list_profiles()
        self._telemetry = TelemetryStore()
        self._sim_counter = 0

        self._process: subprocess.Popen[str] | None = None
        self._log_queue: queue.Queue[str] = queue.Queue(maxsize=300)
        self._preview_win: tk.Toplevel | None = None
        self._preview_after_id: str | None = None

        self._root = tk.Tk()
        self._root.title("HUDMind — Panel de Configuración y Coach IA")
        self._root.geometry("960x660")
        self._root.minsize(850, 580)
        self._root.configure(bg=_BG_ROOT)
        self._root.protocol("WM_DELETE_WINDOW", self._on_close)

        self._init_styles()
        self._init_vars()
        self._build_layout()
        self._refresh_accounts_tree()
        self._refresh_analytics_dashboard()
        self._root.after(150, self._poll_logs)

    def _init_styles(self) -> None:
        style = ttk.Style(self._root)
        with contextlib.suppress(Exception):
            style.theme_use("clam")

        style.configure("TNotebook", background=_BG_ROOT, borderwidth=0)
        style.configure(
            "TNotebook.Tab",
            background=_BG_INPUT,
            foreground=_FG_MUTED,
            padding=(14, 7),
            font=("Segoe UI", 9, "bold"),
        )
        style.map(
            "TNotebook.Tab",
            background=[("selected", _BG_CARD)],
            foreground=[("selected", _ACCENT)],
        )
        style.configure("TFrame", background=_BG_CARD)
        style.configure("TLabel", background=_BG_CARD, foreground=_FG_TEXT, font=("Segoe UI", 9))
        style.configure("TCheckbutton", background=_BG_CARD, foreground=_FG_TEXT)
        style.configure(
            "TButton",
            background=_BG_INPUT,
            foreground=_FG_TEXT,
            padding=(10, 5),
            borderwidth=1,
        )
        style.map("TButton", background=[("active", "#334155")])
        style.configure(
            "Treeview",
            background=_BG_ROOT,
            fieldbackground=_BG_ROOT,
            foreground=_FG_TEXT,
            rowheight=26,
        )
        style.configure(
            "Treeview.Heading",
            background=_BG_INPUT,
            foreground=_ACCENT,
            font=("Segoe UI", 9, "bold"),
        )

    def _init_vars(self) -> None:
        s = self._settings
        # Control tab
        self._var_mode = tk.StringVar(value="test")
        self._var_coach_mode = tk.StringVar(value=s.coach_mode)
        profile_ids = [p.manifest.game_id for p in self._profiles] or ["example_cs2"]
        self._var_profile = tk.StringVar(value=profile_ids[0])
        self._var_rules_game = tk.StringVar(value=profile_ids[0])
        self._var_stats_game = tk.StringVar(value="all")
        self._var_dry_run = tk.BooleanVar(value=False)

        # Language & Voice tab
        self._var_text_lang = tk.StringVar(value=s.text_language)
        self._var_voice_lang = tk.StringVar(value=s.voice_language)
        self._var_enable_voice = tk.BooleanVar(value=s.enable_voice)
        self._var_tts_engine = tk.StringVar(value=s.tts_engine)
        self._var_tts_voice = tk.StringVar(value=s.tts_voice)
        try:
            rate_int = int(s.tts_rate.replace("%", "").replace("+", "").strip() or "10")
        except ValueError:
            rate_int = 10
        self._var_tts_rate = tk.IntVar(value=rate_int)
        self._var_tts_volume = tk.DoubleVar(value=s.tts_volume)

        # Overlay style tab
        self._var_enable_overlay = tk.BooleanVar(value=s.enable_overlay)
        self._var_overlay_theme = tk.StringVar(value=s.overlay_theme)
        self._var_overlay_pos = tk.StringVar(value=s.overlay_position)
        self._var_overlay_opacity = tk.DoubleVar(value=s.overlay_opacity)
        self._var_overlay_width = tk.IntVar(value=s.overlay_width)
        self._var_overlay_duration = tk.DoubleVar(value=s.overlay_duration_seconds)
        self._var_overlay_font = tk.StringVar(value=s.overlay_font_family)
        self._var_overlay_font_size = tk.IntVar(value=s.overlay_font_size)

        # Performance tab
        self._var_token_mode = tk.StringVar(value=s.token_efficiency_mode)
        self._var_image_format = tk.StringVar(value=s.image_format)
        self._var_image_quality = tk.IntVar(value=s.image_compression_quality)
        self._var_monitor = tk.IntVar(value=s.capture_monitor)
        self._var_min_interval = tk.DoubleVar(value=s.min_inference_interval_seconds)
        self._var_low_priority = tk.BooleanVar(value=s.low_process_priority)
        self._var_hotkeys = tk.BooleanVar(value=s.enable_hotkeys)
        self._var_hk_pause = tk.StringVar(value=s.hotkey_toggle_pause)
        self._var_hk_analyze = tk.StringVar(value=s.hotkey_analyze_now)
        self._var_file_log = tk.BooleanVar(value=s.enable_file_logging)
        self._var_status = tk.StringVar(value="Listo — 0% CPU en reposo.")

    def _build_layout(self) -> None:
        # Header
        header = tk.Frame(self._root, bg=_BG_ROOT, padx=16, pady=10)
        header.pack(fill="x")
        tk.Label(
            header,
            text="HUDMind",
            bg=_BG_ROOT,
            fg=_ACCENT,
            font=("Segoe UI", 16, "bold"),
        ).pack(side="left")
        tk.Label(
            header,
            text="  Coach Táctico Multimodal · Ultra-Ligero · Multi-IA con Rotación Automática",
            bg=_BG_ROOT,
            fg=_FG_MUTED,
            font=("Segoe UI", 9),
        ).pack(side="left", pady=(4, 0))

        # Tabs
        self._notebook = ttk.Notebook(self._root)
        self._notebook.pack(fill="both", expand=True, padx=12, pady=(0, 8))

        self._tab_control = ttk.Frame(self._notebook, padding=14)
        self._tab_accounts = ttk.Frame(self._notebook, padding=14)
        self._tab_rules = ttk.Frame(self._notebook, padding=14)
        self._tab_analytics = ttk.Frame(self._notebook, padding=12)
        self._tab_lang_voice = ttk.Frame(self._notebook, padding=14)
        self._tab_overlay = ttk.Frame(self._notebook, padding=14)
        self._tab_perf = ttk.Frame(self._notebook, padding=14)

        self._notebook.add(self._tab_control, text="  Control  ")
        self._notebook.add(self._tab_accounts, text="  Cuentas IA & Rotación  ")
        self._notebook.add(self._tab_rules, text="  Reglas por Juego  ")
        self._notebook.add(self._tab_analytics, text="  📊 Mi Estilo & Uso IA  ")
        self._notebook.add(self._tab_lang_voice, text="  Idioma y Voz  ")
        self._notebook.add(self._tab_overlay, text="  Estilo Overlay  ")
        self._notebook.add(self._tab_perf, text="  Rendimiento  ")

        self._build_control_tab()
        self._build_accounts_tab()
        self._build_rules_tab()
        self._build_analytics_tab()
        self._build_lang_voice_tab()
        self._build_overlay_tab()
        self._build_perf_tab()
        self._notebook.bind(
            "<<NotebookTabChanged>>",
            lambda _e: self._refresh_analytics_dashboard(),
        )

        # Footer bar
        footer = tk.Frame(self._root, bg=_BG_ROOT, padx=14, pady=8)
        footer.pack(fill="x")
        tk.Label(
            footer,
            textvariable=self._var_status,
            bg=_BG_ROOT,
            fg=_SUCCESS,
            font=("Segoe UI", 9),
        ).pack(side="left")
        ttk.Button(
            footer,
            text="Guardar Configuración",
            command=self._save_all,
        ).pack(side="right")

    # ------------------------------------------------------------------ Tab 1: Control
    def _build_control_tab(self) -> None:
        top = tk.Frame(self._tab_control, bg=_BG_CARD)
        top.pack(fill="x", pady=(0, 10))

        tk.Label(top, text="Modo de ejecución:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=0, column=0, sticky="w", padx=6, pady=4
        )
        mode_frame = tk.Frame(top, bg=_BG_CARD)
        mode_frame.grid(row=0, column=1, sticky="w", padx=6)
        ttk.Radiobutton(
            mode_frame,
            text="Detección automática (.exe)",
            variable=self._var_mode,
            value="auto",
        ).pack(side="left", padx=(0, 12))
        ttk.Radiobutton(
            mode_frame,
            text="Modo Prueba (YouTube / cualquier ventana)",
            variable=self._var_mode,
            value="test",
        ).pack(side="left")

        tk.Label(top, text="Perfil de juego (Modo Prueba):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=1, column=0, sticky="w", padx=6, pady=6
        )
        prof_row = tk.Frame(top, bg=_BG_CARD)
        prof_row.grid(row=1, column=1, sticky="w", padx=6, pady=6)
        profile_ids = [p.manifest.game_id for p in self._profiles] or ["example_cs2"]
        self._combo_profile = ttk.Combobox(
            prof_row,
            textvariable=self._var_profile,
            values=profile_ids,
            state="readonly",
            width=24,
        )
        self._combo_profile.pack(side="left", padx=(0, 8))
        ttk.Button(
            prof_row,
            text="+ Nuevo Perfil de Juego",
            command=self._create_new_game_profile,
        ).pack(side="left")

        tk.Label(top, text="Estilo del Coach:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=2, column=0, sticky="w", padx=6, pady=4
        )
        cmode_frame = tk.Frame(top, bg=_BG_CARD)
        cmode_frame.grid(row=2, column=1, sticky="w", padx=6)
        ttk.Radiobutton(
            cmode_frame,
            text="Partida Competitiva (Match)",
            variable=self._var_coach_mode,
            value="match",
        ).pack(side="left", padx=(0, 12))
        ttk.Radiobutton(
            cmode_frame,
            text="Modo Entrenamiento / Práctica (Free Play, Art of War, Aim Botz)",
            variable=self._var_coach_mode,
            value="training",
        ).pack(side="left")

        ttk.Checkbutton(
            top,
            text="Dry-Run (probar captura y filtros sin consumir tokens IA)",
            variable=self._var_dry_run,
        ).grid(row=3, column=1, sticky="w", padx=6, pady=2)

        # Control buttons
        btns = tk.Frame(self._tab_control, bg=_BG_CARD)
        btns.pack(fill="x", pady=(8, 10))

        self._btn_start = tk.Button(
            btns,
            text="▶  Iniciar Coach",
            bg=_SUCCESS,
            fg="#052E16",
            font=("Segoe UI", 10, "bold"),
            relief="flat",
            padx=16,
            pady=7,
            command=lambda: self._start_coach(detach_and_close=False),
        )
        self._btn_start.pack(side="left", padx=(0, 8))

        self._btn_stop = tk.Button(
            btns,
            text="■  Detener",
            bg=_DANGER,
            fg=_FG_TEXT,
            font=("Segoe UI", 10, "bold"),
            relief="flat",
            padx=14,
            pady=7,
            state="disabled",
            command=self._stop_coach,
        )
        self._btn_stop.pack(side="left", padx=(0, 8))

        ttk.Button(
            btns,
            text="Iniciar y Cerrar GUI (Mínimo RAM)",
            command=lambda: self._start_coach(detach_and_close=True),
        ).pack(side="left", padx=(0, 8))

        ttk.Button(
            btns,
            text="Vista Previa Overlay",
            command=self._show_overlay_preview,
        ).pack(side="right")

        hk_bar = tk.Frame(self._tab_control, bg=_BG_CARD)
        hk_bar.pack(fill="x", pady=(0, 6))
        self._lbl_hotkeys_hint = tk.Label(
            hk_bar,
            text="",
            bg=_BG_CARD,
            fg=_ACCENT,
            font=("Segoe UI", 8, "bold"),
        )
        self._lbl_hotkeys_hint.pack(side="left")
        ttk.Button(
            hk_bar,
            text="⌨ Configurar Atajos",
            command=lambda: self._notebook.select(self._tab_perf),
        ).pack(side="right")
        self._refresh_hotkey_hint_label()

        # Live console log viewer
        self._txt_logs = tk.Text(
            self._tab_control,
            bg=_BG_ROOT,
            fg="#CBD5E1",
            insertbackground=_FG_TEXT,
            font=("Consolas", 9),
            height=15,
            state="disabled",
            relief="flat",
            padx=8,
            pady=6,
        )
        self._txt_logs.pack(fill="both", expand=True)

    # ------------------------------------------------------------------ Tab 2: Accounts
    def _build_accounts_tab(self) -> None:
        tk.Label(
            self._tab_accounts,
            text=(
                "Agregá varias cuentas (Gemini, ChatGPT, Claude, Groq gratis, OpenRouter :free, "
                "Pollinations gratis, Ollama Local SIN LOGIN o Custom). Si una cuenta se queda sin "
                "tokens/cuota (HTTP 429/402), HUDMind rota automáticamente a la siguiente."
            ),
            bg=_BG_CARD,
            fg=_FG_MUTED,
            wraplength=780,
            justify="left",
        ).pack(anchor="w", pady=(0, 10))

        cols = ("prio", "label", "provider", "model", "key", "status")
        self._tree = ttk.Treeview(self._tab_accounts, columns=cols, show="headings", height=11)
        self._tree.heading("prio", text="#")
        self._tree.heading("label", text="Cuenta / Alias")
        self._tree.heading("provider", text="Proveedor")
        self._tree.heading("model", text="Modelo")
        self._tree.heading("key", text="API Key")
        self._tree.heading("status", text="Estado")

        self._tree.column("prio", width=40, anchor="center")
        self._tree.column("label", width=185)
        self._tree.column("provider", width=110, anchor="center")
        self._tree.column("model", width=215)
        self._tree.column("key", width=115, anchor="center")
        self._tree.column("status", width=110, anchor="center")
        self._tree.pack(fill="both", expand=True, pady=(0, 10))

        bar = tk.Frame(self._tab_accounts, bg=_BG_CARD)
        bar.pack(fill="x")
        ttk.Button(bar, text="+ Agregar Cuenta", command=self._add_account).pack(side="left", padx=(0, 6))
        ttk.Button(bar, text="Editar", command=self._edit_account).pack(side="left", padx=(0, 6))
        ttk.Button(bar, text="Subir ↑", command=lambda: self._move_account(-1)).pack(side="left", padx=(0, 6))
        ttk.Button(bar, text="Bajar ↓", command=lambda: self._move_account(1)).pack(side="left", padx=(0, 6))
        ttk.Button(bar, text="Activar/Pausar", command=self._toggle_account).pack(side="left", padx=(0, 6))
        ttk.Button(bar, text="Probar Conexión", command=self._test_selected_account).pack(side="left", padx=(0, 6))
        ttk.Button(bar, text="Eliminar", command=self._delete_account).pack(side="right")

        quick = tk.Frame(self._tab_accounts, bg=_BG_CARD)
        quick.pack(fill="x", pady=(10, 0))
        tk.Label(quick, text="Presets gratis rápidos:", bg=_BG_CARD, fg=_FG_MUTED).pack(
            side="left", padx=(0, 8)
        )
        ttk.Button(
            quick,
            text="+ Ollama Local (Gratis sin login)",
            command=lambda: self._quick_add_preset("ollama"),
        ).pack(side="left", padx=(0, 6))
        ttk.Button(
            quick,
            text="+ Groq Gratis",
            command=lambda: self._quick_add_preset("groq"),
        ).pack(side="left", padx=(0, 6))
        ttk.Button(
            quick,
            text="+ OpenRouter :free",
            command=lambda: self._quick_add_preset("openrouter"),
        ).pack(side="left")

    # ------------------------------------------------------------------ Tab 3: Per-Game Custom Rules
    def _build_rules_tab(self) -> None:
        # 1. Explicit Impact Warning Banner
        warn_box = tk.Frame(
            self._tab_rules,
            bg="#1E1B18",
            highlightthickness=1,
            highlightbackground="#F59E0B",
            padx=12,
            pady=8,
        )
        warn_box.pack(fill="x", pady=(0, 10))
        tk.Label(
            warn_box,
            text="⚠ AVISO DE IMPACTO AL AGREGAR REGLAS PERSONALIZADAS POR JUEGO",
            bg="#1E1B18",
            fg="#F59E0B",
            font=("Segoe UI", 9, "bold"),
        ).pack(anchor="w")
        tk.Label(
            warn_box,
            text=(
                "• Consumo de Tokens y Latencia: Tus reglas se suman a la base de conocimiento del juego. "
                "En Gemini/Claude/OpenAI se precargan en caché, pero en proveedores sin caché (o si superás "
                "~200 tokens extra) aumentan el gasto de cuota en cada frame y pueden demorar la respuesta.\n"
                "• Calidad y Foco del Coach: Reglas largas o contradictorias con el manual base pueden diluir la "
                "precisión o hacer que el coach hable de más. Usá viñetas cortas y directas (máx. 5–8 líneas).\n"
                "• Reinicio de Caché: Si el coach ya está corriendo, detenelo y volvelo a iniciar tras guardar."
            ),
            bg="#1E1B18",
            fg="#FDE68A",
            justify="left",
            wraplength=780,
            font=("Segoe UI", 8),
        ).pack(anchor="w", pady=(3, 0))

        # 2. Top selector bar
        top_bar = tk.Frame(self._tab_rules, bg=_BG_CARD)
        top_bar.pack(fill="x", pady=(0, 8))

        tk.Label(top_bar, text="Juego a personalizar:", bg=_BG_CARD, fg=_FG_TEXT).pack(
            side="left", padx=(0, 8)
        )
        profile_ids = [p.manifest.game_id for p in self._profiles] or ["example_cs2"]
        self._combo_rules_game = ttk.Combobox(
            top_bar,
            textvariable=self._var_rules_game,
            values=profile_ids,
            state="readonly",
            width=24,
        )
        self._combo_rules_game.pack(side="left", padx=(0, 10))
        self._combo_rules_game.bind("<<ComboboxSelected>>", lambda _: self._load_selected_game_rules())

        ttk.Button(
            top_bar,
            text="+ Insertar Ejemplo para este Juego",
            command=self._insert_game_rule_example,
        ).pack(side="left", padx=(0, 6))

        ttk.Button(
            top_bar,
            text="Ver Manual Base del Juego",
            command=self._view_base_knowledge,
        ).pack(side="left")

        # 3. Live Impact Meter (Traffic light)
        self._lbl_rules_impact = tk.Label(
            self._tab_rules,
            text="Impacto actual: NULO (0 caracteres | ~0 tokens extra)",
            bg=_BG_CARD,
            fg=_SUCCESS,
            font=("Segoe UI", 9, "bold"),
        )
        self._lbl_rules_impact.pack(anchor="w", pady=(0, 4))

        # 4. Rules Editor
        self._txt_custom_rules = tk.Text(
            self._tab_rules,
            bg=_BG_ROOT,
            fg=_FG_TEXT,
            insertbackground=_FG_TEXT,
            font=("Consolas", 10),
            height=11,
            relief="flat",
            padx=10,
            pady=8,
            wrap="word",
        )
        self._txt_custom_rules.pack(fill="both", expand=True, pady=(0, 8))
        self._txt_custom_rules.bind("<KeyRelease>", lambda _: self._update_rules_impact_meter())

        # 5. Bottom action buttons
        act_bar = tk.Frame(self._tab_rules, bg=_BG_CARD)
        act_bar.pack(fill="x")
        ttk.Button(
            act_bar,
            text="Limpiar Reglas de este Juego",
            command=self._clear_selected_game_rules,
        ).pack(side="left")
        ttk.Button(
            act_bar,
            text="💾  Guardar Reglas de este Juego",
            command=self._save_selected_game_rules,
        ).pack(side="right")

        self._load_selected_game_rules()

    # ------------------------------------------------------------------ Tab 3: Language & Voice
    def _build_lang_voice_tab(self) -> None:
        grid = tk.Frame(self._tab_lang_voice, bg=_BG_CARD)
        grid.pack(fill="x", anchor="n")
        pad: dict[str, Any] = {"padx": 8, "pady": 8}

        tk.Label(grid, text="Idioma de los textos (Overlay):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=0, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            grid,
            textvariable=self._var_text_lang,
            values=["es", "en"],
            state="readonly",
            width=18,
        ).grid(row=0, column=1, sticky="w", **pad)
        tk.Label(grid, text="es = Español  |  en = English", bg=_BG_CARD, fg=_FG_MUTED).grid(
            row=0, column=2, sticky="w", **pad
        )

        tk.Label(grid, text="Idioma del audio (Voz TTS):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=1, column=0, sticky="w", **pad
        )
        cb_vlang = ttk.Combobox(
            grid,
            textvariable=self._var_voice_lang,
            values=["es", "en"],
            state="readonly",
            width=18,
        )
        cb_vlang.grid(row=1, column=1, sticky="w", **pad)
        cb_vlang.bind("<<ComboboxSelected>>", lambda _: self._sync_voice_presets())

        ttk.Checkbutton(
            grid,
            text="Habilitar salida de voz (TTS asíncrono)",
            variable=self._var_enable_voice,
        ).grid(row=2, column=1, columnspan=2, sticky="w", **pad)

        tk.Label(grid, text="Motor TTS:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=3, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            grid,
            textvariable=self._var_tts_engine,
            values=["edge", "pyttsx3", "piper"],
            state="readonly",
            width=18,
        ).grid(row=3, column=1, sticky="w", **pad)
        tk.Label(
            grid,
            text="edge = neural online | pyttsx3 = offline ultra-liviano",
            bg=_BG_CARD,
            fg=_FG_MUTED,
        ).grid(row=3, column=2, sticky="w", **pad)

        tk.Label(grid, text="Voz:", bg=_BG_CARD, fg=_FG_TEXT).grid(row=4, column=0, sticky="w", **pad)
        self._combo_voice = ttk.Combobox(grid, textvariable=self._var_tts_voice, width=28)
        self._combo_voice.grid(row=4, column=1, columnspan=2, sticky="w", **pad)
        self._sync_voice_presets(keep_current=True)

        tk.Label(grid, text="Velocidad de habla (%):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=5, column=0, sticky="w", **pad
        )
        ttk.Scale(
            grid,
            from_=-20,
            to=40,
            variable=self._var_tts_rate,
            orient="horizontal",
            length=220,
            command=lambda v: self._var_tts_rate.set(int(round(float(v)))),
        ).grid(row=5, column=1, sticky="w", **pad)
        tk.Label(grid, textvariable=self._var_tts_rate, bg=_BG_CARD, fg=_ACCENT).grid(
            row=5, column=2, sticky="w", **pad
        )

        tk.Label(grid, text="Volumen:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=6, column=0, sticky="w", **pad
        )
        ttk.Scale(
            grid, from_=0.1, to=1.0, variable=self._var_tts_volume, orient="horizontal", length=220
        ).grid(row=6, column=1, sticky="w", **pad)

        ttk.Button(
            grid,
            text="🔊  Probar Voz Ahora",
            command=self._test_voice_preview,
        ).grid(row=7, column=1, sticky="w", pady=(14, 4), padx=8)

    # ------------------------------------------------------------------ Tab 4: Overlay Style
    def _build_overlay_tab(self) -> None:
        grid = tk.Frame(self._tab_overlay, bg=_BG_CARD)
        grid.pack(fill="x", anchor="n")
        pad: dict[str, Any] = {"padx": 8, "pady": 7}

        ttk.Checkbutton(
            grid,
            text="Mostrar overlay transparente click-through encima del juego",
            variable=self._var_enable_overlay,
        ).grid(row=0, column=0, columnspan=3, sticky="w", **pad)

        tk.Label(grid, text="Tema visual:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=1, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            grid,
            textvariable=self._var_overlay_theme,
            values=[str(t) for t in _THEMES],
            state="readonly",
            width=20,
        ).grid(row=1, column=1, sticky="w", **pad)

        tk.Label(grid, text="Posición en pantalla:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=2, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            grid,
            textvariable=self._var_overlay_pos,
            values=[str(p) for p in _POSITIONS],
            state="readonly",
            width=20,
        ).grid(row=2, column=1, sticky="w", **pad)

        tk.Label(grid, text="Transparencia (Opacidad):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=3, column=0, sticky="w", **pad
        )
        self._lbl_opacity = tk.Label(
            grid,
            text=f"{int(self._var_overlay_opacity.get() * 100)}%",
            bg=_BG_CARD,
            fg=_ACCENT,
        )
        ttk.Scale(
            grid,
            from_=0.25,
            to=1.0,
            variable=self._var_overlay_opacity,
            orient="horizontal",
            length=220,
            command=lambda _: self._lbl_opacity.configure(
                text=f"{int(self._var_overlay_opacity.get() * 100)}%"
            ),
        ).grid(row=3, column=1, sticky="w", **pad)
        self._lbl_opacity.grid(row=3, column=2, sticky="w", **pad)

        tk.Label(grid, text="Tipografía y tamaño:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=4, column=0, sticky="w", **pad
        )
        font_row = tk.Frame(grid, bg=_BG_CARD)
        font_row.grid(row=4, column=1, columnspan=2, sticky="w", **pad)
        ttk.Combobox(
            font_row,
            textvariable=self._var_overlay_font,
            values=_FONT_FAMILIES,
            state="readonly",
            width=14,
        ).pack(side="left", padx=(0, 8))
        ttk.Spinbox(font_row, from_=9, to=18, textvariable=self._var_overlay_font_size, width=5).pack(
            side="left"
        )
        tk.Label(font_row, text=" pt", bg=_BG_CARD, fg=_FG_MUTED).pack(side="left")

        tk.Label(grid, text="Ancho de tarjeta (px):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=5, column=0, sticky="w", **pad
        )
        ttk.Spinbox(
            grid, from_=260, to=700, increment=20, textvariable=self._var_overlay_width, width=8
        ).grid(row=5, column=1, sticky="w", **pad)

        tk.Label(grid, text="Duración visible (segundos):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=6, column=0, sticky="w", **pad
        )
        ttk.Spinbox(
            grid, from_=2.0, to=15.0, increment=0.5, textvariable=self._var_overlay_duration, width=8
        ).grid(row=6, column=1, sticky="w", **pad)

        ttk.Button(
            grid,
            text="👁  Vista Previa en Pantalla (Click-Through Real)",
            command=self._show_overlay_preview,
        ).grid(row=7, column=0, columnspan=2, sticky="w", pady=(14, 4), padx=8)

    # ------------------------------------------------------------------ Tab 5: Performance
    def _build_perf_tab(self) -> None:
        grid = tk.Frame(self._tab_perf, bg=_BG_CARD)
        grid.pack(fill="x", anchor="n")
        pad: dict[str, Any] = {"padx": 8, "pady": 7}

        tk.Label(grid, text="Modo ahorro de tokens:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=0, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            grid,
            textvariable=self._var_token_mode,
            values=["eco", "balanced", "high"],
            state="readonly",
            width=14,
        ).grid(row=0, column=1, sticky="w", **pad)
        tk.Label(
            grid,
            text="eco = 540p (~45% menos tokens) | balanced = 720p | high = máximo detalle",
            bg=_BG_CARD,
            fg=_ACCENT,
        ).grid(row=0, column=2, sticky="w", **pad)

        tk.Label(grid, text="Formato de imagen en RAM:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=1, column=0, sticky="w", **pad
        )
        ttk.Combobox(
            grid,
            textvariable=self._var_image_format,
            values=["jpeg", "webp"],
            state="readonly",
            width=14,
        ).grid(row=1, column=1, sticky="w", **pad)
        tk.Label(
            grid,
            text="jpeg = mínimo CPU (~2ms) | webp = mínimo ancho de banda",
            bg=_BG_CARD,
            fg=_FG_MUTED,
        ).grid(row=1, column=2, sticky="w", **pad)

        tk.Label(grid, text="Calidad compresión (40–90):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=2, column=0, sticky="w", **pad
        )
        ttk.Spinbox(
            grid, from_=40, to=90, increment=5, textvariable=self._var_image_quality, width=8
        ).grid(row=2, column=1, sticky="w", **pad)

        tk.Label(grid, text="Monitor a capturar (1=principal):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=3, column=0, sticky="w", **pad
        )
        ttk.Spinbox(grid, from_=0, to=4, textvariable=self._var_monitor, width=8).grid(
            row=3, column=1, sticky="w", **pad
        )

        tk.Label(grid, text="Intervalo mínimo entre consultas (s):", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=4, column=0, sticky="w", **pad
        )
        ttk.Spinbox(
            grid, from_=1.0, to=20.0, increment=0.5, textvariable=self._var_min_interval, width=8
        ).grid(row=4, column=1, sticky="w", **pad)

        ttk.Checkbutton(
            grid,
            text="Prioridad Windows BELOW_NORMAL (protege 1% low FPS del juego)",
            variable=self._var_low_priority,
        ).grid(row=5, column=0, columnspan=3, sticky="w", **pad)

        ttk.Checkbutton(
            grid,
            text="Habilitar hotkeys globales nativos (Win32 RegisterHotKey · 100% compatible Anti-Cheat)",
            variable=self._var_hotkeys,
        ).grid(row=6, column=0, columnspan=3, sticky="w", **pad)

        tk.Label(grid, text="Atajo Pausar / Reanudar Coach:", bg=_BG_CARD, fg=_FG_TEXT).grid(
            row=7, column=0, sticky="w", **pad
        )
        hk_pause_row = tk.Frame(grid, bg=_BG_CARD)
        hk_pause_row.grid(row=7, column=1, columnspan=2, sticky="w", **pad)
        combo_pause = ttk.Combobox(
            hk_pause_row,
            textvariable=self._var_hk_pause,
            values=SUPPORTED_HOTKEY_PRESETS,
            width=16,
        )
        combo_pause.pack(side="left", padx=(0, 8))
        combo_pause.bind("<<ComboboxSelected>>", lambda _e: self._refresh_hotkey_hint_label())
        ttk.Button(
            hk_pause_row,
            text="🎹 Capturar Tecla",
            command=lambda: self._capture_hotkey_dialog(
                self._var_hk_pause, "Pausar / Reanudar Coach"
            ),
        ).pack(side="left")

        tk.Label(
            grid,
            text="Atajo Pedir Consejo Ahora (saltea cooldown):",
            bg=_BG_CARD,
            fg=_FG_TEXT,
        ).grid(row=8, column=0, sticky="w", **pad)
        hk_analyze_row = tk.Frame(grid, bg=_BG_CARD)
        hk_analyze_row.grid(row=8, column=1, columnspan=2, sticky="w", **pad)
        combo_analyze = ttk.Combobox(
            hk_analyze_row,
            textvariable=self._var_hk_analyze,
            values=SUPPORTED_HOTKEY_PRESETS,
            width=16,
        )
        combo_analyze.pack(side="left", padx=(0, 8))
        combo_analyze.bind("<<ComboboxSelected>>", lambda _e: self._refresh_hotkey_hint_label())
        ttk.Button(
            hk_analyze_row,
            text="🎹 Capturar Tecla",
            command=lambda: self._capture_hotkey_dialog(
                self._var_hk_analyze, "Pedir Consejo Ahora"
            ),
        ).pack(side="left")

        ttk.Checkbutton(
            grid,
            text="Guardar archivo de auditoría rotativo en logs/copilot.log",
            variable=self._var_file_log,
        ).grid(row=9, column=0, columnspan=3, sticky="w", **pad)

    # ------------------------------------------------------------------ Tab 4: Analytics & AI Usage
    def _build_analytics_tab(self) -> None:
        # 1. Top Toolbar: Game Filter + Demo Simulation + Refresh + Reset
        top_bar = tk.Frame(self._tab_analytics, bg=_BG_CARD)
        top_bar.pack(fill="x", pady=(0, 8))

        tk.Label(
            top_bar,
            text="Filtrar por juego:",
            bg=_BG_CARD,
            fg=_FG_TEXT,
            font=("Segoe UI", 9, "bold"),
        ).pack(side="left", padx=(0, 6))

        filter_values = ["all", *[p.manifest.game_id for p in self._profiles]]
        self._combo_stats_game = ttk.Combobox(
            top_bar,
            textvariable=self._var_stats_game,
            values=filter_values,
            state="readonly",
            width=20,
        )
        self._combo_stats_game.pack(side="left", padx=(0, 8))
        self._combo_stats_game.bind(
            "<<ComboboxSelected>>",
            lambda _e: self._refresh_analytics_dashboard(),
        )

        ttk.Button(
            top_bar,
            text="🔄 Actualizar",
            command=self._refresh_analytics_dashboard,
        ).pack(side="left", padx=(0, 6))

        ttk.Button(
            top_bar,
            text="🎮 Simular Partida Demo",
            command=self._simulate_demo_match,
        ).pack(side="left", padx=(0, 6))

        ttk.Button(
            top_bar,
            text="🗑 Reiniciar Estadísticas",
            command=self._reset_analytics_telemetry,
        ).pack(side="right")

        # 2. 4 KPI Summary Cards (Matches Today, Total Since Install, Total Tokens, AI Cost/Saved)
        kpi_row = tk.Frame(self._tab_analytics, bg=_BG_CARD)
        kpi_row.pack(fill="x", pady=(0, 8))
        for col in range(4):
            kpi_row.columnconfigure(col, weight=1)

        def _make_kpi_card(
            col_idx: int,
            heading: str,
            accent_color: str,
        ) -> tuple[tk.Label, tk.Label]:
            card = tk.Frame(
                kpi_row,
                bg=_BG_ROOT,
                padx=10,
                pady=7,
                highlightthickness=1,
                highlightbackground=_BORDER,
            )
            card.grid(row=0, column=col_idx, sticky="nsew", padx=4)
            tk.Label(
                card,
                text=heading,
                bg=_BG_ROOT,
                fg=_FG_MUTED,
                font=("Segoe UI", 8, "bold"),
            ).pack(anchor="w")
            val_lbl = tk.Label(
                card,
                text="0",
                bg=_BG_ROOT,
                fg=accent_color,
                font=("Segoe UI", 13, "bold"),
            )
            val_lbl.pack(anchor="w", pady=(2, 0))
            sub_lbl = tk.Label(
                card,
                text="",
                bg=_BG_ROOT,
                fg=_FG_MUTED,
                font=("Segoe UI", 7),
            )
            sub_lbl.pack(anchor="w")
            return val_lbl, sub_lbl

        self._kpi_today_val, self._kpi_today_sub = _make_kpi_card(
            0, "PARTIDAS JUGADAS HOY", _ACCENT
        )
        self._kpi_total_val, self._kpi_total_sub = _make_kpi_card(
            1, "TOTAL DESDE INSTALACIÓN", _SUCCESS
        )
        self._kpi_tokens_val, self._kpi_tokens_sub = _make_kpi_card(
            2, "USO TOTAL DE TOKENS IA", _FG_TEXT
        )
        self._kpi_cost_val, self._kpi_cost_sub = _make_kpi_card(
            3, "GASTO REAL vs AHORRADO", _SUCCESS
        )

        # 3. Circular Gauges Row (0-100% Performance + 0-100 Playstyle Circles)
        gauges_wrapper = tk.Frame(self._tab_analytics, bg=_BG_CARD)
        gauges_wrapper.pack(fill="x", pady=(0, 6))

        perf_box = tk.LabelFrame(
            gauges_wrapper,
            text=" Rendimiento en Partidas (0 a 100%) ",
            bg=_BG_CARD,
            fg=_ACCENT,
            font=("Segoe UI", 9, "bold"),
            padx=8,
            pady=4,
        )
        perf_box.pack(side="left", fill="y", padx=(0, 8))

        self._gauge_last_match = CircularGauge(
            perf_box,
            title="Última Partida",
            subtitle="Sin datos",
            diameter=84,
            ring_width=8,
            is_percent=True,
        )
        self._gauge_last_match.pack(side="left", padx=6)

        self._gauge_avg_matches = CircularGauge(
            perf_box,
            title="Últimas 10 Partidas",
            subtitle="Promedio",
            diameter=84,
            ring_width=8,
            is_percent=True,
        )
        self._gauge_avg_matches.pack(side="left", padx=6)

        style_box = tk.LabelFrame(
            gauges_wrapper,
            text=" Tu Estilo de Juego (Gráficos Circulares 0 a 100) ",
            bg=_BG_CARD,
            fg=_ACCENT,
            font=("Segoe UI", 9, "bold"),
            padx=8,
            pady=4,
        )
        style_box.pack(side="left", fill="both", expand=True)

        circles_row = tk.Frame(style_box, bg=_BG_CARD)
        circles_row.pack(fill="x")

        self._gauge_aggression = CircularGauge(
            circles_row,
            title="Agresividad",
            subtitle="Peeks / Ataque",
            diameter=72,
            ring_width=7,
            is_percent=False,
            fixed_color="#F97316",
        )
        self._gauge_aggression.pack(side="left", expand=True)

        self._gauge_patience = CircularGauge(
            circles_row,
            title="Paciencia",
            subtitle="Disciplina / Cover",
            diameter=72,
            ring_width=7,
            is_percent=False,
        )
        self._gauge_patience.pack(side="left", expand=True)

        self._gauge_tactical = CircularGauge(
            circles_row,
            title="Visión Táctica",
            subtitle="Radar / Rotación",
            diameter=72,
            ring_width=7,
            is_percent=False,
        )
        self._gauge_tactical.pack(side="left", expand=True)

        self._gauge_economy = CircularGauge(
            circles_row,
            title="Economía / Macro",
            subtitle="Recursos / Boost",
            diameter=72,
            ring_width=7,
            is_percent=False,
        )
        self._gauge_economy.pack(side="left", expand=True)

        self._gauge_consistency = CircularGauge(
            circles_row,
            title="Supervivencia",
            subtitle="Temple / Consistencia",
            diameter=72,
            ring_width=7,
            is_percent=False,
        )
        self._gauge_consistency.pack(side="left", expand=True)

        self._lbl_archetype = tk.Label(
            self._tab_analytics,
            text="Diagnóstico de Estilo: —",
            bg=_BG_ROOT,
            fg=_ACCENT,
            font=("Segoe UI", 8, "bold"),
            padx=10,
            pady=5,
            anchor="w",
        )
        self._lbl_archetype.pack(fill="x", pady=(0, 6))

        # 4. AI Model Usage & Cost Breakdown Table
        tk.Label(
            self._tab_analytics,
            text="Desglose de Uso de IA: Cuánto Gastó Cada Modelo y Cuenta (Tokens, Caché, Latencia y Costo):",
            bg=_BG_CARD,
            fg=_FG_TEXT,
            font=("Segoe UI", 9, "bold"),
        ).pack(anchor="w", pady=(0, 4))

        cols = (
            "account",
            "model",
            "requests",
            "advice",
            "in_tokens",
            "out_tokens",
            "total_tokens",
            "latency",
            "cost",
            "saved",
        )
        self._tree_usage = ttk.Treeview(
            self._tab_analytics,
            columns=cols,
            show="headings",
            height=6,
            selectmode="browse",
        )
        headings = {
            "account": ("Cuenta / Proveedor", 170),
            "model": ("Modelo IA", 135),
            "requests": ("Consultas (429)", 95),
            "advice": ("Consejos", 65),
            "in_tokens": ("Tokens In (Caché)", 120),
            "out_tokens": ("Tokens Out", 75),
            "total_tokens": ("Total Tokens", 85),
            "latency": ("Latencia", 68),
            "cost": ("Gasto Real", 82),
            "saved": ("Ahorrado", 82),
        }
        for col_id, (title_txt, width_px) in headings.items():
            self._tree_usage.heading(col_id, text=title_txt)
            self._tree_usage.column(col_id, width=width_px, anchor="w")
        self._tree_usage.pack(fill="both", expand=True)

    # ------------------------------------------------------------------ Actions
    def _refresh_analytics_dashboard(self) -> None:
        self._telemetry.reload()
        summary = self._telemetry.get_dashboard_summary(
            game_filter=self._var_stats_game.get() or "all"
        )

        # Update KPI cards
        self._kpi_today_val.configure(text=f"{summary['matches_today']} partidas")
        self._kpi_today_sub.configure(text="Jugadas en el día de hoy")

        self._kpi_total_val.configure(text=f"{summary['total_matches_all_time']} partidas")
        install_date = str(summary["installed_at"])[:10]
        self._kpi_total_sub.configure(
            text=f"Desde {install_date} · {summary['total_coaching_minutes']} min coucheados"
        )

        self._kpi_tokens_val.configure(text=f"{summary['total_tokens']:,} tokens")
        self._kpi_tokens_sub.configure(
            text=(
                f"Caché: {summary['total_cached_tokens']:,} · "
                f"{summary['total_frames_filtered_saved']:,} frames filtrados (pHash)"
            )
        )

        cost_usd = summary["total_actual_cost_usd"]
        saved_usd = summary["total_saved_cost_usd"]
        self._kpi_cost_val.configure(
            text=f"${cost_usd:.4f} USD" if cost_usd > 0 else "$0.0000 (Gratis)"
        )
        self._kpi_cost_sub.configure(text=f"Ahorrado por Free/Caché/pHash: ${saved_usd:.4f} USD")

        # Update Circular Gauges (0-100% and 0-100)
        self._gauge_last_match.set_value(
            summary["last_match_performance"],
            subtitle=str(summary["last_match_label"]),
        )
        self._gauge_avg_matches.set_value(
            summary["recent_avg_performance"],
            subtitle="Promedio reciente",
        )

        style: PlaystyleMetrics = summary["playstyle"]
        self._gauge_aggression.set_value(style.aggression)
        self._gauge_patience.set_value(style.patience)
        self._gauge_tactical.set_value(style.tactical_awareness)
        self._gauge_economy.set_value(style.resource_efficiency)
        self._gauge_consistency.set_value(style.consistency)

        self._lbl_archetype.configure(
            text=f"Diagnóstico de tu Estilo de Juego: {summary['archetype']}"
        )

        # Update AI Model Usage Treeview
        for row_id in self._tree_usage.get_children():
            self._tree_usage.delete(row_id)

        for m in summary["models"]:
            req_str = (
                f"{m.requests_ok} ({m.requests_rate_limited} rot)"
                if m.requests_rate_limited > 0
                else f"{m.requests_ok}"
            )
            in_str = (
                f"{m.input_tokens:,} ({m.cached_tokens:,})"
                if m.cached_tokens > 0
                else f"{m.input_tokens:,}"
            )
            cost_str = "$0.0000 (Free)" if m.is_free_tier else f"${m.actual_cost_usd:.4f}"
            saved_str = f"${m.saved_cost_usd:.4f}"
            self._tree_usage.insert(
                "",
                "end",
                values=(
                    f"{m.account_label} [{m.provider}]",
                    m.model,
                    req_str,
                    m.interventions_count,
                    in_str,
                    f"{m.output_tokens:,}",
                    f"{m.total_tokens:,}",
                    f"{m.avg_latency_ms:.0f} ms",
                    cost_str,
                    saved_str,
                ),
            )

    def _simulate_demo_match(self) -> None:
        """Simulate a realistic match entry so the user can test all 7 circular gauges immediately."""
        presets = [
            (
                "rob_run",
                "ROB RUN",
                88,
                PlaystyleMetrics(
                    aggression=74,
                    patience=81,
                    tactical_awareness=85,
                    resource_efficiency=91,
                    consistency=84,
                ),
                "gemini",
                "gemini-2.5-flash",
                "Google Gemini (AI Studio)",
            ),
            (
                "aoe2_de",
                "Age of Empires II: DE",
                82,
                PlaystyleMetrics(
                    aggression=58,
                    patience=84,
                    tactical_awareness=79,
                    resource_efficiency=89,
                    consistency=86,
                ),
                "groq",
                "meta-llama/llama-4-scout-17b-16e-instruct",
                "Groq Cloud (Gratis)",
            ),
            (
                "rocket_league",
                "Rocket League",
                79,
                PlaystyleMetrics(
                    aggression=82,
                    patience=68,
                    tactical_awareness=76,
                    resource_efficiency=74,
                    consistency=78,
                ),
                "openrouter",
                "google/gemma-3-27b-it:free",
                "OpenRouter (:free)",
            ),
            (
                "example_cs2",
                "Counter-Strike 2",
                91,
                PlaystyleMetrics(
                    aggression=67,
                    patience=88,
                    tactical_awareness=90,
                    resource_efficiency=86,
                    consistency=92,
                ),
                "gemini",
                "gemini-2.5-flash",
                "Google Gemini (AI Studio)",
            ),
        ]
        selected_filter = self._var_stats_game.get()
        matching = [p for p in presets if p[0] == selected_filter]
        chosen = matching[0] if matching else presets[self._sim_counter % len(presets)]
        self._sim_counter += 1

        game_id, game_name, perf_score, style, prov, mod, acc_lbl = chosen
        self._telemetry.record_simulated_match(
            game_id=game_id,
            game_name=game_name,
            performance_score=perf_score,
            playstyle=style,
            provider=prov,
            model=mod,
            account_label=acc_lbl,
        )
        self._refresh_analytics_dashboard()
        self._var_status.set(
            f"✓ Partida demo de '{game_name}' registrada ({perf_score}% rendimiento)."
        )

    def _reset_analytics_telemetry(self) -> None:
        if not messagebox.askyesno(
            "Reiniciar Estadísticas",
            "¿Querés volver a cero el historial de partidas, estilo de juego y contadores de tokens?",
            parent=self._root,
        ):
            return
        self._telemetry.reset_all()
        self._refresh_analytics_dashboard()
        self._var_status.set("✓ Estadísticas y contadores de uso de IA reiniciados.")
    def _refresh_hotkey_hint_label(self) -> None:
        hk_p = self._var_hk_pause.get().strip().upper() or "F9"
        hk_a = self._var_hk_analyze.get().strip().upper() or "F10"
        self._lbl_hotkeys_hint.configure(
            text=(
                f"Atajos en partida: {hk_p} = Pausar/Reanudar | "
                f"{hk_a} = Pedir consejo ahora (saltea cooldown)"
            )
        )

    def _capture_hotkey_dialog(self, target_var: tk.StringVar, action_title: str) -> None:
        dlg = tk.Toplevel(self._root)
        dlg.title(f"Capturar Atajo — {action_title}")
        dlg.configure(bg=_BG_ROOT)
        dlg.resizable(False, False)
        dlg.transient(self._root)
        dlg.grab_set()

        frame = tk.Frame(dlg, bg=_BG_CARD, padx=22, pady=18)
        frame.pack(fill="both", expand=True, padx=10, pady=10)
        tk.Label(
            frame,
            text=f"Asignar tecla para: {action_title}",
            bg=_BG_CARD,
            fg=_ACCENT,
            font=("Segoe UI", 10, "bold"),
        ).pack(pady=(0, 8))
        lbl_info = tk.Label(
            frame,
            text=(
                "Presioná cualquier tecla o combinación\n"
                "(ej: F8, Alt+F10, Ctrl+Shift+A, NUM5, INSERT)\n\n"
                "Presioná ESC para cancelar."
            ),
            bg=_BG_CARD,
            fg=_FG_TEXT,
            justify="center",
        )
        lbl_info.pack()

        ignored_modifiers = {
            "CONTROL_L",
            "CONTROL_R",
            "SHIFT_L",
            "SHIFT_R",
            "ALT_L",
            "ALT_R",
            "WIN_L",
            "WIN_R",
            "META_L",
            "META_R",
            "SUPER_L",
            "SUPER_R",
        }
        keysym_aliases = {
            "KP_0": "NUM0",
            "KP_1": "NUM1",
            "KP_2": "NUM2",
            "KP_3": "NUM3",
            "KP_4": "NUM4",
            "KP_5": "NUM5",
            "KP_6": "NUM6",
            "KP_7": "NUM7",
            "KP_8": "NUM8",
            "KP_9": "NUM9",
            "KP_ADD": "ADD",
            "PLUS": "ADD",
            "KP_SUBTRACT": "SUBTRACT",
            "MINUS": "SUBTRACT",
            "KP_MULTIPLY": "MULTIPLY",
            "ASTERISK": "MULTIPLY",
            "KP_DIVIDE": "DIVIDE",
            "SLASH": "DIVIDE",
            "PRIOR": "PAGEUP",
            "NEXT": "PAGEDOWN",
        }

        def _on_keypress(event: tk.Event[Any]) -> None:
            sym = str(event.keysym).upper()
            if sym == "ESCAPE":
                dlg.destroy()
                return
            if sym in ignored_modifiers:
                return

            key_token = keysym_aliases.get(sym, sym)
            state = int(getattr(event, "state", 0))
            mods: list[str] = []
            if state & 0x0004:
                mods.append("Ctrl")
            if (state & 0x20000) or (state & 0x0008):
                mods.append("Alt")
            if state & 0x0001:
                mods.append("Shift")

            candidate = "+".join([*mods, key_token])
            try:
                _, _, canonical = parse_hotkey_string(candidate)
            except ValueError:
                lbl_info.configure(
                    text=f"Tecla '{candidate}' no soportada.\nProbá F1-F12, A-Z, 0-9, NUM0-9 o modificadores.",
                    fg=_DANGER,
                )
                return

            target_var.set(canonical)
            self._refresh_hotkey_hint_label()
            self._var_status.set(
                f"✓ Atajo '{canonical}' asignado para {action_title} (recordá Guardar)."
            )
            dlg.destroy()

        dlg.bind("<KeyPress>", _on_keypress)
        dlg.wait_visibility()
        dlg.focus_force()

    def _sync_voice_presets(self, keep_current: bool = False) -> None:
        lang = self._var_voice_lang.get()
        voices = _VOICE_PRESETS.get(lang, _VOICE_PRESETS["es"])
        self._combo_voice.configure(values=voices)
        if not keep_current or not self._var_tts_voice.get().strip():
            self._var_tts_voice.set(voices[0])

    def _create_new_game_profile(self) -> None:
        dlg = NewProfileDialog(self._root, self._profile_loader)
        self._root.wait_window(dlg)
        if dlg.created_game_id:
            self._profiles = self._profile_loader.list_profiles()
            profile_ids = [p.manifest.game_id for p in self._profiles]
            self._combo_profile.configure(values=profile_ids)
            self._combo_rules_game.configure(values=profile_ids)
            self._combo_stats_game.configure(values=["all", *profile_ids])
            self._var_profile.set(dlg.created_game_id)
            self._var_rules_game.set(dlg.created_game_id)
            self._load_selected_game_rules()
            self._var_status.set(
                f"✓ Nuevo perfil '{dlg.created_game_id}' creado en profiles/{dlg.created_game_id}/"
            )

    def _load_selected_game_rules(self) -> None:
        game_id = self._var_rules_game.get()
        profile = self._profile_loader.get_by_id(game_id)
        self._txt_custom_rules.delete("1.0", "end")
        if profile is not None and profile.custom_rules:
            self._txt_custom_rules.insert("1.0", profile.custom_rules)
        self._update_rules_impact_meter()

    def _update_rules_impact_meter(self) -> None:
        text = self._txt_custom_rules.get("1.0", "end-1c").strip()
        chars = len(text)
        extra_tokens = chars // 4
        game_id = self._var_rules_game.get()
        profile = self._profile_loader.get_by_id(game_id)
        base_tokens = (
            (len(profile.system_prompt) + len(profile.base_knowledge_base or profile.knowledge_base)) // 4
            if profile
            else 0
        )

        if chars == 0:
            level = "NULO — Solo reglas oficiales del juego"
            color = _SUCCESS
        elif extra_tokens <= 200:
            level = "BAJO (Óptimo — bajo costo de tokens y alta precisión)"
            color = _SUCCESS
        elif extra_tokens <= 500:
            level = "MODERADO (Más consumo en proveedores sin caché)"
            color = "#F59E0B"
        else:
            level = "ALTO (¡Cuidado! Gasta más tokens y puede restar foco táctico)"
            color = _DANGER

        self._lbl_rules_impact.configure(
            text=(
                f"Impacto [{game_id}]: {level} | {chars}/{MAX_CUSTOM_RULES_CHARS} chars "
                f"(+{extra_tokens} tokens extra · total perfil ~{base_tokens + extra_tokens} tokens)"
            ),
            fg=color,
        )

    def _insert_game_rule_example(self) -> None:
        game_id = self._var_rules_game.get()
        examples: dict[str, str] = {
            "aoe2_de": (
                "- Juego principalmente con Francos: priorizar Jinetes (Knights) y Castillos baratos.\n"
                "- Recordarme investigar Carretilla (Wheelbarrow) al llegar a ~35 aldeanos.\n"
                "- En mapas cerrados (Arena), priorizar Fast Castle y reliquias con Monjes."
            ),
            "rocket_league": (
                "- Juego 2v2: priorizar cubrir siempre como segundo hombre cuando mi compañero va a la pared.\n"
                "-En Free Play estoy practicando Fast Aerials y aterrizajes con Powerslide.\n"
                "- Recordarme no gastar todo el boost al volver si ya estoy en velocidad supersónica."
            ),
            "rob_run": (
                "- Priorizar golpes sigilosos en joyerías y cajeros antes de intentar el banco central.\n"
                "- Si llevo más de $15,000 o planos raros en la mochila, avisarme extraer de inmediato.\n"
                "- Si juego con loadout caro y me rodean 2+ policías con poco botín, recordarme usar Surrender."
            ),
            "example_cs2": (
                "- En lado CT juego ancla de B: priorizar guardar Molotov e Incendiaria para frenar rush.\n"
                "- Con AWP recordarme comprar siempre pistola de respaldo o mantener distancia larga.\n"
                "- En eco grupal no dejarme bajar de $2,000 para tener full buy en la siguiente."
            ),
            "example_valorant": (
                "- Juego controlador (Omen/Clove): recordarme no pushear primero sin gastar mis humos.\n"
                "- Priorizar Vandal y recordarme tirar ráfagas de 2-3 balas a larga distancia.\n"
                "- Si tengo Ultimate lista al inicio de ronda, recordarme buscar orbe o jugada con ult."
            ),
        }
        sample = examples.get(
            game_id,
            "- Priorizar supervivencia y avisarme cuando mis recursos o vida bajen del 30%.\n"
            "- Recordarme usar mis habilidades/utilidad antes de iniciar un duelo difícil.",
        )
        current = self._txt_custom_rules.get("1.0", "end-1c").strip()
        new_text = f"{current}\n{sample}".strip() if current else sample
        self._txt_custom_rules.delete("1.0", "end")
        self._txt_custom_rules.insert("1.0", new_text)
        self._update_rules_impact_meter()

    def _view_base_knowledge(self) -> None:
        game_id = self._var_rules_game.get()
        profile = self._profile_loader.get_by_id(game_id)
        if profile is None:
            return
        viewer = tk.Toplevel(self._root)
        viewer.title(f"Manual Base — {profile.manifest.display_name} ({game_id})")
        viewer.geometry("720x500")
        viewer.configure(bg=_BG_ROOT)
        viewer.transient(self._root)

        txt = tk.Text(
            viewer,
            bg=_BG_ROOT,
            fg="#CBD5E1",
            font=("Consolas", 9),
            padx=12,
            pady=10,
            wrap="word",
        )
        txt.pack(fill="both", expand=True, padx=10, pady=10)
        txt.insert("1.0", profile.base_knowledge_base or profile.knowledge_base)
        txt.configure(state="disabled")

    def _clear_selected_game_rules(self) -> None:
        self._txt_custom_rules.delete("1.0", "end")
        self._save_selected_game_rules()

    def _save_selected_game_rules(self) -> None:
        game_id = self._var_rules_game.get()
        raw_text = self._txt_custom_rules.get("1.0", "end-1c").strip()
        extra_tokens = len(raw_text) // 4

        if extra_tokens > 500:
            proceed = messagebox.askyesno(
                "Advertencia de Impacto Alto en Tokens",
                f"Tus reglas para '{game_id}' agregan ~{extra_tokens} tokens extra.\n\n"
                "Esto puede aumentar el consumo de cuota (especialmente en proveedores sin caché) "
                "y hacer que el coach pierda precisión.\n\n"
                "¿Querés guardarlas de todos modos?",
                parent=self._root,
            )
            if not proceed:
                return

        try:
            updated = self._profile_loader.save_custom_rules(game_id, raw_text)
            self._profiles = self._profile_loader.list_profiles()
            self._update_rules_impact_meter()
            self._var_status.set(
                f"✓ Reglas de '{updated.manifest.display_name}' guardadas "
                f"(+{updated.custom_rules_estimated_tokens} tokens extra)."
            )
        except Exception as exc:
            messagebox.showerror("Error guardando reglas", str(exc), parent=self._root)

    @staticmethod
    def _safe_int(var: tk.IntVar, default: int, low: int, high: int) -> int:
        try:
            val = int(var.get())
        except (tk.TclError, ValueError):
            return default
        return max(low, min(high, val))

    @staticmethod
    def _safe_float(var: tk.DoubleVar, default: float, low: float, high: float) -> float:
        try:
            val = float(var.get())
        except (tk.TclError, ValueError):
            return default
        return max(low, min(high, val))

    def _build_settings_from_ui(self) -> AppSettings:
        rate_val = self._safe_int(self._var_tts_rate, 10, -20, 40)
        rate_str = f"+{rate_val}%" if rate_val >= 0 else f"{rate_val}%"
        _, _, canonical_pause = parse_hotkey_string(self._var_hk_pause.get().strip() or "F9")
        _, _, canonical_analyze = parse_hotkey_string(self._var_hk_analyze.get().strip() or "F10")
        if canonical_pause == canonical_analyze:
            raise ValueError(
                f"El atajo de Pausar ({canonical_pause}) y el de Pedir Consejo ({canonical_analyze}) "
                "no pueden ser la misma tecla."
            )
        self._var_hk_pause.set(canonical_pause)
        self._var_hk_analyze.set(canonical_analyze)
        self._refresh_hotkey_hint_label()

        return self._settings.model_copy(
            update={
                "text_language": self._var_text_lang.get(),
                "voice_language": self._var_voice_lang.get(),
                "coach_mode": self._var_coach_mode.get(),
                "enable_voice": bool(self._var_enable_voice.get()),
                "enable_overlay": bool(self._var_enable_overlay.get()),
                "enable_hotkeys": bool(self._var_hotkeys.get()),
                "hotkey_toggle_pause": canonical_pause,
                "hotkey_analyze_now": canonical_analyze,
                "tts_engine": self._var_tts_engine.get(),
                "tts_voice": self._var_tts_voice.get().strip() or "es-AR-TomasNeural",
                "tts_rate": rate_str,
                "tts_volume": round(self._safe_float(self._var_tts_volume, 0.9, 0.0, 1.0), 2),
                "overlay_theme": self._var_overlay_theme.get(),
                "overlay_position": self._var_overlay_pos.get(),
                "overlay_opacity": round(
                    self._safe_float(self._var_overlay_opacity, 0.9, 0.25, 1.0), 2
                ),
                "overlay_width": self._safe_int(self._var_overlay_width, 440, 260, 700),
                "overlay_duration_seconds": round(
                    self._safe_float(self._var_overlay_duration, 6.0, 1.0, 30.0), 1
                ),
                "overlay_font_family": self._var_overlay_font.get(),
                "overlay_font_size": self._safe_int(self._var_overlay_font_size, 12, 9, 20),
                "token_efficiency_mode": self._var_token_mode.get(),
                "image_format": self._var_image_format.get(),
                "image_compression_quality": self._safe_int(self._var_image_quality, 78, 30, 95),
                "capture_monitor": self._safe_int(self._var_monitor, 1, 0, 8),
                "min_inference_interval_seconds": round(
                    self._safe_float(self._var_min_interval, 2.5, 0.5, 60.0), 1
                ),
                "low_process_priority": bool(self._var_low_priority.get()),
                "enable_file_logging": bool(self._var_file_log.get()),
            }
        )

    def _save_all(self) -> bool:
        try:
            self._settings = self._build_settings_from_ui()
            save_user_settings(self._settings)
            save_accounts(self._settings.accounts_file, self._accounts)
            self._var_status.set("✓ Configuración y cuentas guardadas localmente (gitignored).")
            return True
        except Exception as exc:
            messagebox.showerror("Error guardando", str(exc), parent=self._root)
            return False

    def _insert_or_update_account(
        self,
        target: ProviderAccount,
        replace_index: int | None = None,
    ) -> None:
        """Insert or update an account while respecting explicit user-chosen priority."""
        if replace_index is not None:
            del self._accounts[replace_index]
        desired = max(1, target.priority)
        for existing in self._accounts:
            if existing.priority >= desired:
                existing.priority += 1
        self._accounts.append(target)
        self._refresh_accounts_tree()
        self._save_all()

    def _refresh_accounts_tree(self) -> None:
        self._accounts.sort(key=lambda a: (a.priority, a.label.lower()))
        for row_id in self._tree.get_children():
            self._tree.delete(row_id)
        for idx, acc in enumerate(self._accounts, start=1):
            acc.priority = idx
            preset = PROVIDER_PRESETS[acc.provider]
            if not acc.enabled:
                status = "Pausada"
            elif acc.is_ready():
                status = "Gratis/Lista" if preset.is_free_tier else "Lista"
            else:
                status = "Falta Key"
            self._tree.insert(
                "",
                "end",
                iid=acc.id,
                values=(idx, acc.label, acc.provider, acc.model, acc.masked_key(), status),
            )

    def _selected_account_index(self) -> int | None:
        sel = self._tree.selection()
        if not sel:
            return None
        target_id = sel[0]
        for idx, acc in enumerate(self._accounts):
            if acc.id == target_id:
                return idx
        return None

    def _add_account(self) -> None:
        dlg = AccountEditorDialog(self._root, default_priority=len(self._accounts) + 1)
        self._root.wait_window(dlg)
        if dlg.result is not None:
            self._insert_or_update_account(dlg.result)

    def _quick_add_preset(self, kind: ProviderKind) -> None:
        preset = PROVIDER_PRESETS[kind]
        if not preset.requires_api_key:
            acc = ProviderAccount(
                label=preset.display_name,
                provider=kind,
                model=preset.default_model,
                base_url=preset.default_base_url,
                priority=len(self._accounts) + 1,
                enabled=True,
            )
            self._insert_or_update_account(acc)
            return
        draft = ProviderAccount(
            label=preset.display_name,
            provider=kind,
            model=preset.default_model,
            base_url=preset.default_base_url,
            priority=len(self._accounts) + 1,
            enabled=True,
        )
        dlg = AccountEditorDialog(self._root, account=draft)
        self._root.wait_window(dlg)
        if dlg.result is not None:
            self._insert_or_update_account(dlg.result)

    def _edit_account(self) -> None:
        idx = self._selected_account_index()
        if idx is None:
            return
        dlg = AccountEditorDialog(self._root, account=self._accounts[idx])
        self._root.wait_window(dlg)
        if dlg.result is not None:
            self._insert_or_update_account(dlg.result, replace_index=idx)

    def _move_account(self, delta: int) -> None:
        idx = self._selected_account_index()
        if idx is None:
            return
        new_idx = idx + delta
        if not (0 <= new_idx < len(self._accounts)):
            return
        self._accounts[idx], self._accounts[new_idx] = (
            self._accounts[new_idx],
            self._accounts[idx],
        )
        for i, acc in enumerate(self._accounts, start=1):
            acc.priority = i
        moved_id = self._accounts[new_idx].id
        self._refresh_accounts_tree()
        self._tree.selection_set(moved_id)
        self._save_all()

    def _toggle_account(self) -> None:
        idx = self._selected_account_index()
        if idx is None:
            return
        self._accounts[idx].enabled = not self._accounts[idx].enabled
        self._refresh_accounts_tree()
        self._save_all()

    def _delete_account(self) -> None:
        idx = self._selected_account_index()
        if idx is None:
            return
        del self._accounts[idx]
        self._refresh_accounts_tree()
        self._save_all()

    def _test_selected_account(self) -> None:
        idx = self._selected_account_index()
        if idx is None:
            messagebox.showinfo(
                "Probar conexión", "Seleccioná una cuenta de la lista.", parent=self._root
            )
            return
        acc = self._accounts[idx]
        # Build settings on main Tkinter thread before spawning worker thread (thread-safe Tcl)
        cfg = self._build_settings_from_ui()
        self._var_status.set(f"Probando conexión con '{acc.label}'...")

        def _worker() -> None:
            from src.ai.providers import create_provider_adapter

            async def _run_test() -> tuple[bool, str]:
                adapter = create_provider_adapter(acc, cfg)
                try:
                    return await adapter.test_connection()
                finally:
                    await adapter.close_session()

            ok, raw_msg = asyncio.run(_run_test())
            safe_msg = redact_secrets_in_text(raw_msg, (acc.api_key,))
            self._root.after(0, lambda: self._on_test_result(acc.label, ok, safe_msg))

        threading.Thread(target=_worker, daemon=True).start()

    def _on_test_result(self, label: str, ok: bool, msg: str) -> None:
        self._var_status.set(f"{'✓' if ok else '✗'} {label}: {msg}")
        if ok:
            messagebox.showinfo("Conexión Exitosa", f"{label}\n\n{msg}", parent=self._root)
        else:
            messagebox.showwarning("Error de Conexión", f"{label}\n\n{msg}", parent=self._root)

    def _test_voice_preview(self) -> None:
        cfg = self._build_settings_from_ui()
        sample = (
            "Watch your flank, radar is open behind you."
            if cfg.voice_language == "en"
            else "Cuidá la espalda, el radar quedó abierto atrás."
        )
        self._var_status.set(f"Reproduciendo prueba de voz ({cfg.tts_engine}: {cfg.tts_voice})...")

        def _worker() -> None:
            from src.output.tts import VoiceService

            async def _play() -> None:
                svc = VoiceService(settings=cfg, enabled=True)
                await svc.start()
                try:
                    svc.enqueue(sample)
                    await svc.wait_until_idle(timeout=12.0)
                finally:
                    await svc.stop()

            asyncio.run(_play())
            self._root.after(0, lambda: self._var_status.set("✓ Prueba de voz completada."))

        threading.Thread(target=_worker, daemon=True).start()

    def _show_overlay_preview(self) -> None:
        """Display a live click-through preview card using the SAME Tk interpreter."""
        cfg = self._build_settings_from_ui()
        if self._preview_win is not None:
            with contextlib.suppress(Exception):
                self._preview_win.destroy()
            self._preview_win = None
        if self._preview_after_id is not None:
            with contextlib.suppress(Exception):
                self._root.after_cancel(self._preview_after_id)
            self._preview_after_id = None

        win = tk.Toplevel(self._root)
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        win.attributes("-alpha", cfg.overlay_opacity)
        win.configure(bg="#010101")
        if sys.platform == "win32":
            win.attributes("-transparentcolor", "#010101")

        card_bg, border_col, txt_prim, txt_muted = OVERLAY_THEME_PALETTES[cfg.overlay_theme]
        sample_text = (
            "Bomb planted — play time, don't dry-peek!"
            if cfg.text_language == "en"
            else "C4 plantado: jugá al tiempo, no peekees"
        )

        card = tk.Frame(win, bg=card_bg, highlightthickness=1, highlightbackground=border_col)
        card.pack(fill="both", expand=True, padx=4, pady=4)
        tk.Frame(card, bg="#F59E0B", width=6).pack(side="left", fill="y")

        body = tk.Frame(card, bg=card_bg, padx=14, pady=10)
        body.pack(side="left", fill="both", expand=True)
        hdr = tk.Frame(body, bg=card_bg)
        hdr.pack(fill="x", anchor="w")
        tk.Label(
            hdr,
            text="TACTIC",
            bg="#F59E0B",
            fg="#0B1120",
            font=(cfg.overlay_font_family, max(8, cfg.overlay_font_size - 4), "bold"),
            padx=6,
            pady=1,
        ).pack(side="left")
        tk.Label(
            hdr,
            text="HUDMIND PREVIEW",
            bg=card_bg,
            fg=txt_muted,
            font=(cfg.overlay_font_family, max(8, cfg.overlay_font_size - 3), "bold"),
            padx=8,
        ).pack(side="left")
        tk.Label(
            body,
            text=sample_text,
            bg=card_bg,
            fg=txt_prim,
            font=(cfg.overlay_font_family, cfg.overlay_font_size, "bold"),
            justify="left",
            anchor="w",
            wraplength=max(200, cfg.overlay_width - 48),
        ).pack(fill="x", pady=(6, 0), anchor="w")

        win.update_idletasks()
        sw, sh = win.winfo_screenwidth(), win.winfo_screenheight()
        req_h = max(72, win.winfo_reqheight())
        w = cfg.overlay_width
        pos = cfg.overlay_position
        if pos == "top-right":
            x, y = sw - w - 24, 28
        elif pos == "top-left":
            x, y = 24, 28
        elif pos == "bottom-right":
            x, y = sw - w - 24, sh - req_h - 64
        elif pos == "bottom-left":
            x, y = 24, sh - req_h - 64
        else:
            x, y = (sw - w) // 2, 28
        win.geometry(f"{w}x{req_h}+{max(0, x)}+{max(0, y)}")

        # Apply click-through on Windows using 64-bit safe ctypes signatures
        if sys.platform == "win32":
            with contextlib.suppress(Exception):
                user32 = _configure_win32_user32()
                if user32 is not None:
                    raw_hwnd = win.winfo_id()
                    parent = user32.GetParent(raw_hwnd)
                    hwnd = parent if parent else raw_hwnd
                    style = user32.GetWindowLongW(hwnd, -20)
                    user32.SetWindowLongW(hwnd, -20, style | 0x00080000 | 0x00000020 | 0x08000000)

        self._preview_win = win
        self._preview_after_id = self._root.after(
            int(cfg.overlay_duration_seconds * 1000),
            self._close_preview,
        )

    def _close_preview(self) -> None:
        self._preview_after_id = None
        if self._preview_win is not None:
            with contextlib.suppress(Exception):
                self._preview_win.destroy()
            self._preview_win = None

    def _start_coach(self, *, detach_and_close: bool) -> None:
        if not self._save_all():
            return

        ready = [a for a in self._accounts if a.is_ready()]
        if not self._var_dry_run.get() and not ready:
            from src.ai.providers import build_auto_free_fallback_accounts

            self._accounts = build_auto_free_fallback_accounts(self._accounts)
            self._refresh_accounts_tree()
            self._save_all()

        cmd = [sys.executable, "-m", "src.main"]
        if self._var_mode.get() == "test":
            cmd.extend(["--test-mode", "--profile", self._var_profile.get()])
        if self._var_coach_mode.get() == "training":
            cmd.append("--training-mode")
        if self._var_dry_run.get():
            cmd.append("--dry-run")

        if detach_and_close:
            self._stop_coach(wait_seconds=2.0)
            creationflags = (
                getattr(subprocess, "CREATE_NEW_CONSOLE", 0) if sys.platform == "win32" else 0
            )
            subprocess.Popen(cmd, creationflags=creationflags)
            self._close_preview()
            self._root.destroy()
            return

        if self._process is not None and self._process.poll() is None:
            return

        self._append_log(f"Iniciando: {' '.join(cmd)}\n")
        env = dict(os.environ)
        env["PYTHONUNBUFFERED"] = "1"
        env["PYTHONIOENCODING"] = "utf-8"
        env["PYTHONUTF8"] = "1"
        creationflags = 0
        if sys.platform == "win32":
            creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(
                subprocess, "CREATE_NEW_PROCESS_GROUP", 0
            )
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            creationflags=creationflags,
        )
        self._process = proc
        self._btn_start.configure(state="disabled")
        self._btn_stop.configure(state="normal")
        self._var_status.set("Coach ACTIVO en segundo plano.")
        threading.Thread(target=self._read_process_output, args=(proc,), daemon=True).start()

    def _read_process_output(self, proc: subprocess.Popen[str]) -> None:
        if proc.stdout is None:
            return
        for line in proc.stdout:
            with contextlib.suppress(queue.Full):
                self._log_queue.put_nowait(line)
        code = proc.wait()
        with contextlib.suppress(queue.Full):
            self._log_queue.put_nowait(f"\n[Proceso finalizado con código {code}]\n")
        self._root.after(0, lambda: self._on_process_stopped(proc))

    def _on_process_stopped(self, proc: subprocess.Popen[str]) -> None:
        if proc is not self._process:
            return
        self._btn_start.configure(state="normal")
        self._btn_stop.configure(state="disabled")
        self._var_status.set("Coach detenido — 0% CPU en reposo.")
        self._refresh_analytics_dashboard()

    def _stop_coach(self, wait_seconds: float = 0.0) -> None:
        proc = self._process
        if proc is None or proc.poll() is not None:
            return
        self._var_status.set("Deteniendo coach...")

        def _graceful_terminate(target: subprocess.Popen[str], timeout_s: float) -> None:
            if sys.platform == "win32" and hasattr(signal, "CTRL_BREAK_EVENT"):
                with contextlib.suppress(Exception):
                    target.send_signal(signal.CTRL_BREAK_EVENT)
            else:
                with contextlib.suppress(Exception):
                    target.terminate()
            try:
                target.wait(timeout=max(0.5, timeout_s))
            except subprocess.TimeoutExpired:
                with contextlib.suppress(Exception):
                    target.terminate()
                with contextlib.suppress(Exception):
                    target.wait(timeout=1.0)

        if wait_seconds > 0:
            _graceful_terminate(proc, wait_seconds)
        else:
            threading.Thread(target=_graceful_terminate, args=(proc, 2.5), daemon=True).start()

    def _append_log(self, text: str) -> None:
        self._txt_logs.configure(state="normal")
        self._txt_logs.insert("end", text)
        self._txt_logs.see("end")
        # Keep log buffer bounded so RAM never grows over long sessions
        line_count = int(self._txt_logs.index("end-1c").split(".")[0])
        if line_count > 400:
            self._txt_logs.delete("1.0", "100.0")
        self._txt_logs.configure(state="disabled")

    def _poll_logs(self) -> None:
        chunks: list[str] = []
        try:
            while len(chunks) < 60:
                chunks.append(self._log_queue.get_nowait())
        except queue.Empty:
            pass
        if chunks:
            self._append_log("".join(chunks))
        active = self._process is not None and self._process.poll() is None
        self._root.after(150 if active else 450, self._poll_logs)

    def _on_close(self) -> None:
        self._stop_coach(wait_seconds=2.0)
        self._close_preview()
        self._root.destroy()

    def run(self) -> int:
        self._root.mainloop()
        return 0


def launch_gui() -> int:
    """Launch the HUDMind native Tkinter Control Center."""
    app = HUDMindGUI()
    return app.run()


if __name__ == "__main__":
    raise SystemExit(launch_gui())
