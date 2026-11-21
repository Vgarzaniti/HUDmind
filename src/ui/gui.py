"""HUDMind Control Center — Ultra-lightweight native Tkinter/ttk GUI.

Designed for minimal memory/CPU footprint (~15 MB while open, 0 MB when closed
using detached launch mode). Uses only Python stdlib `tkinter`/`ttk` (zero
Electron/Qt overhead) and runs the active coach in an isolated child process so
Tkinter never collides with the click-through overlay's Tk thread.
"""

from __future__ import annotations

import asyncio
import contextlib
import ctypes
import logging
import os
import queue
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import messagebox, ttk
from typing import Any

from src.config.settings import AppSettings, OverlayPosition, OverlayTheme
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
    _ensure_tcl_tk_env,
    enable_windows_dpi_awareness,
)
from src.profiles.loader import GameProfileLoader

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

        kind_raw = self._var_provider.get()
        kind: ProviderKind = kind_raw if kind_raw in PROVIDER_PRESETS else "gemini"  # type: ignore[assignment]
        kwargs: dict[str, Any] = {
            "label": label,
            "provider": kind,
            "api_key": self._var_api_key.get().strip(),
            "model": model,
            "base_url": self._var_base_url.get().strip(),
            "priority": int(self._var_priority.get()),
            "enabled": bool(self._var_enabled.get()),
        }
        if self._existing_id:
            kwargs["id"] = self._existing_id

        self.result = ProviderAccount(**kwargs)
        self.destroy()


