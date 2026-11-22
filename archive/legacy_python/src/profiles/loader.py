"""Game profile loader and Windows active foreground process detector."""

from __future__ import annotations

import contextlib
import json
import logging
import re
import sys
import time
from pathlib import Path

import psutil
from pydantic import BaseModel, Field, ValidationError, field_validator

logger = logging.getLogger(__name__)

_SAFE_GAME_ID_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{0,63}$")
_HORIZONTAL_RULE_RE = re.compile(r"(?m)^\s*---+\s*$")
_MULTI_BLANK_RE = re.compile(r"\n{3,}")
_MAX_PROMPT_BYTES = 512 * 1024  # 512 KB ceiling per markdown profile document
MAX_CUSTOM_RULES_CHARS = 4000  # ~1,000 tokens ceiling for per-game user custom rules


def compact_markdown(text: str) -> str:
    """Strip decorative horizontal rules and excess blank lines to save ~15-20% tokens."""
    no_rules = _HORIZONTAL_RULE_RE.sub("", text)
    lines = [line.rstrip() for line in no_rules.splitlines()]
    joined = "\n".join(lines).strip()
    return _MULTI_BLANK_RE.sub("\n\n", joined)

# Optional Windows-only imports for active foreground window detection
if sys.platform == "win32":
    try:
        import win32gui  # type: ignore[import-untyped]
        import win32process  # type: ignore[import-untyped]

        _WIN32_AVAILABLE = True
    except ImportError:
        win32gui = None
        win32process = None
        _WIN32_AVAILABLE = False
else:
    win32gui = None
    win32process = None
    _WIN32_AVAILABLE = False


class GameManifest(BaseModel):
    """Validated schema for profiles/<game_name>/manifest.json (ISO/IEC 25010 Integrity)."""

    game_id: str = Field(..., min_length=1, max_length=64, description="Unique profile identifier.")
    display_name: str = Field(..., min_length=1, max_length=120, description="Game title.")
    process_names: list[str] = Field(
        ...,
        min_length=1,
        description="Executable names (.exe) that trigger this profile.",
    )
    capture_fps: float = Field(default=1.0, gt=0.0, le=30.0)
    phash_threshold: int = Field(default=8, ge=0, le=64)
    cooldown_seconds: float = Field(default=10.0, ge=0.0, le=300.0)
    max_resolution: tuple[int, int] = Field(default=(1280, 720))
    language: str = Field(default="es", min_length=2, max_length=16)
    notes: str = Field(default="", max_length=500)

    @field_validator("game_id")
    @classmethod
    def validate_safe_game_id(cls, value: str) -> str:
        if not _SAFE_GAME_ID_RE.match(value):
            raise ValueError(
                "game_id debe contener únicamente minúsculas, dígitos, '-' o '_' (sin rutas)."
            )
        return value

    @field_validator("process_names")
    @classmethod
    def validate_process_names(cls, names: list[str]) -> list[str]:
        cleaned = [n.strip() for n in names if n.strip()]
        if not cleaned:
            raise ValueError("process_names debe incluir al menos un nombre de ejecutable válido.")
        for name in cleaned:
            if "/" in name or "\\" in name or ".." in name:
                raise ValueError(f"Nombre de proceso inválido: {name!r}")
        return cleaned

    @field_validator("max_resolution")
    @classmethod
    def validate_resolution(cls, value: tuple[int, int]) -> tuple[int, int]:
        width, height = value
        if not (320 <= width <= 3840 and 240 <= height <= 2160):
            raise ValueError(
                f"max_resolution fuera de rango seguro (320x240 .. 3840x2160): {value}"
            )
        return value


