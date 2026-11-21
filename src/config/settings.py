"""Application settings, secret-redacting logging, and runtime telemetry metrics."""

from __future__ import annotations

import logging
import re
import sys
import threading
from dataclasses import dataclass, field
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Literal

from dotenv import load_dotenv
from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict

# Load .env early so both pydantic-settings and third-party SDKs see configured values
load_dotenv(override=False)

OverlayPosition = Literal["top-right", "top-left", "bottom-right", "bottom-left", "top-center"]
TTSEngineType = Literal["edge", "pyttsx3", "piper"]
ImageFormatType = Literal["jpeg", "webp"]
LanguageCode = Literal["es", "en"]
OverlayTheme = Literal["cyber-dark", "minimal-glass", "high-contrast", "matrix-green", "tactical-amber"]
TokenEfficiencyMode = Literal["eco", "balanced", "high"]
CoachModeType = Literal["match", "training"]

# Multi-provider secret detection patterns (ISO/IEC 27001 / OWASP CWE-532)
_SECRET_PATTERNS: tuple[tuple[re.Pattern[str], str], ...] = (
    (re.compile(r"AIza[0-9A-Za-z_\-]{20,}"), "[REDACTED_API_KEY]"),
    (re.compile(r"sk-(?:proj-|ant-|or-v1-)?[0-9A-Za-z_\-]{16,}"), "[REDACTED_API_KEY]"),
    (re.compile(r"gsk_[0-9A-Za-z_\-]{16,}"), "[REDACTED_API_KEY]"),
    (re.compile(r"(?i)(Bearer\s+)[0-9A-Za-z._\-]{12,}"), r"\1[REDACTED_TOKEN]"),
    (re.compile(r"(?i)([?&](?:api_)?key=)[^&\s\"']+"), r"\1[REDACTED_API_KEY]"),
)


def redact_secrets_in_text(text: str, secrets: tuple[str, ...] = ()) -> str:
    """Redact known API key patterns and explicit secret values from an arbitrary string."""
    sanitized = text
    for pattern, replacement in _SECRET_PATTERNS:
        sanitized = pattern.sub(replacement, sanitized)
    for secret in secrets:
        clean = secret.strip()
        if len(clean) >= 4 and clean in sanitized:
            sanitized = sanitized.replace(clean, "[REDACTED_SECRET]")
    return sanitized


class SecretRedactingFilter(logging.Filter):
    """Logging filter that redacts configured secrets and multi-provider API key patterns."""

    def __init__(self, secrets: tuple[str, ...] = ()) -> None:
        super().__init__()
        self._secrets = tuple(s.strip() for s in secrets if s and len(s.strip()) >= 4)

    def _redact(self, text: str) -> str:
        return redact_secrets_in_text(text, self._secrets)

    def _redact_arg(self, arg: object) -> object:
        if isinstance(arg, str):
            return self._redact(arg)
        try:
            text = str(arg)
        except Exception:
            return arg
        redacted = self._redact(text)
        return redacted if redacted != text else arg

    def filter(self, record: logging.LogRecord) -> bool:
        if isinstance(record.msg, str):
            record.msg = self._redact(record.msg)
        elif record.msg is not None:
            record.msg = self._redact(str(record.msg))
        if record.args:
            if isinstance(record.args, dict):
                record.args = {k: self._redact_arg(v) for k, v in record.args.items()}
            elif isinstance(record.args, tuple):
                record.args = tuple(self._redact_arg(arg) for arg in record.args)
        if record.exc_text:
            record.exc_text = self._redact(record.exc_text)
        return True


