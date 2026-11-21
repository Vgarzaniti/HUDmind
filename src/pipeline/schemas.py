"""Pydantic schemas for Gemini Structured Outputs and Intervention State Machine."""

from __future__ import annotations

import logging
import time
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

    @property
    def state(self) -> InterventionState:
        """Return current state (automatically clearing expired cooldown lazily)."""
        return self._state

    @property
    def is_busy(self) -> bool:
        """Return True while an analysis or output delivery is actively in progress."""
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

        if self.is_busy:
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
        self._state = InterventionState.ANALYZING
        self._last_analysis_time = current
        self._total_analyses += 1

    def finish_analysis(self, response: CoachResponse | None) -> bool:
        """Transition after Gemini returns a response.

        Returns True if an actionable intervention should now be delivered.
        """
        if response is not None and response.is_actionable:
            self._state = InterventionState.DELIVERING
            self._last_urgency = response.urgency
            return True

        self._state = InterventionState.IDLE
        return False

    def complete_delivery(self, now: float | None = None) -> None:
        """Mark intervention delivery complete and enter COOLDOWN."""
        current = time.monotonic() if now is None else now
        self._last_intervention_time = current
        self._total_interventions += 1
        self._state = InterventionState.COOLDOWN
        logger.debug(
            "StateMachine -> COOLDOWN (total_interventions=%d, urgency=%s)",
            self._total_interventions,
            self._last_urgency,
        )

    def abort_analysis(self) -> None:
        """Recover cleanly to IDLE if an inference call raises an error."""
        self._state = InterventionState.IDLE

    def reset(self) -> None:
        """Reset all timers and states (e.g., when switching active game profile)."""
        self._state = InterventionState.IDLE
        self._last_analysis_time = 0.0
        self._last_intervention_time = 0.0
        self._last_urgency = None
