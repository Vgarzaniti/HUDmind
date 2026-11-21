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
import io
import json
import logging
import time
from abc import ABC, abstractmethod
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any
from urllib.parse import quote

import aiohttp
from PIL import Image
from pydantic import SecretStr, ValidationError

from src.ai.gemini_client import (
    GeminiCoachClient,
    build_coach_mode_instruction,
    build_language_instruction,
    build_universal_game_context_instruction,
)
from src.config.settings import AppSettings, redact_secrets_in_text
from src.config.telemetry import TelemetryStore
from src.config.user_store import ProviderAccount, ProviderKind
from src.pipeline.hud_ocr import LiveHUDState, RealTimeHUDAnalyzer
from src.pipeline.schemas import CoachResponse
from src.profiles.loader import GameProfile

logger = logging.getLogger(__name__)

_JSON_SCHEMA_REMINDER = (
    'Respond ONLY with valid JSON matching exactly: '
    '{"should_intervene": bool, "urgency": "low"|"medium"|"high", '
    '"overlay_text": "max 8 words or empty", "voice_line": "max 12 words or empty", '
    '"match_context": "1-line summary of score/timer, visible teammates, rivals, and player resources"}. '
    "No markdown fences, no extra prose."
)


@dataclass(slots=True)
class ProviderCallResult:
    """Outcome of a single provider inference call."""

    response: CoachResponse | None = None
    error_code: int | None = None
    error_message: str = ""
    quota_or_auth_failure: bool = False


def _normalize_coach_dict(obj: dict[str, Any]) -> dict[str, Any]:
    """Clamp overly verbose text fields from free models so Pydantic max_length never fails."""
    cleaned = dict(obj)
    if isinstance(cleaned.get("overlay_text"), str) and len(cleaned["overlay_text"]) > 115:
        cleaned["overlay_text"] = cleaned["overlay_text"][:112].rsplit(" ", 1)[0] + "..."
    if isinstance(cleaned.get("voice_line"), str) and len(cleaned["voice_line"]) > 175:
        cleaned["voice_line"] = cleaned["voice_line"][:172].rsplit(" ", 1)[0] + "..."
    urg = str(cleaned.get("urgency", "low")).lower().strip()
    if urg not in ("low", "medium", "high"):
        cleaned["urgency"] = "medium"
    return cleaned


def extract_coach_response_from_text(raw_text: str) -> CoachResponse | None:
    """Parse JSON (even if surrounded by prose or markdown fences) into CoachResponse."""
    text = raw_text.strip()
    if not text:
        return None

    # 1. Fast path: pure JSON string
    with contextlib.suppress(ValidationError, json.JSONDecodeError):
        return CoachResponse.model_validate_json(text)

    # 2. Scan candidate '{' positions using non-greedy raw_decode
    decoder = json.JSONDecoder()
    start = text.find("{")
    while start != -1:
        with contextlib.suppress(json.JSONDecodeError, ValidationError):
            obj, _ = decoder.raw_decode(text, start)
            if isinstance(obj, dict):
                return CoachResponse.model_validate(_normalize_coach_dict(obj))
        start = text.find("{", start + 1)

    logger.warning("Respuesta JSON no válida del proveedor: %r", raw_text[:200])
    return None


@dataclass(slots=True)
class SpatialFrameAnalysis:
    """Universal cross-game spatial & HUD analysis extracted in <1.5ms on CPU."""

    ally_sector: str
    ally_intensity: int
    rival_sector: str
    rival_intensity: int
    tactical_balance: str
    resource_pct: int
    alert_pct: int
    action_phase: str
    delta_note: str

    def to_prompt_telemetry(self) -> str:
        res_tag = "BAJO" if self.resource_pct < 28 else ("ALTO" if self.resource_pct > 68 else "MEDIO")
        return (
            f"CONTEXTO_VISUAL_EN_VIVO: Compañeros=[sector {self.ally_sector}, presencia={self.ally_intensity}%] | "
            f"Rivales=[sector {self.rival_sector}, presión={self.rival_intensity}%] | "
            f"Balance=[{self.tactical_balance}] | "
            f"HUD_Recursos_Boost=[{self.resource_pct}% ({res_tag})] | "
            f"Alerta_Peligro=[{self.alert_pct}%] | "
            f"Fase=[{self.action_phase}] | "
            f"Evolución=[{self.delta_note}]"
        )

    def short_context_line(self, game_name: str) -> str:
        return (
            f"{game_name} | Aliado:{self.ally_sector} vs Rival:{self.rival_sector} "
            f"({self.tactical_balance}) | HUD:{self.resource_pct}% | {self.action_phase}"
        )[:155]


