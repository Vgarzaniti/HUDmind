"""Local player performance analytics, playstyle gauges (0-100), and AI token/cost telemetry.

Persists match history, daily/all-time match counters since installation, 5-axis playstyle
scores (Aggression, Patience, Tactical Awareness, Resource Efficiency, Consistency), and
per-model token/cost/latency metrics in `config/telemetry.json` (gitignored, atomic write).
Zero extra AI token overhead: playstyle and match scores are derived in <0.05ms on CPU from
real-time intervention patterns and clean-frame ratios.
"""

from __future__ import annotations

import contextlib
import json
import logging
import os
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Any
from uuid import uuid4

from pydantic import BaseModel, Field

from src.pipeline.schemas import CoachResponse

logger = logging.getLogger(__name__)

DEFAULT_TELEMETRY_PATH = Path("config/telemetry.json")
MAX_STORED_RECENT_MATCHES = 100

# Reference pricing per 1,000,000 tokens (USD) for cost & savings estimation.
# Tuple format: (input_per_1m_usd, cached_input_per_1m_usd, output_per_1m_usd)
_MODEL_PRICING_PER_1M: dict[str, tuple[float, float, float]] = {
    "gpt-4o-mini": (0.15, 0.075, 0.60),
    "gpt-4.1-mini": (0.40, 0.10, 1.60),
    "gpt-4o": (2.50, 1.25, 10.00),
    "claude-3-5-haiku": (0.80, 0.08, 4.00),
    "claude-sonnet": (3.00, 0.30, 15.00),
    "claude-3-7-sonnet": (3.00, 0.30, 15.00),
    "gemini-3.5-flash-lite": (0.075, 0.01875, 0.30),
    "gemini-3.5-flash": (0.15, 0.0375, 0.60),
    "gemini-3.1-flash-lite": (0.075, 0.01875, 0.30),
    "gemini-2.5-flash-lite": (0.075, 0.01875, 0.30),
    "gemini-2.5-flash": (0.15, 0.0375, 0.60),
}

# Bilingual keyword sets for <0.05ms heuristic playstyle classification
_KW_AGGRESSION = (
    "peek",
    "push",
    "rush",
    "duelo",
    "ataque",
    "adelant",
    "sobreext",
    "overcommit",
    "double commit",
    "challenge",
    "pelea",
    "policía",
    "policia",
    "cops",
    "swat",
    "arriesg",
    "solo",
    "agresiv",
)
_KW_PATIENCE = (
    "espera",
    "wait",
    "tiempo",
    "time",
    "cubr",
    "cover",
    "calma",
    "hold",
    "extrae",
    "extract",
    "surrender",
    "rendir",
    "retir",
    "back",
    "atrás",
    "atras",
    "no asomes",
    "don't peek",
    "recarg",
    "reload",
    "pacien",
)
_KW_TACTICAL = (
    "radar",
    "mapa",
    "minimap",
    "espalda",
    "flank",
    "flanco",
    "rotación",
    "rotacion",
    "rotate",
    "backpost",
    "segundo palo",
    "visión",
    "vision",
    "objetivo",
    "c4",
    "spike",
    "reliquia",
    "castillo",
    "salida",
    "escape",
    "ángulo",
    "angulo",
    "crosshair",
    "mira",
)
_KW_ECONOMY = (
    "eco",
    "compra",
    "buy",
    "dinero",
    "money",
    "crédito",
    "credito",
    "aldeano",
    "villager",
    "tc",
    "inactivo",
    "idle",
    "madera",
    "comida",
    "oro",
    "wood",
    "food",
    "gold",
    "población",
    "poblacion",
    "housed",
    "casa",
    "boost",
    "pad",
    "botín",
    "botin",
    "loot",
    "mochila",
    "bag",
    "utilidad",
    "humo",
    "flash",
    "molotov",
)


def _clamp_score(value: float) -> int:
    return max(0, min(100, round(value)))


def _today_str() -> str:
    return datetime.now().strftime("%Y-%m-%d")


def _now_iso() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _lookup_pricing(model: str) -> tuple[float, float, float]:
    m_low = model.lower()
    for prefix, rates in _MODEL_PRICING_PER_1M.items():
        if prefix in m_low:
            return rates
    return (0.15, 0.0375, 0.60)


