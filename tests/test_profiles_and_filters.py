"""Unit tests for GameProfileLoader security/validation and Chain of Responsibility filters."""

from __future__ import annotations

import json
import time
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest
from PIL import Image, ImageDraw
from pydantic import ValidationError

from src.capture.base import CapturedFrame
from src.capture.mss_capture import MSSCaptureStrategy
from src.pipeline.filters import (
    CooldownFilter,
    FilterPipeline,
    FocusFilter,
    FrameContext,
    ImageOptimizer,
    PerceptualHashFilter,
)
from src.pipeline.schemas import InterventionStateMachine
from src.profiles.loader import (
    MAX_CUSTOM_RULES_CHARS,
    ForegroundProcessDetector,
    GameManifest,
    GameProfileLoader,
)


class _StubDetector(ForegroundProcessDetector):
    def __init__(self, current_exe: str | None) -> None:
        self.current_exe = current_exe

    @property
    def is_supported(self) -> bool:
        return True

    def get_foreground_executable(self) -> str | None:
        return self.current_exe


def _make_frame(frame_id: int, variant: int = 0, size: tuple[int, int] = (1920, 1080)) -> CapturedFrame:
    img = Image.new("RGB", size, color=(20, 30, 40))
    draw = ImageDraw.Draw(img)
    if variant == 0:
        draw.rectangle([50, 50, 900, 600], fill=(200, 40, 40))
    else:
        draw.ellipse([900, 200, 1800, 950], fill=(40, 220, 120))
        draw.line([0, 0, size[0], size[1]], fill=(255, 255, 255), width=40)
    return CapturedFrame(
        image=img,
        timestamp=time.monotonic(),
        wall_time=time.time(),
        frame_id=frame_id,
        source="unit-test",
    )


def test_manifest_security_and_bounds_validation() -> None:
    with pytest.raises(ValidationError):
        GameManifest(
            game_id="../escape",
            display_name="Bad",
            process_names=["game.exe"],
        )

    with pytest.raises(ValidationError):
        GameManifest(
            game_id="valid_game",
            display_name="Bad Res",
            process_names=["game.exe"],
            max_resolution=(100, 100),
        )


def test_profile_loader_loads_example_and_blocks_traversal(tmp_path: Path) -> None:
    loader = GameProfileLoader(Path("profiles"), process_detector=_StubDetector("cs2.exe"))
    cs2 = loader.get_by_id("example_cs2")
    assert cs2 is not None
    assert cs2.matches_process("CS2.EXE")
    assert loader.get_by_id("../profiles/example_cs2") is None
    assert loader.get_by_id("..\\secret") is None

    exe, detected = loader.detect_active_profile()
    assert exe == "cs2.exe"
    assert detected is not None and detected.manifest.game_id == "example_cs2"

    # Incomplete profile directory is safely skipped
    bad_dir = tmp_path / "broken_game"
    bad_dir.mkdir()
    (bad_dir / "manifest.json").write_text(json.dumps({"game_id": "broken"}), encoding="utf-8")
    empty_loader = GameProfileLoader(tmp_path, process_detector=_StubDetector(None))
    assert empty_loader.list_profiles() == []


