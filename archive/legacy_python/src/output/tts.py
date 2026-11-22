"""Decoupled asynchronous Text-to-Speech (TTS) engines and non-blocking audio playback."""

from __future__ import annotations

import asyncio
import contextlib
import io
import logging
import os
import queue
import subprocess
import sys
import threading
from abc import ABC, abstractmethod
from collections import OrderedDict
from typing import Any

# Keep stdout clean when pygame initializes its audio mixer
os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")

from src.config.settings import AppSettings

logger = logging.getLogger(__name__)


class AudioPlayer:
    """Non-blocking in-memory audio player backed by `pygame.mixer`."""

    _MIXER_LOCK = threading.Lock()

    def __init__(self, volume: float = 0.9) -> None:
        self._volume = max(0.0, min(1.0, volume))
        self._initialized = False
        self._stop_flag = threading.Event()

    def _ensure_mixer(self) -> bool:
        if self._initialized:
            return True
        with self._MIXER_LOCK:
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
        with self._MIXER_LOCK:
            try:
                pygame.mixer.music.load(stream, namehint=fmt)
                pygame.mixer.music.set_volume(self._volume)
                pygame.mixer.music.play()
            except Exception as exc:
                logger.error("Error reproduciendo audio (%s): %s", fmt, exc)
                return

        try:
            while not self._stop_flag.is_set():
                with self._MIXER_LOCK:
                    busy = bool(pygame.mixer.get_init() and pygame.mixer.music.get_busy())
                if not busy:
                    break
                self._stop_flag.wait(0.05)
        finally:
            with self._MIXER_LOCK, contextlib.suppress(Exception):
                if pygame.mixer.get_init():
                    pygame.mixer.music.unload()

    def stop(self) -> None:
        """Stop active playback immediately."""
        self._stop_flag.set()
        if not self._initialized:
            return
        with self._MIXER_LOCK, contextlib.suppress(Exception):
            import pygame  # type: ignore[import-untyped]

            if pygame.mixer.get_init():
                pygame.mixer.music.stop()

    def close(self) -> None:
        """Release audio device cleanly."""
        self.stop()
        with self._MIXER_LOCK:
            if self._initialized:
                with contextlib.suppress(Exception):
                    import pygame  # type: ignore[import-untyped]

                    if pygame.mixer.get_init():
                        pygame.mixer.quit()
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

    def close(self) -> None:  # noqa: B027
        """Release any persistent background resources cleanly."""


