"""Gemini multimodal client with Context Caching, Circuit Breaker, and Structured Outputs."""

from __future__ import annotations

import asyncio
import base64
import contextlib
import json
import logging
import random
import time
from typing import Any
from urllib.parse import quote

import aiohttp
from google import genai
from google.genai import errors as genai_errors
from google.genai import types
from pydantic import BaseModel, ValidationError

from src.config.settings import AppSettings
from src.pipeline.schemas import CoachResponse, UrgencyLevel
from src.profiles.loader import GameProfile

logger = logging.getLogger(__name__)


class _CoachWireSchema(BaseModel):
    """Compact wire schema without verbose field descriptions (~95 fewer uncached tokens/frame)."""

    should_intervene: bool
    urgency: UrgencyLevel
    overlay_text: str
    voice_line: str
    match_context: str = ""


def build_universal_game_context_instruction() -> str:
    """Universal cross-game visual context + high-value real-time eSports coach protocol."""
    return (
        "REAL ESPORTS COACH PERSONA & MOTION-AWARE PROTOCOL (applies to ANY game): "
        "1) HIGH-VALUE MACRO & PATTERN COACHING (NEVER LATE MICRO-REFLEXES): Because audio takes ~1s to hear, "
        "NEVER give split-second twitch commands that expire in 0.5s. Instead, coach positioning, rotation, "
        "boost/resource economy, spacing with teammates, opponent habits, and clock/score strategy that stay "
        "actionable for the next 10-30 seconds and genuinely help the player win. "
        "2) PROVE YOU ARE WATCHING LIVE: When 2 frames are attached (Frame 1 = ~1s ago, Frame 2 = NOW), read "
        "actual motion, trajectory, and HUD changes between them. Mention a concrete live detail you just saw "
        "(exact boost/HP/score/timer, or where a teammate/rival just moved) naturally in your callout. "
        "3) HUMAN DISCORD COACH TONE: Improvise concise, natural, energetic Spanish/English coaching (no textbook "
        "wording, no stock phrases). Vary tone: encouragement, urgent warning, clock reading, or positioning tweak. "
        "4) MATCH MEMORY (`match_context`) & ANTI-REPETITION: Always fill `match_context` (max 16 words: score/clock, "
        "teammates, rivals, HUD). Never repeat any phrase or topic from recent history."
    )

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


def build_coach_mode_instruction(coach_mode: str = "auto") -> str:
    """Return mode instructions for auto-detection, competitive match, or practice drills."""
    if coach_mode == "training":
        return (
            "TRAINING / PRACTICE MODE ACTIVE (coach_mode=training): "
            "Do NOT ignore warmup, Free Play, The Range, Aim Botz, Art of War, or practice servers. "
            "Act as a real, encouraging, hands-on mechanical & macro drill coach right beside the player."
        )
    if coach_mode == "auto":
        return (
            "AUTO-MODE DETECTION ACTIVE: First check whether the player is in a MAIN MENU / LOBBY "
            "(if so, return should_intervene=false), in TRAINING / FREE PLAY / PRACTICE (no match timer/score, "
            "practice arena -> coach mechanics naturally like a trainer), or in a COMPETITIVE MATCH "
            "(timer, scoreboard, rivals -> coach teammates, rotations, clock & score like a live head coach)."
        )
    return ""


def build_realtime_compact_prompt(
    profile: GameProfile,
    *,
    coach_mode: str = "auto",
    text_language: str = "es",
    voice_language: str = "es",
    recent_history: str = "",
    force_advice: bool = False,
) -> str:
    """Ultra-compact (<140 tokens) prompt for sub-second real-time live vision coaching."""
    lang_code = (voice_language or text_language or "es").lower()
    lang_label = "Spanish (español natural, directo, estilo coach en Discord)" if lang_code == "es" else "English (concise Discord coach)"
    if coach_mode == "training":
        mode_rule = "Mode=TRAINING: coach mechanics, positioning & consistency like a live trainer."
    elif coach_mode == "auto":
        mode_rule = "Auto-detect: if menu/lobby/loading -> NONE; if training/free play -> coach mechanics; if live match -> coach rotation, teammates, economy & clock."
    else:
        mode_rule = "Mode=MATCH: coach rotation, teammates, resources & score/clock. If menu -> NONE."

    parts = [
        f"Live human eSports coach watching {profile.manifest.display_name}. Language: {lang_label}.",
        mode_rule,
        "Give 1 improvised macro/positioning/economy callout useful for the next 10-25s (never late split-second reflexes, never textbook/robotic phrasing). Reference 1 real HUD/player detail you see.",
    ]
    if profile.custom_rules.strip():
        parts.append(f"Player rules: {profile.custom_rules.strip()[:260]}")
    if force_advice:
        parts.append("PLAYER PRESSED F10: give your best tactical read NOW (do not reply NONE).")
    if recent_history.strip():
        # Keep only the newest 4 lines so prompt stays sub-second fast
        hist_lines = [ln.strip() for ln in recent_history.strip().splitlines() if ln.strip()][-4:]
        if hist_lines:
            parts.append("Do NOT repeat: " + " ; ".join(hist_lines))
    parts.append(
        "Reply STRICTLY 1 line: H|<max 8 words> OR M|<max 8 words> OR L|<max 8 words> OR NONE."
    )
    return "\n".join(parts)


