"""Pydantic schemas for Gemini Structured Outputs and Intervention State Machine."""

from __future__ import annotations

import logging
import threading
import time
from collections import deque
from dataclasses import dataclass
from enum import StrEnum
from typing import Literal

from pydantic import BaseModel, Field

logger = logging.getLogger(__name__)

UrgencyLevel = Literal["low", "medium", "high"]


class CoachResponse(BaseModel):
    """Strict structured output returned by Gemini on every analyzed frame."""

    should_intervene: bool = Field(
        description=(
            "True únicamente si existe una recomendación táctica accionable, riesgo inmediato "
            "o error evitable claramente visible en el frame. False en caso contrario."
        )
    )
    urgency: UrgencyLevel = Field(
        default="low",
        description="Nivel de prioridad de la intervención: 'low', 'medium' o 'high'.",
    )
    overlay_text: str = Field(
        default="",
        description=(
            "Texto ultra breve (máximo 8 palabras) para el overlay visual. "
            "Cadena vacía si should_intervene es false."
        ),
    )
    voice_line: str = Field(
        default="",
        description=(
            "Frase corta natural (máximo 12 palabras) para TTS por voz. "
            "Cadena vacía si should_intervene es false."
        ),
    )
    match_context: str = Field(
        default="",
        description=(
            "Resumen opcional de 1 línea del estado observado en pantalla "
            "(marcador/reloj, compañeros, rivales y recursos)."
        ),
    )

    @property
    def is_actionable(self) -> bool:
        """Return True only when the model explicitly requests intervention with content."""
        return self.should_intervene and bool(
            self.overlay_text.strip() or self.voice_line.strip()
        )


class InterventionState(StrEnum):
    """Lifecycle states of a coaching intervention."""

    IDLE = "idle"
    ANALYZING = "analyzing"
    DELIVERING = "delivering"
    COOLDOWN = "cooldown"


class InterventionStateMachine:
    """Coordinates coaching cadence, cooldown windows, and active delivery state.

    State transitions:
        IDLE -> ANALYZING -> (no intervention) -> IDLE
        IDLE -> ANALYZING -> DELIVERING -> COOLDOWN -> IDLE
    """

    def __init__(self) -> None:
        self._state: InterventionState = InterventionState.IDLE
        self._last_analysis_time: float = 0.0
        self._last_intervention_time: float = 0.0
        self._last_urgency: UrgencyLevel | None = None
        self._total_analyses: int = 0
        self._total_interventions: int = 0
        self._lock = threading.Lock()

    @property
    def state(self) -> InterventionState:
        """Return current lifecycle state."""
        with self._lock:
            return self._state

    @property
    def is_busy(self) -> bool:
        """Return True while an analysis or output delivery is actively in progress."""
        with self._lock:
            return self._state in (InterventionState.ANALYZING, InterventionState.DELIVERING)

    @property
    def total_analyses(self) -> int:
        return self._total_analyses

    @property
    def total_interventions(self) -> int:
        return self._total_interventions

    def seconds_since_last_intervention(self, now: float | None = None) -> float:
        """Return elapsed seconds since the last real coach intervention."""
        if self._last_intervention_time <= 0.0:
            return float("inf")
        current = time.monotonic() if now is None else now
        return max(0.0, current - self._last_intervention_time)

    def seconds_since_last_analysis(self, now: float | None = None) -> float:
        """Return elapsed seconds since the last frame was dispatched to Gemini."""
        if self._last_analysis_time <= 0.0:
            return float("inf")
        current = time.monotonic() if now is None else now
        return max(0.0, current - self._last_analysis_time)

    def can_analyze(
        self,
        cooldown_seconds: float,
        min_inference_interval: float,
        now: float | None = None,
    ) -> tuple[bool, str]:
        """Evaluate whether a new frame is allowed to enter Gemini inference."""
        current = time.monotonic() if now is None else now
        with self._lock:
            if self._state in (InterventionState.ANALYZING, InterventionState.DELIVERING):
                return False, f"state={self._state.value}"

            since_intervention = self.seconds_since_last_intervention(current)
            if since_intervention < cooldown_seconds:
                remaining = cooldown_seconds - since_intervention
                return False, f"intervention cooldown ({remaining:.1f}s remaining)"

            if self._state == InterventionState.COOLDOWN:
                self._state = InterventionState.IDLE

            since_analysis = self.seconds_since_last_analysis(current)
            if since_analysis < min_inference_interval:
                remaining = min_inference_interval - since_analysis
                return False, f"min inference interval ({remaining:.1f}s remaining)"

            return True, "ok"

    def begin_analysis(self, now: float | None = None) -> None:
        """Transition into ANALYZING state."""
        current = time.monotonic() if now is None else now
        with self._lock:
            self._state = InterventionState.ANALYZING
            self._last_analysis_time = current
            self._total_analyses += 1

    def finish_analysis(self, response: CoachResponse | None) -> bool:
        """Transition after Gemini returns a response.

        Returns True if an actionable intervention should now be delivered.
        """
        with self._lock:
            if response is not None and response.is_actionable:
                self._state = InterventionState.DELIVERING
                self._last_urgency = response.urgency
                return True

            self._state = InterventionState.IDLE
            return False

    def complete_delivery(self, now: float | None = None) -> None:
        """Mark intervention delivery complete and enter COOLDOWN."""
        current = time.monotonic() if now is None else now
        with self._lock:
            self._last_intervention_time = current
            self._total_interventions += 1
            self._state = InterventionState.COOLDOWN
            total = self._total_interventions
            urgency = self._last_urgency
        logger.debug(
            "StateMachine -> COOLDOWN (total_interventions=%d, urgency=%s)",
            total,
            urgency,
        )

    def abort_analysis(self) -> None:
        """Recover cleanly to IDLE if an inference call raises an error."""
        with self._lock:
            self._state = InterventionState.IDLE

    def reset(self) -> None:
        """Reset all timers and states (e.g., when switching active game profile)."""
        with self._lock:
            self._state = InterventionState.IDLE
            self._last_analysis_time = 0.0
            self._last_intervention_time = 0.0
            self._last_urgency = None


