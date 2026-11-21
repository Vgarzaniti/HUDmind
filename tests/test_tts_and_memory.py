"""Unit tests for TTS fallback, VoiceService Drop-Oldest, InterventionMemory, and Hotkeys."""

from __future__ import annotations

import asyncio
from pathlib import Path

import pytest

from src.config.hotkeys import GlobalHotkeyManager
from src.config.settings import AppSettings
from src.output.tts import AudioPlayer, EdgeTTSEngine, TTSEngine, VoiceService
from src.pipeline.schemas import CoachResponse, InterventionMemory
from src.profiles.loader import GameProfileLoader


class _RecordingTTSEngine(TTSEngine):
    def __init__(self, delay_seconds: float = 0.0) -> None:
        self._delay = delay_seconds
        self.spoken: list[str] = []

    @property
    def name(self) -> str:
        return "recording-test-tts"

    async def speak(self, text: str) -> None:
        if self._delay > 0:
            await asyncio.sleep(self._delay)
        self.spoken.append(text)


async def test_edge_tts_falls_back_to_offline_engine_on_network_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    fallback = _RecordingTTSEngine()
    engine = EdgeTTSEngine(fallback_engine=fallback)

    async def _fail_synth(_: str) -> bytes:
        raise ConnectionError("offline")

    monkeypatch.setattr(engine, "synthesize_mp3", _fail_synth)
    await engine.speak("Recargá en cobertura")
    assert fallback.spoken == ["Recargá en cobertura"]


async def test_voice_service_drop_oldest_never_queues_stale_lines() -> None:
    settings = AppSettings(enable_voice=True, tts_engine="pyttsx3")
    service = VoiceService(settings=settings, enabled=True)
    fake_engine = _RecordingTTSEngine(delay_seconds=0.06)
    service._engine = fake_engine

    await service.start()
    service.enqueue("primera")
    await asyncio.sleep(0.01)  # worker picks up "primera"
    service.enqueue("obsoleta")
    service.enqueue("fresca")  # replaces "obsoleta" via Drop-Oldest
    await asyncio.sleep(0.18)
    await service.stop()

    assert "primera" in fake_engine.spoken
    assert "fresca" in fake_engine.spoken
    assert "obsoleta" not in fake_engine.spoken


def test_intervention_memory_maxlen_ttl_and_prompt_formatting() -> None:
    mem = InterventionMemory(max_items=2, ttl_seconds=60.0)
    assert mem.format_prompt_context(now=100.0) == ""

    r1 = CoachResponse(should_intervene=True, urgency="low", overlay_text="Revisá radar", voice_line="")
    r2 = CoachResponse(should_intervene=True, urgency="medium", overlay_text="Usá flash", voice_line="")
    r3 = CoachResponse(should_intervene=True, urgency="high", overlay_text="Cuidá espalda", voice_line="")

    mem.record(r1, now=100.0)
    mem.record(r2, now=110.0)
    mem.record(r3, now=120.0)

    # maxlen=2 keeps only r2 and r3
    ctx = mem.format_prompt_context(now=130.0)
    assert "Revisá radar" not in ctx
    assert "Usá flash" in ctx
    assert "Cuidá espalda" in ctx
    assert "hace 10s [HIGH]" in ctx

    # After TTL (60s), old records expire automatically
    assert mem.format_prompt_context(now=200.0) == ""


def test_hotkey_manager_dispatches_callbacks_and_respects_disabled() -> None:
    events: list[str] = []
    mgr = GlobalHotkeyManager(
        enabled=False,
        on_toggle_pause=lambda: events.append("pause"),
        on_analyze_now=lambda: events.append("analyze"),
    )
    mgr.start()
    assert mgr.is_running is False
    mgr.dispatch_action(1)
    mgr.dispatch_action(2)
    mgr.stop()
    assert events == ["pause", "analyze"]


def test_multi_game_profiles_load_both_cs2_and_valorant() -> None:
    loader = GameProfileLoader(Path("profiles"))
    assert loader.get_by_id("example_cs2") is not None
    val = loader.get_by_id("example_valorant")
    assert val is not None
    assert val.matches_process("VALORANT-Win64-Shipping.exe")


async def test_edge_tts_in_memory_lru_cache_hits_and_eviction(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    synth_calls: list[str] = []
    played_bytes: list[bytes] = []

    player = AudioPlayer()
    monkeypatch.setattr(player, "play_bytes_blocking", lambda b, fmt="mp3": played_bytes.append(b))
    engine = EdgeTTSEngine(player=player, cache_max_entries=2)

    async def _fake_synth(text: str) -> bytes:
        synth_calls.append(text)
        return f"mp3:{text}".encode()

    monkeypatch.setattr(engine, "synthesize_mp3", _fake_synth)

    await engine.speak("Recargá en cobertura")
    # Repeated call (even with extra spaces/case difference) must hit RAM cache (0 network calls)
    await engine.speak("  recargá   en COBERTURA ")
    assert len(synth_calls) == 1
    assert engine.cache_hits == 1
    assert engine.cache_size == 1
    assert len(played_bytes) == 2

    # Fill and exceed max_entries=2 to verify LRU eviction
    await engine.speak("Segunda frase")
    await engine.speak("Tercera frase")
    assert engine.cache_size == 2