_PREV_SPATIAL_STATE: dict[str, Any] = {}


def analyze_frame_spatial_context(image_bytes: bytes) -> SpatialFrameAnalysis:
    """Universal <1.5ms spatial detector for teammates, rivals, HUD meters, and motion deltas."""
    try:
        with Image.open(io.BytesIO(image_bytes)) as img:
            thumb = img.convert("RGB").resize((48, 27), Image.Resampling.NEAREST)
            w, h = thumb.size
            pixels = list(thumb.getdata())

        ally_cols = [0, 0, 0]  # left, center, right
        rival_cols = [0, 0, 0]
        upper_energy = 0
        lower_energy = 0
        center_box_uniform = 0
        hud_resource_fill = 0
        hud_resource_total = 0
        top_red_alerts = 0
        top_total = 0

        for y in range(h):
            row_off = y * w
            for x in range(w):
                r, g, b = pixels[row_off + x]
                col_idx = 0 if x < 16 else (1 if x < 32 else 2)

                # Top header / scoreboard / police stars / radar (y < 6)
                if y < 6:
                    top_total += 1
                    if r > 165 and g < 85 and b < 85:
                        top_red_alerts += 1

                # Main gameplay field (4 <= y <= 21)
                if 4 <= y <= 21:
                    # Ally signatures: cyan/blue/green/teal nameplates, cars, outlines
                    if (b > r + 38 and b > 125) or (g > r + 35 and g > 130 and b > 80):
                        ally_cols[col_idx] += 1
                    # Rival signatures: orange/red/crimson/magenta enemy indicators
                    elif r > b + 48 and r > 150 and g < 145:
                        rival_cols[col_idx] += 1

                    luma = int(0.299 * r + 0.587 * g + 0.114 * b)
                    if y < 12:
                        upper_energy += luma
                    else:
                        lower_energy += luma

                    if 12 <= x <= 35 and 7 <= y <= 19 and 25 < luma < 85 and abs(r - b) < 18:
                        center_box_uniform += 1

                # Bottom HUD corners (boost / ammo / money / health / villagers)
                if y >= 21 and (x >= 33 or x <= 14):
                    hud_resource_total += 1
                    if (r > 170 and g > 120) or (b > 165 and g > 140) or (r > 200 and g > 200 and b > 200):
                        hud_resource_fill += 1

        sector_names = ("izquierda", "centro", "derecha")
        ally_max_idx = max(range(3), key=lambda i: ally_cols[i])
        rival_max_idx = max(range(3), key=lambda i: rival_cols[i])

        ally_total = sum(ally_cols)
        rival_total = sum(rival_cols)
        ally_sector = sector_names[ally_max_idx] if ally_total >= 4 else "atrás/rotando"
        rival_sector = sector_names[rival_max_idx] if rival_total >= 4 else "distancia media"

        ally_int = min(100, int((ally_total * 100) / 140))
        rival_int = min(100, int((rival_total * 100) / 140))

        if ally_total > rival_total * 1.35 and ally_total >= 8:
            balance = f"superioridad aliada (2v1 por {ally_sector})"
        elif rival_total > ally_total * 1.35 and rival_total >= 8:
            balance = f"presión rival alta (1v2 en {rival_sector})"
        elif ally_sector == rival_sector and ally_total >= 6:
            balance = f"disputa directa en {ally_sector} (evitar Double Commit)"
        else:
            balance = f"aliado en {ally_sector}, rival cerrando {rival_sector}"

        res_pct = min(100, max(8, int((hud_resource_fill * 185) / max(1, hud_resource_total))))
        alert_pct = min(100, int((top_red_alerts * 220) / max(1, top_total)))

        if center_box_uniform > 175:
            phase = "tabla de posiciones (TAB) / marcador visible"
        elif upper_energy > lower_energy * 1.18:
            phase = "acción aérea / pared alta"
        elif ally_cols[1] + rival_cols[1] > 28:
            phase = "duelo cercano en el centro"
        else:
            phase = "transición y posicionamiento en campo"

        prev_ally = _PREV_SPATIAL_STATE.get("ally_sector")
        prev_rival = _PREV_SPATIAL_STATE.get("rival_sector")
        prev_res = _PREV_SPATIAL_STATE.get("resource_pct", res_pct)

        deltas: list[str] = []
        if prev_ally and prev_ally != ally_sector:
            deltas.append(f"compañero rotó de {prev_ally} a {ally_sector}")
        if prev_rival and prev_rival != rival_sector:
            deltas.append(f"amenaza rival se movió a {rival_sector}")
        if abs(res_pct - prev_res) >= 15:
            direction = "subió" if res_pct > prev_res else "bajó"
            deltas.append(f"medidor HUD {direction} a {res_pct}%")
        if not deltas:
            deltas.append(f"foco activo en {rival_sector}")

        _PREV_SPATIAL_STATE["ally_sector"] = ally_sector
        _PREV_SPATIAL_STATE["rival_sector"] = rival_sector
        _PREV_SPATIAL_STATE["resource_pct"] = res_pct

        return SpatialFrameAnalysis(
            ally_sector=ally_sector,
            ally_intensity=ally_int,
            rival_sector=rival_sector,
            rival_intensity=rival_int,
            tactical_balance=balance,
            resource_pct=res_pct,
            alert_pct=alert_pct,
            action_phase=phase,
            delta_note=", ".join(deltas),
        )
    except Exception:
        return SpatialFrameAnalysis(
            ally_sector="centro",
            ally_intensity=30,
            rival_sector="frente",
            rival_intensity=35,
            tactical_balance="duelo parejo en transición",
            resource_pct=55,
            alert_pct=10,
            action_phase="acción en campo",
            delta_note="ritmo activo",
        )


