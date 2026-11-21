"""Unit tests for CaptureStrategy mocks and ScreenObserver Drop-Oldest async Pub-Sub."""

from __future__ import annotations

import asyncio
from pathlib import Path

from PIL import Image

from src.capture.mss_capture import VideoMockCaptureStrategy
from src.capture.observer import ScreenObserver


def test_video_mock_directory_sequence(tmp_path: Path) -> None:
    for idx, color in enumerate([(10, 20, 30), (100, 150, 200)]):
        img = Image.new("RGB", (640, 360), color=color)
        img.save(tmp_path / f"frame_{idx}.png")

    mock = VideoMockCaptureStrategy(source_path=tmp_path, loop=False)
    with mock:
        f1 = mock.capture_frame()
        f2 = mock.capture_frame()
        f3 = mock.capture_frame()

    assert f1 is not None and f1.width == 640 and f1.height == 360
    assert f2 is not None and f2.frame_id == 2
    assert f3 is None


async def test_screen_observer_drop_oldest_and_pause() -> None:
    mock = VideoMockCaptureStrategy(source_path=None)
    observer = ScreenObserver(strategy=mock, target_fps=25.0)
    q_fast = observer.subscribe("consumer-a")
    q_slow = observer.subscribe("consumer-b")

    await observer.start()
    await asyncio.sleep(0.25)

    # Slow consumer should receive the freshest frame, never stale backlog
    latest_a = q_fast.get_nowait()
    latest_b = q_slow.get_nowait()
    assert latest_a.frame_id == latest_b.frame_id
    assert observer.published_count >= 2
    assert observer.dropped_count >= 1

    # Pause stops publishing new frames
    observer.pause()
    await asyncio.sleep(0.08)
    count_when_paused = observer.published_count
    await asyncio.sleep(0.15)
    assert observer.published_count == count_when_paused

    # Dynamic FPS update clamps safely
    observer.update_fps(500.0)
    assert observer.target_fps == 30.0
    observer.update_fps(0.01)
    assert observer.target_fps == 0.1

    await observer.stop()
