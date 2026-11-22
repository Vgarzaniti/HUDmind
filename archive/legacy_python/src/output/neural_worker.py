"""Persistent low-overhead Neural TTS sidecar worker for HUDMind Native (.NET 9).

Reads compact JSON-line commands from stdin:
  {"cmd":"speak","text":"...","voice":"es-AR-TomasNeural","rate":"+15%","volume":0.8}
  {"cmd":"stop"}
  {"cmd":"quit"}

Keeps `edge_tts` + `pygame.mixer` warm in RAM with an LRU cache (32 phrases, <0.5 MB)
so repeated callouts play in <5ms with zero disk files and 0% GPU.
"""

from __future__ import annotations

import asyncio
import contextlib
import io
import json
import os
import sys
import threading
from collections import OrderedDict

os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")


class _MixerPlayer:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._ready = False

    def _ensure(self, volume: float) -> bool:
        with self._lock:
            try:
                import pygame  # type: ignore[import-untyped]

                if not self._ready or not pygame.mixer.get_init():
                    pygame.mixer.init()
                    self._ready = True
                pygame.mixer.music.set_volume(max(0.0, min(1.0, volume)))
                return True
            except Exception:
                return False

    def play_mp3(self, mp3_bytes: bytes, volume: float) -> bool:
        if not mp3_bytes or not self._ensure(volume):
            return False
        import pygame  # type: ignore[import-untyped]

        with self._lock:
            try:
                pygame.mixer.music.stop()
                pygame.mixer.music.load(io.BytesIO(mp3_bytes), namehint="mp3")
                pygame.mixer.music.set_volume(max(0.0, min(1.0, volume)))
                pygame.mixer.music.play()
                return True
            except Exception:
                return False

    def stop(self) -> None:
        if not self._ready:
            return
        with self._lock, contextlib.suppress(Exception):
            import pygame  # type: ignore[import-untyped]

            if pygame.mixer.get_init():
                pygame.mixer.music.stop()

    def close(self) -> None:
        self.stop()
        with self._lock, contextlib.suppress(Exception):
            import pygame  # type: ignore[import-untyped]

            if self._ready and pygame.mixer.get_init():
                pygame.mixer.quit()
            self._ready = False


async def _synthesize(text: str, voice: str, rate: str) -> bytes:
    import edge_tts

    comm = edge_tts.Communicate(text=text, voice=voice, rate=rate)
    chunks: list[bytes] = []
    async for chunk in comm.stream():
        if chunk.get("type") == "audio":
            data = chunk.get("data")
            if isinstance(data, bytes):
                chunks.append(data)
    return b"".join(chunks)


async def _main() -> None:
    player = _MixerPlayer()
    cache: OrderedDict[tuple[str, str, str], bytes] = OrderedDict()
    active_task: asyncio.Task[None] | None = None
    loop = asyncio.get_running_loop()

    def _emit(obj: dict[str, object]) -> None:
        sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
        sys.stdout.flush()

    async def _handle_speak(text: str, voice: str, rate: str, volume: float, req_id: int) -> None:
        key = (voice, rate, " ".join(text.strip().lower().split()))
        try:
            mp3 = cache.get(key)
            if mp3 is not None:
                cache.move_to_end(key)
            else:
                mp3 = await asyncio.wait_for(_synthesize(text, voice, rate), timeout=6.0)
                if mp3 and len(mp3) <= 256 * 1024:
                    cache[key] = mp3
                    cache.move_to_end(key)
                    while len(cache) > 32:
                        cache.popitem(last=False)

            ok = player.play_mp3(mp3, volume) if mp3 else False
            _emit({"ok": ok, "id": req_id})
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            _emit({"ok": False, "id": req_id, "err": str(exc)[:140]})

    _emit({"ready": True})

    try:
        while True:
            raw = await loop.run_in_executor(None, sys.stdin.readline)
            if not raw:
                break
            raw = raw.strip()
            if not raw:
                continue
            try:
                msg = json.loads(raw)
            except json.JSONDecodeError:
                _emit({"ok": False, "err": "bad_json"})
                continue

            cmd = str(msg.get("cmd", "")).lower()
            if cmd == "quit":
                break
            if cmd == "stop":
                if active_task is not None and not active_task.done():
                    active_task.cancel()
                player.stop()
                continue
            if cmd == "speak":
                text = str(msg.get("text", "")).strip()
                voice = str(msg.get("voice", "es-AR-TomasNeural")).strip()
                rate = str(msg.get("rate", "+15%")).strip()
                volume = float(msg.get("volume", 0.75))
                req_id = int(msg.get("id", 0))
                if not text:
                    continue
                # Drop-Oldest: cancel any in-flight older synthesis immediately
                if active_task is not None and not active_task.done():
                    active_task.cancel()
                active_task = asyncio.create_task(_handle_speak(text, voice, rate, volume, req_id))
    finally:
        if active_task is not None and not active_task.done():
            active_task.cancel()
        player.close()


if __name__ == "__main__":
    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(_main())