def _extract_cpu_hud_telemetry(image_bytes: bytes) -> str:
    """Extract live spatial teammate/rival/HUD telemetry string for AI prompts."""
    return analyze_frame_spatial_context(image_bytes).to_prompt_telemetry()


def _compose_static_system_prompt(profile: GameProfile) -> str:
    """Combine compact system prompt and knowledge base into a static prefix (>1024 tokens).

    Placing all static game rules in the system block enables Automatic Prefix Caching
    in OpenAI/Ollama and Ephemeral Prompt Caching in Anthropic Claude (up to 90% discount).
    """
    return (
        f"{profile.compact_system_prompt}\n\n"
        f"# Knowledge Base: {profile.manifest.display_name}\n\n"
        f"{profile.compact_knowledge_base}"
    )


def _compose_dynamic_turn_prompt(
    *,
    recent_history: str,
    force_advice: bool,
    text_language: str,
    voice_language: str,
    coach_mode: str = "match",
    include_json_reminder: bool = True,
) -> str:
    """Build the minimal per-frame dynamic prompt."""
    parts = [
        "Analizá este frame actual del juego usando los anclajes del HUD y las reglas del sistema.",
        build_universal_game_context_instruction(),
        build_language_instruction(text_language, voice_language),
    ]
    mode_note = build_coach_mode_instruction(coach_mode)
    if mode_note:
        parts.append(mode_note)
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
        # Single fast try inside failover pool so rotation to backup accounts stays snappy
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
        prev_img: bytes | None = getattr(self, "_previous_image_bytes", None)
        resp = await self._client.analyze_frame(
            image_bytes=image_bytes,
            mime_type=mime_type,
            profile=profile,
            recent_history=recent_history,
            force_advice=force_advice,
            text_language=text_language,
            voice_language=voice_language,
            previous_image_bytes=prev_img,
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
        api_key = self.account.api_key.strip()
        if not api_key:
            return False, "Falta API Key de Gemini."
        try:
            safe_model = quote(self.account.model.strip(), safe="-_.")
            url = f"https://generativelanguage.googleapis.com/v1beta/models/{safe_model}"
            headers = {"x-goog-api-key": api_key, "User-Agent": "HUDMind/0.4.0"}
            timeout = aiohttp.ClientTimeout(total=8)
            async with (
                aiohttp.ClientSession(
                    timeout=timeout,
                    cookie_jar=aiohttp.DummyCookieJar(),
                ) as session,
                session.get(url, headers=headers) as r,
            ):
                if r.status == 200:
                    return True, f"OK — Modelo '{self.account.model}' verificado."
                return False, f"HTTP {r.status} al verificar API key / modelo."
        except Exception as exc:
            safe_err = redact_secrets_in_text(str(exc), (api_key,))
            return False, f"Error de red: {safe_err}"

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
            self._http = aiohttp.ClientSession(
                timeout=timeout,
                cookie_jar=aiohttp.DummyCookieJar(),
            )
        return self._http

    def _build_headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json", "User-Agent": "HUDMind/0.4.0"}
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

        system_content = _compose_static_system_prompt(profile)
        user_prompt = _compose_dynamic_turn_prompt(
            recent_history=recent_history,
            force_advice=force_advice,
            text_language=text_language,
            voice_language=voice_language,
            coach_mode=self._settings.coach_mode,
        )

        # Keyless anonymous cloud mode (Pollinations & Airforce): 100% live improvised AI coach
        is_keyless_cloud = (
            self.account.provider in ("pollinations", "airforce")
            and not self.account.api_key.strip()
        )
        live_hud: LiveHUDState | None = getattr(self, "_current_live_hud", None)
        effective_mode: str = getattr(self, "_effective_mode", self._settings.coach_mode)
        spatial: SpatialFrameAnalysis | None = None
        hud_scan = ""
        if is_keyless_cloud:
            if live_hud is not None:
                hud_scan = live_hud.to_prompt_block(profile.manifest.display_name)
            else:
                spatial = analyze_frame_spatial_context(image_bytes)
                hud_scan = spatial.to_prompt_telemetry()

            custom_block = (
                f"\nReglas personales del jugador:\n{profile.custom_rules[:500]}"
                if profile.custom_rules.strip()
                else ""
            )
            spontaneous_coach_sys = (
                f"Sos un coach humano real de {profile.manifest.display_name} hablando en vivo por Discord "
                "al lado del jugador. NO recites consejos de manual ni frases predefinidas ni robóticas: "
                "improvisá naturalmente como un entrenador real reaccionando a lo que está pasando AHORA "
                "(aliento, lectura rápida de compañeros/rivales/reloj/boost, humor sutil o corrección al hueso). "
                "Variá siempre tu estilo y tono; si no hay nada útil nuevo que decir, devolvé should_intervene=false."
                f"{custom_block}"
            )
            chosen_model = self.account.model.strip() or (
                "openai-fast" if self.account.provider == "pollinations" else "open-mistral-nemo"
            )
            payload: dict[str, object] = {
                "model": chosen_model,
                "temperature": 0.78,
                "messages": [
                    {"role": "system", "content": spontaneous_coach_sys},
                    {
                        "role": "user",
                        "content": (
                            f"{hud_scan}\n\n"
                            f"Modo actual detectado: {effective_mode.upper()}.\n"
                            f"{user_prompt}"
                        ),
                    },
                ],
            }
            endpoint = (
                "https://text.pollinations.ai/openai"
                if self.account.provider == "pollinations"
                else f"{base_url}/chat/completions"
            )
        else:
            b64 = base64.b64encode(image_bytes).decode("ascii")
            max_tokens = {"eco": 85, "balanced": 115, "high": 150}.get(
                self._settings.token_efficiency_mode, 115
            )
            image_url_obj: dict[str, str] = {"url": f"data:{mime_type};base64,{b64}"}
            if self.account.provider in ("openai", "pollinations"):
                is_eco_mode = self._settings.token_efficiency_mode in ("eco",)
                image_url_obj["detail"] = "low" if is_eco_mode else "auto"

            payload = {
                "model": self.account.model,
                "temperature": self._settings.gemini_temperature,
                "max_tokens": max_tokens,
                "messages": [
                    {"role": "system", "content": system_content},
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": user_prompt},
                            {"type": "image_url", "image_url": image_url_obj},
                        ],
                    },
                ],
            }
            if self.account.provider in ("openai", "groq"):
                payload["response_format"] = {"type": "json_object"}
            endpoint = (
                base_url
                if base_url.endswith("/openai")
                else f"{base_url}/chat/completions"
            )
        try:
            session = self._get_session()
            req_timeout = (
                aiohttp.ClientTimeout(total=10.0)
                if is_keyless_cloud
                else aiohttp.ClientTimeout(total=self._settings.gemini_timeout_seconds)
            )
            async with session.post(
                endpoint,
                headers=self._build_headers(),
                json=payload,
                timeout=req_timeout,
            ) as resp:
                if resp.status != 200:
                    err_body = (await resp.text())[:220]
                    return ProviderCallResult(
                        error_code=resp.status,
                        error_message=err_body,
                        quota_or_auth_failure=resp.status in (401, 402, 403, 429),
                    )
                data = await resp.json()
                choices = data.get("choices") or []
                first_msg = choices[0].get("message", {}) if choices else {}
                content = first_msg.get("content") or ""
                parsed = extract_coach_response_from_text(str(content))
                if parsed is None:
                    return ProviderCallResult(error_code=422, error_message="JSON inválido")
                if is_keyless_cloud and not parsed.match_context.strip():
                    ctx_line = (
                        live_hud.short_context_line(profile.manifest.display_name)
                        if live_hud is not None
                        else (
                            spatial.short_context_line(profile.manifest.display_name)
                            if spatial is not None
                            else ""
                        )
                    )
                    if ctx_line:
                        parsed = parsed.model_copy(update={"match_context": ctx_line})
                return ProviderCallResult(response=parsed)
        except TimeoutError:
            return ProviderCallResult(error_code=408, error_message="timeout")
        except Exception as exc:
            return ProviderCallResult(error_code=503, error_message=str(exc))

    async def test_connection(self) -> tuple[bool, str]:
        base_url = self.account.resolved_base_url()
        if not base_url:
            return False, "Falta Base URL."
        is_keyless_pollinations = (
            self.account.provider == "pollinations" and not self.account.api_key.strip()
        )
        models_url = (
            "https://text.pollinations.ai/models"
            if is_keyless_pollinations or base_url.endswith("/openai")
            else f"{base_url}/models"
        )
        try:
            timeout = aiohttp.ClientTimeout(total=8)
            async with (
                aiohttp.ClientSession(
                    timeout=timeout,
                    cookie_jar=aiohttp.DummyCookieJar(),
                ) as session,
                session.get(models_url, headers=self._build_headers()) as r,
            ):
                if r.status == 200:
                    return True, f"OK — Endpoint {models_url} accesible (100% Gratis SIN LOGIN)."
                return False, f"HTTP {r.status} en {models_url}."
        except Exception as exc:
            safe_err = redact_secrets_in_text(str(exc), (self.account.api_key,))
            return False, f"No se pudo conectar a {base_url}: {safe_err}"

    async def close_session(self) -> None:
        if self._http is not None and not self._http.closed:
            await self._http.close()
            self._http = None


