"""Local persistence for GUI user preferences and multi-provider AI accounts (gitignored)."""

from __future__ import annotations

import contextlib
import json
import logging
import os
import stat
from pathlib import Path
from typing import Any, Literal
from urllib.parse import urlparse
from uuid import uuid4

from pydantic import BaseModel, Field, ValidationError, field_validator

from src.config.settings import AppSettings

logger = logging.getLogger(__name__)

_LOOPBACK_HOSTS = {"localhost", "127.0.0.1", "::1"}
_BLOCKED_HOSTS = {"0.0.0.0", "169.254.169.254", "metadata.google.internal"}  # noqa: S104

ProviderKind = Literal[
    "gemini",
    "openai",
    "claude",
    "groq",
    "openrouter",
    "pollinations",
    "ollama",
    "custom",
]


class ProviderPreset(BaseModel):
    """Metadata describing a supported AI provider."""

    kind: ProviderKind
    display_name: str
    default_model: str
    suggested_models: list[str]
    default_base_url: str
    requires_api_key: bool
    is_free_tier: bool
    hint: str


PROVIDER_PRESETS: dict[ProviderKind, ProviderPreset] = {
    "gemini": ProviderPreset(
        kind="gemini",
        display_name="Google Gemini (AI Studio)",
        default_model="gemini-2.5-flash",
        suggested_models=["gemini-2.5-flash", "gemini-2.5-flash-lite"],
        default_base_url="",
        requires_api_key=True,
        is_free_tier=True,
        hint="Free tier en aistudio.google.com · Soporta Context Caching · Podés agregar varias cuentas.",
    ),
    "openai": ProviderPreset(
        kind="openai",
        display_name="OpenAI (ChatGPT API)",
        default_model="gpt-4o-mini",
        suggested_models=["gpt-4o-mini", "gpt-4.1-mini", "gpt-4o"],
        default_base_url="https://api.openai.com/v1",
        requires_api_key=True,
        is_free_tier=False,
        hint="Requiere API key de platform.openai.com con créditos.",
    ),
    "claude": ProviderPreset(
        kind="claude",
        display_name="Anthropic Claude",
        default_model="claude-3-5-haiku-latest",
        suggested_models=["claude-3-5-haiku-latest", "claude-sonnet-4-20250514"],
        default_base_url="https://api.anthropic.com/v1",
        requires_api_key=True,
        is_free_tier=False,
        hint="Requiere API key de console.anthropic.com.",
    ),
    "groq": ProviderPreset(
        kind="groq",
        display_name="Groq Cloud (Gratis / Ultra-rápido)",
        default_model="meta-llama/llama-4-scout-17b-16e-instruct",
        suggested_models=[
            "meta-llama/llama-4-scout-17b-16e-instruct",
            "meta-llama/llama-4-maverick-17b-128e-instruct",
        ],
        default_base_url="https://api.groq.com/openai/v1",
        requires_api_key=True,
        is_free_tier=True,
        hint="Free tier muy rápido con visión en console.groq.com.",
    ),
    "openrouter": ProviderPreset(
        kind="openrouter",
        display_name="OpenRouter (Modelos :free)",
        default_model="google/gemma-3-27b-it:free",
        suggested_models=[
            "google/gemma-3-27b-it:free",
            "qwen/qwen2.5-vl-72b-instruct:free",
            "openrouter/free",
        ],
        default_base_url="https://openrouter.ai/api/v1",
        requires_api_key=True,
        is_free_tier=True,
        hint="Modelos gratuitos (:free) con cuenta gratis en openrouter.ai.",
    ),
    "pollinations": ProviderPreset(
        kind="pollinations",
        display_name="Pollinations.ai (Gratis)",
        default_model="openai/gpt-5.4-nano",
        suggested_models=["openai/gpt-5.4-nano", "openai/gpt-5.4-mini"],
        default_base_url="https://gen.pollinations.ai/v1",
        requires_api_key=True,
        is_free_tier=True,
        hint="Modelos gratuitos con clave gratis en enter.pollinations.ai/keys.",
    ),
    "ollama": ProviderPreset(
        kind="ollama",
        display_name="Ollama Local (100% Gratis SIN LOGIN)",
        default_model="qwen2.5vl:3b",
        suggested_models=["qwen2.5vl:3b", "llama3.2-vision", "gemma3:4b", "llava"],
        default_base_url="http://localhost:11434/v1",
        requires_api_key=False,
        is_free_tier=True,
        hint="100% local, gratis y sin cuenta/login. Requiere tener Ollama corriendo en tu PC.",
    ),
    "custom": ProviderPreset(
        kind="custom",
        display_name="OpenAI-Compatible (LM Studio / Sin Login)",
        default_model="local-model",
        suggested_models=["local-model"],
        default_base_url="http://localhost:1234/v1",
        requires_api_key=False,
        is_free_tier=True,
        hint="Cualquier servidor compatible con OpenAI (LM Studio, vLLM, endpoint propio sin login).",
    ),
}


