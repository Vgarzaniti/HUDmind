"""Multi-provider AI Strategy adapters and automatic quota/failover router.

Supports stacking multiple accounts across Gemini, OpenAI (ChatGPT), Anthropic Claude,
Groq (free tier), OpenRouter (:free), Pollinations (free), Ollama Local (100% free,
no login), and any custom OpenAI-compatible server. Automatically rotates to the next
available account on HTTP 429 (rate limit / quota exhausted), 402 (credits), 401/403,
or transient server errors.
"""

from __future__ import annotations

import base64
import contextlib
import json
import logging
import re
import time
from abc import ABC, abstractmethod
from collections.abc import Callable
from dataclasses import dataclass

import aiohttp
from pydantic import SecretStr, ValidationError

from src.ai.gemini_client import GeminiCoachClient, build_language_instruction
from src.config.settings import AppSettings
from src.config.user_store import ProviderAccount
from src.pipeline.schemas import CoachResponse
from src.profiles.loader import GameProfile

logger = logging.getLogger(__name__)

_JSON_BLOCK_RE = re.compile(r"\{.*\}", re.DOTALL)

_JSON_SCHEMA_REMINDER = (
    'Respond ONLY with valid JSON matching exactly: '
    '{"should_intervene": bool, "urgency": "low"|"medium"|"high", '
    '"overlay_text": "max 8 words or empty", "voice_line": "max 12 words or empty"}. '
    "No markdown fences, no extra prose."
)


@dataclass(slots=True)
class ProviderCallResult:
    """Outcome of a single provider inference call."""

    response: CoachResponse | None = None
    error_code: int | None = None
    error_message: str = ""
    quota_or_auth_failure: bool = False


def extract_coach_response_from_text(raw_text: str) -> CoachResponse | None:
    """Parse JSON (even if wrapped in markdown code fences) into CoachResponse."""
    text = raw_text.strip()
    if not text:
        return None
    if text.startswith("```"):
        match = _JSON_BLOCK_RE.search(text)
        if match:
            text = match.group(0)
    try:
        return CoachResponse.model_validate_json(text)
    except ValidationError:
        match = _JSON_BLOCK_RE.search(text)
        if match:
            with contextlib.suppress(ValidationError, json.JSONDecodeError):
                return CoachResponse.model_validate_json(match.group(0))
        logger.warning("Respuesta JSON no válida del proveedor: %r", raw_text[:200])
        return None


def _compose_user_prompt(
    *,
    profile: GameProfile,
    recent_history: str,
    force_advice: bool,
    text_language: str,
    voice_language: str,
    include_json_reminder: bool = True,
) -> str:
    parts = [
        f"# Knowledge Base: {profile.manifest.display_name}\n\n{profile.knowledge_base}",
        "Analizá este frame actual del juego usando los anclajes del HUD y las reglas provistas.",
        build_language_instruction(text_language, voice_language),
    ]
    if force_advice:
        parts.append(
            "MANUAL PLAYER REQUEST (F10): the player explicitly asked for an immediate "
            "tactical tip on this frame. Deliver the best actionable tip now."
        )
    if recent_history.strip():
        parts.append(recent_history.strip())
    if include_json_reminder:
        parts.append(_JSON_SCHEMA_REMINDER)
    return "\n\n".join(parts)


class BaseAIProvider(ABC):
    """Strategy interface for pluggable multimodal LLM providers."""

    def __init__(self, account: ProviderAccount, settings: AppSettings) -> None:
        self.account = account
        self._settings = settings

    async def prepare_session(self, profile: GameProfile) -> bool:  # noqa: B027
        """Optional session warm-up (e.g. Gemini Context Caching). Returns True if cached."""
        return False

    @abstractmethod
    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
        text_language: str = "es",
        voice_language: str = "es",
    ) -> ProviderCallResult:
        """Run multimodal vision inference on a single compressed frame."""

    @abstractmethod
    async def test_connection(self) -> tuple[bool, str]:
        """Lightweight connectivity & credential check for the GUI."""

    async def close_session(self) -> None:  # noqa: B027
        """Release remote or HTTP resources cleanly."""


