"""Unit tests for GeminiCoachClient: Context Caching, auto-fallback, retries, and Circuit Breaker."""

from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace
from typing import Any

from google.genai import errors as genai_errors

from src.ai.gemini_client import GeminiCoachClient
from src.config.settings import AppSettings
from src.pipeline.schemas import CoachResponse
from src.profiles.loader import GameProfileLoader


class _FakeCachesAPI:
    def __init__(self, create_behavior: Any) -> None:
        self._create_behavior = create_behavior
        self.created_calls: list[dict[str, Any]] = []
        self.deleted_names: list[str] = []

    async def create(self, **kwargs: Any) -> Any:
        self.created_calls.append(kwargs)
        if callable(self._create_behavior):
            return self._create_behavior(**kwargs)
        raise self._create_behavior

    async def delete(self, name: str) -> None:
        self.deleted_names.append(name)


class _FakeModelsAPI:
    def __init__(self, responses: list[Any]) -> None:
        self._responses = list(responses)
        self.calls: list[dict[str, Any]] = []

    async def generate_content(self, **kwargs: Any) -> Any:
        self.calls.append(kwargs)
        item = self._responses.pop(0)
        if isinstance(item, Exception):
            raise item
        return item


def _make_fake_client(cache_behavior: Any, model_responses: list[Any]) -> Any:
    caches = _FakeCachesAPI(cache_behavior)
    models = _FakeModelsAPI(model_responses)
    return SimpleNamespace(aio=SimpleNamespace(caches=caches, models=models))


def _load_cs2_profile() -> Any:
    profile = GameProfileLoader(Path("profiles")).get_by_id("example_cs2")
    assert profile is not None
    return profile


def _api_error(code: int, message: str = "err") -> genai_errors.APIError:
    return genai_errors.APIError(code, {"error": {"message": message}}, None)


async def test_context_cache_success_and_clean_close() -> None:
    expected = CoachResponse(
        should_intervene=True,
        urgency="high",
        overlay_text="Recargá ahora",
        voice_line="Te quedan tres balas, recargá.",
    )
    fake = _make_fake_client(
        cache_behavior=lambda **_: SimpleNamespace(name="cachedContents/test-123"),
        model_responses=[SimpleNamespace(parsed=expected, text=expected.model_dump_json())],
    )
    settings = AppSettings(gemini_api_key="test-key-12345678")  # type: ignore[arg-type]
    client = GeminiCoachClient(settings, client=fake)
    profile = _load_cs2_profile()

    assert await client.prepare_session(profile) is True
    assert client.is_using_cache is True

    result = await client.analyze_frame(b"fake-webp", "image/webp", profile)
    assert result == expected

    # Verify cached mode does NOT duplicate system_instruction
    call_cfg = fake.aio.models.calls[0]["config"]
    assert call_cfg.cached_content == "cachedContents/test-123"
    assert call_cfg.system_instruction is None

    await client.close_session()
    assert fake.aio.caches.deleted_names == ["cachedContents/test-123"]
    assert client.is_using_cache is False


async def test_context_cache_min_tokens_automatic_fallback() -> None:
    expected = CoachResponse(should_intervene=False, urgency="low", overlay_text="", voice_line="")
    fake = _make_fake_client(
        cache_behavior=_api_error(400, "Cached content is too small (min 1024 tokens)"),
        model_responses=[SimpleNamespace(parsed=None, text=expected.model_dump_json())],
    )
    settings = AppSettings(gemini_api_key="test-key-12345678")  # type: ignore[arg-type]
    client = GeminiCoachClient(settings, client=fake)
    profile = _load_cs2_profile()

    assert await client.prepare_session(profile) is False
    assert client.is_using_cache is False

    result = await client.analyze_frame(b"fake-webp", "image/webp", profile)
    assert result == expected

    # Verify fallback passes system_instruction inline and omits cached_content
    call_cfg = fake.aio.models.calls[0]["config"]
    assert call_cfg.cached_content is None
    assert call_cfg.system_instruction == profile.system_prompt


async def test_cache_expiration_404_recovers_inline_and_circuit_breaker() -> None:
    expected = CoachResponse(
        should_intervene=True,
        urgency="medium",
        overlay_text="Usá flash antes de abrir",
        voice_line="Tirá flash antes de salir.",
    )
    fake = _make_fake_client(
        cache_behavior=lambda **_: SimpleNamespace(name="cachedContents/expiring"),
        model_responses=[
            _api_error(404, "CachedContent not found"),
            SimpleNamespace(parsed=expected, text=expected.model_dump_json()),
            _api_error(401, "API key invalid"),
        ],
    )
    settings = AppSettings(
        gemini_api_key="test-key-12345678",  # type: ignore[arg-type]
        circuit_breaker_threshold=2,
    )
    client = GeminiCoachClient(settings, max_retries=1, client=fake)
    profile = _load_cs2_profile()

    assert await client.prepare_session(profile) is True
    # First call hits 404 on cache -> drops cache -> retries inline -> succeeds
    result = await client.analyze_frame(b"fake-webp", "image/webp", profile)
    assert result == expected
    assert client.is_using_cache is False

    # HTTP 401 opens circuit breaker permanently
    auth_fail = await client.analyze_frame(b"fake-webp", "image/webp", profile)
    assert auth_fail is None
    assert client.is_circuit_open is True
    # Subsequent call is short-circuited without hitting models API again
    calls_before = len(fake.aio.models.calls)
    assert await client.analyze_frame(b"fake-webp", "image/webp", profile) is None
    assert len(fake.aio.models.calls) == calls_before
