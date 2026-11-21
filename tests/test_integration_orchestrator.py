"""End-to-end integration tests for GameCopilotOrchestrator."""

from __future__ import annotations

import asyncio
from typing import Any

from src.config.settings import AppSettings
from src.main import GameCopilotOrchestrator
from src.pipeline.schemas import CoachResponse
from src.profiles.loader import GameProfile


class _MockCoachClient:
    """Deterministic async AI client double for end-to-end pipeline verification."""

    def __init__(self) -> None:
        self.sessions_prepared = 0
        self.sessions_closed = 0
        self.frames_analyzed = 0
        self.received_histories: list[str] = []
        self.received_force_flags: list[bool] = []

    async def prepare_session(self, profile: GameProfile) -> bool:
        self.sessions_prepared += 1
        return True

    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
        *,
        recent_history: str = "",
        force_advice: bool = False,
    ) -> CoachResponse | None:
        self.frames_analyzed += 1
        self.received_histories.append(recent_history)
        self.received_force_flags.append(force_advice)
        assert len(image_bytes) > 0
        assert mime_type in ("image/webp", "image/jpeg")
        return CoachResponse(
            should_intervene=True,
            urgency="high",
            overlay_text="C4 plantado: jugá al tiempo",
            voice_line="Jugá al tiempo, no peekees.",
        )

    async def close_session(self) -> None:
        self.sessions_closed += 1


async def test_orchestrator_dry_run_end_to_end() -> None:
    settings = AppSettings(
        enable_voice=False,
        enable_overlay=False,
        enable_hotkeys=False,
        min_inference_interval_seconds=0.5,
    )
    orchestrator = GameCopilotOrchestrator(
        settings=settings,
        test_mode=True,
        forced_profile_id="example_cs2",
        use_synthetic_mock=True,
        dry_run=True,
    )
    await orchestrator.run(max_runtime_seconds=2.5)
    snap = orchestrator.metrics.snapshot()
    assert snap["frames_filtered"] >= 1


async def test_orchestrator_mocked_ai_intervention_cooldown_and_f10_on_demand() -> None:
    settings = AppSettings(
        enable_voice=False,
        enable_overlay=False,
        enable_hotkeys=False,
        min_inference_interval_seconds=0.5,
    )
    mock_client: Any = _MockCoachClient()
    orchestrator = GameCopilotOrchestrator(
        settings=settings,
        test_mode=True,
        forced_profile_id="example_cs2",
        use_synthetic_mock=True,
        dry_run=False,
        ai_client=mock_client,
    )

    async def _trigger_f10_mid_run() -> None:
        await asyncio.sleep(1.3)
        # During 10s cooldown, pressing F10 must force an immediate 2nd analysis
        # carrying the 1st intervention in anti-repetition recent_history!
        orchestrator.trigger_on_demand_analysis()

    trigger_task = asyncio.create_task(_trigger_f10_mid_run())
    await orchestrator.run(max_runtime_seconds=2.7)
    await trigger_task

    snap = orchestrator.metrics.snapshot()
    assert mock_client.sessions_prepared == 1
    assert mock_client.sessions_closed == 1
    assert mock_client.frames_analyzed == 2
    assert mock_client.received_force_flags == [False, True]
    assert mock_client.received_histories[0] == ""
    assert "C4 plantado" in mock_client.received_histories[1]
    assert snap["interventions_delivered"] == 2
    assert snap["api_errors"] == 0