def is_provider_free_tier(provider: str, model: str) -> bool:
    """Return True if the provider/model operates on a $0 free tier."""
    p = provider.lower()
    m = model.lower()
    if p in ("groq", "pollinations", "airforce", "local_cpu", "ollama", "custom"):
        return True
    if p == "openrouter" and (":free" in m or m.endswith("/free")):
        return True
    return p == "gemini"


class PlaystyleMetrics(BaseModel):
    """0-100 circular gauge metrics representing the player's tactical style."""

    aggression: int = Field(default=50, ge=0, le=100)
    patience: int = Field(default=75, ge=0, le=100)
    tactical_awareness: int = Field(default=75, ge=0, le=100)
    resource_efficiency: int = Field(default=75, ge=0, le=100)
    consistency: int = Field(default=80, ge=0, le=100)

    def archetype_label(self) -> str:
        """Return a human-readable tactical archetype diagnosis from the 5 gauges."""
        if self.aggression >= 72 and self.patience < 60:
            return "Entry / Atacante de Alto Riesgo (Mucha iniciativa, cuidar sobre-extensión)"
        if self.patience >= 78 and self.tactical_awareness >= 75:
            return "Estratega Calculador (Excelente disciplina, lectura de mapa y timings)"
        if self.resource_efficiency >= 80 and self.consistency >= 75:
            return "Maestro de Macro y Economía (Gestión sólida de recursos/boost/botín)"
        if self.aggression >= 65 and self.consistency >= 75:
            return "Playmaker Agresivo Controlado (Presión constante con buena supervivencia)"
        if self.patience < 55 or self.consistency < 55:
            return "En Ajuste Táctico (Priorizar cubrirse, extraer a tiempo y no apurar duelos)"
        return "Táctico Equilibrado (Buen balance entre ataque, paciencia y economía)"


class ModelUsageStats(BaseModel):
    """Cumulative token, request, latency, and cost metrics for one AI model/account."""

    key: str
    provider: str
    model: str
    account_label: str
    is_free_tier: bool = True
    requests_ok: int = 0
    requests_rate_limited: int = 0
    requests_failed: int = 0
    interventions_count: int = 0
    input_tokens: int = 0
    cached_tokens: int = 0
    output_tokens: int = 0
    actual_cost_usd: float = 0.0
    saved_cost_usd: float = 0.0
    avg_latency_ms: float = 0.0
    last_used_at: str = ""

    @property
    def total_tokens(self) -> int:
        return self.input_tokens + self.output_tokens


class MatchRecord(BaseModel):
    """Completed or active match/training session analytics."""

    match_id: str = Field(default_factory=lambda: uuid4().hex[:10])
    game_id: str = "example_cs2"
    game_name: str = "Counter-Strike 2"
    coach_mode: str = "match"
    started_at: str = Field(default_factory=_now_iso)
    ended_at: str = Field(default_factory=_now_iso)
    date_str: str = Field(default_factory=_today_str)
    duration_seconds: float = 0.0
    frames_analyzed: int = 0
    frames_filtered_saved: int = 0
    interventions: int = 0
    high_urgency_count: int = 0
    performance_score: int = Field(default=82, ge=0, le=100)
    playstyle: PlaystyleMetrics = Field(default_factory=PlaystyleMetrics)


class TelemetryData(BaseModel):
    """Root persisted telemetry document stored in `config/telemetry.json`."""

    installed_at: str = Field(default_factory=_now_iso)
    total_matches_all_time: int = 0
    total_coaching_seconds_all_time: float = 0.0
    total_frames_filtered_saved: int = 0
    daily_match_counts: dict[str, int] = Field(default_factory=dict)
    models: dict[str, ModelUsageStats] = Field(default_factory=dict)
    recent_matches: list[MatchRecord] = Field(default_factory=list)
    active_match: MatchRecord | None = None


