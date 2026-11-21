"""Gemini multimodal client with Context Caching, Circuit Breaker, and Structured Outputs."""

from __future__ import annotations

import asyncio
import logging
import random
import time
from typing import Any

from google import genai
from google.genai import errors as genai_errors
from google.genai import types
from pydantic import ValidationError

from src.config.settings import AppSettings
from src.pipeline.schemas import CoachResponse
from src.profiles.loader import GameProfile

logger = logging.getLogger(__name__)

_FRAME_ANALYSIS_PROMPT = (
    "Analizá este frame actual del juego usando las reglas y anclajes del HUD provistos. "
    "Respondé estrictamente en JSON según el esquema CoachResponse. "
    "Si no hay una intervención táctica clara y urgente/útil, devolvé should_intervene=false."
)


def build_language_instruction(text_language: str = "es", voice_language: str = "es") -> str:
    """Build explicit language directive supporting independent Spanish/English text & voice."""
    lang_names = {"es": "Spanish (español natural y conciso)", "en": "English (concise, natural)"}
    t_name = lang_names.get(text_language, lang_names["es"])
    v_name = lang_names.get(voice_language, lang_names["es"])
    if text_language == voice_language:
        return f"LANGUAGE RULE: Write both overlay_text and voice_line strictly in {t_name}."
    return (
        f"LANGUAGE RULE: Write overlay_text strictly in {t_name}, "
        f"and write voice_line strictly in {v_name}."
    )