class HUDMindGUI:
    """Main lightweight Tkinter Control Center for HUDMind."""

    def __init__(self) -> None:
        enable_windows_dpi_awareness()
        _ensure_tcl_tk_env()

        self._settings: AppSettings = load_effective_settings()
        self._accounts: list[ProviderAccount] = load_accounts(self._settings)
        self._profiles = GameProfileLoader(self._settings.profiles_dir).list_profiles()

        self._process: subprocess.Popen[str] | None = None
        self._log_queue: queue.Queue[str] = queue.Queue(maxsize=300)
        self._preview_win: tk.Toplevel | None = None
        self._preview_after_id: str | None = None

        self._root = tk.Tk()
        self._root.title("HUDMind — Panel de Configuración y Coach IA")
        self._root.geometry("860x620")
        self._root.minsize(780, 560)
        self._root.configure(bg=_BG_ROOT)
        self._root.protocol("WM_DELETE_WINDOW", self._on_close)

        self._init_styles()
        self._init_vars()
        self._build_layout()
        self._refresh_accounts_tree()
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
        profile_ids = [p.manifest.game_id for p in self._profiles] or ["example_cs2"]
        self._var_profile = tk.StringVar(value=profile_ids[0])
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
        self._tab_lang_voice = ttk.Frame(self._notebook, padding=14)
        self._tab_overlay = ttk.Frame(self._notebook, padding=14)
        self._tab_perf = ttk.Frame(self._notebook, padding=14)

        self._notebook.add(self._tab_control, text="  Control  ")
        self._notebook.add(self._tab_accounts, text="  Cuentas IA & Rotación  ")
        self._notebook.add(self._tab_lang_voice, text="  Idioma y Voz  ")
        self._notebook.add(self._tab_overlay, text="  Estilo Overlay  ")
        self._notebook.add(self._tab_perf, text="  Rendimiento  ")

        self._build_control_tab()
        self._build_accounts_tab()
        self._build_lang_voice_tab()
        self._build_overlay_tab()
        self._build_perf_tab()

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
        profile_ids = [p.manifest.game_id for p in self._profiles] or ["example_cs2"]
        ttk.Combobox(
            top,
            textvariable=self._var_profile,
            values=profile_ids,
            state="readonly",
            width=26,
        ).grid(row=1, column=1, sticky="w", padx=6, pady=6)

        ttk.Checkbutton(
            top,
            text="Dry-Run (probar captura y filtros sin consumir tokens IA)",
            variable=self._var_dry_run,
        ).grid(row=2, column=1, sticky="w", padx=6, pady=2)

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

        tk.Label(
            self._tab_control,
            text="Atajos en partida: F9 = Pausar/Reanudar | F10 = Pedir consejo ahora (saltea cooldown)",
            bg=_BG_CARD,
            fg=_ACCENT,
            font=("Segoe UI", 8, "bold"),
        ).pack(anchor="w", pady=(0, 6))

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
            grid, from_=-20, to=40, variable=self._var_tts_rate, orient="horizontal", length=220
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
            text="Habilitar hotkeys globales nativos (F9 Pausar / F10 Analizar ahora)",
            variable=self._var_hotkeys,
        ).grid(row=6, column=0, columnspan=3, sticky="w", **pad)

        ttk.Checkbutton(
            grid,
            text="Guardar archivo de auditoría rotativo en logs/copilot.log",
            variable=self._var_file_log,
        ).grid(row=7, column=0, columnspan=3, sticky="w", **pad)

    # ------------------------------------------------------------------ Actions
    def _sync_voice_presets(self, keep_current: bool = False) -> None:
        lang = self._var_voice_lang.get()
        voices = _VOICE_PRESETS.get(lang, _VOICE_PRESETS["es"])
        self._combo_voice.configure(values=voices)
        if not keep_current or not self._var_tts_voice.get().strip():
            self._var_tts_voice.set(voices[0])

    def _build_settings_from_ui(self) -> AppSettings:
        rate_val = int(self._var_tts_rate.get())
        rate_str = f"+{rate_val}%" if rate_val >= 0 else f"{rate_val}%"
        return self._settings.model_copy(
            update={
                "text_language": self._var_text_lang.get(),
                "voice_language": self._var_voice_lang.get(),
                "enable_voice": bool(self._var_enable_voice.get()),
                "enable_overlay": bool(self._var_enable_overlay.get()),
                "enable_hotkeys": bool(self._var_hotkeys.get()),
                "tts_engine": self._var_tts_engine.get(),
                "tts_voice": self._var_tts_voice.get().strip() or "es-AR-TomasNeural",
                "tts_rate": rate_str,
                "tts_volume": round(float(self._var_tts_volume.get()), 2),
                "overlay_theme": self._var_overlay_theme.get(),
                "overlay_position": self._var_overlay_pos.get(),
                "overlay_opacity": round(float(self._var_overlay_opacity.get()), 2),
                "overlay_width": int(self._var_overlay_width.get()),
                "overlay_duration_seconds": round(float(self._var_overlay_duration.get()), 1),
                "overlay_font_family": self._var_overlay_font.get(),
                "overlay_font_size": int(self._var_overlay_font_size.get()),
                "token_efficiency_mode": self._var_token_mode.get(),
                "image_format": self._var_image_format.get(),
                "image_compression_quality": int(self._var_image_quality.get()),
                "capture_monitor": int(self._var_monitor.get()),
                "min_inference_interval_seconds": round(float(self._var_min_interval.get()), 1),
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
            self._accounts.append(dlg.result)
            self._refresh_accounts_tree()
            self._save_all()

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
            self._accounts.append(acc)
            self._refresh_accounts_tree()
            self._save_all()
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
            self._accounts.append(dlg.result)
            self._refresh_accounts_tree()
            self._save_all()

    def _edit_account(self) -> None:
        idx = self._selected_account_index()
        if idx is None:
            return
        dlg = AccountEditorDialog(self._root, account=self._accounts[idx])
        self._root.wait_window(dlg)
        if dlg.result is not None:
            self._accounts[idx] = dlg.result
            self._refresh_accounts_tree()
            self._save_all()

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
            messagebox.showinfo("Probar conexión", "Seleccioná una cuenta de la lista.", parent=self._root)
            return
        acc = self._accounts[idx]
        self._var_status.set(f"Probando conexión con '{acc.label}'...")

        def _worker() -> None:
            from src.ai.providers import create_provider_adapter

            adapter = create_provider_adapter(acc, self._build_settings_from_ui())
            ok, msg = asyncio.run(adapter.test_connection())
            self._root.after(0, lambda: self._on_test_result(acc.label, ok, msg))

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
                svc.enqueue(sample)
                await asyncio.sleep(4.0)
                await svc.stop()

            asyncio.run(_play())

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

        # Apply click-through on Windows
        if sys.platform == "win32":
            with contextlib.suppress(Exception):
                user32 = ctypes.windll.user32
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
            messagebox.showwarning(
                "Sin cuentas IA configuradas",
                "Agregá al menos una cuenta IA en la pestaña 'Cuentas IA & Rotación' "
                "(o activá Dry-Run para probar sin IA).",
                parent=self._root,
            )
            self._notebook.select(self._tab_accounts)
            return

        cmd = [sys.executable, "-m", "src.main"]
        if self._var_mode.get() == "test":
            cmd.extend(["--test-mode", "--profile", self._var_profile.get()])
        if self._var_dry_run.get():
            cmd.append("--dry-run")

        if detach_and_close:
            creationflags = getattr(subprocess, "CREATE_NEW_CONSOLE", 0) if sys.platform == "win32" else 0
            subprocess.Popen(cmd, creationflags=creationflags)
            self._root.destroy()
            return

        if self._process is not None and self._process.poll() is None:
            return

        self._append_log(f"Iniciando: {' '.join(cmd)}\n")
        env = dict(os.environ)
        env["PYTHONUNBUFFERED"] = "1"
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0
        self._process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            creationflags=creationflags,
        )
        self._btn_start.configure(state="disabled")
        self._btn_stop.configure(state="normal")
        self._var_status.set("Coach ACTIVO en segundo plano.")
        threading.Thread(target=self._read_process_output, daemon=True).start()

    def _read_process_output(self) -> None:
        proc = self._process
        if proc is None or proc.stdout is None:
            return
        for line in proc.stdout:
            with contextlib.suppress(queue.Full):
                self._log_queue.put_nowait(line)
        code = proc.wait()
        with contextlib.suppress(queue.Full):
            self._log_queue.put_nowait(f"\n[Proceso finalizado con código {code}]\n")
        self._root.after(0, self._on_process_stopped)

    def _on_process_stopped(self) -> None:
        self._btn_start.configure(state="normal")
        self._btn_stop.configure(state="disabled")
        self._var_status.set("Coach detenido — 0% CPU en reposo.")

    def _stop_coach(self) -> None:
        if self._process is not None and self._process.poll() is None:
            self._process.terminate()
            self._var_status.set("Deteniendo coach...")

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
        try:
            while True:
                line = self._log_queue.get_nowait()
                self._append_log(line)
        except queue.Empty:
            pass
        self._root.after(150, self._poll_logs)

    def _on_close(self) -> None:
        self._stop_coach()
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