class GeminiProviderAdapter(BaseAIProvider):
    """Adapter wrapping GeminiCoachClient with Context Caching & Structured Outputs."""

    def __init__(self, account: ProviderAccount, settings: AppSettings) -> None:
        super().__init__(account, settings)
        derived = settings.model_copy(
            update={
                "gemini_api_key": SecretStr(account.api_key.strip()),
                "default_model": account.model,
            }
        )
        # Single fast retry inside failover pool so rotation to backup accounts stays snappy
        self._client = GeminiCoachClient(derived, max_retries=0)

    async def prepare_session(self, profile: GameProfile) -> bool:
        return await self._client.prepare_session(profile)

    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
        text_language: str = "es",
        voice_language: str = "es",
    ) -> ProviderCallResult:
        resp = await self._client.analyze_frame(
            image_bytes=image_bytes,
            mime_type=mime_type,
            profile=profile,
            recent_history=recent_history,
            force_advice=force_advice,
            text_language=text_language,
            voice_language=voice_language,
        )
        if resp is not None:
            return ProviderCallResult(response=resp)
        code = self._client.last_error_code
        return ProviderCallResult(
            response=None,
            error_code=code,
            error_message=self._client.last_error_message,
            quota_or_auth_failure=code in (401, 402, 403, 429),
        )

    async def test_connection(self) -> tuple[bool, str]:
        if not self.account.api_key.strip():
            return False, "Falta API Key de Gemini."
        try:
            url = (
                f"https://generativelanguage.googleapis.com/v1beta/models/"
                f"{self.account.model}?key={self.account.api_key.strip()}"
            )
            timeout = aiohttp.ClientTimeout(total=8)
            async with aiohttp.ClientSession(timeout=timeout) as session, session.get(url) as r:
                if r.status == 200:
                    return True, f"OK — Modelo '{self.account.model}' verificado."
                return False, f"HTTP {r.status} al verificar API key / modelo."
        except Exception as exc:
            return False, f"Error de red: {exc}"

    async def close_session(self) -> None:
        await self._client.close_session()


class OpenAICompatibleProvider(BaseAIProvider):
    """Unified async vision client for OpenAI, Groq, OpenRouter, Pollinations, Ollama, & Custom."""

    def __init__(self, account: ProviderAccount, settings: AppSettings) -> None:
        super().__init__(account, settings)
        self._http: aiohttp.ClientSession | None = None

    def _get_session(self) -> aiohttp.ClientSession:
        if self._http is None or self._http.closed:
            timeout = aiohttp.ClientTimeout(total=self._settings.gemini_timeout_seconds)
            self._http = aiohttp.ClientSession(timeout=timeout)
        return self._http

    def _build_headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json", "User-Agent": "HUDMind/0.4"}
        key = self.account.api_key.strip()
        if key:
            headers["Authorization"] = f"Bearer {key}"
        if self.account.provider == "openrouter":
            headers["HTTP-Referer"] = "https://github.com/hudmind"
            headers["X-Title"] = "HUDMind"
        return headers

    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
        text_language: str = "es",
        voice_language: str = "es",
    ) -> ProviderCallResult:
        base_url = self.account.resolved_base_url()
        if not base_url:
            return ProviderCallResult(error_code=400, error_message="Base URL vacía")

        b64 = base64.b64encode(image_bytes).decode("ascii")
        user_prompt = _compose_user_prompt(
            profile=profile,
            recent_history=recent_history,
            force_advice=force_advice,
            text_language=text_language,
            voice_language=voice_language,
        )

        max_tokens = {"eco": 120, "balanced": 160, "high": 220}.get(
            self._settings.token_efficiency_mode, 160
        )
        payload: dict[str, object] = {
            "model": self.account.model,
            "temperature": self._settings.gemini_temperature,
            "max_tokens": max_tokens,
            "messages": [
                {"role": "system", "content": profile.system_prompt},
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": user_prompt},
                        {"type": "image_url", "image_url": {"url": f"data:{mime_type};base64,{b64}"}},
                    ],
                },
            ],
        }
        if self.account.provider in ("openai", "groq"):
            payload["response_format"] = {"type": "json_object"}

        endpoint = f"{base_url}/chat/completions"
        try:
            session = self._get_session()
            async with session.post(endpoint, headers=self._build_headers(), json=payload) as resp:
                if resp.status != 200:
                    err_body = (await resp.text())[:220]
                    return ProviderCallResult(
                        error_code=resp.status,
                        error_message=err_body,
                        quota_or_auth_failure=resp.status in (401, 402, 403, 429),
                    )
                data = await resp.json()
                content = data["choices"][0]["message"]["content"]
                parsed = extract_coach_response_from_text(str(content))
                if parsed is None:
                    return ProviderCallResult(error_code=422, error_message="JSON inválido")
                return ProviderCallResult(response=parsed)
        except TimeoutError:
            return ProviderCallResult(error_code=408, error_message="timeout")
        except Exception as exc:
            return ProviderCallResult(error_code=503, error_message=str(exc))

    async def test_connection(self) -> tuple[bool, str]:
        base_url = self.account.resolved_base_url()
        if not base_url:
            return False, "Falta Base URL."
        try:
            timeout = aiohttp.ClientTimeout(total=8)
            async with (
                aiohttp.ClientSession(timeout=timeout) as session,
                session.get(f"{base_url}/models", headers=self._build_headers()) as r,
            ):
                if r.status == 200:
                    return True, f"OK — Endpoint {base_url} accesible."
                return False, f"HTTP {r.status} en {base_url}/models."
        except Exception as exc:
            return False, f"No se pudo conectar a {base_url}: {exc}"

    async def close_session(self) -> None:
        if self._http is not None and not self._http.closed:
            await self._http.close()
            self._http = None