class AnthropicClaudeProvider(BaseAIProvider):
    """Async vision adapter for Anthropic Claude Messages API with Ephemeral Prompt Caching."""

    def __init__(self, account: ProviderAccount, settings: AppSettings) -> None:
        super().__init__(account, settings)
        self._http: aiohttp.ClientSession | None = None

    def _get_session(self) -> aiohttp.ClientSession:
        if self._http is None or self._http.closed:
            timeout = aiohttp.ClientTimeout(total=self._settings.gemini_timeout_seconds)
            self._http = aiohttp.ClientSession(
                timeout=timeout,
                cookie_jar=aiohttp.DummyCookieJar(),
            )
        return self._http

    def _build_headers(self) -> dict[str, str]:
        return {
            "Content-Type": "application/json",
            "x-api-key": self.account.api_key.strip(),
            "anthropic-version": "2023-06-01",
            "User-Agent": "HUDMind/0.4.0",
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
        system_content = _compose_static_system_prompt(profile)
        user_prompt = _compose_dynamic_turn_prompt(
            recent_history=recent_history,
            force_advice=force_advice,
            text_language=text_language,
            voice_language=voice_language,
            coach_mode=self._settings.coach_mode,
        )
        max_tokens = {"eco": 85, "balanced": 115, "high": 150}.get(
            self._settings.token_efficiency_mode, 115
        )
        payload = {
            "model": self.account.model,
            "max_tokens": max_tokens,
            "temperature": self._settings.gemini_temperature,
            "system": [
                {
                    "type": "text",
                    "text": system_content,
                    "cache_control": {"type": "ephemeral"},
                }
            ],
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
        api_key = self.account.api_key.strip()
        if not api_key:
            return False, "Falta API Key de Anthropic Claude."
        base_url = self.account.resolved_base_url() or "https://api.anthropic.com/v1"
        try:
            timeout = aiohttp.ClientTimeout(total=8)
            async with (
                aiohttp.ClientSession(
                    timeout=timeout,
                    cookie_jar=aiohttp.DummyCookieJar(),
                ) as session,
                session.get(f"{base_url}/models", headers=self._build_headers()) as r,
            ):
                if r.status == 200:
                    return True, "OK — API Key de Anthropic verificada."
                return False, f"HTTP {r.status} en Anthropic API."
        except Exception as exc:
            safe_err = redact_secrets_in_text(str(exc), (api_key,))
            return False, f"Error de conexión: {safe_err}"

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


# 10 Pure Live AI Free No-Login Models Pool (zero hardcoded tips — 100% live AI generation)
TEN_FREE_NOLOGIN_SPECS: list[tuple[str, str, ProviderKind, str, str, int]] = [
    (
        "auto-free-pollinations",
        "1. Pollinations · GPT-OSS-20B Fast (Sin Login)",
        "pollinations",
        "openai-fast",
        "https://text.pollinations.ai/openai",
        981,
    ),
    (
        "auto-free-pollinations-std",
        "2. Pollinations · OpenAI Standard (Sin Login)",
        "pollinations",
        "openai",
        "https://text.pollinations.ai/openai",
        982,
    ),
    (
        "auto-free-pollinations-mistral",
        "3. Pollinations · Mistral Small (Sin Login)",
        "pollinations",
        "mistral",
        "https://text.pollinations.ai/openai",
        983,
    ),
    (
        "auto-free-pollinations-qwen",
        "4. Pollinations · Qwen 2.5 Instruct (Sin Login)",
        "pollinations",
        "qwen",
        "https://text.pollinations.ai/openai",
        984,
    ),
    (
        "auto-free-pollinations-deepseek",
        "5. Pollinations · DeepSeek V3 (Sin Login)",
        "pollinations",
        "deepseek",
        "https://text.pollinations.ai/openai",
        985,
    ),
    (
        "auto-free-airforce-nemo",
        "6. Airforce Cloud · Open-Mistral-Nemo (Sin Login)",
        "airforce",
        "open-mistral-nemo",
        "https://api.airforce/v1",
        986,
    ),
    (
        "auto-free-airforce-large",
        "7. Airforce Cloud · Mistral Large (Sin Login)",
        "airforce",
        "mistral-large-latest",
        "https://api.airforce/v1",
        987,
    ),
    (
        "auto-free-airforce-codestral",
        "8. Airforce Cloud · Codestral Latest (Sin Login)",
        "airforce",
        "codestral-latest",
        "https://api.airforce/v1",
        988,
    ),
    (
        "auto-free-airforce-tiny",
        "9. Airforce Cloud · Mistral Tiny (Sin Login)",
        "airforce",
        "mistral-tiny-latest",
        "https://api.airforce/v1",
        989,
    ),
    (
        "auto-free-ollama",
        "10. Ollama Local · Qwen2.5-VL Vision (Sin Login)",
        "ollama",
        "qwen2.5vl:3b",
        "http://localhost:11434/v1",
        990,
    ),
]


def build_ten_free_nologin_accounts(
    existing: list[ProviderAccount] | None = None,
) -> list[ProviderAccount]:
    """Build or append the full 10-model free no-login rotation pool."""
    result = list(existing) if existing else []
    existing_keys = {(a.provider, a.model) for a in result}
    base_prio = len(result) + 1

    for offset, (acc_id, label, kind, model, base_url, _) in enumerate(TEN_FREE_NOLOGIN_SPECS):
        if (kind, model) not in existing_keys:
            result.append(
                ProviderAccount(
                    id=acc_id,
                    label=label,
                    provider=kind,
                    model=model,
                    base_url=base_url,
                    priority=base_prio + offset,
                    enabled=True,
                )
            )
    return result


def build_auto_free_fallback_accounts(
    existing: list[ProviderAccount],
) -> list[ProviderAccount]:
    """Append the 10 built-in keyless free fallback models at the bottom of the rotation pool.

    Ensures that if all user-configured API keys hit rate/quota limits (or if no keys
    were configured at all), the router automatically iterates through all 10 free
    no-login models without interrupting gameplay.
    """
    result = list(existing)
    existing_pairs = {(a.provider, a.model) for a in result}

    for acc_id, label, kind, model, base_url, prio in TEN_FREE_NOLOGIN_SPECS:
        if (kind, model) not in existing_pairs:
            result.append(
                ProviderAccount(
                    id=acc_id,
                    label=label,
                    provider=kind,
                    model=model,
                    base_url=base_url,
                    priority=prio,
                    enabled=True,
                )
            )
    return result


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
        auto_free_fallback: bool | None = None,
        telemetry: TelemetryStore | None = None,
    ) -> None:
        self._settings = settings
        self._on_failover = on_failover
        self._telemetry = telemetry
        factory = provider_factory or create_provider_adapter
        use_auto_fallback = (
            (provider_factory is None) if auto_free_fallback is None else auto_free_fallback
        )

        effective_accounts = (
            build_auto_free_fallback_accounts(accounts) if use_auto_fallback else list(accounts)
        )
        ready_accounts = [a for a in effective_accounts if a.is_ready()]
        ready_accounts.sort(key=lambda a: (a.priority, a.label.lower()))

        self._providers: list[BaseAIProvider] = [factory(acc, settings) for acc in ready_accounts]
        self._cooldown_until: dict[str, float] = {}
        self._prepared_ids: set[str] = set()
        self._active_profile: GameProfile | None = None
        self._active_index: int = 0
        self._hud_analyzer = RealTimeHUDAnalyzer()
        self._last_detected_mode: str = "match"
        self._previous_frame_bytes: bytes | None = None
        self._previous_frame_ts: float = 0.0

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
        """Warm up the primary provider and clean up any previously prepared sessions."""
        for prov in self._providers:
            if prov.account.id in self._prepared_ids:
                with contextlib.suppress(Exception):
                    await prov.close_session()
        self._prepared_ids.clear()
        self._active_profile = profile
        self._hud_analyzer.reset()
        self._previous_frame_bytes = None
        self._previous_frame_ts = 0.0

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

        # If every account is currently in cooldown, respect the cooldown unless forced by F10
        if not candidates:
            if not force_advice:
                logger.debug("Todos los proveedores IA están en enfriamiento temporal; omitiendo frame.")
                return None
            earliest_idx = min(
                range(len(self._providers)),
                key=lambda i: self._cooldown_until.get(self._providers[i].account.id, 0.0),
            )
            candidates = [(earliest_idx, self._providers[earliest_idx])]

        sys_chars = len(profile.system_prompt) + len(profile.knowledge_base)
        eff_mode = self._settings.token_efficiency_mode

        # Real-time ~40ms Windows 11 Native OCR + Spatial HUD scan (auto-detects Match vs Training vs Menu)
        live_hud: LiveHUDState | None = None
        effective_coach_mode: str = self._settings.coach_mode
        if len(image_bytes) > 64:
            live_hud = await self._hud_analyzer.analyze(
                image_bytes,
                game_id=profile.manifest.game_id,
            )
            if self._settings.coach_mode == "auto":
                if live_hud.detected_mode == "menu" and not force_advice:
                    logger.debug("Menú/Lobby detectado automáticamente por OCR; coach en silencio.")
                    return CoachResponse(should_intervene=False)
                effective_coach_mode = (
                    "training" if live_hud.detected_mode == "training" else "match"
                )
                if effective_coach_mode != self._last_detected_mode:
                    self._last_detected_mode = effective_coach_mode
                    logger.info(
                        "Detección automática de modo: %s (OCR=%.0fms, timer=%r, score=%r)",
                        effective_coach_mode.upper(),
                        live_hud.ocr_ms,
                        live_hud.timer_text,
                        live_hud.score_text,
                    )

        # Attach previous frame only if captured recently (<= 6s ago) so Gemini sees real motion!
        prev_for_motion = (
            self._previous_frame_bytes
            if (self._previous_frame_bytes and (now - self._previous_frame_ts) <= 6.0)
            else None
        )
        self._previous_frame_bytes = image_bytes if len(image_bytes) > 64 else None
        self._previous_frame_ts = now

        keyless_cloud_attempts = 0

        for idx, prov in candidates:
            acc = prov.account
            is_keyless_cloud = acc.provider in ("pollinations", "airforce") and not acc.api_key.strip()
            # Try at most 2 keyless cloud models per frame to stay responsive without long waterfalls
            if is_keyless_cloud and keyless_cloud_attempts >= 2:
                continue
            if is_keyless_cloud:
                keyless_cloud_attempts += 1

            if acc.id not in self._prepared_ids and self._active_profile is not None:
                await prov.prepare_session(self._active_profile)
                self._prepared_ids.add(acc.id)

            prov._current_live_hud = live_hud  # type: ignore[attr-defined]
            prov._effective_mode = effective_coach_mode  # type: ignore[attr-defined]
            prov._previous_image_bytes = prev_for_motion  # type: ignore[attr-defined]

            t_call_start = time.monotonic()
            result = await prov.analyze_frame(
                image_bytes=image_bytes,
                mime_type=mime_type,
                profile=profile,
                recent_history=recent_history,
                force_advice=force_advice,
                text_language=t_lang,
                voice_language=v_lang,
            )
            call_ms = (time.monotonic() - t_call_start) * 1000.0
            used_cache = acc.provider in ("gemini", "claude", "openai")

            if result.response is not None:
                if self._telemetry is not None:
                    self._telemetry.record_ai_call(
                        provider=acc.provider,
                        model=acc.model,
                        account_label=acc.label,
                        system_chars=sys_chars,
                        efficiency_mode=eff_mode,
                        used_cache=used_cache,
                        latency_ms=call_ms,
                        response=result.response,
                    )
                if idx != self._active_index:
                    prev_label = self._providers[self._active_index].account.label
                    self._active_index = idx
                    logger.info("Failover completado: '%s' -> '%s'", prev_label, acc.label)
                    if self._on_failover is not None:
                        self._on_failover(prev_label, acc.label)
                return result.response

            # Do not penalize or rotate provider on transient empty/malformed output (422)
            code = result.error_code
            if code == 422:
                logger.debug("Proveedor '%s' devolvió salida no estructurada (422).", acc.label)
                return None

            if self._telemetry is not None:
                self._telemetry.record_ai_call(
                    provider=acc.provider,
                    model=acc.model,
                    account_label=acc.label,
                    system_chars=sys_chars,
                    efficiency_mode=eff_mode,
                    used_cache=used_cache,
                    latency_ms=call_ms,
                    response=None,
                    rate_limited=(code in (402, 429)),
                    failed=(code not in (402, 429)),
                )

            is_keyless_cloud = acc.provider in ("pollinations", "airforce") and not acc.api_key.strip()
            if code in (401, 403):
                cooldown_s = 600.0
                reason = f"auth/acceso (HTTP {code})"
            elif code == 402 and is_keyless_cloud:
                cooldown_s = 45.0
                reason = "límite gratuito temporal por IP (HTTP 402)"
            elif code == 402:
                cooldown_s = 600.0
                reason = "sin créditos (HTTP 402)"
            elif code == 429:
                cooldown_s = 30.0 if is_keyless_cloud else 60.0
                reason = "cuota / rate limit (HTTP 429)"
            elif acc.provider == "ollama" and code == 503:
                cooldown_s = 600.0
                reason = "Ollama local no está corriendo en localhost:11434 (HTTP 503)"
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
