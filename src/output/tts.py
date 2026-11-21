"""Decoupled asynchronous Text-to-Speech (TTS) engines and non-blocking audio playback."""

from __future__ import annotations

import asyncio
import contextlib
import io
import logging
import os
import subprocess
import threading
import time
from abc import ABC, abstractmethod

# Keep stdout clean when pygame initializes its audio mixer
os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")

from src.config.settings import AppSettings

logger = logging.getLogger(__name__)


class AudioPlayer:
    """Non-blocking in-memory audio player backed by `pygame.mixer`."""

    def __init__(self, volume: float = 0.9) -> None:
        self._volume = max(0.0, min(1.0, volume))
        self._initialized = False
        self._lock = threading.Lock()
        self._stop_flag = threading.Event()

    def _ensure_mixer(self) -> bool:
        if self._initialized:
            return True
        with self._lock:
            if self._initialized:
                return True
            try:
                import pygame  # type: ignore[import-untyped]

                if not pygame.mixer.get_init():
                    pygame.mixer.init()
                pygame.mixer.music.set_volume(self._volume)
                self._initialized = True
                return True
            except Exception as exc:
                logger.warning("No se pudo inicializar pygame.mixer para audio: %s", exc)
                return False

    def play_bytes_blocking(self, audio_bytes: bytes, fmt: str = "mp3") -> None:
        """Play MP3/WAV bytes from memory without creating temporary files on disk."""
        if not audio_bytes or not self._ensure_mixer():
            return

        import pygame  # type: ignore[import-untyped]

        self._stop_flag.clear()
        stream = io.BytesIO(audio_bytes)
        with self._lock:
            try:
                pygame.mixer.music.load(stream, namehint=fmt)
                pygame.mixer.music.set_volume(self._volume)
                pygame.mixer.music.play()
            except Exception as exc:
                logger.error("Error reproduciendo audio (%s): %s", fmt, exc)
                return

        while pygame.mixer.music.get_busy() and not self._stop_flag.is_set():
            time.sleep(0.05)

    def stop(self) -> None:
        """Stop active playback immediately."""
        self._stop_flag.set()
        if not self._initialized:
            return
        try:
            import pygame  # type: ignore[import-untyped]

            if pygame.mixer.get_init():
                pygame.mixer.music.stop()
        except Exception:
            pass

    def close(self) -> None:
        """Release audio device cleanly."""
        self.stop()
        if self._initialized:
            try:
                import pygame  # type: ignore[import-untyped]

                if pygame.mixer.get_init():
                    pygame.mixer.quit()
            except Exception:
                pass
            self._initialized = False


class TTSEngine(ABC):
    """Strategy interface for interchangeable TTS backends."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Human-readable engine name."""

    @abstractmethod
    async def speak(self, text: str) -> None:
        """Synthesize and play `text` asynchronously without blocking the event loop."""

    def stop(self) -> None:  # noqa: B027
        """Interrupt active speech if supported."""


class Pyttsx3TTSEngine(TTSEngine):
    """Zero-network offline TTS strategy using Windows SAPI5 via `pyttsx3`."""

    def __init__(self, rate_wpm: int = 195, volume: float = 0.9) -> None:
        self._rate_wpm = rate_wpm
        self._volume = max(0.0, min(1.0, volume))
        self._lock = threading.Lock()

    @property
    def name(self) -> str:
        return "pyttsx3(offline)"

    def _speak_blocking(self, text: str) -> None:
        with self._lock:
            try:
                import pyttsx3  # type: ignore[import-untyped]

                engine = pyttsx3.init()
                engine.setProperty("rate", self._rate_wpm)
                engine.setProperty("volume", self._volume)
                engine.say(text)
                engine.runAndWait()
                engine.stop()
            except Exception as exc:
                logger.error("Error en Pyttsx3TTSEngine: %s", exc)

    async def speak(self, text: str) -> None:
        await asyncio.to_thread(self._speak_blocking, text)


class EdgeTTSEngine(TTSEngine):
    """Neural online TTS strategy using `edge-tts` with in-memory MP3 playback.

    Falls back automatically to `Pyttsx3TTSEngine` if the network is unavailable.
    """

    def __init__(
        self,
        voice: str = "es-AR-TomasNeural",
        rate: str = "+10%",
        player: AudioPlayer | None = None,
        fallback_engine: TTSEngine | None = None,
    ) -> None:
        self._voice = voice
        self._rate = rate
        self._player = player or AudioPlayer()
        self._fallback = fallback_engine

    @property
    def name(self) -> str:
        return f"edge-tts({self._voice})"

    async def synthesize_mp3(self, text: str) -> bytes:
        import edge_tts

        communicate = edge_tts.Communicate(text=text, voice=self._voice, rate=self._rate)
        chunks: list[bytes] = []
        async for chunk in communicate.stream():
            if chunk.get("type") == "audio":
                data = chunk.get("data")
                if isinstance(data, bytes):
                    chunks.append(data)
        return b"".join(chunks)

    async def speak(self, text: str) -> None:
        try:
            audio_bytes = await asyncio.wait_for(self.synthesize_mp3(text), timeout=8.0)
            if audio_bytes:
                await asyncio.to_thread(self._player.play_bytes_blocking, audio_bytes, "mp3")
                return
        except asyncio.CancelledError:
            self._player.stop()
            raise
        except Exception as exc:
            logger.warning("edge-tts falló (%s). Intentando fallback offline...", exc)

        if self._fallback is not None:
            await self._fallback.speak(text)

    def stop(self) -> None:
        self._player.stop()


