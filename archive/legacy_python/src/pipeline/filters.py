"""Chain of Responsibility pre-AI frame filtering and in-memory optimization pipeline."""

from __future__ import annotations

import io
import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass

import imagehash
from PIL import Image

from src.capture.base import CapturedFrame
from src.pipeline.schemas import InterventionStateMachine
from src.profiles.loader import ForegroundProcessDetector, GameProfile

logger = logging.getLogger(__name__)


@dataclass
class FrameContext:
    """Mutable context passed along the Chain of Responsibility filter pipeline."""

    frame: CapturedFrame
    profile: GameProfile
    test_mode: bool = False
    active_process: str | None = None
    phash: imagehash.ImageHash | None = None
    phash_distance: int | None = None
    encoded_bytes: bytes | None = None
    mime_type: str = "image/webp"
    dropped_by: str | None = None
    drop_reason: str | None = None


class FrameFilter(ABC):
    """Abstract handler in the Chain of Responsibility."""

    def __init__(self) -> None:
        self._next: FrameFilter | None = None

    @property
    def name(self) -> str:
        return self.__class__.__name__

    def set_next(self, next_filter: FrameFilter) -> FrameFilter:
        """Attach the next filter in the chain and return it for fluent chaining."""
        self._next = next_filter
        return next_filter

    def handle(self, context: FrameContext) -> FrameContext | None:
        """Evaluate this filter and delegate to the next filter if passed."""
        passed = self._process(context)
        if not passed:
            context.dropped_by = self.name
            logger.debug(
                "Frame #%d descartado por %s (%s)",
                context.frame.frame_id,
                self.name,
                context.drop_reason or "sin motivo",
            )
            return None

        if self._next is not None:
            return self._next.handle(context)
        return context

    @abstractmethod
    def _process(self, context: FrameContext) -> bool:
        """Return True to pass the frame onward, or False to drop it."""


class FocusFilter(FrameFilter):
    """Ensures the target game window is currently focused in the foreground."""

    def __init__(self, detector: ForegroundProcessDetector) -> None:
        super().__init__()
        self._detector = detector

    def _process(self, context: FrameContext) -> bool:
        if context.test_mode:
            return True

        active_exe = self._detector.get_foreground_executable()
        context.active_process = active_exe

        if not context.profile.matches_process(active_exe):
            context.drop_reason = f"ventana activa '{active_exe}' != {context.profile.manifest.process_names}"
            return False
        return True


class PerceptualHashFilter(FrameFilter):
    """Discards visually redundant frames using perceptual hashing (pHash)."""

    def __init__(self, hash_size: int = 8) -> None:
        super().__init__()
        self._hash_size = hash_size
        self._last_accepted_hash: imagehash.ImageHash | None = None

    def reset(self) -> None:
        """Clear stored hash when switching game profiles or restarting sessions."""
        self._last_accepted_hash = None

    def commit_hash(self, frame_hash: imagehash.ImageHash | None) -> None:
        """Record the hash of a frame that was actually sent to Gemini."""
        if frame_hash is not None:
            self._last_accepted_hash = frame_hash

    def _process(self, context: FrameContext) -> bool:
        # Downscale first with area-averaging BOX filter before grayscale conversion:
        # avoids allocating a full-res grayscale buffer (~2MB) and matches pHash 32x32 DCT input.
        dct_size = self._hash_size * 4
        tiny_rgb = context.frame.image.resize((dct_size, dct_size), Image.Resampling.BOX)
        try:
            small_gray = tiny_rgb.convert("L")
            try:
                current_hash = imagehash.phash(small_gray, hash_size=self._hash_size)
            finally:
                small_gray.close()
        finally:
            tiny_rgb.close()
        context.phash = current_hash

        if self._last_accepted_hash is None:
            context.phash_distance = 64
            return True

        distance = int(current_hash - self._last_accepted_hash)
        context.phash_distance = distance
        threshold = context.profile.manifest.phash_threshold

        if distance < threshold:
            context.drop_reason = f"pHash distance {distance} < threshold {threshold}"
            return False

        return True


class CooldownFilter(FrameFilter):
    """Enforces intervention cooldown and minimum API request interval."""

    def __init__(
        self,
        state_machine: InterventionStateMachine,
        min_inference_interval: float = 2.5,
        cooldown_multiplier: float = 1.0,
    ) -> None:
        super().__init__()
        self._state_machine = state_machine
        self._min_inference_interval = min_inference_interval
        self._cooldown_multiplier = max(0.2, cooldown_multiplier)

    def _process(self, context: FrameContext) -> bool:
        base_cd = context.profile.manifest.cooldown_seconds
        effective_cd = (
            max(3.5, round(base_cd * self._cooldown_multiplier, 1))
            if self._cooldown_multiplier < 1.0
            else base_cd
        )
        allowed, reason = self._state_machine.can_analyze(
            cooldown_seconds=effective_cd,
            min_inference_interval=self._min_inference_interval,
        )
        if not allowed:
            context.drop_reason = reason
            return False
        return True


