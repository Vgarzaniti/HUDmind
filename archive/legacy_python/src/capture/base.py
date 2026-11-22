"""Abstract Strategy interface and data models for screen capture."""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from types import TracebackType

from PIL import Image


@dataclass(slots=True)
class CapturedFrame:
    """Immutable snapshot of a captured frame held entirely in RAM."""

    image: Image.Image
    timestamp: float
    wall_time: float
    frame_id: int
    source: str
    width: int = field(init=False)
    height: int = field(init=False)

    def __post_init__(self) -> None:
        self.width, self.height = self.image.size


class CaptureStrategy(ABC):
    """Strategy interface for pluggable screen or mock capture backends."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Human-readable identifier of the capture strategy."""

    def start(self) -> None:  # noqa: B027
        """Initialize backend resources (optional override)."""

    @abstractmethod
    def capture_frame(self) -> CapturedFrame | None:
        """Capture and return a single RGB frame, or None if unavailable."""

    def stop(self) -> None:  # noqa: B027
        """Release backend resources (optional override)."""

    def __enter__(self) -> CaptureStrategy:
        self.start()
        return self

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc_val: BaseException | None,
        exc_tb: TracebackType | None,
    ) -> None:
        self.stop()
