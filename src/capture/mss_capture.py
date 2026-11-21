"""Concrete CaptureStrategy implementations: MSS, VideoMock, and DXCam-ready adapter."""

from __future__ import annotations

import contextlib
import logging
import threading
import time
from collections.abc import Callable
from pathlib import Path
from typing import TYPE_CHECKING, Any

from PIL import Image, ImageDraw

from src.capture.base import CapturedFrame, CaptureStrategy

if TYPE_CHECKING:
    from mss.base import MSSBase

logger = logging.getLogger(__name__)

_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}
_VIDEO_EXTENSIONS = {".mp4", ".mkv", ".avi", ".mov", ".webm"}


class MSSCaptureStrategy(CaptureStrategy):
    """Fast cross-monitor screen capture using MSS with thread-local GDI contexts.

    On Windows, MSS uses GDI device contexts tied to the thread that created the
    `mss.mss()` instance. Storing the handle in `threading.local()` makes it safe
    to invoke `capture_frame()` repeatedly via `asyncio.to_thread()`.
    """

    def __init__(
        self,
        monitor_index: int = 1,
        region_provider: Callable[[], tuple[int, int, int, int] | None] | None = None,
    ) -> None:
        self._monitor_index = monitor_index
        self._region_provider = region_provider
        self._local = threading.local()
        self._frame_counter = 0
        self._lock = threading.Lock()
        self._instances: list[MSSBase] = []
        self._capture_degraded = False

    @property
    def name(self) -> str:
        return f"mss(monitor={self._monitor_index})"

    def _get_sct(self) -> MSSBase:
        import mss

        sct: MSSBase | None = getattr(self._local, "sct", None)
        if sct is None:
            factory = getattr(mss, "MSS", mss.mss)
            sct = factory()
            self._local.sct = sct
            with self._lock:
                self._instances.append(sct)
        return sct

    def _reset_thread_sct(self) -> None:
        """Close and discard the thread-local GDI handle after BitBlt/display-mode errors."""
        sct: MSSBase | None = getattr(self._local, "sct", None)
        if sct is not None:
            with contextlib.suppress(Exception):
                sct.close()
            delattr(self._local, "sct")
            with self._lock:
                if sct in self._instances:
                    self._instances.remove(sct)

    def _resolve_target_region(self, sct: MSSBase) -> dict[str, int]:
        """Resolve active window client rectangle if available, else full monitor."""
        monitors = sct.monitors
        idx = self._monitor_index if 0 <= self._monitor_index < len(monitors) else 1
        fallback_mon: dict[str, int] = dict(monitors[idx])

        if self._region_provider is None:
            return fallback_mon

        bbox = self._region_provider()
        if bbox is None:
            return fallback_mon

        left, top, width, height = bbox
        if width < 320 or height < 240:
            return fallback_mon

        # Clamp to virtual desktop bounds (monitors[0]) so partially off-screen windows never fail BitBlt
        virt = monitors[0]
        v_left = int(virt["left"])
        v_top = int(virt["top"])
        v_right = v_left + int(virt["width"])
        v_bottom = v_top + int(virt["height"])

        c_left = max(v_left, left)
        c_top = max(v_top, top)
        c_right = min(v_right, left + width)
        c_bottom = min(v_bottom, top + height)
        c_width = c_right - c_left
        c_height = c_bottom - c_top

        if c_width < 320 or c_height < 240:
            return fallback_mon

        return {"left": c_left, "top": c_top, "width": c_width, "height": c_height}

    def capture_frame(self) -> CapturedFrame | None:
        try:
            sct = self._get_sct()
            target_region = self._resolve_target_region(sct)

            raw = sct.grab(target_region)
            # Zero-copy buffer protocol from raw.raw (bytearray) to RGB PIL Image:
            # avoids raw.bgra which allocates an extra 8.3MB (1080p) / 33MB (4K) bytes() copy.
            buf = raw.raw if hasattr(raw, "raw") else raw.bgra
            image = Image.frombytes("RGB", raw.size, bytes(buf) if not isinstance(buf, (bytes, bytearray, memoryview)) else buf, "raw", "BGRX")
            del raw

            if self._capture_degraded:
                logger.info("Captura MSS recuperada correctamente.")
                self._capture_degraded = False

            with self._lock:
                self._frame_counter += 1
                frame_id = self._frame_counter

            return CapturedFrame(
                image=image,
                timestamp=time.monotonic(),
                wall_time=time.time(),
                frame_id=frame_id,
                source=self.name,
            )
        except Exception as exc:
            self._reset_thread_sct()
            if not self._capture_degraded:
                logger.warning(
                    "Captura MSS temporalmente no disponible (%s). "
                    "Puede ocurrir con sesión bloqueada, UAC o cambio de resolución; reintentando...",
                    exc,
                )
                self._capture_degraded = True
            else:
                logger.debug("Captura MSS aún no disponible: %s", exc)
            return None

    def stop(self) -> None:
        with self._lock:
            for instance in self._instances:
                with contextlib.suppress(Exception):
                    instance.close()
            self._instances.clear()
        if hasattr(self._local, "sct"):
            delattr(self._local, "sct")