class AnthropicClaudeProvider(BaseAIProvider):
    """Async vision adapter for Anthropic Claude Messages API."""

    def __init__(self, account: ProviderAccount, settings: AppSettings) -> None:
        super().__init__(account, settings)
        self._http: aiohttp.ClientSession | None = None

    def _get_session(self) -> aiohttp.ClientSession:
        if self._http is None or self._http.closed:
            timeout = aiohttp.ClientTimeout(total=self._settings.gemini_timeout_seconds)
            self._http = aiohttp.ClientSession(timeout=timeout)
        return self._http

    def _build_headers(self) -> dict[str, str]:
        return {
            "Content-Type": "application/json",
            "x-api-key": self.account.api_key.strip(),
            "anthropic-version": "2023-06-01",
        }

    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
        text_language: str = "es",
        voice_language: str = "es",
    ) -> ProviderCallResult:
        base_url = self.account.resolved_base_url() or "https://api.anthropic.com/v1"
        b64 = base64.b64encode(image_bytes).decode("ascii")
        user_prompt = _compose_user_prompt(
            profile=profile,
            recent_history=recent_history,
            force_advice=force_advice,
            text_language=text_language,
            voice_language=voice_language,
        )
        max_tokens = {"eco": 120, "balanced": 160, "high": 220}.get(
            self._settings.token_efficiency_mode, 160
        )
        payload = {
            "model": self.account.model,
            "max_tokens": max_tokens,
            "temperature": self._settings.gemini_temperature,
            "system": profile.system_prompt,
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "image",
                            "source": {
                                "type": "base64",
                                "media_type": mime_type,
                                "data": b64,
                            },
                        },
                        {"type": "text", "text": user_prompt},
                    ],
                }
            ],
        }
        try:
            session = self._get_session()
            async with session.post(
                f"{base_url}/messages",
                headers=self._build_headers(),
                json=payload,
            ) as resp:
                if resp.status != 200:
                    err_body = (await resp.text())[:220]
                    return ProviderCallResult(
                        error_code=resp.status,
                        error_message=err_body,
                        quota_or_auth_failure=resp.status in (401, 402, 403, 429),
                    )
                data = await resp.json()
                blocks = data.get("content", [])
                text = "".join(b.get("text", "") for b in blocks if b.get("type") == "text")
                parsed = extract_coach_response_from_text(text)
                if parsed is None:
                    return ProviderCallResult(error_code=422, error_message="JSON inválido")
                return ProviderCallResult(response=parsed)
        except TimeoutError:
            return ProviderCallResult(error_code=408, error_message="timeout")
        except Exception as exc:
            return ProviderCallResult(error_code=503, error_message=str(exc))

    async def test_connection(self) -> tuple[bool, str]:
        if not self.account.api_key.strip():
            return False, "Falta API Key de Anthropic Claude."
        base_url = self.account.resolved_base_url() or "https://api.anthropic.com/v1"
        try:
            timeout = aiohttp.ClientTimeout(total=8)
            async with (
                aiohttp.ClientSession(timeout=timeout) as session,
                session.get(f"{base_url}/models", headers=self._build_headers()) as r,
            ):
                if r.status == 200:
                    return True, "OK — API Key de Anthropic verificada."
                return False, f"HTTP {r.status} en Anthropic API."
        except Exception as exc:
            return False, f"Error de conexión: {exc}"

    async def close_session(self) -> None:
        if self._http is not None and not self._http.closed:
            await self._http.close()
            self._http = None


def create_provider_adapter(account: ProviderAccount, settings: AppSettings) -> BaseAIProvider:
    """Factory instantiating the right Strategy adapter for a ProviderAccount."""
    if account.provider == "gemini":
        return GeminiProviderAdapter(account, settings)
    if account.provider == "claude":
        return AnthropicClaudeProvider(account, settings)
    return OpenAICompatibleProvider(account, settings)


