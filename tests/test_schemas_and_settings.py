"""Unit tests for schemas, InterventionStateMachine, SecretRedactingFilter, and RuntimeMetrics."""

from __future__ import annotations

import logging

import pytest
from pydantic import ValidationError

from src.config.settings import (
    AppSettings,
    RuntimeMetrics,
    SecretRedactingFilter,
    configure_logging,
)
from src.pipeline.schemas import CoachResponse, InterventionState, InterventionStateMachine


def test_coach_response_actionable_logic() -> None:
    silent = CoachResponse(should_intervene=False, urgency="low", overlay_text="", voice_line="")
    assert not silent.is_actionable

    empty_intervention = CoachResponse(
        should_intervene=True, urgency="high", overlay_text="   ", voice_line=""
    )
    assert not empty_intervention.is_actionable

    valid = CoachResponse(
        should_intervene=True,
        urgency="high",
        overlay_text="Recargá en cobertura",
        voice_line="Quedan cuatro balas, recargá atrás de la caja.",
    )
    assert valid.is_actionable

    with pytest.raises(ValidationError):
        CoachResponse(
            should_intervene=True,
            urgency="critical",  # type: ignore[arg-type]
            overlay_text="Bad urgency",
            voice_line="",
        )


def test_state_machine_full_lifecycle() -> None:
    sm = InterventionStateMachine()
    assert sm.state == InterventionState.IDLE

    # Initially allowed
    allowed, reason = sm.can_analyze(cooldown_seconds=10.0, min_inference_interval=2.0, now=100.0)
    assert allowed and reason == "ok"

    # During analysis -> busy
    sm.begin_analysis(now=100.0)
    assert sm.state == InterventionState.ANALYZING
    allowed, reason = sm.can_analyze(cooldown_seconds=10.0, min_inference_interval=2.0, now=100.5)
    assert not allowed and "analyzing" in reason

    # Non-intervening response returns to IDLE and respects min_inference_interval only
    silent = CoachResponse(should_intervene=False, urgency="low", overlay_text="", voice_line="")
    assert sm.finish_analysis(silent) is False
    assert sm.state == InterventionState.IDLE

    allowed, reason = sm.can_analyze(cooldown_seconds=10.0, min_inference_interval=2.0, now=101.0)
    assert not allowed and "min inference interval" in reason

    allowed, _ = sm.can_analyze(cooldown_seconds=10.0, min_inference_interval=2.0, now=102.5)
    assert allowed

    # Intervening response -> DELIVERING -> COOLDOWN
    sm.begin_analysis(now=103.0)
    alert = CoachResponse(
        should_intervene=True,
        urgency="high",
        overlay_text="Cuidá espalda",
        voice_line="Atento a la espalda, radar abierto.",
    )
    assert sm.finish_analysis(alert) is True
    assert sm.state == InterventionState.DELIVERING

    sm.complete_delivery(now=103.2)
    assert sm.state == InterventionState.COOLDOWN
    assert sm.total_interventions == 1
    assert sm.total_analyses == 2

    # Blocked during cooldown
    allowed, reason = sm.can_analyze(cooldown_seconds=10.0, min_inference_interval=2.0, now=110.0)
    assert not allowed and "cooldown" in reason

    # Allowed once cooldown expires
    allowed, _ = sm.can_analyze(cooldown_seconds=10.0, min_inference_interval=2.0, now=114.0)
    assert allowed
    assert sm.state == InterventionState.IDLE


def test_secret_redacting_filter_iso27001() -> None:
    fake_secret = "MY_SUPER_SECRET_TOKEN_987654"
    fake_google_key = "AIzaSyAbCdEfGhIjKlMnOpQrStUvWxYz123456"
    fake_openai_key = "sk-proj-AbCdEfGhIjKlMnOpQrStUvWxYz123456"
    fake_groq_key = "gsk_AbCdEfGhIjKlMnOpQrStUvWxYz123456"
    redactor = SecretRedactingFilter(secrets=(fake_secret, "shortKey"))

    # Pass an Exception instance in args (not a plain str) + query string key
    exc = RuntimeError(
        f"Failed https://example.com?key={fake_google_key} with {fake_openai_key} "
        f"and {fake_groq_key} and Bearer secretBearerToken123456 and shortKey"
    )
    record = logging.LogRecord(
        name="test",
        level=logging.ERROR,
        pathname=__file__,
        lineno=1,
        msg="API error: %s | raw=%s",
        args=(exc, fake_secret),
        exc_info=None,
    )
    assert redactor.filter(record) is True
    rendered = record.getMessage()
    for leaked in (
        fake_secret,
        fake_google_key,
        fake_openai_key,
        fake_groq_key,
        "secretBearerToken123456",
        "shortKey",
    ):
        assert leaked not in rendered
    assert "[REDACTED_SECRET]" in rendered
    assert "[REDACTED_API_KEY]" in rendered
    assert "[REDACTED_TOKEN]" in rendered

    # Verify configure_logging replaces previous SecretRedactingFilter when new secrets arrive
    configure_logging("INFO", secrets=("initialSecret111",))
    configure_logging("INFO", secrets=("updatedSecret222",))
    root_filters = [
        f
        for h in logging.getLogger().handlers
        for f in h.filters
        if isinstance(f, SecretRedactingFilter)
    ]
    assert root_filters
    probe = logging.LogRecord("t", logging.INFO, __file__, 1, "val=%s", ("updatedSecret222",), None)
    root_filters[0].filter(probe)
    assert "updatedSecret222" not in probe.getMessage()


def test_runtime_metrics_snapshot() -> None:
    metrics = RuntimeMetrics()
    metrics.record_capture(12.0)
    metrics.record_capture(18.0)
    metrics.record_filter(10.0, passed=False)
    metrics.record_filter(20.0, passed=True, payload_kb=42.5)
    metrics.record_inference(450.0, intervened=True, failed=False)

    snap = metrics.snapshot()
    assert snap["frames_captured"] == 2
    assert snap["avg_capture_ms"] == 15.0
    assert snap["filter_drop_rate_pct"] == 50.0
    assert snap["avg_payload_kb"] == 42.5
    assert snap["interventions_delivered"] == 1
    assert snap["api_errors"] == 0


def test_settings_placeholder_key_rejected() -> None:
    settings = AppSettings(gemini_api_key="your_api_key_here")  # type: ignore[arg-type]
    assert settings.has_valid_api_key() is False
