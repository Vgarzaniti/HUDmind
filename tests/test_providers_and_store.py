"""Unit tests for multi-provider failover routing, keyless local providers, and GUI store."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest
from pydantic import ValidationError

from src.ai.gemini_client import build_language_instruction
from src.ai.providers import (
    BaseAIProvider,
    GeminiProviderAdapter,
    ProviderCallResult,
    ProviderFailoverRouter,
    build_auto_free_fallback_accounts,
    extract_coach_response_from_text,
)
from src.config.settings import AppSettings
from src.config.telemetry import PlaystyleMetrics, TelemetryStore
from src.config.user_store import (
    ProviderAccount,
    load_accounts,
    save_accounts,
    save_user_settings,
)
from src.pipeline.schemas import CoachResponse
from src.profiles.loader import GameProfile, GameProfileLoader


class _StubProvider(BaseAIProvider):
    def __init__(
        self,
        account: ProviderAccount,
        settings: AppSettings,
        outcome: ProviderCallResult,
    ) -> None:
        super().__init__(account, settings)
        self._outcome = outcome
        self.calls: list[dict[str, Any]] = []

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
        self.calls.append({"text_language": text_language, "voice_language": voice_language})
        return self._outcome

    async def test_connection(self) -> tuple[bool, str]:
        return True, "ok"


async def test_failover_router_rotates_on_quota_429_to_second_account() -> None:
    profile = GameProfileLoader(Path("profiles")).get_by_id("example_cs2")
    assert profile is not None

    acc1 = ProviderAccount(
        id="acc-1", label="Gemini 1", provider="gemini", api_key="key-one-1234", priority=1
    )
    acc2 = ProviderAccount(
        id="acc-2", label="Ollama Local", provider="ollama", api_key="", model="qwen2.5vl:3b", priority=2
    )
    assert acc1.is_ready() and acc2.is_ready()  # Ollama requires NO api key

    expected = CoachResponse(
        should_intervene=True,
        urgency="high",
        overlay_text="Reload in cover",
        voice_line="Four bullets left, reload behind cover.",
    )
    outcomes = {
        "acc-1": ProviderCallResult(error_code=429, error_message="quota exceeded", quota_or_auth_failure=True),
        "acc-2": ProviderCallResult(response=expected),
    }
    created: dict[str, _StubProvider] = {}
    failovers: list[tuple[str, str]] = []

    def _factory(acc: ProviderAccount, cfg: AppSettings) -> BaseAIProvider:
        stub = _StubProvider(acc, cfg, outcomes[acc.id])
        created[acc.id] = stub
        return stub

    settings = AppSettings(text_language="en", voice_language="es")
    router = ProviderFailoverRouter(
        accounts=[acc2, acc1],  # unsorted on purpose to test priority sorting
        settings=settings,
        on_failover=lambda old, new: failovers.append((old, new)),
        provider_factory=_factory,
    )

    await router.prepare_session(profile)
    result = await router.analyze_frame(b"jpeg-bytes", "image/jpeg", profile)

    assert result == expected
    assert failovers == [("Gemini 1", "Ollama Local")]
    assert "Ollama Local" in router.active_account_label
    assert created["acc-2"].calls[0] == {"text_language": "en", "voice_language": "es"}

    # Next frame should skip cooled-down acc-1 completely and go straight to acc-2
    assert await router.analyze_frame(b"jpeg-bytes", "image/jpeg", profile) == expected
    assert len(created["acc-1"].calls) == 1
    assert len(created["acc-2"].calls) == 2
    await router.close_session()


def test_extract_json_handles_markdown_fences_and_language_directive() -> None:
    fenced = 'Note: {not valid json}\n```json\n{"should_intervene": true, "urgency": "medium", "overlay_text": "Use flash", "voice_line": "Flash before peeking."}\n```'
    parsed = extract_coach_response_from_text(fenced)
    assert parsed is not None
    assert parsed.overlay_text == "Use flash"

    mixed = build_language_instruction("en", "es")
    assert "English" in mixed and "Spanish" in mixed


def test_provider_account_ssrf_and_https_validation() -> None:
    # Local loopback HTTP is allowed for Ollama / LM Studio
    local = ProviderAccount(
        label="Ollama",
        provider="ollama",
        base_url="http://localhost:11434/v1",
    )
    assert local.resolved_base_url() == "http://localhost:11434/v1"

    # Remote cleartext HTTP must be rejected (CWE-319)
    with pytest.raises(ValidationError):
        ProviderAccount(
            label="Insecure Remote",
            provider="custom",
            base_url="http://api.remote-llm.example.com/v1",
        )

    # Cloud metadata / link-local SSRF must be rejected (CWE-918)
    with pytest.raises(ValidationError):
        ProviderAccount(
            label="SSRF Metadata",
            provider="custom",
            base_url="http://169.254.169.254/latest/meta-data",
        )

    # Non-HTTP scheme must be rejected
    with pytest.raises(ValidationError):
        ProviderAccount(
            label="File Scheme",
            provider="custom",
            base_url="file:///etc/passwd",
        )


async def test_gemini_test_connection_uses_header_and_never_exposes_key_in_url(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured_urls: list[str] = []
    captured_headers: list[dict[str, str]] = []

    class _FakeResponse:
        status = 200

        async def __aenter__(self) -> _FakeResponse:
            return self

        async def __aexit__(self, *_: object) -> None:
            pass

    class _FakeSession:
        def __init__(self, **_: object) -> None:
            pass

        async def __aenter__(self) -> _FakeSession:
            return self

        async def __aexit__(self, *_: object) -> None:
            pass

        def get(self, url: str, headers: dict[str, str] | None = None) -> _FakeResponse:
            captured_urls.append(url)
            captured_headers.append(headers or {})
            return _FakeResponse()

    import aiohttp

    secret_key = "AIzaSySecretKeyNeverInUrl999999"
    acc = ProviderAccount(label="Gem", provider="gemini", api_key=secret_key, model="gemini-2.5-flash")
    adapter = GeminiProviderAdapter(acc, AppSettings())
    monkeypatch.setattr(aiohttp, "ClientSession", _FakeSession)
    ok, msg = await adapter.test_connection()
    await adapter.close_session()

    assert ok is True and "gemini-2.5-flash" in msg
    assert len(captured_urls) == 1
    assert secret_key not in captured_urls[0]
    assert "?key=" not in captured_urls[0]
    assert captured_headers[0].get("x-goog-api-key") == secret_key


def test_user_store_saves_and_reloads_accounts_and_preferences(tmp_path: Path) -> None:
    acc_file = tmp_path / "accounts.json"
    cfg_file = tmp_path / "user_config.json"

    accounts = [
        ProviderAccount(label="Gemini A", provider="gemini", api_key="AIzaTestKey12345", priority=1),
        ProviderAccount(label="Ollama Local", provider="ollama", api_key="", model="llava", priority=2),
    ]
    save_accounts(acc_file, accounts)

    settings = AppSettings(
        accounts_file=acc_file,
        user_config_file=cfg_file,
        text_language="en",
        voice_language="es",
        overlay_opacity=0.65,
        overlay_theme="matrix-green",
    )
    save_user_settings(settings)

    loaded = load_accounts(settings)
    assert len(loaded) == 2
    assert loaded[0].label == "Gemini A"
    assert loaded[0].masked_key() == "AIza...2345"
    assert loaded[1].provider == "ollama" and loaded[1].is_ready()
    assert cfg_file.is_file()


async def test_auto_free_fallback_appends_keyless_providers_and_rotates_silently() -> None:
    profile = GameProfileLoader(Path("profiles")).get_by_id("rob_run")
    assert profile is not None

    # User only configured 1 keyed account: auto fallback appends the 10-model keyless free pool
    keyed = [ProviderAccount(id="user-key-1", label="Mi Key 1", provider="gemini", api_key="AIzaKey12345", priority=1)]
    expanded = build_auto_free_fallback_accounts(keyed)
    assert len(expanded) == 11  # 1 user key + 10 free no-login AI models
    assert expanded[0].provider == "gemini"
    assert expanded[1].provider == "pollinations"
    assert expanded[-1].provider == "ollama"

    expected = CoachResponse(
        should_intervene=True,
        urgency="medium",
        overlay_text="Extraé el botín ahora",
        voice_line="Llevás mucho botín, andá al punto de extracción.",
    )
    outcomes: dict[str, ProviderCallResult] = {
        "user-key-1": ProviderCallResult(error_code=429, error_message="quota limit", quota_or_auth_failure=True),
    }

    router = ProviderFailoverRouter(
        accounts=keyed,
        settings=AppSettings(),
        provider_factory=lambda acc, cfg: _StubProvider(
            acc,
            cfg,
            outcomes.get(acc.id, ProviderCallResult(response=expected)),
        ),
        auto_free_fallback=True,
    )
    await router.prepare_session(profile)
    res = await router.analyze_frame(b"jpeg", "image/jpeg", profile)
    assert res == expected
    assert "Pollinations" in router.active_account_label
    await router.close_session()


def test_telemetry_store_tracks_matches_playstyle_gauges_and_model_costs(
    tmp_path: Path,
) -> None:
    store = TelemetryStore(tmp_path / "telemetry.json")
    store.start_match_session("rob_run", "ROB RUN", "match")
    store.record_frame_filtered()
    store.record_frame_filtered()

    # 1. Free-tier Gemini call with aggressive peek warning
    store.record_ai_call(
        provider="gemini",
        model="gemini-2.5-flash",
        account_label="Gemini Principal",
        system_chars=2400,
        efficiency_mode="eco",
        used_cache=True,
        latency_ms=340.0,
        response=CoachResponse(
            should_intervene=True,
            urgency="high",
            overlay_text="No hagas peek solo contra SWAT",
            voice_line="Tenés mucho botín, esperá cobertura y extraé.",
        ),
    )

    # 2. Paid OpenAI call with clean frame (should_intervene=False)
    store.record_ai_call(
        provider="openai",
        model="gpt-4o-mini",
        account_label="ChatGPT Backup",
        system_chars=2400,
        efficiency_mode="eco",
        used_cache=True,
        latency_ms=510.0,
        response=CoachResponse(
            should_intervene=False,
            urgency="low",
            overlay_text="",
            voice_line="",
        ),
    )

    match = store.finish_active_match()
    assert match is not None
    assert match.game_id == "rob_run"
    assert 0 <= match.performance_score <= 100
    assert match.playstyle.aggression > 52  # Nudged up by peek/SWAT/botín intervention

    # Add a second simulated match for AoE2
    store.record_simulated_match(
        game_id="aoe2_de",
        game_name="Age of Empires II: DE",
        performance_score=90,
        playstyle=PlaystyleMetrics(
            aggression=60,
            patience=85,
            tactical_awareness=88,
            resource_efficiency=92,
            consistency=89,
        ),
    )

    summary_all = store.get_dashboard_summary("all")
    assert summary_all["matches_today"] == 2
    assert summary_all["total_matches_all_time"] == 2
    assert summary_all["last_match_performance"] == 90
    assert 0 < summary_all["recent_avg_performance"] <= 100
    assert summary_all["total_tokens"] > 0
    assert summary_all["total_actual_cost_usd"] > 0.0  # Paid gpt-4o-mini incurred small cost
    assert summary_all["total_saved_cost_usd"] > 0.0  # Free Gemini + cache + pHash saved cost

    summary_rob = store.get_dashboard_summary("rob_run")
    assert summary_rob["last_match_performance"] == match.performance_score


async def test_realtime_hud_ocr_auto_detects_match_training_menu_and_events() -> None:
    import io

    from PIL import Image, ImageDraw

    from src.pipeline.hud_ocr import RealTimeHUDAnalyzer

    analyzer = RealTimeHUDAnalyzer()

    # 1. Competitive match frame with score, timer, low boost & teammate/rival
    img_match = Image.new("RGB", (896, 504), (25, 45, 55))
    d1 = ImageDraw.Draw(img_match)
    d1.text((380, 14), "1   00:45   2", fill=(255, 255, 255))
    d1.text((760, 440), "12", fill=(255, 210, 40))
    buf1 = io.BytesIO()
    img_match.save(buf1, format="JPEG")
    st1 = await analyzer.analyze(buf1.getvalue(), game_id="rocket_league")
    assert st1.detected_mode == "match"

    # 2. Free Play / Training frame
    img_train = Image.new("RGB", (896, 504), (35, 55, 45))
    d2 = ImageDraw.Draw(img_train)
    d2.text((340, 20), "ENTRENAMIENTO LIBRE", fill=(255, 255, 255))
    buf2 = io.BytesIO()
    img_train.save(buf2, format="JPEG")
    st2 = await analyzer.analyze(buf2.getvalue(), game_id="rocket_league")
    assert st2.detected_mode == "training"