class PiperTTSEngine(TTSEngine):
    """Local neural TTS strategy using the `piper` binary piped to in-memory WAV playback."""

    def __init__(
        self,
        binary_path: str = "piper",
        model_path: str = "",
        player: AudioPlayer | None = None,
        fallback_engine: TTSEngine | None = None,
    ) -> None:
        self._binary_path = binary_path
        self._model_path = model_path
        self._player = player or AudioPlayer()
        self._fallback = fallback_engine

    @property
    def name(self) -> str:
        return f"piper({self._model_path or 'unconfigured'})"

    def _synthesize_wav_blocking(self, text: str) -> bytes:
        import wave

        if not self._model_path:
            raise RuntimeError("PIPER_MODEL_PATH no configurado en .env")
        proc = subprocess.run(
            [self._binary_path, "--model", self._model_path, "--output-raw"],
            input=text.encode("utf-8"),
            capture_output=True,
            check=True,
            timeout=10,
        )
        wav_buf = io.BytesIO()
        with wave.open(wav_buf, "wb") as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)
            wf.setframerate(22050)
            wf.writeframes(proc.stdout)
        return wav_buf.getvalue()

    async def speak(self, text: str) -> None:
        try:
            wav_bytes = await asyncio.to_thread(self._synthesize_wav_blocking, text)
            if wav_bytes:
                await asyncio.to_thread(self._player.play_bytes_blocking, wav_bytes, "wav")
                return
        except Exception as exc:
            logger.warning("PiperTTSEngine falló (%s). Usando fallback...", exc)

        if self._fallback is not None:
            await self._fallback.speak(text)

    def stop(self) -> None:
        self._player.stop()


def create_tts_engine(settings: AppSettings, player: AudioPlayer) -> TTSEngine:
    """Factory that instantiates the configured TTSEngine strategy."""
    offline_fallback = Pyttsx3TTSEngine(volume=settings.tts_volume)

    if settings.tts_engine == "pyttsx3":
        return offline_fallback
    if settings.tts_engine == "piper":
        return PiperTTSEngine(
            binary_path=settings.piper_binary,
            model_path=settings.piper_model_path,
            player=player,
            fallback_engine=offline_fallback,
        )
    return EdgeTTSEngine(
        voice=settings.tts_voice,
        rate=settings.tts_rate,
        player=player,
        fallback_engine=offline_fallback,
    )


class VoiceService:
    """Asynchronous Drop-Oldest voice dispatcher wrapping a pluggable TTSEngine."""

    def __init__(self, settings: AppSettings, enabled: bool = True) -> None:
        self._enabled = enabled
        self._player = AudioPlayer(volume=settings.tts_volume)
        self._engine: TTSEngine = create_tts_engine(settings, self._player)
        self._queue: asyncio.Queue[str] = asyncio.Queue(maxsize=1)
        self._worker_task: asyncio.Task[None] | None = None
        self._running = False

    @property
    def engine_name(self) -> str:
        return self._engine.name

    async def start(self) -> None:
        if not self._enabled or self._running:
            return
        self._running = True
        self._worker_task = asyncio.create_task(self._worker_loop(), name="tts-worker-loop")
        logger.info("VoiceService iniciado | motor=%s", self._engine.name)

    def enqueue(self, text: str) -> None:
        """Schedule a voice line using Drop-Oldest so outdated lines never pile up."""
        if not self._enabled or not self._running:
            return
        clean = text.strip()
        if not clean:
            return

        if self._queue.full():
            with contextlib.suppress(asyncio.QueueEmpty):
                self._queue.get_nowait()
        with contextlib.suppress(asyncio.QueueFull):
            self._queue.put_nowait(clean)

    async def _worker_loop(self) -> None:
        while self._running:
            try:
                line = await self._queue.get()
                await self._engine.speak(line)
                self._queue.task_done()
            except asyncio.CancelledError:
                self._engine.stop()
                raise
            except Exception as exc:
                logger.error("Error inesperado en VoiceService: %s", exc)

    async def stop(self) -> None:
        self._running = False
        self._engine.stop()
        if self._worker_task is not None:
            self._worker_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._worker_task
            self._worker_task = None
        self._player.close()
        logger.info("VoiceService detenido.")