def test_filter_chain_focus_phash_cooldown_and_resize() -> None:
    detector = _StubDetector("notepad.exe")
    loader = GameProfileLoader(Path("profiles"), process_detector=detector)
    profile = loader.get_by_id("example_cs2")
    assert profile is not None

    sm = InterventionStateMachine()
    pipeline = FilterPipeline(
        detector=detector,
        state_machine=sm,
        min_inference_interval=0.0,
        compression_quality=75,
    )

    # 1. FocusFilter drops frame when wrong .exe is active in normal mode
    frame1 = _make_frame(1, variant=0)
    assert pipeline.run(frame1, profile, test_mode=False) is None

    # 2. FocusFilter passes in test_mode and ImageOptimizer downscales 1080p -> 720p WebP
    ctx1 = pipeline.run(frame1, profile, test_mode=True)
    assert ctx1 is not None
    assert ctx1.encoded_bytes is not None and len(ctx1.encoded_bytes) > 0
    assert ctx1.mime_type in ("image/webp", "image/jpeg")
    pipeline.commit_analyzed_frame(ctx1)

    # 3. PerceptualHashFilter drops identical frame
    frame2_same = _make_frame(2, variant=0)
    assert pipeline.run(frame2_same, profile, test_mode=True) is None

    # 4. PerceptualHashFilter passes visually distinct frame
    frame3_diff = _make_frame(3, variant=1)
    ctx3 = pipeline.run(frame3_diff, profile, test_mode=True)
    assert ctx3 is not None
    assert (ctx3.phash_distance or 0) >= profile.manifest.phash_threshold

    # 5. CooldownFilter blocks while inside cooldown window
    sm.begin_analysis()
    sm.complete_delivery()
    assert pipeline.run(_make_frame(4, variant=0), profile, test_mode=True) is None

    # Sanity check direct filter instantiation (both JPEG fast mode and WebP mode)
    f_ctx = FrameContext(frame=frame1, profile=profile, test_mode=True)
    assert FocusFilter(detector).handle(f_ctx) is not None
    assert PerceptualHashFilter().handle(f_ctx) is not None
    assert CooldownFilter(InterventionStateMachine(), 0.0).handle(f_ctx) is not None
    jpeg_ctx = ImageOptimizer(quality=75, prefer_webp=False).handle(f_ctx)
    assert jpeg_ctx is not None and jpeg_ctx.mime_type == "image/jpeg"
    webp_ctx = ImageOptimizer(quality=75, prefer_webp=True).handle(f_ctx)
    assert webp_ctx is not None and webp_ctx.mime_type == "image/webp"

    # Verify eco mode caps 1080p down to <= 896x504 (<=512px vertical tile boundary)
    eco_pipe = FilterPipeline(
        detector=detector,
        state_machine=InterventionStateMachine(),
        min_inference_interval=0.0,
        efficiency_mode="eco",
    )
    eco_ctx = eco_pipe.run(frame1, profile, test_mode=True)
    assert eco_ctx is not None and eco_ctx.encoded_bytes is not None
    import io

    with Image.open(io.BytesIO(eco_ctx.encoded_bytes)) as decoded:
        assert decoded.width <= 896 and decoded.height <= 504
    assert len(profile.compact_knowledge_base) < len(profile.knowledge_base)


def test_create_profile_scaffold_and_window_region_clamping(tmp_path: Path) -> None:
    loader = GameProfileLoader(tmp_path, process_detector=_StubDetector("League of Legends.exe"))
    created = loader.create_profile_scaffold(
        game_id="league_of_legends",
        display_name="League of Legends",
        process_name="League of Legends",
        language="es",
    )
    assert created.manifest.game_id == "league_of_legends"
    assert created.matches_process("League of Legends.exe")
    assert (tmp_path / "league_of_legends" / "manifest.json").is_file()

    # Duplicate creation raises FileExistsError
    with pytest.raises(FileExistsError):
        loader.create_profile_scaffold(
            game_id="league_of_legends",
            display_name="LoL",
            process_name="League of Legends.exe",
        )

    # Active window region provider clamps to virtual desktop and falls back if too small
    fake_sct: Any = SimpleNamespace(
        monitors=[
            {"left": 0, "top": 0, "width": 1920, "height": 1080},
            {"left": 0, "top": 0, "width": 1920, "height": 1080},
        ]
    )
    strat_crop = MSSCaptureStrategy(
        monitor_index=1,
        region_provider=lambda: (-20, 50, 1300, 740),
    )
    rect = strat_crop._resolve_target_region(fake_sct)
    assert rect == {"left": 0, "top": 50, "width": 1280, "height": 740}

    strat_tiny = MSSCaptureStrategy(
        monitor_index=1,
        region_provider=lambda: (0, 0, 100, 100),
    )
    assert strat_tiny._resolve_target_region(fake_sct)["width"] == 1920

    # Per-game custom rules lifecycle: save, inject into KB, enforce ceiling, and clear
    updated = loader.save_custom_rules(
        "league_of_legends",
        "- Priorizar control de dragones.\n- Comprar wards de control.",
    )
    assert "Priorizar control de dragones" in updated.custom_rules
    assert "Reglas Personalizadas del Jugador" in updated.knowledge_base
    assert "Priorizar control de dragones" in updated.compact_knowledge_base
    assert updated.custom_rules_estimated_tokens > 0
    assert "Reglas Personalizadas del Jugador" not in updated.base_knowledge_base

    with pytest.raises(ValueError):
        loader.save_custom_rules("league_of_legends", "x" * (MAX_CUSTOM_RULES_CHARS + 10))

    cleared = loader.save_custom_rules("league_of_legends", "   ")
    assert cleared.custom_rules == ""
    assert cleared.custom_rules_estimated_tokens == 0
    assert not (tmp_path / "league_of_legends" / "custom_rules.md").exists()