class VideoMockCaptureStrategy(CaptureStrategy):
    """Mock capture strategy for offline testing without running a live game.

    Supports three input modes:
    1. A directory of screenshots (`.png`, `.jpg`, `.webp`, etc.) played sequentially.
    2. A single image file.
    3. A video file (`.mp4`, `.mkv`, etc.) if `opencv-python` (`cv2`) is installed.
    4. Synthetic frames generated in memory if `source_path` is None.
    """

    def __init__(
        self,
        source_path: Path | str | None = None,
        loop: bool = True,
        synthetic_size: tuple[int, int] = (1280, 720),
    ) -> None:
        self._source_path = Path(source_path) if source_path is not None else None
        self._loop = loop
        self._synthetic_size = synthetic_size
        self._image_paths: list[Path] = []
        self._image_index = 0
        self._video_cap: Any = None
        self._frame_counter = 0

    @property
    def name(self) -> str:
        src = str(self._source_path) if self._source_path else "synthetic"
        return f"video_mock({src})"

    def start(self) -> None:
        if self._source_path is None:
            logger.info("VideoMockCaptureStrategy iniciado en modo sintético.")
            return

        if not self._source_path.exists():
            raise FileNotFoundError(f"Ruta mock no encontrada: {self._source_path}")

        if self._source_path.is_dir():
            self._image_paths = sorted(
                p for p in self._source_path.iterdir() if p.suffix.lower() in _IMAGE_EXTENSIONS
            )
            if not self._image_paths:
                raise ValueError(
                    f"No se encontraron imágenes soportadas en: {self._source_path}"
                )
            logger.info(
                "VideoMockCaptureStrategy cargado con %d imágenes desde %s",
                len(self._image_paths),
                self._source_path,
            )
            return

        suffix = self._source_path.suffix.lower()
        if suffix in _IMAGE_EXTENSIONS:
            self._image_paths = [self._source_path]
        elif suffix in _VIDEO_EXTENSIONS:
            try:
                import cv2  # type: ignore[import-not-found]
            except ImportError as exc:
                raise RuntimeError(
                    "Para reproducir archivos de video en VideoMockCaptureStrategy instala "
                    "'opencv-python-headless', o usa una carpeta de capturas (.png/.jpg)."
                ) from exc

            self._video_cap = cv2.VideoCapture(str(self._source_path))
            if not self._video_cap.isOpened():
                raise RuntimeError(f"No se pudo abrir el archivo de video: {self._source_path}")
            logger.info("VideoMockCaptureStrategy abierto con video: %s", self._source_path)
        else:
            raise ValueError(f"Formato de mock no soportado: {self._source_path.suffix}")

    def capture_frame(self) -> CapturedFrame | None:
        image: Image.Image | None = None

        if self._video_cap is not None:
            image = self._read_video_frame()
        elif self._image_paths:
            image = self._read_image_sequence_frame()
        else:
            image = self._generate_synthetic_frame()

        if image is None:
            return None

        self._frame_counter += 1
        return CapturedFrame(
            image=image,
            timestamp=time.monotonic(),
            wall_time=time.time(),
            frame_id=self._frame_counter,
            source=self.name,
        )

    def _read_image_sequence_frame(self) -> Image.Image | None:
        if self._image_index >= len(self._image_paths):
            if not self._loop:
                return None
            self._image_index = 0

        path = self._image_paths[self._image_index]
        self._image_index += 1
        with Image.open(path) as img:
            return img.convert("RGB")

    def _read_video_frame(self) -> Image.Image | None:
        import cv2  # type: ignore[import-not-found]

        ok, frame_bgr = self._video_cap.read()
        if not ok:
            if not self._loop:
                return None
            self._video_cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
            ok, frame_bgr = self._video_cap.read()
            if not ok:
                return None

        frame_rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
        return Image.fromarray(frame_rgb)

    def _generate_synthetic_frame(self) -> Image.Image:
        """Create an evolving synthetic HUD frame for deterministic smoke tests."""
        w, h = self._synthetic_size
        step = self._frame_counter
        bg_shade = (20 + (step * 17) % 40, 24 + (step * 11) % 30, 32)
        img = Image.new("RGB", (w, h), color=bg_shade)
        draw = ImageDraw.Draw(img)

        # Simulated minimap top-left + changing block positions so pHash varies cleanly
        draw.rectangle([20, 20, 220, 220], outline=(80, 180, 255), width=3)
        marker_x = 40 + (step * 37) % 150
        marker_y = 40 + (step * 29) % 150
        draw.ellipse([marker_x, marker_y, marker_x + 18, marker_y + 18], fill=(240, 60, 60))

        # Simulated HP & Ammo HUD bars
        hp_w = max(40, 260 - ((step * 35) % 220))
        draw.rectangle([30, h - 70, 30 + hp_w, h - 35], fill=(220, 70, 70))
        draw.rectangle([w - 220, h - 70, w - 30, h - 35], fill=(235, 200, 75))
        return img

    def stop(self) -> None:
        if self._video_cap is not None:
            with contextlib.suppress(Exception):
                self._video_cap.release()
            self._video_cap = None