@dataclass
class RuntimeMetrics:
    """Thread-safe performance telemetry counters (ISO/IEC 25010 Performance Efficiency)."""

    frames_captured: int = 0
    frames_dropped_by_filter: int = 0
    frames_sent_to_ai: int = 0
    interventions_delivered: int = 0
    api_errors: int = 0
    _capture_ms_total: float = 0.0
    _filter_ms_total: float = 0.0
    _filter_samples: int = 0
    _inference_ms_total: float = 0.0
    _inference_samples: int = 0
    _payload_kb_total: float = 0.0
    _payload_samples: int = 0
    _lock: threading.Lock = field(default_factory=threading.Lock, repr=False)

    def record_capture(self, elapsed_ms: float) -> None:
        with self._lock:
            self.frames_captured += 1
            self._capture_ms_total += elapsed_ms

    def record_filter(self, elapsed_ms: float, passed: bool, payload_kb: float = 0.0) -> None:
        with self._lock:
            self._filter_samples += 1
            self._filter_ms_total += elapsed_ms
            if not passed:
                self.frames_dropped_by_filter += 1
            elif payload_kb > 0.0:
                self._payload_samples += 1
                self._payload_kb_total += payload_kb

    def record_inference(self, elapsed_ms: float, intervened: bool, failed: bool = False) -> None:
        with self._lock:
            self.frames_sent_to_ai += 1
            self._inference_samples += 1
            self._inference_ms_total += elapsed_ms
            if intervened:
                self.interventions_delivered += 1
            if failed:
                self.api_errors += 1

    def snapshot(self) -> dict[str, float | int]:
        with self._lock:
            avg_capture = (
                self._capture_ms_total / self.frames_captured if self.frames_captured else 0.0
            )
            avg_filter = (
                self._filter_ms_total / self._filter_samples if self._filter_samples else 0.0
            )
            avg_inference = (
                self._inference_ms_total / self._inference_samples
                if self._inference_samples
                else 0.0
            )
            avg_payload_kb = (
                self._payload_kb_total / self._payload_samples if self._payload_samples else 0.0
            )
            drop_pct = (
                (self.frames_dropped_by_filter / self._filter_samples) * 100.0
                if self._filter_samples
                else 0.0
            )
            return {
                "frames_captured": self.frames_captured,
                "frames_filtered": self._filter_samples,
                "frames_dropped_by_filter": self.frames_dropped_by_filter,
                "filter_drop_rate_pct": round(drop_pct, 1),
                "frames_sent_to_ai": self.frames_sent_to_ai,
                "interventions_delivered": self.interventions_delivered,
                "api_errors": self.api_errors,
                "avg_capture_ms": round(avg_capture, 2),
                "avg_filter_ms": round(avg_filter, 2),
                "avg_inference_ms": round(avg_inference, 2),
                "avg_payload_kb": round(avg_payload_kb, 1),
            }


class AppSettings(BaseSettings):
    """Typed runtime configuration for Game AI Copilot."""

    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
        case_sensitive=False,
    )

    # --- Gemini API ---
    gemini_api_key: SecretStr = Field(
        default=SecretStr(""),
        description="Google AI Studio / Gemini API key.",
    )
    default_model: str = Field(
        default="gemini-2.5-flash",
        description="Gemini multimodal model identifier.",
    )
    cache_ttl_seconds: int = Field(
        default=3600,
        ge=60,
        le=86400,
        description="Time-to-live in seconds for Gemini Context Caching.",
    )
    gemini_timeout_seconds: float = Field(
        default=15.0,
        ge=2.0,
        le=120.0,
        description="Per-request timeout for Gemini inference calls.",
    )
    gemini_temperature: float = Field(
        default=0.2,
        ge=0.0,
        le=2.0,
        description="Sampling temperature (low values improve tactical consistency).",
    )
    circuit_breaker_threshold: int = Field(
        default=5,
        ge=1,
        le=20,
        description="Consecutive API failures before temporarily opening the circuit breaker.",
    )
    circuit_breaker_cooldown_seconds: float = Field(
        default=30.0,
        ge=5.0,
        le=300.0,
        description="Cooldown period while the circuit breaker is open.",
    )

    # --- Language Settings (Text & Voice independently configurable) ---
    text_language: LanguageCode = Field(
        default="es",
        description="Language for on-screen overlay text ('es' or 'en').",
    )
    voice_language: LanguageCode = Field(
        default="es",
        description="Language for TTS spoken voice lines ('es' or 'en').",
    )
    coach_mode: CoachModeType = Field(
        default="match",
        description="Coaching style: 'match' (competitive match, silent by default) or 'training' (proactive practice/drills instructor).",
    )

    # --- Output Channels & Controls ---
    enable_voice: bool = Field(default=True, description="Enable asynchronous TTS output.")
    enable_overlay: bool = Field(default=True, description="Enable click-through overlay.")
    enable_hotkeys: bool = Field(
        default=True,
        description="Enable native Windows global hotkeys (pause/mute & analyze now).",
    )
    hotkey_toggle_pause: str = Field(
        default="F9",
        description="Global hotkey for pausing/resuming the coach (e.g. 'F9', 'Alt+F9').",
    )
    hotkey_analyze_now: str = Field(
        default="F10",
        description="Global hotkey for triggering immediate analysis (e.g. 'F10', 'Alt+F10').",
    )

    # --- TTS Configuration ---
    tts_engine: TTSEngineType = Field(default="edge", description="Active TTS engine strategy.")
    tts_voice: str = Field(
        default="es-AR-TomasNeural",
        description="Voice identifier for edge-tts or local voice name.",
    )
    tts_rate: str = Field(default="+10%", description="Speech rate modifier for edge-tts.")
    tts_volume: float = Field(default=0.9, ge=0.0, le=1.0, description="Playback volume (0.0-1.0).")
    piper_binary: str = Field(default="piper", description="Path to piper executable if used.")
    piper_model_path: str = Field(default="", description="Path to .onnx Piper voice model.")

    # --- Overlay Configuration ---
    overlay_duration_seconds: float = Field(
        default=6.0,
        ge=1.0,
        le=30.0,
        description="Seconds an overlay notification remains visible before auto-hiding.",
    )
    overlay_position: OverlayPosition = Field(
        default="top-right",
        description="Screen corner/edge where overlay notifications appear.",
    )
    overlay_width: int = Field(default=440, ge=240, le=900)
    overlay_opacity: float = Field(default=0.90, ge=0.25, le=1.0)
    overlay_theme: OverlayTheme = Field(
        default="cyber-dark",
        description="Visual theme preset for the transparent overlay HUD.",
    )
    overlay_font_family: str = Field(default="Segoe UI", description="Overlay typography family.")
    overlay_font_size: int = Field(default=12, ge=9, le=20, description="Overlay body font size.")
    accounts_file: Path = Field(
        default=Path("config/accounts.json"),
        description="Local JSON file storing multi-provider AI accounts (gitignored).",
    )
    user_config_file: Path = Field(
        default=Path("config/user_config.json"),
        description="Local JSON file storing GUI preferences (gitignored).",
    )

    # --- Capture & Pipeline ---
    capture_monitor: int = Field(
        default=1,
        ge=0,
        description="MSS monitor index (1 = primary monitor, 0 = all combined).",
    )
    min_inference_interval_seconds: float = Field(
        default=2.5,
        ge=0.5,
        le=60.0,
        description="Minimum gap between Gemini API calls even when no intervention occurs.",
    )
    image_compression_quality: int = Field(
        default=78,
        ge=30,
        le=95,
        description="JPEG/WebP compression quality before sending frames to Gemini.",
    )
    image_format: ImageFormatType = Field(
        default="jpeg",
        description="Encoded image format ('jpeg' = lowest CPU ~2.4ms; 'webp' = smallest bandwidth).",
    )
    token_efficiency_mode: TokenEfficiencyMode = Field(
        default="balanced",
        description="Token/resolution profile: 'eco' (540p, lowest tokens), 'balanced' (720p), 'high'.",
    )
    low_process_priority: bool = Field(
        default=True,
        description="Run coach at Windows BELOW_NORMAL priority to protect game 1% low FPS.",
    )

    # --- Profiles & Logging ---
    profiles_dir: Path = Field(
        default=Path("profiles"),
        description="Directory containing per-game profile folders.",
    )
    log_level: str = Field(default="INFO", description="Logging verbosity level.")
    enable_file_logging: bool = Field(
        default=False,
        description="Write rotating audit logs to logs/copilot.log.",
    )
    log_file: Path = Field(
        default=Path("logs/copilot.log"),
        description="Path for rotating log file when enable_file_logging=true.",
    )
    process_poll_interval_seconds: float = Field(
        default=2.0,
        ge=0.5,
        le=30.0,
        description="Polling interval for active foreground .exe detection.",
    )

    def get_api_key_value(self) -> str:
        """Return the raw API key string without exposing it in logs or repr."""
        return self.gemini_api_key.get_secret_value().strip()

    def has_valid_api_key(self) -> bool:
        """Check whether a non-placeholder Gemini API key is configured."""
        raw = self.get_api_key_value()
        return bool(raw) and raw != "your_api_key_here"