class Pyttsx3TTSEngine(TTSEngine):
    """Zero-network instant offline TTS (~8ms start) using persistent Windows SAPI5 COM STA.

    Keeps a single pre-warmed `SAPI.SpVoice` instance alive on a dedicated STA thread so
    speaking a tactical callout never pays the ~970ms COM/pyttsx3 re-initialization penalty
    or the 1–4s Azure network latency of online TTS. Falls back to `pyttsx3` if SAPI5 COM
    is unavailable.
    """

    _SVS_FLAGS_ASYNC = 1
    _SVS_FPURGE_BEFORE_SPEAK = 2

    def __init__(
        self,
        rate_wpm: int = 205,
        volume: float = 0.9,
        language: str = "es",
    ) -> None:
        self._rate_wpm = rate_wpm
        self._volume = max(0.0, min(1.0, volume))
        self._language = (language or "es").lower()
        self._lock = threading.Lock()
        self._cmd_queue: queue.Queue[tuple[str, str, threading.Event | None] | None] = queue.Queue()
        self._worker_thread: threading.Thread | None = None
        self._sapi_ready = False
        self._sapi_failed = False

    @property
    def name(self) -> str:
        return f"sapi5-instant({self._language})" if self._sapi_ready else f"pyttsx3-offline({self._language})"

    def _sapi_rate(self) -> int:
        # Map WPM (~150..240) to SAPI5 Rate (-10..+10); 205 WPM -> +2 (snappy Discord coach)
        return max(-4, min(6, round((self._rate_wpm - 175) / 15)))

    def _ensure_sapi_worker(self) -> bool:
        if sys.platform != "win32" or self._sapi_failed:
            return False
        with self._lock:
            if self._worker_thread is not None and self._worker_thread.is_alive():
                return self._sapi_ready
            ready_ev = threading.Event()
            self._worker_thread = threading.Thread(
                target=self._sapi_sta_loop,
                args=(ready_ev,),
                name="sapi5-instant-tts",
                daemon=True,
            )
            self._worker_thread.start()
        ready_ev.wait(timeout=3.0)
        return self._sapi_ready

    def _select_best_voice(self, sp_voice: Any) -> None:
        with contextlib.suppress(Exception):
            tokens = sp_voice.GetVoices()
            count = int(tokens.Count)
            want_es = self._language.startswith("es")
            best_token = None
            best_score = -1
            for i in range(count):
                tok = tokens.Item(i)
                desc = str(tok.GetDescription()).lower()
                score = 0
                if want_es:
                    if "sabina" in desc or "es-mx" in desc or "spanish (mexico)" in desc:
                        score = 100
                    elif "raul" in desc or "pablo" in desc or "es-ar" in desc or "es-419" in desc:
                        score = 95
                    elif "helena" in desc or "spanish" in desc or "español" in desc:
                        score = 80
                else:
                    if "zira" in desc or "david" in desc or "en-us" in desc:
                        score = 100
                    elif "english" in desc:
                        score = 80
                if score > best_score:
                    best_score = score
                    best_token = tok
            if best_token is not None and best_score > 0:
                sp_voice.Voice = best_token

    def _sapi_sta_loop(self, ready_ev: threading.Event) -> None:
        com_initialized = False
        sp_voice: Any = None
        try:
            import comtypes  # type: ignore[import-untyped]
            import comtypes.client  # type: ignore[import-untyped]

            comtypes.CoInitialize()
            com_initialized = True
            sp_voice = comtypes.client.CreateObject("SAPI.SpVoice")
            self._select_best_voice(sp_voice)
            sp_voice.Rate = self._sapi_rate()
            sp_voice.Volume = int(round(self._volume * 100))
            # Silent pre-warm so the first live tactical callout starts in ~8ms
            sp_voice.Speak("", self._SVS_FLAGS_ASYNC | self._SVS_FPURGE_BEFORE_SPEAK)
            self._sapi_ready = True
        except Exception as exc:
            logger.debug("SAPI5 persistente no disponible (%s); usando pyttsx3 fallback.", exc)
            self._sapi_failed = True
            self._sapi_ready = False
            ready_ev.set()
            if com_initialized:
                with contextlib.suppress(Exception):
                    import comtypes

                    comtypes.CoUninitialize()
            return

        ready_ev.set()
        try:
            while True:
                item = self._cmd_queue.get()
                if item is None:
                    break
                cmd, text, done_ev = item
                try:
                    if sp_voice is not None:
                        if cmd == "speak" and text:
                            sp_voice.Speak(
                                text,
                                self._SVS_FLAGS_ASYNC | self._SVS_FPURGE_BEFORE_SPEAK,
                            )
                        elif cmd == "stop":
                            sp_voice.Speak(
                                "",
                                self._SVS_FLAGS_ASYNC | self._SVS_FPURGE_BEFORE_SPEAK,
                            )
                except Exception as exc:
                    logger.error("Error en SAPI5 instantáneo: %s", exc)
                finally:
                    if done_ev is not None:
                        done_ev.set()
        finally:
            if sp_voice is not None:
                with contextlib.suppress(Exception):
                    sp_voice.Speak("", self._SVS_FLAGS_ASYNC | self._SVS_FPURGE_BEFORE_SPEAK)
            if com_initialized:
                with contextlib.suppress(Exception):
                    import comtypes

                    comtypes.CoUninitialize()
            self._sapi_ready = False

    def _speak_fallback_blocking(self, text: str) -> None:
        with self._lock:
            com_initialized = False
            try:
                with contextlib.suppress(Exception):
                    import comtypes

                    comtypes.CoInitialize()
                    com_initialized = True
                import pyttsx3  # type: ignore[import-untyped]

                engine = pyttsx3.init()
                engine.setProperty("rate", self._rate_wpm)
                engine.setProperty("volume", self._volume)
                engine.say(text)
                engine.runAndWait()
                engine.stop()
            except Exception as exc:
                logger.error("Error en Pyttsx3TTSEngine fallback: %s", exc)
            finally:
                if com_initialized:
                    with contextlib.suppress(Exception):
                        import comtypes

                        comtypes.CoUninitialize()

    def _speak_blocking(self, text: str) -> None:
        if self._ensure_sapi_worker():
            done_ev = threading.Event()
            self._cmd_queue.put(("speak", text, done_ev))
            done_ev.wait(timeout=2.0)
            return
        self._speak_fallback_blocking(text)

    async def speak(self, text: str) -> None:
        await asyncio.to_thread(self._speak_blocking, text)

    def stop(self) -> None:
        if self._sapi_ready:
            self._cmd_queue.put(("stop", "", None))

    def close(self) -> None:
        if self._worker_thread is not None and self._worker_thread.is_alive():
            self._cmd_queue.put(None)
            self._worker_thread.join(timeout=1.5)
            self._worker_thread = None
        self._sapi_ready = False