class ImageOptimizer(FrameFilter):
    """Resizes frame to target resolution and compresses to JPEG/WebP entirely in RAM."""

    def __init__(
        self,
        quality: int = 78,
        prefer_webp: bool = False,
        max_resolution_cap: tuple[int, int] | None = None,
    ) -> None:
        super().__init__()
        self._quality = quality
        self._prefer_webp = prefer_webp
        self._max_resolution_cap = max_resolution_cap

    @staticmethod
    def _compute_aspect_size(
        width: int,
        height: int,
        max_w: int,
        max_h: int,
    ) -> tuple[int, int]:
        scale = min(max_w / width, max_h / height)
        return max(1, int(width * scale)), max(1, int(height * scale))

    def _process(self, context: FrameContext) -> bool:
        max_w, max_h = context.profile.manifest.max_resolution
        if self._max_resolution_cap is not None:
            max_w = min(max_w, self._max_resolution_cap[0])
            max_h = min(max_h, self._max_resolution_cap[1])
        img = context.frame.image

        created_resized = False
        if img.width > max_w or img.height > max_h:
            # Direct resize avoids the redundant full-frame copy() required by Image.thumbnail()
            target_size = self._compute_aspect_size(img.width, img.height, max_w, max_h)
            resized = img.resize(target_size, Image.Resampling.BILINEAR)
            created_resized = True
        else:
            resized = img

        buffer = io.BytesIO()
        try:
            if self._prefer_webp:
                try:
                    # method=0 minimizes WebP encoding latency for real-time gaming workloads
                    resized.save(buffer, format="WEBP", quality=self._quality, method=0)
                    context.mime_type = "image/webp"
                except Exception:
                    buffer = io.BytesIO()
                    resized.save(
                        buffer,
                        format="JPEG",
                        quality=self._quality,
                        optimize=False,
                        subsampling=2,
                    )
                    context.mime_type = "image/jpeg"
            else:
                # Baseline JPEG without extra Huffman optimization pass encodes 720p in ~2-4ms
                resized.save(
                    buffer,
                    format="JPEG",
                    quality=self._quality,
                    optimize=False,
                    subsampling=2,
                )
                context.mime_type = "image/jpeg"
        finally:
            if created_resized:
                resized.close()

        context.encoded_bytes = buffer.getvalue()
        if not context.encoded_bytes:
            context.drop_reason = "falló compresión de imagen en memoria"
            return False

        return True


class FilterPipeline:
    """Facade assembling the ordered Chain of Responsibility pre-AI pipeline."""

    def __init__(
        self,
        detector: ForegroundProcessDetector,
        state_machine: InterventionStateMachine,
        min_inference_interval: float = 2.5,
        compression_quality: int = 78,
        image_format: str = "jpeg",
        efficiency_mode: str = "balanced",
        cooldown_multiplier: float = 1.0,
    ) -> None:
        self.focus_filter = FocusFilter(detector)
        self.phash_filter = PerceptualHashFilter()
        self.cooldown_filter = CooldownFilter(
            state_machine,
            min_inference_interval,
            cooldown_multiplier=cooldown_multiplier,
        )
        is_eco = efficiency_mode == "eco"
        is_balanced = efficiency_mode == "balanced"
        # Sub-second vision payload sizing:
        # - eco (Real-Time): 512x288 JPEG q58 (~4-6 KB, ~2ms encode, minimal ViT tiles)
        # - balanced: 640x360 JPEG q68 (~10-15 KB, crisp HUD numbers + sub-second speed)
        # - high: full profile max resolution (e.g., 1280x720)
        if is_eco:
            res_cap: tuple[int, int] | None = (512, 288)
            effective_quality = min(compression_quality, 58)
        elif is_balanced:
            res_cap = (640, 360)
            effective_quality = min(compression_quality, 68)
        else:
            res_cap = None
            effective_quality = compression_quality
        self.image_optimizer = ImageOptimizer(
            quality=effective_quality,
            prefer_webp=(image_format.lower() == "webp"),
            max_resolution_cap=res_cap,
        )

        # Chain: Focus (O(1)) -> Cooldown (O(1)) -> pHash -> Resize/Compress
        (
            self.focus_filter.set_next(self.cooldown_filter)
            .set_next(self.phash_filter)
            .set_next(self.image_optimizer)
        )

    def run(
        self,
        frame: CapturedFrame,
        profile: GameProfile,
        test_mode: bool = False,
    ) -> FrameContext | None:
        """Run a captured frame synchronously through all CPU filters."""
        context = FrameContext(frame=frame, profile=profile, test_mode=test_mode)
        return self.focus_filter.handle(context)

    def commit_analyzed_frame(self, context: FrameContext) -> None:
        """Update pHash baseline only once a frame is accepted for analysis."""
        self.phash_filter.commit_hash(context.phash)

    def reset(self) -> None:
        """Reset stateful filters when switching games."""
        self.phash_filter.reset()