class DXCamCaptureStrategy(CaptureStrategy):
    """Windows DirectX Desktop Duplication adapter (optional high-FPS upgrade).

    Requires installing `dxcam` on Windows (`pip install dxcam`). Kept decoupled
    behind `CaptureStrategy` so swapping from MSS requires zero pipeline changes.
    """

    def __init__(self, output_idx: int = 0) -> None:
        self._output_idx = output_idx
        self._camera: Any = None
        self._frame_counter = 0

    @property
    def name(self) -> str:
        return f"dxcam(output={self._output_idx})"

    def start(self) -> None:
        try:
            import dxcam  # type: ignore[import-not-found]
        except ImportError as exc:
            raise RuntimeError(
                "DXCamCaptureStrategy requiere instalar 'dxcam' en Windows (pip install dxcam)."
            ) from exc

        self._camera = dxcam.create(output_idx=self._output_idx, output_color="RGB")
        logger.info("DXCamCaptureStrategy inicializado en salida %d.", self._output_idx)

    def capture_frame(self) -> CapturedFrame | None:
        if self._camera is None:
            return None
        frame_np = self._camera.grab()
        if frame_np is None:
            return None
        image = Image.fromarray(frame_np)
        self._frame_counter += 1
        return CapturedFrame(
            image=image,
            timestamp=time.monotonic(),
            wall_time=time.time(),
            frame_id=self._frame_counter,
            source=self.name,
        )

    def stop(self) -> None:
        if self._camera is not None:
            with contextlib.suppress(Exception):
                del self._camera
            self._camera = None
