"""End-to-end integration tests for GameCopilotOrchestrator."""

from __future__ import annotations

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

    async def prepare_session(self, profile: GameProfile) -> bool:
        self.sessions_prepared += 1
        return True

    async def analyze_frame(
        self,
        image_bytes: bytes,
        mime_type: str,
        profile: GameProfile,
    ) -> CoachResponse | None:
        self.frames_analyzed += 1
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
        min_inference_interval_seconds=0.5,
    )
    orchestrator = GameCopilotOrchestrator(
        settings=settings,
        test_mode=True,
        forced_profile_id="example_cs2",
        use_synthetic_mock=True,
        dry_run=True,
    )
    await orchestrator.run(max_runtime_seconds=1.6)
    snap = orchestrator.metrics.snapshot()
    assert snap["frames_filtered"] >= 1


async def test_orchestrator_mocked_ai_intervention_and_cooldown() -> None:
    settings = AppSettings(
        enable_voice=False,
        enable_overlay=False,
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
    await orchestrator.run(max_runtime_seconds=2.2)

    snap = orchestrator.metrics.snapshot()
    assert mock_client.sessions_prepared == 1
    assert mock_client.sessions_closed == 1
    # Cooldown of 10s in example_cs2 guarantees only 1 intervention within 2.2s
    assert mock_client.frames_analyzed == 1
    assert snap["interventions_delivered"] == 1
    assert snap["api_errors"] == 0