class ProviderFailoverRouter:
    """Automatic multi-account failover & quota rotation coordinator.

    Tries ready accounts in ascending priority order. When an account exhausts
    tokens/quota (HTTP 429 / 402), fails authentication (401/403), or errors out,
    it is placed into cooldown and the router immediately fails over to the next
    configured provider on the very same frame.
    """

    def __init__(
        self,
        accounts: list[ProviderAccount],
        settings: AppSettings,
        *,
        on_failover: Callable[[str, str], None] | None = None,
        provider_factory: Callable[[ProviderAccount, AppSettings], BaseAIProvider] | None = None,
    ) -> None:
        self._settings = settings
        self._on_failover = on_failover
        factory = provider_factory or create_provider_adapter

        ready_accounts = [a for a in accounts if a.is_ready()]
        ready_accounts.sort(key=lambda a: (a.priority, a.label.lower()))

        self._providers: list[BaseAIProvider] = [factory(acc, settings) for acc in ready_accounts]
        self._cooldown_until: dict[str, float] = {}
        self._prepared_ids: set[str] = set()
        self._active_profile: GameProfile | None = None
        self._active_index: int = 0

    @property
    def account_count(self) -> int:
        return len(self._providers)

    @property
    def active_account_label(self) -> str:
        if not self._providers:
            return "(sin cuentas activas)"
        idx = min(self._active_index, len(self._providers) - 1)
        acc = self._providers[idx].account
        return f"{acc.label} [{acc.provider}:{acc.model}]"

    def get_all_secrets(self) -> tuple[str, ...]:
        """Return all configured API keys for ISO 27001 log redaction."""
        return tuple(
            p.account.api_key.strip() for p in self._providers if p.account.api_key.strip()
        )

    async def prepare_session(self, profile: GameProfile) -> bool:
        """Warm up the primary provider (e.g. Gemini Context Caching)."""
        self._active_profile = profile
        self._prepared_ids.clear()
        if not self._providers:
            logger.error("No hay cuentas IA habilitadas en el pool de proveedores.")
            return False

        primary = self._providers[0]
        cached = await primary.prepare_session(profile)
        self._prepared_ids.add(primary.account.id)
        self._active_index = 0
        logger.info("Proveedor IA principal activo: %s", self.active_account_label)
        return cached

    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
        text_language: str | None = None,
        voice_language: str | None = None,
    ) -> CoachResponse | None:
        """Analyze a frame with seamless automatic failover across configured accounts."""
        if not self._providers:
            return None

        t_lang = text_language or self._settings.text_language
        v_lang = voice_language or self._settings.voice_language
        now = time.monotonic()

        # Build candidate order starting from current healthy priority
        candidates: list[tuple[int, BaseAIProvider]] = [
            (idx, prov)
            for idx, prov in enumerate(self._providers)
            if now >= self._cooldown_until.get(prov.account.id, 0.0)
        ]

        # If every account is cooled down, try the one whose cooldown expires earliest
        if not candidates:
            earliest_idx = min(
                range(len(self._providers)),
                key=lambda i: self._cooldown_until.get(self._providers[i].account.id, 0.0),
            )
            candidates = [(earliest_idx, self._providers[earliest_idx])]

        for idx, prov in candidates:
            acc = prov.account
            if acc.id not in self._prepared_ids and self._active_profile is not None:
                await prov.prepare_session(self._active_profile)
                self._prepared_ids.add(acc.id)

            result = await prov.analyze_frame(
                image_bytes=image_bytes,
                mime_type=mime_type,
                profile=profile,
                recent_history=recent_history,
                force_advice=force_advice,
                text_language=t_lang,
                voice_language=v_lang,
            )

            if result.response is not None:
                if idx != self._active_index:
                    prev_label = self._providers[self._active_index].account.label
                    self._active_index = idx
                    logger.info("Failover completado: '%s' -> '%s'", prev_label, acc.label)
                    if self._on_failover is not None:
                        self._on_failover(prev_label, acc.label)
                return result.response

            # Determine cooldown based on failure type
            code = result.error_code
            if code in (401, 402, 403):
                cooldown_s = 600.0
                reason = f"auth/créditos (HTTP {code})"
            elif code == 429:
                cooldown_s = 60.0
                reason = "cuota / rate limit (HTTP 429)"
            else:
                cooldown_s = 20.0
                reason = f"error (HTTP {code})"

            self._cooldown_until[acc.id] = time.monotonic() + cooldown_s
            logger.warning(
                "Proveedor '%s' falló por %s -> rotando al siguiente disponible (pausa %.0fs).",
                acc.label,
                reason,
                cooldown_s,
            )

        logger.error("Todos los proveedores IA del pool fallaron en este frame.")
        return None

    async def close_session(self) -> None:
        """Close all instantiated provider sessions cleanly."""
        for prov in self._providers:
            with contextlib.suppress(Exception):
                await prov.close_session()
        self._prepared_ids.clear()