class EdgeTTSEngine(TTSEngine):
    """Neural online TTS strategy using `edge-tts` with in-memory MP3 playback & LRU cache.

    Caches the most recent synthesized phrases in RAM (`OrderedDict`, max 32 items,
    <0.5 MB total) so recurring tactical callouts play in 0ms without network I/O.
    Falls back automatically to `Pyttsx3TTSEngine` if the network is unavailable.
    """

    _MAX_CACHEABLE_BYTES = 256 * 1024

    def __init__(
        self,
        voice: str = "es-AR-TomasNeural",
        rate: str = "+10%",
        player: AudioPlayer | None = None,
        fallback_engine: TTSEngine | None = None,
        cache_max_entries: int = 32,
    ) -> None:
        self._voice = voice
        self._rate = rate
        self._player = player or AudioPlayer()
        self._fallback = fallback_engine
        self._cache_max_entries = max(0, cache_max_entries)
        self._audio_cache: OrderedDict[tuple[str, str, str], bytes] = OrderedDict()
        self._cache_hits = 0

    @property
    def name(self) -> str:
        return f"edge-tts({self._voice})"

    @property
    def cache_hits(self) -> int:
        return self._cache_hits

    @property
    def cache_size(self) -> int:
        return len(self._audio_cache)

    def _cache_key(self, text: str) -> tuple[str, str, str]:
        normalized = " ".join(text.strip().lower().split())
        return (self._voice, self._rate, normalized)

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

    async def _get_or_synthesize_mp3(self, text: str) -> bytes:
        key = self._cache_key(text)
        if self._cache_max_entries > 0 and key in self._audio_cache:
            self._audio_cache.move_to_end(key)
            self._cache_hits += 1
            return self._audio_cache[key]

        audio_bytes = await asyncio.wait_for(self.synthesize_mp3(text), timeout=8.0)
        if (
            audio_bytes
            and self._cache_max_entries > 0
            and len(audio_bytes) <= self._MAX_CACHEABLE_BYTES
        ):
            self._audio_cache[key] = audio_bytes
            self._audio_cache.move_to_end(key)
            while len(self._audio_cache) > self._cache_max_entries:
                self._audio_cache.popitem(last=False)
        return audio_bytes

    async def speak(self, text: str) -> None:
        try:
            audio_bytes = await self._get_or_synthesize_mp3(text)
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
    offline_fallback = Pyttsx3TTSEngine(
        volume=settings.tts_volume,
        language=settings.voice_language,
    )

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
        # Pre-warm persistent offline SAPI5 STA worker so first live callout starts in ~8ms
        if isinstance(self._engine, Pyttsx3TTSEngine):
            await asyncio.to_thread(self._engine._ensure_sapi_worker)
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

    async def wait_until_idle(self, timeout: float = 15.0) -> None:
        """Wait until all queued voice lines have finished speaking."""
        if not self._enabled or not self._running:
            return
        with contextlib.suppress(TimeoutError):
            await asyncio.wait_for(self._queue.join(), timeout=timeout)

    async def stop(self) -> None:
        self._running = False
        self._engine.stop()
        if self._worker_task is not None:
            self._worker_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._worker_task
            self._worker_task = None
        self._engine.close()
        self._player.close()
        logger.info("VoiceService detenido.")