class GameProfile(BaseModel):
    """Fully loaded game cartridge (manifest + system prompt + knowledge base + custom rules)."""

    manifest: GameManifest
    system_prompt: str
    knowledge_base: str
    base_knowledge_base: str = ""
    custom_rules: str = ""
    profile_dir: Path

    def matches_process(self, executable_name: str | None) -> bool:
        """Case-insensitive check against the profile's registered process names."""
        if not executable_name:
            return False
        normalized = executable_name.strip().lower()
        return any(proc.strip().lower() == normalized for proc in self.manifest.process_names)

    @property
    def estimated_tokens(self) -> int:
        """Rough character-based token estimate (~4 chars/token) for diagnostics."""
        total_chars = len(self.system_prompt) + len(self.knowledge_base)
        return max(1, total_chars // 4)

    @property
    def custom_rules_estimated_tokens(self) -> int:
        """Rough token estimate added specifically by the user's custom rules."""
        clean = self.custom_rules.strip()
        return len(clean) // 4 if clean else 0

    @property
    def compact_system_prompt(self) -> str:
        """Token-optimized system prompt with decorative markdown rules stripped."""
        return compact_markdown(self.system_prompt)

    @property
    def compact_knowledge_base(self) -> str:
        """Token-optimized knowledge base with decorative markdown rules stripped."""
        return compact_markdown(self.knowledge_base)


class ForegroundProcessDetector:
    """Detects the executable name (.exe) of the currently focused Windows window.

    Caches the resolved process name per immutable `(HWND, PID)` pair so repeated
    focus checks while gaming avoid redundant `OpenProcess` kernel syscalls.
    """

    def __init__(self, cache_ttl_seconds: float = 1.0) -> None:
        self._cache_ttl = cache_ttl_seconds
        self._cached_hwnd: int = 0
        self._cached_pid: int = 0
        self._cached_exe: str | None = None
        self._cached_at: float = 0.0
        if not _WIN32_AVAILABLE:
            logger.warning(
                "pywin32 no disponible o plataforma distinta de Windows; "
                "la detección automática de ventana en primer plano queda deshabilitada."
            )

    @property
    def is_supported(self) -> bool:
        return _WIN32_AVAILABLE

    def get_foreground_executable(self) -> str | None:
        """Return the .exe name of the foreground window, or None if unavailable."""
        if not _WIN32_AVAILABLE or win32gui is None or win32process is None:
            return None

        try:
            hwnd = int(win32gui.GetForegroundWindow())
            if not hwnd:
                self._cached_hwnd = 0
                self._cached_pid = 0
                self._cached_exe = None
                return None

            now = time.monotonic()
            if hwnd == self._cached_hwnd and (now - self._cached_at) < self._cache_ttl:
                return self._cached_exe

            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid <= 0:
                return None

            # If (HWND, PID) pair is unchanged, reuse cached exe name without OpenProcess syscall
            if (
                hwnd == self._cached_hwnd
                and pid == self._cached_pid
                and self._cached_exe is not None
            ):
                self._cached_at = now
                return self._cached_exe

            process = psutil.Process(pid)
            exe_name = str(process.name())
            self._cached_hwnd = hwnd
            self._cached_pid = int(pid)
            self._cached_exe = exe_name
            self._cached_at = now
            return exe_name
        except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
            self._cached_hwnd = 0
            self._cached_pid = 0
            self._cached_exe = None
            return None
        except Exception as exc:
            logger.debug("Error obteniendo proceso en primer plano: %s", exc)
            return None

    def get_foreground_window_bbox(self) -> tuple[int, int, int, int] | None:
        """Return `(left, top, width, height)` of the active window's client area on Windows.

        Capturing only the game's client rectangle prevents desktop/taskbar bleed in
        windowed mode (privacy) and dedicates 100% of vision resolution to the game HUD.
        """
        if not _WIN32_AVAILABLE or win32gui is None:
            return None
        try:
            hwnd = int(win32gui.GetForegroundWindow())
            if not hwnd or win32gui.IsIconic(hwnd):
                return None

            left, top, right, bottom = win32gui.GetClientRect(hwnd)
            width = int(right - left)
            height = int(bottom - top)
            if width >= 320 and height >= 240:
                screen_x, screen_y = win32gui.ClientToScreen(hwnd, (0, 0))
                return int(screen_x), int(screen_y), width, height

            w_left, w_top, w_right, w_bottom = win32gui.GetWindowRect(hwnd)
            w_width = int(w_right - w_left)
            w_height = int(w_bottom - w_top)
            if w_width >= 320 and w_height >= 240:
                return int(w_left), int(w_top), w_width, w_height
            return None
        except Exception:
            return None

    def list_running_executables(self) -> list[str]:
        """Return sorted unique `.exe` names currently running with visible windows or processes."""
        names: set[str] = set()
        if _WIN32_AVAILABLE and win32gui is not None and win32process is not None:
            with contextlib.suppress(Exception):
                def _enum_cb(hwnd: int, _: object) -> None:
                    if win32gui.IsWindowVisible(hwnd) and win32gui.GetWindowText(hwnd):
                        _, pid = win32process.GetWindowThreadProcessId(hwnd)
                        if pid > 0:
                            with contextlib.suppress(Exception):
                                names.add(str(psutil.Process(pid).name()))

                win32gui.EnumWindows(_enum_cb, None)

        if not names:
            for proc in psutil.process_iter(["name"]):
                with contextlib.suppress(Exception):
                    pname = proc.info.get("name")
                    if isinstance(pname, str) and pname.lower().endswith(".exe"):
                        names.add(pname)
        return sorted(names, key=str.lower)


class GameProfileLoader:
    """Loads game profile cartridges dynamically from disk and matches active processes."""

    REQUIRED_FILES = ("manifest.json", "system_prompt.md", "knowledge_base.md")

    def __init__(
        self,
        profiles_dir: Path,
        process_detector: ForegroundProcessDetector | None = None,
    ) -> None:
        self._profiles_dir = profiles_dir
        self._detector = process_detector or ForegroundProcessDetector()
        self._profiles: dict[str, GameProfile] = {}
        self._process_index: dict[str, GameProfile] = {}
        self.reload()

    @property
    def detector(self) -> ForegroundProcessDetector:
        return self._detector

    def reload(self) -> dict[str, GameProfile]:
        """Scan profiles directory and load all valid game cartridges."""
        self._profiles.clear()
        self._process_index.clear()

        if not self._profiles_dir.exists():
            logger.warning("Directorio de perfiles no encontrado: %s", self._profiles_dir.resolve())
            return {}

        base_resolved = self._profiles_dir.resolve()
        for entry in sorted(self._profiles_dir.iterdir()):
            if not entry.is_dir() or entry.name.startswith("."):
                continue

            # Guard against symlink escapes outside profiles_dir (CWE-22)
            try:
                entry.resolve().relative_to(base_resolved)
            except ValueError:
                logger.warning("Ignorando ruta de perfil fuera de directorio base: %s", entry)
                continue

            profile = self._load_single_profile(entry)
            if profile is None:
                continue

            self._profiles[profile.manifest.game_id] = profile
            for proc_name in profile.manifest.process_names:
                self._process_index[proc_name.strip().lower()] = profile

        logger.info(
            "Perfiles cargados (%d): %s",
            len(self._profiles),
            ", ".join(self._profiles.keys()) or "(ninguno)",
        )
        return dict(self._profiles)

    def _load_single_profile(self, folder: Path) -> GameProfile | None:
        """Validate and load a single profile folder."""
        missing = [fname for fname in self.REQUIRED_FILES if not (folder / fname).is_file()]
        if missing:
            logger.warning(
                "Ignorando perfil incompleto '%s' (faltan: %s)",
                folder.name,
                ", ".join(missing),
            )
            return None

        try:
            prompt_path = folder / "system_prompt.md"
            kb_path = folder / "knowledge_base.md"
            if (
                prompt_path.stat().st_size > _MAX_PROMPT_BYTES
                or kb_path.stat().st_size > _MAX_PROMPT_BYTES
            ):
                logger.warning("Perfil '%s' excede el límite de tamaño de documento.", folder.name)
                return None

            raw_manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
            manifest = GameManifest.model_validate(raw_manifest)
            system_prompt = prompt_path.read_text(encoding="utf-8").strip()
            base_kb = kb_path.read_text(encoding="utf-8").strip()

            if not system_prompt or not base_kb:
                logger.warning(
                    "Perfil '%s' tiene system_prompt.md o knowledge_base.md vacío.",
                    folder.name,
                )
                return None

            custom_rules = ""
            custom_path = folder / "custom_rules.md"
            if custom_path.is_file() and custom_path.stat().st_size <= _MAX_PROMPT_BYTES:
                custom_rules = custom_path.read_text(encoding="utf-8").strip()[:MAX_CUSTOM_RULES_CHARS]

            full_kb = (
                f"{base_kb}\n\n## Reglas Personalizadas del Jugador (Prioridad Alta)\n{custom_rules}"
                if custom_rules
                else base_kb
            )

            return GameProfile(
                manifest=manifest,
                system_prompt=system_prompt,
                knowledge_base=full_kb,
                base_knowledge_base=base_kb,
                custom_rules=custom_rules,
                profile_dir=folder,
            )
        except (json.JSONDecodeError, ValidationError, OSError) as exc:
            logger.error("Error cargando perfil '%s': %s", folder.name, exc)
            return None

    def list_profiles(self) -> list[GameProfile]:
        """Return all loaded profiles."""
        return list(self._profiles.values())

    def get_by_id(self, game_id: str) -> GameProfile | None:
        """Retrieve a profile by its validated game_id (rejects path traversal inputs)."""
        if not game_id or "/" in game_id or "\\" in game_id or ".." in game_id:
            logger.warning("Identificador de perfil rechazado por seguridad: %r", game_id)
            return None

        if game_id in self._profiles:
            return self._profiles[game_id]
        for profile in self._profiles.values():
            if profile.profile_dir.name.lower() == game_id.lower():
                return profile
        return None

    def get_for_process(self, executable_name: str | None) -> GameProfile | None:
        """Match a running executable name against registered game profiles."""
        if not executable_name:
            return None
        return self._process_index.get(executable_name.strip().lower())

    def detect_active_profile(self) -> tuple[str | None, GameProfile | None]:
        """Inspect foreground window and return (executable_name, matching_profile)."""
        exe_name = self._detector.get_foreground_executable()
        profile = self.get_for_process(exe_name)
        return exe_name, profile

    def create_profile_scaffold(
        self,
        *,
        game_id: str,
        display_name: str,
        process_name: str,
        language: str = "es",
        notes: str = "",
    ) -> GameProfile:
        """Create a new game profile cartridge on disk and register it immediately."""
        clean_id = game_id.strip().lower()
        clean_name = display_name.strip()
        clean_proc = process_name.strip()
        if not clean_proc.lower().endswith(".exe"):
            clean_proc = f"{clean_proc}.exe"

        manifest = GameManifest(
            game_id=clean_id,
            display_name=clean_name,
            process_names=[clean_proc],
            capture_fps=1.0,
            phash_threshold=8,
            cooldown_seconds=10.0,
            max_resolution=(1280, 720),
            language=language.strip() or "es",
            notes=notes.strip() or f"Perfil táctico para {clean_name}.",
        )

        target_dir = (self._profiles_dir / manifest.game_id).resolve()
        base_resolved = self._profiles_dir.resolve()
        target_dir.relative_to(base_resolved)  # Enforces CWE-22 containment
        if target_dir.exists():
            raise FileExistsError(f"El perfil '{manifest.game_id}' ya existe en {target_dir}")

        target_dir.mkdir(parents=True, exist_ok=False)
        (target_dir / "manifest.json").write_text(
            json.dumps(manifest.model_dump(), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
        system_prompt = (
            f"# Rol: Coach Táctico en Tiempo Real — {clean_name}\n\n"
            f"Observás la pantalla del jugador durante una partida de **{clean_name}**.\n\n"
            "## Reglas Estrictas\n"
            "1. **Silencio por Defecto (`should_intervene = false`):** Solo intervení ante riesgo "
            "inmediato, error táctico claro o decisión clave de recursos/objetivos.\n"
            "2. **Brevedad Extrema:** `overlay_text` máximo 8 palabras; `voice_line` máximo 12 palabras.\n"
            "3. **Cero Alucinaciones:** Basate exclusivamente en elementos legibles del HUD.\n"
        )
        knowledge_base = (
            f"# Base de Conocimiento Táctico — {clean_name}\n\n"
            "## 1. Anclajes Visuales del HUD\n"
            "- **Minimapa / Radar:** Posición de aliados, enemigos y objetivos activos.\n"
            "- **Barras de Estado (Vida / Recursos / Munición / Cooldowns):** Alertar cuando estén en nivel crítico.\n"
            "- **Reloj / Temporizador de Partida:** Priorizar objetivos próximos a activarse.\n\n"
            "## 2. Heurísticas de Decisión\n"
            "- `" + '"high"' + "`: Peligro inminente de muerte evitable, flanco descubierto o recurso en 0.\n"
            "- `" + '"medium"' + "`: Uso de habilidad/utilidad disponible o rotación táctica.\n"
            "- `" + '"low"' + "`: Recordatorio de economía o posicionamiento general.\n\n"
            "## 3. Lista Negra de UI (`should_intervene = false`)\n"
            "- Menú principal, lobby, pantalla de carga, tabla de puntuaciones (`TAB`), cámara de muerte o chat.\n"
        )
        (target_dir / "system_prompt.md").write_text(system_prompt, encoding="utf-8")
        (target_dir / "knowledge_base.md").write_text(knowledge_base, encoding="utf-8")

        self.reload()
        created = self.get_by_id(manifest.game_id)
        if created is None:
            raise RuntimeError(f"No se pudo cargar el perfil recién creado: {manifest.game_id}")
        return created

    def save_custom_rules(self, game_id: str, rules_text: str) -> GameProfile:
        """Save or clear per-game custom player rules (`profiles/<game_id>/custom_rules.md`)."""
        profile = self.get_by_id(game_id)
        if profile is None:
            raise KeyError(f"Perfil no encontrado: {game_id!r}")

        clean = rules_text.strip()
        if len(clean) > MAX_CUSTOM_RULES_CHARS:
            raise ValueError(
                f"Las reglas personalizadas superan el máximo permitido de "
                f"{MAX_CUSTOM_RULES_CHARS} caracteres (~1,000 tokens)."
            )

        target_dir = profile.profile_dir.resolve()
        target_dir.relative_to(self._profiles_dir.resolve())  # CWE-22 guard
        custom_path = target_dir / "custom_rules.md"

        if not clean:
            if custom_path.is_file():
                custom_path.unlink()
        else:
            tmp_path = target_dir / "custom_rules.md.tmp"
            tmp_path.write_text(clean + "\n", encoding="utf-8")
            tmp_path.replace(custom_path)

        self.reload()
        updated = self.get_by_id(profile.manifest.game_id)
        if updated is None:
            raise RuntimeError(f"Error recargando perfil '{game_id}' tras guardar reglas.")
        return updated