def configure_logging(
    level_name: str = "INFO",
    *,
    secrets: tuple[str, ...] = (),
    enable_file_logging: bool = False,
    log_file: Path | None = None,
) -> None:
    """Configure timestamped, secret-redacting console (and optional rotating file) logging."""
    level = getattr(logging, level_name.upper(), logging.INFO)
    root_logger = logging.getLogger()
    root_logger.setLevel(level)

    redactor = SecretRedactingFilter(secrets=secrets)
    formatter = logging.Formatter(
        fmt="%(asctime)s | %(levelname)-7s | %(name)-24s | %(message)s",
        datefmt="%H:%M:%S",
    )

    if not root_logger.handlers:
        console_handler = logging.StreamHandler(sys.stdout)
        console_handler.setLevel(level)
        console_handler.setFormatter(formatter)
        console_handler.addFilter(redactor)
        root_logger.addHandler(console_handler)
    else:
        for handler in root_logger.handlers:
            handler.setLevel(level)
            for existing in list(handler.filters):
                if isinstance(existing, SecretRedactingFilter):
                    handler.removeFilter(existing)
            handler.addFilter(redactor)

    if enable_file_logging and log_file is not None:
        log_file.parent.mkdir(parents=True, exist_ok=True)
        has_file_handler = any(isinstance(h, RotatingFileHandler) for h in root_logger.handlers)
        if not has_file_handler:
            file_handler = RotatingFileHandler(
                log_file,
                maxBytes=5 * 1024 * 1024,
                backupCount=3,
                encoding="utf-8",
            )
            file_handler.setLevel(level)
            file_handler.setFormatter(formatter)
            file_handler.addFilter(redactor)
            root_logger.addHandler(file_handler)

    # Reduce noise from verbose third-party loggers
    logging.getLogger("httpx").setLevel(logging.WARNING)
    logging.getLogger("httpcore").setLevel(logging.WARNING)
    logging.getLogger("google_genai").setLevel(logging.WARNING)
    logging.getLogger("PIL").setLevel(logging.WARNING)