def parse_compact_coach_line(raw_text: str) -> CoachResponse | None:
    """Parse 1-line compact wire protocol (`U|callout|ctx` or `NONE`) into CoachResponse."""
    text = raw_text.strip()
    if not text:
        return None

    # Strip accidental markdown fences
    if text.startswith("```"):
        text = "\n".join(
            ln for ln in text.splitlines() if not ln.strip().startswith("```")
        ).strip()
    if not text:
        return None

    # JSON fallback if model replied with JSON object
    if text.startswith("{"):
        with contextlib.suppress(ValidationError, json.JSONDecodeError):
            return CoachResponse.model_validate_json(text)

    first_line = next((ln.strip() for ln in text.splitlines() if ln.strip()), "")
    if not first_line:
        return None

    upper = first_line.upper().strip(" .!\"'`")
    if upper in ("NONE", "NO", "SILENCE", "SKIP", "NULL"):
        return CoachResponse(should_intervene=False, urgency="low", overlay_text="", voice_line="")

    if "|" in first_line:
        segs = [s.strip().strip("\"'`") for s in first_line.split("|")]
        urg_map: dict[str, UrgencyLevel] = {
            "H": "high",
            "HIGH": "high",
            "ALTA": "high",
            "M": "medium",
            "MED": "medium",
            "MEDIUM": "medium",
            "MEDIA": "medium",
            "L": "low",
            "LOW": "low",
            "BAJA": "low",
        }
        urgency = urg_map.get(segs[0].upper())
        if urgency is not None and len(segs) >= 2:
            callout = segs[1]
            if not callout or callout.upper() == "NONE":
                return CoachResponse(
                    should_intervene=False, urgency="low", overlay_text="", voice_line=""
                )
            ctx = segs[2][:155] if len(segs) >= 3 else ""
            if len(callout) > 115:
                callout = callout[:112].rsplit(" ", 1)[0] + "..."
            return CoachResponse(
                should_intervene=True,
                urgency=urgency,
                overlay_text=callout,
                voice_line=callout,
                match_context=ctx,
            )

    # Graceful fallback: short direct single-line coach sentence
    cleaned = first_line.strip("\"'` ")
    if 4 <= len(cleaned) <= 135 and not cleaned.startswith("{"):
        short = cleaned if len(cleaned) <= 115 else cleaned[:112].rsplit(" ", 1)[0] + "..."
        return CoachResponse(
            should_intervene=True,
            urgency="medium",
            overlay_text=short,
            voice_line=cleaned[:175],
            match_context="",
        )
    return None


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
        self._fast_http: aiohttp.ClientSession | None = None

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

            # Sub-second Real-Time Fast Path: skip heavy remote Context Cache in eco/balanced
            # real-API mode and pre-warm persistent HTTP/TLS Keep-Alive socket instead!
            if isinstance(self._client, genai.Client) and (
                self._settings.token_efficiency_mode in ("eco", "balanced")
                or profile.estimated_tokens < 1024
            ):
                self._cache_name = None
                self._using_cache = False
                await self._prewarm_fast_http()
                logger.info(
                    "Sesión Gemini Ultra-Fast Keep-Alive lista para '%s' (modelo=%s, modo=%s).",
                    profile.manifest.game_id,
                    self._model,
                    self._settings.token_efficiency_mode,
                )
                return False

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
        previous_image_bytes: bytes | None = None,
    ) -> CoachResponse | None:
        """Analyze current (and optional previous) compressed game frame for motion-aware coaching."""
        if self.is_circuit_open:
            logger.debug("Circuit Breaker abierto; omitiendo llamada a Gemini.")
            return None

        self.last_error_code = None
        self.last_error_message = ""

        # Renew cache proactively if close to TTL expiration (proportional to TTL to avoid thrashing)
        lead_time = min(60.0, self._settings.cache_ttl_seconds * 0.2)
        if (
            self._using_cache
            and self._cache_expires_at > 0
            and time.monotonic() >= (self._cache_expires_at - lead_time)
        ):
            logger.info("Context Cache próximo a expirar; renovando sesión...")
            await self.prepare_session(profile)

        if self._should_use_fast_rest():
            return await self._analyze_frame_fast_rest(
                image_bytes=image_bytes,
                mime_type=mime_type,
                profile=profile,
                recent_history=recent_history,
                force_advice=force_advice,
                text_language=text_language,
                voice_language=voice_language,
            )

        inline_fallback_retried = False
        attempt = 0
        while attempt <= self._max_retries:
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
                        previous_image_bytes=previous_image_bytes,
                    ),
                    timeout=self._settings.gemini_timeout_seconds,
                )
                parsed = self._parse_response(response)
                if parsed is None:
                    self.last_error_code = 422
                    self.last_error_message = "Respuesta vacía o JSON inválido"
                else:
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
                # immediately without consuming a retry attempt (works even when max_retries=0).
                if self._using_cache and code in (400, 404) and not inline_fallback_retried:
                    logger.warning(
                        "Context Cache inválido/expirado (HTTP %s). Cambiando a fallback inline.",
                        code,
                    )
                    self._using_cache = False
                    self._cache_name = None
                    inline_fallback_retried = True
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
                    attempt += 1
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
                    attempt += 1
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
        previous_image_bytes: bytes | None = None,
    ) -> Any:
        """Execute multimodal generate_content using either Context Cache or inline fallback."""
        image_part = types.Part.from_bytes(data=image_bytes, mime_type=mime_type)
        image_parts: list[types.Part | str] = []
        if previous_image_bytes:
            prev_part = types.Part.from_bytes(data=previous_image_bytes, mime_type=mime_type)
            image_parts.extend([prev_part, image_part])
        else:
            image_parts.append(image_part)

        prompt_parts = [
            _FRAME_ANALYSIS_PROMPT,
            build_universal_game_context_instruction(),
            build_language_instruction(text_language, voice_language),
        ]
        if previous_image_bytes:
            prompt_parts.append(
                "MOTION SEQUENCE ATTACHED: Image 1 is from ~1s ago; Image 2 is NOW. "
                "Read actual movement, ball/enemy trajectory, and HUD changes between Image 1 and Image 2."
            )
        mode_note = build_coach_mode_instruction(self._settings.coach_mode)
        if mode_note:
            prompt_parts.append(mode_note)
        if force_advice:
            prompt_parts.append(
                "SOLICITUD MANUAL DEL JUGADOR (F10): el jugador pidió una lectura táctica "
                "inmediata de este frame. Entregá el mejor consejo concreto disponible."
            )
        if recent_history.strip():
            prompt_parts.append(recent_history.strip())
        prompt_text = "\n\n".join(prompt_parts)

        max_out = {"eco": 95, "balanced": 125, "high": 160}.get(
            self._settings.token_efficiency_mode, 125
        )
        # Minimize hidden internal thinking tokens for sub-second real-time coaching:
        # Gemini 2.5 uses thinking_budget=0; Gemini 3.x uses thinking_level=MINIMAL (2.5x faster!).
        m_low = self._model.lower()
        thinking_cfg: types.ThinkingConfig | None = None
        if "2.5" in m_low:
            thinking_cfg = types.ThinkingConfig(thinking_budget=0)
        elif "3." in m_low or "3-" in m_low:
            thinking_cfg = types.ThinkingConfig(thinking_level=types.ThinkingLevel.MINIMAL)

        media_res = (
            types.MediaResolution.MEDIA_RESOLUTION_LOW
            if previous_image_bytes or self._settings.token_efficiency_mode in ("eco",)
            else None
        )

        if self._using_cache and self._cache_name:
            # IMPORTANT: Do NOT pass system_instruction when cached_content is set
            config = types.GenerateContentConfig(
                cached_content=self._cache_name,
                temperature=min(0.65, max(0.35, self._settings.gemini_temperature + 0.15)),
                max_output_tokens=max_out,
                thinking_config=thinking_cfg,
                media_resolution=media_res,
                response_mime_type="application/json",
                response_schema=_CoachWireSchema,
            )
            contents: list[types.Part | str] = [
                *image_parts,
                prompt_text,
            ]
        else:
            # Standard fallback mode: send system prompt + compact knowledge base inline
            config = types.GenerateContentConfig(
                system_instruction=profile.system_prompt,
                temperature=min(0.65, max(0.35, self._settings.gemini_temperature + 0.15)),
                max_output_tokens=max_out,
                thinking_config=thinking_cfg,
                media_resolution=media_res,
                response_mime_type="application/json",
                response_schema=_CoachWireSchema,
            )
            contents = [
                f"# Knowledge Base: {profile.manifest.display_name}\n\n{profile.compact_knowledge_base}",
                *image_parts,
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
        if isinstance(parsed, _CoachWireSchema):
            return CoachResponse.model_validate(parsed.model_dump())

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

    def _should_use_fast_rest(self) -> bool:
        """Use persistent aiohttp Keep-Alive + 1-line compact wire protocol for real Gemini calls."""
        return (
            isinstance(self._client, genai.Client)
            and not self._using_cache
            and self._settings.token_efficiency_mode in ("eco", "balanced")
        )

    def _get_fast_http(self) -> aiohttp.ClientSession:
        if self._fast_http is None or self._fast_http.closed:
            connector = aiohttp.TCPConnector(
                ttl_dns_cache=300,
                keepalive_timeout=60,
                limit=4,
            )
            self._fast_http = aiohttp.ClientSession(
                connector=connector,
                timeout=aiohttp.ClientTimeout(total=min(6.0, self._settings.gemini_timeout_seconds)),
                cookie_jar=aiohttp.DummyCookieJar(),
            )
        return self._fast_http

    async def _prewarm_fast_http(self) -> None:
        """Establish TLS + HTTP Keep-Alive socket at session start so frame #1 has zero handshake delay."""
        api_key = self._settings.get_api_key_value().strip()
        if not api_key:
            return
        safe_model = quote(self._model.strip(), safe="-_.")
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{safe_model}"
        with contextlib.suppress(Exception):
            session = self._get_fast_http()
            async with session.get(
                url,
                headers={"x-goog-api-key": api_key, "User-Agent": "HUDMind/0.5.0"},
                timeout=aiohttp.ClientTimeout(total=3.0),
            ) as resp:
                await resp.read()

    async def _analyze_frame_fast_rest(
        self,
        *,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        recent_history: str,
        force_advice: bool,
        text_language: str,
        voice_language: str,
    ) -> CoachResponse | None:
        """Sub-second direct REST Keep-Alive inference using 1-line compact wire protocol."""
        api_key = self._settings.get_api_key_value().strip()
        if not api_key:
            self.last_error_code = 401
            self.last_error_message = "Falta API key de Gemini"
            self._record_failure(fatal_auth=True)
            return None

        safe_model = quote(self._model.strip(), safe="-_.")
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{safe_model}:generateContent"
        b64 = base64.b64encode(image_bytes).decode("ascii")
        prompt = build_realtime_compact_prompt(
            profile,
            coach_mode=self._settings.coach_mode,
            text_language=text_language,
            voice_language=voice_language,
            recent_history=recent_history,
            force_advice=force_advice,
        )
        m_low = self._model.lower()
        gen_cfg: dict[str, Any] = {
            "temperature": 0.55,
            "maxOutputTokens": 24,
            "mediaResolution": "MEDIA_RESOLUTION_LOW",
        }
        if "2.5" in m_low:
            gen_cfg["thinkingConfig"] = {"thinkingBudget": 0}
        elif "3." in m_low or "3-" in m_low:
            gen_cfg["thinkingConfig"] = {"thinkingLevel": "MINIMAL"}

        payload: dict[str, Any] = {
            "contents": [
                {
                    "parts": [
                        {"inlineData": {"mimeType": mime_type, "data": b64}},
                        {"text": prompt},
                    ]
                }
            ],
            "generationConfig": gen_cfg,
        }
        headers = {
            "Content-Type": "application/json",
            "x-goog-api-key": api_key,
            "User-Agent": "HUDMind/0.5.0",
        }

        try:
            session = self._get_fast_http()
            req_timeout = aiohttp.ClientTimeout(total=min(5.0, self._settings.gemini_timeout_seconds))
            async with session.post(url, headers=headers, json=payload, timeout=req_timeout) as resp:
                if resp.status != 200:
                    body = (await resp.text())[:220]
                    self.last_error_code = resp.status
                    self.last_error_message = body
                    if resp.status in (401, 403):
                        self._record_failure(fatal_auth=True)
                    else:
                        self._record_failure()
                    logger.error("Error Gemini Fast REST (HTTP %s): %s", resp.status, body)
                    return None
                data = await resp.json()
                candidates = data.get("candidates") or []
                parts = (candidates[0].get("content") or {}).get("parts") or [] if candidates else []
                raw_text = "".join(str(p.get("text", "")) for p in parts if isinstance(p, dict)).strip()
                parsed = parse_compact_coach_line(raw_text)
                if parsed is None:
                    self.last_error_code = 422
                    self.last_error_message = f"Salida compacta no válida: {raw_text[:100]!r}"
                    return None
                self._record_success()
                return parsed
        except TimeoutError:
            self.last_error_code = 408
            self.last_error_message = "timeout"
            self._record_failure()
            return None
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            self.last_error_code = 500
            self.last_error_message = str(exc)
            self._record_failure()
            return None

    async def close_session(self) -> None:
        """Delete active remote Context Cache and close Keep-Alive HTTP pool cleanly."""
        async with self._session_lock:
            await self._delete_active_cache_unlocked()
            if self._fast_http is not None and not self._fast_http.closed:
                with contextlib.suppress(Exception):
                    await self._fast_http.close()
                self._fast_http = None
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