class GeminiCoachClient:
    """Manages Gemini Context Caching sessions, fault tolerance, and multimodal inference."""

    def __init__(
        self,
        settings: AppSettings,
        max_retries: int = 2,
        client: Any | None = None,
    ) -> None:
        self._settings = settings
        self._max_retries = max_retries
        self._model = settings.default_model
        self._client: Any = (
            client if client is not None else genai.Client(api_key=settings.get_api_key_value())
        )

        self._active_profile: GameProfile | None = None
        self._cache_name: str | None = None
        self._cache_expires_at: float = 0.0
        self._using_cache: bool = False
        self._session_lock = asyncio.Lock()

        # Circuit Breaker state (ISO/IEC 25010 Reliability / Fault Tolerance)
        self._consecutive_failures: int = 0
        self._circuit_open_until: float = 0.0
        self._auth_failed: bool = False
        self.last_error_code: int | None = None
        self.last_error_message: str = ""

    @property
    def is_using_cache(self) -> bool:
        return self._using_cache and self._cache_name is not None

    @property
    def active_cache_name(self) -> str | None:
        return self._cache_name

    @property
    def is_circuit_open(self) -> bool:
        if self._auth_failed:
            return True
        return time.monotonic() < self._circuit_open_until

    def _record_success(self) -> None:
        self._consecutive_failures = 0
        self._circuit_open_until = 0.0

    def _record_failure(self, fatal_auth: bool = False) -> None:
        if fatal_auth:
            self._auth_failed = True
            logger.error(
                "Error de autenticación (HTTP 401/403). Circuit Breaker abierto permanentemente: "
                "verificá GEMINI_API_KEY en tu archivo .env."
            )
            return

        self._consecutive_failures += 1
        if self._consecutive_failures >= self._settings.circuit_breaker_threshold:
            cooldown = self._settings.circuit_breaker_cooldown_seconds
            self._circuit_open_until = time.monotonic() + cooldown
            logger.warning(
                "Circuit Breaker ABIERTO tras %d fallos consecutivos; pausando llamadas %.0fs.",
                self._consecutive_failures,
                cooldown,
            )

    async def prepare_session(self, profile: GameProfile) -> bool:
        """Initialize a game session and attempt to create a Gemini Context Cache.

        Automatically falls back to standard inline-context mode if the knowledge
        base is smaller than the minimum token threshold required by the Gemini
        Context Caching API (typically 1,024–4,096 tokens depending on model).

        Returns:
            True if Context Caching was activated, False if running in fallback mode.
        """
        async with self._session_lock:
            await self._delete_active_cache_unlocked()
            self._active_profile = profile
            self._using_cache = False

            ttl_seconds = self._settings.cache_ttl_seconds
            display_name = f"copilot-{profile.manifest.game_id}-{int(time.time())}"

            logger.info(
                "Inicializando sesión Gemini para '%s' (modelo=%s, ~%d tokens estimados)...",
                profile.manifest.display_name,
                self._model,
                profile.estimated_tokens,
            )

            try:
                cached = await asyncio.wait_for(
                    self._client.aio.caches.create(
                        model=self._model,
                        config=types.CreateCachedContentConfig(
                            display_name=display_name,
                            system_instruction=profile.system_prompt,
                            contents=[
                                types.Content(
                                    role="user",
                                    parts=[
                                        types.Part.from_text(
                                            text=(
                                                f"# Knowledge Base: {profile.manifest.display_name}\n\n"
                                                f"{profile.knowledge_base}"
                                            )
                                        )
                                    ],
                                )
                            ],
                            ttl=f"{ttl_seconds}s",
                        ),
                    ),
                    timeout=self._settings.gemini_timeout_seconds,
                )
                self._cache_name = cached.name
                self._cache_expires_at = time.monotonic() + ttl_seconds
                self._using_cache = True
                logger.info(
                    "Context Cache creado con éxito: %s (TTL=%ds) [Zero Search Latency]",
                    self._cache_name,
                    ttl_seconds,
                )
                return True

            except genai_errors.APIError as exc:
                status = getattr(exc, "code", None)
                if status in (401, 403):
                    self._record_failure(fatal_auth=True)
                    return False

                logger.warning(
                    "Context Caching no disponible para '%s' (HTTP %s: %s). "
                    "Activando fallback automático a modo estándar inline.",
                    profile.manifest.game_id,
                    status,
                    exc.message if hasattr(exc, "message") else str(exc),
                )
                self._cache_name = None
                self._using_cache = False
                return False

            except (TimeoutError, Exception) as exc:
                logger.warning(
                    "No se pudo crear Context Cache (%s). Continuando en modo estándar inline.",
                    exc,
                )
                self._cache_name = None
                self._using_cache = False
                return False

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
    ) -> CoachResponse | None:
        """Analyze a single compressed game frame and return a structured CoachResponse."""
        self.last_error_code = None
        self.last_error_message = ""
        if self.is_circuit_open:
            logger.debug("Circuit Breaker abierto; omitiendo llamada a Gemini.")
            return None

        # Renew cache proactively if close to TTL expiration
        if (
            self._using_cache
            and self._cache_expires_at > 0
            and time.monotonic() >= (self._cache_expires_at - 60.0)
        ):
            logger.info("Context Cache próximo a expirar; renovando sesión...")
            await self.prepare_session(profile)

        for attempt in range(self._max_retries + 1):
            try:
                response = await asyncio.wait_for(
                    self._invoke_model(
                        image_bytes,
                        mime_type,
                        profile,
                        recent_history=recent_history,
                        force_advice=force_advice,
                        text_language=text_language,
                        voice_language=voice_language,
                    ),
                    timeout=self._settings.gemini_timeout_seconds,
                )
                parsed = self._parse_response(response)
                self._record_success()
                return parsed

            except genai_errors.APIError as exc:
                code = getattr(exc, "code", None)
                self.last_error_code = int(code) if isinstance(code, int) else None
                self.last_error_message = str(exc)

                if code in (401, 403):
                    self._record_failure(fatal_auth=True)
                    return None

                # If cached content expired or became invalid mid-session, drop cache and retry inline
                if self._using_cache and code in (400, 404):
                    logger.warning(
                        "Context Cache inválido/expirado (HTTP %s). Cambiando a fallback inline.",
                        code,
                    )
                    self._using_cache = False
                    self._cache_name = None
                    continue

                # Transient rate limits / server overload -> bounded exponential backoff
                if code in (429, 500, 503, 504) and attempt < self._max_retries:
                    delay = (2**attempt) + random.uniform(0.2, 0.7)
                    logger.warning(
                        "Gemini API HTTP %s (intento %d/%d). Reintentando en %.1fs...",
                        code,
                        attempt + 1,
                        self._max_retries + 1,
                        delay,
                    )
                    await asyncio.sleep(delay)
                    continue

                logger.error("Error de Gemini API (HTTP %s): %s", code, exc)
                self._record_failure()
                return None

            except TimeoutError:
                self.last_error_code = 408
                self.last_error_message = "timeout"
                if attempt < self._max_retries:
                    logger.warning(
                        "Timeout llamando a Gemini (%.1fs, intento %d/%d).",
                        self._settings.gemini_timeout_seconds,
                        attempt + 1,
                        self._max_retries + 1,
                    )
                    continue
                logger.error("Timeout persistente en inferencia Gemini; frame descartado.")
                self._record_failure()
                return None

            except asyncio.CancelledError:
                raise

            except Exception as exc:
                self.last_error_code = 500
                self.last_error_message = str(exc)
                logger.error("Error inesperado en inferencia Gemini: %s", exc)
                self._record_failure()
                return None

        return None

    async def _invoke_model(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
        text_language: str = "es",
        voice_language: str = "es",
    ) -> Any:
        """Execute multimodal generate_content using either Context Cache or inline fallback."""
        image_part = types.Part.from_bytes(data=image_bytes, mime_type=mime_type)

        prompt_parts = [
            _FRAME_ANALYSIS_PROMPT,
            build_language_instruction(text_language, voice_language),
        ]
        if force_advice:
            prompt_parts.append(
                "SOLICITUD MANUAL DEL JUGADOR (F10): el jugador pidió una lectura táctica "
                "inmediata de este frame. Entregá el mejor consejo concreto disponible."
            )
        if recent_history.strip():
            prompt_parts.append(recent_history.strip())
        prompt_text = "\n\n".join(prompt_parts)

        max_out = {"eco": 120, "balanced": 160, "high": 220}.get(
            self._settings.token_efficiency_mode, 160
        )
        # Disable hidden internal thinking tokens on Gemini 2.5 Flash for sub-second latency
        # and ~70-85% lower output token consumption on real-time coaching frames.
        thinking_cfg = (
            types.ThinkingConfig(thinking_budget=0) if "2.5" in self._model.lower() else None
        )

        if self._using_cache and self._cache_name:
            # IMPORTANT: Do NOT pass system_instruction when cached_content is set
            config = types.GenerateContentConfig(
                cached_content=self._cache_name,
                temperature=self._settings.gemini_temperature,
                max_output_tokens=max_out,
                thinking_config=thinking_cfg,
                response_mime_type="application/json",
                response_schema=CoachResponse,
            )
            contents: list[types.Part | str] = [
                image_part,
                prompt_text,
            ]
        else:
            # Standard fallback mode: send system prompt + knowledge base inline
            config = types.GenerateContentConfig(
                system_instruction=profile.system_prompt,
                temperature=self._settings.gemini_temperature,
                max_output_tokens=max_out,
                thinking_config=thinking_cfg,
                response_mime_type="application/json",
                response_schema=CoachResponse,
            )
            contents = [
                f"# Knowledge Base: {profile.manifest.display_name}\n\n{profile.knowledge_base}",
                image_part,
                prompt_text,
            ]

        return await self._client.aio.models.generate_content(
            model=self._model,
            contents=contents,
            config=config,
        )

    @staticmethod
    def _parse_response(response: Any) -> CoachResponse | None:
        """Validate structured JSON output into a CoachResponse model."""
        parsed = getattr(response, "parsed", None)
        if isinstance(parsed, CoachResponse):
            return parsed

        try:
            raw_text = (getattr(response, "text", None) or "").strip()
        except ValueError as exc:
            logger.warning("Gemini bloqueó o devolvió respuesta sin texto: %s", exc)
            return None

        if not raw_text:
            logger.warning("Gemini devolvió una respuesta vacía.")
            return None

        try:
            return CoachResponse.model_validate_json(raw_text)
        except ValidationError as exc:
            logger.warning(
                "Respuesta JSON no válida respecto a CoachResponse: %s | raw=%r",
                exc,
                raw_text,
            )
            return None

    async def close_session(self) -> None:
        """Delete active remote Context Cache cleanly."""
        async with self._session_lock:
            await self._delete_active_cache_unlocked()
            self._active_profile = None

    async def _delete_active_cache_unlocked(self) -> None:
        if not self._cache_name:
            return
        cache_to_delete = self._cache_name
        self._cache_name = None
        self._using_cache = False
        self._cache_expires_at = 0.0
        try:
            await asyncio.wait_for(
                self._client.aio.caches.delete(name=cache_to_delete),
                timeout=8.0,
            )
            logger.info("Context Cache eliminado correctamente: %s", cache_to_delete)
        except Exception as exc:
            logger.debug("No se pudo eliminar Context Cache '%s': %s", cache_to_delete, exc)