class TelemetryStore:
    """Thread-safe manager for AI usage telemetry and player performance/playstyle gauges."""

    def __init__(self, path: Path = DEFAULT_TELEMETRY_PATH) -> None:
        self._path = path
        self._lock = threading.Lock()
        self._data = self._load_from_disk()
        self._active_match: MatchRecord | None = None
        self._active_match_start_mono: float = 0.0
        # Floating accumulators for current match gauges
        self._cur_perf: float = 84.0
        self._cur_agg: float = 52.0
        self._cur_pat: float = 78.0
        self._cur_tac: float = 78.0
        self._cur_eco: float = 78.0
        self._cur_con: float = 82.0
        self._last_disk_flush_mono: float = 0.0

    @property
    def path(self) -> Path:
        return self._path

    def _load_from_disk(self) -> TelemetryData:
        if not self._path.is_file():
            data = TelemetryData()
            self._write_atomic(data)
            return data
        try:
            raw = json.loads(self._path.read_text(encoding="utf-8"))
            return TelemetryData.model_validate(raw)
        except Exception as exc:
            logger.warning("No se pudo leer %s (%s); iniciando telemetría limpia.", self._path, exc)
            return TelemetryData()

    def reload(self) -> TelemetryData:
        """Reload telemetry from disk (used by GUI to observe live child-process updates)."""
        with self._lock:
            self._data = self._load_from_disk()
            return self._data.model_copy(deep=True)

    def _write_atomic(self, data: TelemetryData) -> None:
        with contextlib.suppress(OSError):
            self._path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self._path.with_suffix(".json.tmp")
            tmp.write_text(
                json.dumps(data.model_dump(), indent=2, ensure_ascii=False),
                encoding="utf-8",
            )
            os.replace(tmp, self._path)
            self._last_disk_flush_mono = time.monotonic()

    def _maybe_flush_throttled(self, *, force: bool = False) -> None:
        now = time.monotonic()
        if force or (now - self._last_disk_flush_mono >= 8.0):
            self._write_atomic(self._data)

    # ------------------------------------------------------------------ Match Lifecycle
    def start_match_session(
        self,
        game_id: str,
        game_name: str,
        coach_mode: str = "match",
    ) -> None:
        """Begin tracking a new match/practice session."""
        self.finish_active_match()
        with self._lock:
            self._active_match = MatchRecord(
                game_id=game_id,
                game_name=game_name,
                coach_mode=coach_mode,
                started_at=_now_iso(),
                date_str=_today_str(),
            )
            self._active_match_start_mono = time.monotonic()
            self._cur_perf = 84.0
            self._cur_agg = 52.0
            self._cur_pat = 78.0
            self._cur_tac = 78.0
            self._cur_eco = 78.0
            self._cur_con = 82.0

    def finish_active_match(self) -> MatchRecord | None:
        """Finalize and persist the current active match session if it had activity."""
        with self._lock:
            if self._active_match is None:
                return None
            match = self._active_match
            self._active_match = None
            self._data.active_match = None

            elapsed = max(1.0, time.monotonic() - self._active_match_start_mono)
            match.duration_seconds = round(elapsed, 1)
            match.ended_at = _now_iso()

            # Only count as a played match if at least 1 frame was analyzed or filtered
            if match.frames_analyzed == 0 and match.frames_filtered_saved < 3:
                return None

            self._sync_active_gauges_into_record(match)
            self._data.active_match = None
            self._commit_match_locked(match)
            self._write_atomic(self._data)
            return match

    def _sync_active_gauges_into_record(self, match: MatchRecord) -> None:
        match.ended_at = _now_iso()
        match.performance_score = _clamp_score(self._cur_perf)
        match.playstyle = PlaystyleMetrics(
            aggression=_clamp_score(self._cur_agg),
            patience=_clamp_score(self._cur_pat),
            tactical_awareness=_clamp_score(self._cur_tac),
            resource_efficiency=_clamp_score(self._cur_eco),
            consistency=_clamp_score(self._cur_con),
        )
        self._data.active_match = match

    def _commit_match_locked(self, match: MatchRecord) -> None:
        self._data.total_matches_all_time += 1
        self._data.total_coaching_seconds_all_time = round(
            self._data.total_coaching_seconds_all_time + match.duration_seconds, 1
        )
        day = match.date_str
        self._data.daily_match_counts[day] = self._data.daily_match_counts.get(day, 0) + 1
        self._data.recent_matches.append(match)
        if len(self._data.recent_matches) > MAX_STORED_RECENT_MATCHES:
            self._data.recent_matches = self._data.recent_matches[-MAX_STORED_RECENT_MATCHES:]

    # ------------------------------------------------------------------ Frame & AI Events
    def record_frame_filtered(self) -> None:
        """Record a frame skipped by pHash / cooldown (tokens saved)."""
        with self._lock:
            self._data.total_frames_filtered_saved += 1
            if self._active_match is not None:
                self._active_match.frames_filtered_saved += 1
            self._maybe_flush_throttled(force=False)

    def record_ai_call(
        self,
        *,
        provider: str,
        model: str,
        account_label: str,
        system_chars: int,
        efficiency_mode: str,
        used_cache: bool,
        latency_ms: float,
        response: CoachResponse | None,
        rate_limited: bool = False,
        failed: bool = False,
    ) -> None:
        """Record token usage, cost, savings, and update live playstyle/performance gauges."""
        with self._lock:
            key = f"{provider}:{model}:{account_label}"
            free_tier = is_provider_free_tier(provider, model)
            stats = self._data.models.get(key)
            if stats is None:
                stats = ModelUsageStats(
                    key=key,
                    provider=provider,
                    model=model,
                    account_label=account_label,
                    is_free_tier=free_tier,
                )
                self._data.models[key] = stats

            stats.last_used_at = _now_iso()

            if rate_limited:
                stats.requests_rate_limited += 1
                self._maybe_flush_throttled(force=False)
                return

            if failed or response is None:
                stats.requests_failed += 1
                self._maybe_flush_throttled(force=False)
                return

            # Estimate tokens for this multimodal call
            text_tokens = max(120, system_chars // 4)
            img_tokens = 258 if efficiency_mode in ("eco",) else 516
            cached_toks = int(text_tokens * 0.85) if used_cache else 0
            uncached_input_toks = (text_tokens - cached_toks) + img_tokens
            total_input_toks = cached_toks + uncached_input_toks
            out_chars = len(response.overlay_text) + len(response.voice_line) + 42
            output_toks = max(16, out_chars // 3)

            in_rate, cached_rate, out_rate = _lookup_pricing(model)
            full_unoptimized_cost = (
                (total_input_toks / 1_000_000.0) * in_rate
                + (output_toks / 1_000_000.0) * out_rate
            )
            optimized_cost = (
                (uncached_input_toks / 1_000_000.0) * in_rate
                + (cached_toks / 1_000_000.0) * cached_rate
                + (output_toks / 1_000_000.0) * out_rate
            )

            prev_n = stats.requests_ok
            stats.requests_ok += 1
            stats.input_tokens += total_input_toks
            stats.cached_tokens += cached_toks
            stats.output_tokens += output_toks
            stats.avg_latency_ms = round(
                ((stats.avg_latency_ms * prev_n) + latency_ms) / stats.requests_ok, 1
            )

            if free_tier:
                stats.actual_cost_usd = 0.0
                stats.saved_cost_usd = round(stats.saved_cost_usd + full_unoptimized_cost, 6)
            else:
                stats.actual_cost_usd = round(stats.actual_cost_usd + optimized_cost, 6)
                stats.saved_cost_usd = round(
                    stats.saved_cost_usd + max(0.0, full_unoptimized_cost - optimized_cost), 6
                )

            if response.should_intervene:
                stats.interventions_count += 1

            self._update_playstyle_from_response_locked(response)
            self._maybe_flush_throttled(force=True)

    def _update_playstyle_from_response_locked(self, response: CoachResponse) -> None:
        if self._active_match is None:
            self._active_match = MatchRecord()
            self._active_match_start_mono = time.monotonic()

        match = self._active_match
        match.frames_analyzed += 1
        match.duration_seconds = round(
            max(1.0, time.monotonic() - self._active_match_start_mono), 1
        )

        if not response.should_intervene:
            # Clean gameplay frame: player made no tactical errors
            self._cur_perf = min(98.0, self._cur_perf + 1.6)
            self._cur_pat = min(96.0, self._cur_pat + 0.9)
            self._cur_tac = min(96.0, self._cur_tac + 0.9)
            self._cur_eco = min(96.0, self._cur_eco + 0.9)
            self._cur_con = min(98.0, self._cur_con + 1.2)
            self._sync_active_gauges_into_record(match)
            return

        match.interventions += 1
        text = f"{response.overlay_text} {response.voice_line}".lower()

        if response.urgency == "high":
            match.high_urgency_count += 1
            self._cur_perf = max(18.0, self._cur_perf - 3.6)
            self._cur_con = max(20.0, self._cur_con - 3.2)
            urgency_weight = 1.4
        elif response.urgency == "medium":
            self._cur_perf = max(22.0, self._cur_perf - 1.5)
            self._cur_con = max(25.0, self._cur_con - 1.2)
            urgency_weight = 1.0
        else:
            self._cur_perf = max(28.0, self._cur_perf - 0.4)
            urgency_weight = 0.6

        if any(k in text for k in _KW_AGGRESSION):
            self._cur_agg = min(98.0, self._cur_agg + 4.5 * urgency_weight)
            self._cur_pat = max(18.0, self._cur_pat - 2.4 * urgency_weight)

        if any(k in text for k in _KW_PATIENCE):
            self._cur_agg = min(96.0, self._cur_agg + 2.5 * urgency_weight)
            self._cur_pat = max(18.0, self._cur_pat - 3.0 * urgency_weight)

        if any(k in text for k in _KW_TACTICAL):
            self._cur_tac = max(20.0, self._cur_tac - 2.6 * urgency_weight)
        else:
            self._cur_tac = min(95.0, self._cur_tac + 0.5)

        if any(k in text for k in _KW_ECONOMY):
            self._cur_eco = max(20.0, self._cur_eco - 2.8 * urgency_weight)
        else:
            self._cur_eco = min(95.0, self._cur_eco + 0.5)

        self._sync_active_gauges_into_record(match)

    # ------------------------------------------------------------------ Queries & Simulation
    def record_simulated_match(
        self,
        *,
        game_id: str,
        game_name: str,
        performance_score: int,
        playstyle: PlaystyleMetrics,
        provider: str = "gemini",
        model: str = "gemini-2.5-flash",
        account_label: str = "Google Gemini (AI Studio)",
        frames_analyzed: int = 18,
        frames_filtered_saved: int = 142,
        interventions: int = 5,
        duration_seconds: float = 780.0,
    ) -> MatchRecord:
        """Record a complete match entry (used by tests and GUI interactive simulator)."""
        with self._lock:
            match = MatchRecord(
                game_id=game_id,
                game_name=game_name,
                coach_mode="match",
                started_at=_now_iso(),
                ended_at=_now_iso(),
                date_str=_today_str(),
                duration_seconds=duration_seconds,
                frames_analyzed=frames_analyzed,
                frames_filtered_saved=frames_filtered_saved,
                interventions=interventions,
                high_urgency_count=max(0, (85 - performance_score) // 15),
                performance_score=_clamp_score(performance_score),
                playstyle=playstyle,
            )
            self._data.total_frames_filtered_saved += frames_filtered_saved
            self._commit_match_locked(match)

            # Also record realistic AI model usage for the match
            key = f"{provider}:{model}:{account_label}"
            free_tier = is_provider_free_tier(provider, model)
            stats = self._data.models.get(key)
            if stats is None:
                stats = ModelUsageStats(
                    key=key,
                    provider=provider,
                    model=model,
                    account_label=account_label,
                    is_free_tier=free_tier,
                )
                self._data.models[key] = stats

            in_toks = frames_analyzed * 920
            cached_toks = int(frames_analyzed * 560)
            out_toks = frames_analyzed * 32
            in_rate, _, out_rate = _lookup_pricing(model)
            saved_val = ((in_toks / 1_000_000.0) * in_rate) + ((out_toks / 1_000_000.0) * out_rate)

            stats.requests_ok += frames_analyzed
            stats.interventions_count += interventions
            stats.input_tokens += in_toks
            stats.cached_tokens += cached_toks
            stats.output_tokens += out_toks
            stats.avg_latency_ms = 420.0 if stats.avg_latency_ms == 0 else stats.avg_latency_ms
            stats.saved_cost_usd = round(stats.saved_cost_usd + saved_val, 6)
            stats.last_used_at = _now_iso()

            self._write_atomic(self._data)
            return match

    def get_dashboard_summary(self, game_filter: str = "all") -> dict[str, Any]:
        """Compute aggregated metrics for the GUI Player Performance & AI Usage dashboard."""
        with self._lock:
            today = _today_str()
            matches_today = self._data.daily_match_counts.get(today, 0)
            total_matches = self._data.total_matches_all_time

            all_matches = list(self._data.recent_matches)
            live_source = self._active_match if self._active_match is not None else self._data.active_match
            if (
                live_source is not None
                and live_source.frames_analyzed > 0
            ):
                live_copy = live_source.model_copy(deep=True)
                if self._active_match is not None:
                    self._sync_active_gauges_into_record(live_copy)
                all_matches.append(live_copy)
                matches_today += 1
                total_matches += 1

            if game_filter and game_filter != "all":
                filtered = [m for m in all_matches if m.game_id == game_filter]
            else:
                filtered = all_matches

            if filtered:
                last_match = filtered[-1]
                last_perf = last_match.performance_score
                window = filtered[-10:]
                avg_perf = _clamp_score(
                    sum(m.performance_score for m in window) / len(window)
                )
                avg_style = PlaystyleMetrics(
                    aggression=_clamp_score(
                        sum(m.playstyle.aggression for m in window) / len(window)
                    ),
                    patience=_clamp_score(
                        sum(m.playstyle.patience for m in window) / len(window)
                    ),
                    tactical_awareness=_clamp_score(
                        sum(m.playstyle.tactical_awareness for m in window) / len(window)
                    ),
                    resource_efficiency=_clamp_score(
                        sum(m.playstyle.resource_efficiency for m in window) / len(window)
                    ),
                    consistency=_clamp_score(
                        sum(m.playstyle.consistency for m in window) / len(window)
                    ),
                )
                last_match_label = (
                    f"{last_match.game_name} ({last_match.ended_at[-8:]})"
                )
            else:
                last_perf = 0
                avg_perf = 0
                avg_style = PlaystyleMetrics(
                    aggression=0,
                    patience=0,
                    tactical_awareness=0,
                    resource_efficiency=0,
                    consistency=0,
                )
                last_match_label = "Sin partidas registradas aún"

            models_list = sorted(
                self._data.models.values(),
                key=lambda s: (s.requests_ok + s.requests_rate_limited, s.total_tokens),
                reverse=True,
            )
            total_input = sum(m.input_tokens for m in models_list)
            total_cached = sum(m.cached_tokens for m in models_list)
            total_output = sum(m.output_tokens for m in models_list)
            total_cost = round(sum(m.actual_cost_usd for m in models_list), 4)
            # Also add estimated savings from pHash-filtered frames (~650 tokens saved per skipped frame)
            phash_saved_usd = (self._data.total_frames_filtered_saved * 650 / 1_000_000.0) * 0.15
            total_saved = round(
                sum(m.saved_cost_usd for m in models_list) + phash_saved_usd, 4
            )

            return {
                "installed_at": self._data.installed_at,
                "matches_today": matches_today,
                "total_matches_all_time": total_matches,
                "total_coaching_minutes": round(
                    self._data.total_coaching_seconds_all_time / 60.0, 1
                ),
                "total_frames_filtered_saved": self._data.total_frames_filtered_saved,
                "last_match_performance": last_perf,
                "recent_avg_performance": avg_perf,
                "last_match_label": last_match_label,
                "playstyle": avg_style,
                "archetype": (
                    avg_style.archetype_label()
                    if filtered
                    else "Jugá tu primera partida (o tocá 'Simular Partida Demo') para ver tu diagnóstico."
                ),
                "recent_matches": list(reversed(filtered[-12:])),
                "models": [m.model_copy(deep=True) for m in models_list],
                "total_input_tokens": total_input,
                "total_cached_tokens": total_cached,
                "total_output_tokens": total_output,
                "total_tokens": total_input + total_output,
                "total_actual_cost_usd": total_cost,
                "total_saved_cost_usd": total_saved,
            }

    def reset_all(self) -> None:
        """Reset all telemetry counters while preserving initial install date."""
        with self._lock:
            installed = self._data.installed_at
            self._data = TelemetryData(installed_at=installed)
            self._active_match = None
            self._write_atomic(self._data)