@dataclass(frozen=True, slots=True)
class InterventionRecord:
    """Snapshot of a delivered coaching tip stored for short-term anti-repetition memory."""

    timestamp: float
    urgency: UrgencyLevel
    overlay_text: str
    voice_line: str


class InterventionMemory:
    """Bounded rolling memory of recent interventions and match context blackboard."""

    def __init__(self, max_items: int = 3, ttl_seconds: float = 90.0) -> None:
        self._max_items = max(1, max_items)
        self._ttl_seconds = max(5.0, ttl_seconds)
        self._records: deque[InterventionRecord] = deque(maxlen=self._max_items)
        self._context_notes: deque[tuple[float, str]] = deque(maxlen=5)

    @staticmethod
    def _significant_tokens(text: str) -> set[str]:
        cleaned = "".join(ch.lower() if ch.isalnum() else " " for ch in text)
        return {w for w in cleaned.split() if len(w) >= 4}

    def is_repetitive(self, response: CoachResponse, now: float | None = None) -> bool:
        """Return True if `response` duplicates or heavily overlaps any recent intervention."""
        if not response.is_actionable:
            return False
        current = time.monotonic() if now is None else now
        recent_items = self.recent(now=current)
        if not recent_items:
            return False

        cand_ov = response.overlay_text.strip().lower()
        cand_vc = response.voice_line.strip().lower()
        cand_tokens = self._significant_tokens(f"{cand_ov} {cand_vc}")

        for rec in recent_items:
            rec_ov = rec.overlay_text.strip().lower()
            rec_vc = rec.voice_line.strip().lower()
            if (cand_ov and cand_ov == rec_ov) or (cand_vc and cand_vc == rec_vc):
                return True
            rec_tokens = self._significant_tokens(f"{rec_ov} {rec_vc}")
            if cand_tokens and rec_tokens:
                overlap = len(cand_tokens & rec_tokens) / min(len(cand_tokens), len(rec_tokens))
                if overlap >= 0.58:
                    return True
        return False

    def record_context(self, match_context: str, now: float | None = None) -> None:
        """Record a 1-line match state snapshot (teammates, rivals, score, phase)."""
        clean = match_context.strip()
        if not clean:
            return
        current = time.monotonic() if now is None else now
        if self._context_notes and self._context_notes[-1][1].lower() == clean.lower():
            return
        self._context_notes.append((current, clean[:160]))

    def record(self, response: CoachResponse, now: float | None = None) -> None:
        """Store an actionable coach response and any observed match context."""
        current = time.monotonic() if now is None else now
        if response.match_context.strip():
            self.record_context(response.match_context, now=current)
        if not response.is_actionable:
            return
        self._records.append(
            InterventionRecord(
                timestamp=current,
                urgency=response.urgency,
                overlay_text=response.overlay_text.strip(),
                voice_line=response.voice_line.strip(),
            )
        )

    def recent(self, now: float | None = None) -> list[InterventionRecord]:
        """Return unexpired recent interventions in chronological order."""
        current = time.monotonic() if now is None else now
        cutoff = current - self._ttl_seconds
        while self._records and self._records[0].timestamp < cutoff:
            self._records.popleft()
        return list(self._records)

    def recent_context_notes(self, now: float | None = None) -> list[tuple[int, str]]:
        """Return unexpired match state blackboard snapshots as `(age_seconds, text)`."""
        current = time.monotonic() if now is None else now
        cutoff = current - max(self._ttl_seconds, 180.0)
        while self._context_notes and self._context_notes[0][0] < cutoff:
            self._context_notes.popleft()
        return [(max(0, int(current - ts)), note) for ts, note in self._context_notes]

    def format_prompt_context(self, now: float | None = None) -> str:
        """Format match context blackboard + anti-repetition history for the AI prompt."""
        current = time.monotonic() if now is None else now
        items = self.recent(now=current)
        ctx_notes = self.recent_context_notes(now=current)
        if not items and not ctx_notes:
            return ""

        blocks: list[str] = []
        if ctx_notes:
            ctx_lines = [
                "Contexto acumulado de la partida (evolución de marcador, compañeros y rivales):"
            ]
            for age_s, note in ctx_notes:
                ctx_lines.append(f"- hace {age_s}s: {note}")
            blocks.append("\n".join(ctx_lines))

        if items:
            lines = [
                "Intervenciones recientes ya comunicadas al jugador "
                "(PROHIBIDO repetir el mismo consejo, frase o eje táctico):"
            ]
            for rec in items:
                age_s = max(0, int(current - rec.timestamp))
                summary = rec.overlay_text or rec.voice_line
                lines.append(f"- hace {age_s}s [{rec.urgency.upper()}]: {summary}")
            blocks.append("\n".join(lines))

        return "\n\n".join(blocks)

    def clear(self) -> None:
        """Erase rolling memory when switching game profiles."""
        self._records.clear()
        self._context_notes.clear()
