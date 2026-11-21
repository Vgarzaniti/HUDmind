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
    extract_coach_response_from_text,
)
from src.config.settings import AppSettings
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