class ProviderAccount(BaseModel):
    """Single configured AI provider account participating in the failover pool."""

    id: str = Field(default_factory=lambda: uuid4().hex[:10])
    label: str = Field(..., min_length=1, max_length=80)
    provider: ProviderKind = Field(default="gemini")
    api_key: str = Field(default="")
    model: str = Field(default="gemini-2.5-flash", min_length=1, max_length=120)
    base_url: str = Field(default="")
    enabled: bool = Field(default=True)
    priority: int = Field(default=10, ge=1, le=999)

    @field_validator("base_url")
    @classmethod
    def validate_safe_base_url(cls, value: str) -> str:
        """Enforce HTTPS for remote endpoints and block SSRF / link-local targets (CWE-918/319)."""
        clean = value.strip()
        if not clean:
            return ""
        parsed = urlparse(clean)
        if parsed.scheme not in ("http", "https") or not parsed.hostname:
            raise ValueError("base_url debe comenzar con https:// o http:// y tener un host válido.")
        if parsed.username or parsed.password:
            raise ValueError("base_url no debe contener credenciales embebidas.")
        host = parsed.hostname.lower()
        if host in _BLOCKED_HOSTS or host.startswith("169.254."):
            raise ValueError(f"Host bloqueado por seguridad (SSRF protection): {host}")
        if parsed.scheme == "http" and host not in _LOOPBACK_HOSTS:
            raise ValueError(
                f"Endpoints remotos ({host}) requieren https:// para proteger tu API Key."
            )
        return clean.rstrip("/")

    def resolved_base_url(self) -> str:
        if self.base_url.strip():
            return self.base_url.strip().rstrip("/")
        return PROVIDER_PRESETS[self.provider].default_base_url.rstrip("/")

    def is_ready(self) -> bool:
        """Return True if enabled and has an API key when required by the provider."""
        if not self.enabled:
            return False
        preset = PROVIDER_PRESETS[self.provider]
        if not preset.requires_api_key:
            return True
        clean = self.api_key.strip()
        return bool(clean) and clean != "your_api_key_here"

    def masked_key(self) -> str:
        """Return a safe redacted preview of the API key for GUI display."""
        clean = self.api_key.strip()
        if not clean:
            return "(sin key)"
        if len(clean) <= 8:
            return "****"
        return f"{clean[:4]}...{clean[-4:]}"


_PERSISTED_SETTING_KEYS = (
    "text_language",
    "voice_language",
    "enable_voice",
    "enable_overlay",
    "enable_hotkeys",
    "tts_engine",
    "tts_voice",
    "tts_rate",
    "tts_volume",
    "overlay_duration_seconds",
    "overlay_position",
    "overlay_width",
    "overlay_opacity",
    "overlay_theme",
    "overlay_font_family",
    "overlay_font_size",
    "capture_monitor",
    "min_inference_interval_seconds",
    "image_compression_quality",
    "image_format",
    "token_efficiency_mode",
    "low_process_priority",
    "enable_file_logging",
    "log_level",
)


def load_accounts(settings: AppSettings) -> list[ProviderAccount]:
    """Load configured accounts from `accounts_file`, seeding from `.env` if absent."""
    path = settings.accounts_file
    accounts: list[ProviderAccount] = []

    if path.is_file():
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(raw, list):
                for item in raw:
                    try:
                        accounts.append(ProviderAccount.model_validate(item))
                    except ValidationError as exc:
                        logger.warning("Cuenta IA inválida ignorada en %s: %s", path, exc)
        except (OSError, json.JSONDecodeError) as exc:
            logger.warning("No se pudo leer %s: %s", path, exc)

    # Backward-compatible automatic seed from .env GEMINI_API_KEY
    if not accounts and settings.has_valid_api_key():
        accounts.append(
            ProviderAccount(
                id="env-gemini",
                label="Gemini (.env)",
                provider="gemini",
                api_key=settings.get_api_key_value(),
                model=settings.default_model,
                enabled=True,
                priority=1,
            )
        )

    accounts.sort(key=lambda a: (a.priority, a.label.lower()))
    return accounts


def _atomic_write_json(path: Path, data: Any, *, restrict_permissions: bool = False) -> None:
    """Atomically write JSON via a temp file and optionally apply owner-only 0o600 permissions."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    serialized = json.dumps(data, indent=2, ensure_ascii=False)
    tmp_path.write_text(serialized, encoding="utf-8")
    if restrict_permissions:
        with contextlib.suppress(OSError):
            os.chmod(tmp_path, stat.S_IRUSR | stat.S_IWUSR)
    os.replace(tmp_path, path)
    if restrict_permissions:
        with contextlib.suppress(OSError):
            os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)


def save_accounts(path: Path, accounts: list[ProviderAccount]) -> None:
    """Persist provider accounts locally (gitignored, atomic write + 0o600 permissions)."""
    ordered = sorted(accounts, key=lambda a: (a.priority, a.label.lower()))
    payload = [acc.model_dump() for acc in ordered]
    _atomic_write_json(path, payload, restrict_permissions=True)


def load_effective_settings() -> AppSettings:
    """Load AppSettings from `.env` and apply any saved local GUI overrides."""
    base = AppSettings()
    user_file = base.user_config_file
    if not user_file.is_file():
        return base

    try:
        raw: dict[str, Any] = json.loads(user_file.read_text(encoding="utf-8"))
        if not isinstance(raw, dict):
            return base
        filtered = {k: v for k, v in raw.items() if k in _PERSISTED_SETTING_KEYS}
        merged = base.model_dump()
        merged.update(filtered)
        return AppSettings.model_validate(merged)
    except (OSError, json.JSONDecodeError, ValidationError) as exc:
        logger.warning("No se pudo aplicar %s (%s); usando configuración base.", user_file, exc)
        return base


def save_user_settings(settings: AppSettings) -> None:
    """Persist non-secret GUI preferences atomically to `user_config.json`."""
    path = settings.user_config_file
    data = {key: getattr(settings, key) for key in _PERSISTED_SETTING_KEYS}
    _atomic_write_json(path, data, restrict_permissions=False)
