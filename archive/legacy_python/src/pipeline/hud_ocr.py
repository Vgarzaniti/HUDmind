"""Real-time Windows 11 Native OCR + Spatial HUD Analyzer (<50ms CPU, 0% GPU, 0 login).

Uses Windows 11's built-in WinRT `Windows.Media.Ocr.OcrEngine` (with graceful pure-CPU
fallback on non-Windows or missing language packs) to read real on-screen game state:
- Automatic detection of Competitive Match vs Training/Free Play vs Menu/Lobby
- Real scoreboard numbers, match timer / overtime, boost / ammo / HP / money / loot
- Visible teammate vs rival nameplates and left/center/right spatial sectors
- Frame-to-frame live event triggers (goal/round scored, clutch timer, low boost, 1v2/2v1)
"""

from __future__ import annotations

import asyncio
import contextlib
import io
import logging
import re
import sys
from dataclasses import dataclass, field
from typing import Any, Literal

from PIL import Image

logger = logging.getLogger(__name__)

DetectedMode = Literal["match", "training", "menu"]

_TIMER_RE = re.compile(r"\b(\+?\d{1,2}:\d{2})\b")
_SCORE_PAIR_RE = re.compile(r"\b(\d{1,2})\s*[-:|]\s*(\d{1,2})\b")
_AMMO_RE = re.compile(r"\b(\d{1,3})\s*/\s*(\d{1,3})\b")
_MONEY_RE = re.compile(r"\$\s*(\d[\d,\.]{2,})")
_INT_RE = re.compile(r"\b(\d{1,3})\b")

_TRAINING_KEYWORDS = (
    "free play",
    "juego libre",
    "entrenamiento",
    "training",
    "practice",
    "práctica",
    "practica",
    "warmup",
    "calentamiento",
    "aim botz",
    "the range",
    "campo de tiro",
    "art of war",
    "arte de la guerra",
    "reset shot",
    "reiniciar tiro",
    "custom training",
    "tiro libre",
)

_MATCH_KEYWORDS = (
    "overtime",
    "prórroga",
    "prorroga",
    "tiempo extra",
    "competitive",
    "competitivo",
    "ranked",
    "clasificatoria",
    "tournament",
    "torneo",
    "round",
    "ronda",
    "match point",
    "punto de partido",
    "defuse",
    "spike",
    "plantada",
    "extraction",
    "extracción",
    "wanted",
    "alerta",
    "feudal",
    "castillos",
    "imperial",
)

_MENU_KEYWORDS = (
    "main menu",
    "menú principal",
    "menu principal",
    "garage",
    "garaje",
    "item shop",
    "tienda de objetos",
    "rocket pass",
    "buscando partida",
    "searching",
    "play online",
    "jugar en línea",
    "settings",
    "configuración",
    "configuracion",
    "salir al menú",
    "exit to menu",
    "lobby",
    "sala de espera",
    "iniciar búsqueda",
)


@dataclass(slots=True)
class OCRLine:
    """Single recognized HUD text line with normalized screen coordinates (0.0..1.0)."""

    text: str
    x: float
    y: float
    team_tint: Literal["ally", "rival", "neutral"] = "neutral"


@dataclass(slots=True)
class LiveHUDState:
    """Real-time structured snapshot extracted from a live game frame via OCR + spatial vision."""

    detected_mode: DetectedMode = "match"
    timer_text: str = ""
    timer_seconds: int | None = None
    is_overtime: bool = False
    score_text: str = ""
    hud_number_br: int | None = None  # e.g., Rocket League boost (0-100) or CS2 ammo
    hud_number_bl: int | None = None  # e.g., HP / Shield
    money_text: str = ""
    ammo_text: str = ""
    ally_names: list[str] = field(default_factory=list)
    rival_names: list[str] = field(default_factory=list)
    ally_sector: str = "atrás/rotando"
    rival_sector: str = "distancia media"
    tactical_balance: str = "transición"
    resource_pct: int = 50
    alert_pct: int = 0
    action_phase: str = "juego activo"
    raw_ocr_snippets: list[str] = field(default_factory=list)
    live_events: list[str] = field(default_factory=list)
    ocr_ms: float = 0.0

    def to_prompt_block(self, game_name: str) -> str:
        """Format a rich, real-time situational briefing for any AI coach model."""
        parts = [f"JUEGO: {game_name} | MODO DETECTADO: {self.detected_mode.upper()}"]
        if self.timer_text:
            ot_tag = " (PRÓRROGA/OVERTIME)" if self.is_overtime else ""
            parts.append(f"RELOJ: {self.timer_text}{ot_tag}")
        if self.score_text:
            parts.append(f"MARCADOR: {self.score_text}")
        if self.hud_number_br is not None:
            parts.append(f"MEDIDOR_DERECHO (Boost/Recurso): {self.hud_number_br}")
        else:
            parts.append(f"MEDIDOR_HUD_ESTIMADO: {self.resource_pct}%")
        if self.hud_number_bl is not None:
            parts.append(f"VIDA/ESCUDO_IZQ: {self.hud_number_bl}")
        if self.ammo_text:
            parts.append(f"MUNICIÓN: {self.ammo_text}")
        if self.money_text:
            parts.append(f"DINERO/BOTÍN: {self.money_text}")

        ally_desc = f"sector {self.ally_sector}"
        if self.ally_names:
            ally_desc += f" ({', '.join(self.ally_names[:3])})"
        rival_desc = f"sector {self.rival_sector}"
        if self.rival_names:
            rival_desc += f" ({', '.join(self.rival_names[:3])})"
        parts.append(f"COMPAÑEROS: {ally_desc} | RIVALES: {rival_desc}")
        parts.append(f"BALANCE: {self.tactical_balance} | FASE: {self.action_phase}")
        if self.live_events:
            parts.append(f"EVENTOS EN TIEMPO REAL AHORA MISMO: {' ; '.join(self.live_events)}")
        elif self.raw_ocr_snippets:
            parts.append(f"TEXTOS HUD VISIBLES: {' | '.join(self.raw_ocr_snippets[:6])}")
        return "\n".join(parts)

    def short_context_line(self, game_name: str) -> str:
        pieces = [f"{game_name} [{self.detected_mode}]"]
        if self.score_text:
            pieces.append(f"Marcador {self.score_text}")
        if self.timer_text:
            pieces.append(f"Reloj {self.timer_text}")
        if self.hud_number_br is not None:
            pieces.append(f"Boost/Rec {self.hud_number_br}")
        pieces.append(f"Aliado:{self.ally_sector} vs Rival:{self.rival_sector}")
        return " | ".join(pieces)[:155]


class RealTimeHUDAnalyzer:
    """Fast native Windows 11 OCR + Spatial Vision analyzer with frame-to-frame event memory."""

    def __init__(self) -> None:
        self._ocr_engine: Any = None
        self._ocr_init_attempted = False
        self._prev_state: LiveHUDState | None = None
        self._consecutive_no_timer_frames = 0

    def _ensure_ocr_engine(self) -> Any:
        if self._ocr_init_attempted:
            return self._ocr_engine
        self._ocr_init_attempted = True
        if sys.platform != "win32":
            return None
        try:
            import winrt.windows.media.ocr as winrt_ocr

            self._ocr_engine = winrt_ocr.OcrEngine.try_create_from_user_profile_languages()
        except Exception as exc:
            logger.debug("WinRT OCR no disponible (%s); usando solo análisis espacial CPU.", exc)
            self._ocr_engine = None
        return self._ocr_engine

    async def _run_winrt_ocr(self, img_rgba: Image.Image) -> list[OCRLine]:
        engine = self._ensure_ocr_engine()
        if engine is None:
            return []
        try:
            import winrt.windows.graphics.imaging as imaging
            import winrt.windows.storage.streams as streams

            w, h = img_rgba.size
            raw_bgra = img_rgba.tobytes("raw", "BGRA")
            writer = streams.DataWriter()
            writer.write_bytes(raw_bgra)
            ibuf = writer.detach_buffer()
            sb = imaging.SoftwareBitmap.create_copy_from_buffer(
                ibuf,
                imaging.BitmapPixelFormat.BGRA8,
                w,
                h,
                imaging.BitmapAlphaMode.PREMULTIPLIED,
            )
            result = await engine.recognize_async(sb)

            rgb_pixels = img_rgba.convert("RGB")
            lines: list[OCRLine] = []
            for line in result.lines:
                txt = str(line.text).strip()
                if not txt or not line.words:
                    continue
                first_rect = line.words[0].bounding_rect
                nx = max(0.0, min(1.0, float(first_rect.x) / max(1, w)))
                ny = max(0.0, min(1.0, float(first_rect.y) / max(1, h)))
                tint = self._sample_tint(
                    rgb_pixels,
                    int(first_rect.x),
                    int(first_rect.y),
                    int(first_rect.width),
                    int(first_rect.height),
                )
                lines.append(OCRLine(text=txt, x=nx, y=ny, team_tint=tint))
            return lines
        except Exception as exc:
            logger.debug("Error leyendo OCR en frame: %s", exc)
            return []

    @staticmethod
    def _sample_tint(
        rgb_img: Image.Image,
        x: int,
        y: int,
        bw: int,
        bh: int,
    ) -> Literal["ally", "rival", "neutral"]:
        w, h = rgb_img.size
        x0 = max(0, x - 4)
        y0 = max(0, y - 4)
        x1 = min(w, x + max(8, bw) + 4)
        y1 = min(h, y + max(8, bh) + 4)
        if x1 <= x0 or y1 <= y0:
            return "neutral"
        crop = rgb_img.crop((x0, y0, x1, y1))
        raw = crop.tobytes()
        ally_px = 0
        rival_px = 0
        for idx in range(0, len(raw), 3):
            r, g, b = raw[idx], raw[idx + 1], raw[idx + 2]
            if (b > r + 40 and b > 125) or (g > r + 38 and g > 130 and b > 80):
                ally_px += 1
            elif r > b + 50 and r > 155 and g < 140:
                rival_px += 1
        if ally_px >= 6 and ally_px > rival_px * 1.4:
            return "ally"
        if rival_px >= 6 and rival_px > ally_px * 1.4:
            return "rival"
        return "neutral"

    def reset(self) -> None:
        self._prev_state = None
        self._consecutive_no_timer_frames = 0

    async def analyze(self, image_bytes: bytes, game_id: str = "") -> LiveHUDState:
        """Run native OCR + spatial analysis and compute real-time delta events."""
        import time

        t0 = time.perf_counter()
        state = LiveHUDState()
        ocr_lines: list[OCRLine] = []

        try:
            with Image.open(io.BytesIO(image_bytes)) as raw_img:
                # Cap OCR input to 896x504 for consistent <45ms latency
                work = raw_img.convert("RGBA")
                if work.width > 960 or work.height > 540:
                    work.thumbnail((896, 504), Image.Resampling.BILINEAR)

                # Spatial sector analysis on 48x27 thumbnail (<1ms)
                self._fill_spatial_fields(work, state)

                # Run Windows 11 native WinRT OCR in ~30-50ms
                with contextlib.suppress(Exception):
                    ocr_lines = await asyncio.wait_for(self._run_winrt_ocr(work), timeout=0.6)
        except Exception as exc:
            logger.debug("RealTimeHUDAnalyzer fallback: %s", exc)

        self._parse_ocr_lines(ocr_lines, state, game_id=game_id)
        self._compute_live_events(state)
        state.ocr_ms = round((time.perf_counter() - t0) * 1000.0, 1)
        self._prev_state = state
        return state

    def _fill_spatial_fields(self, work_rgba: Image.Image, state: LiveHUDState) -> None:
        thumb = work_rgba.convert("RGB").resize((48, 27), Image.Resampling.NEAREST)
        w, h = thumb.size
        raw = thumb.tobytes()

        ally_cols = [0, 0, 0]
        rival_cols = [0, 0, 0]
        upper_energy = 0
        lower_energy = 0
        hud_res_fill = 0
        hud_res_tot = 0
        top_red = 0
        top_tot = 0

        for y in range(h):
            off = y * w * 3
            for x in range(w):
                p = off + x * 3
                r, g, b = raw[p], raw[p + 1], raw[p + 2]
                cidx = 0 if x < 16 else (1 if x < 32 else 2)
                if y < 6:
                    top_tot += 1
                    if r > 165 and g < 85 and b < 85:
                        top_red += 1
                if 4 <= y <= 21:
                    if (b > r + 38 and b > 125) or (g > r + 35 and g > 130 and b > 80):
                        ally_cols[cidx] += 1
                    elif r > b + 48 and r > 150 and g < 145:
                        rival_cols[cidx] += 1
                    luma = int(0.299 * r + 0.587 * g + 0.114 * b)
                    if y < 12:
                        upper_energy += luma
                    else:
                        lower_energy += luma
                if y >= 21 and (x >= 33 or x <= 14):
                    hud_res_tot += 1
                    if (r > 170 and g > 120) or (b > 165 and g > 140) or (r > 200 and g > 200 and b > 200):
                        hud_res_fill += 1

        sectors = ("izquierda", "centro", "derecha")
        a_tot = sum(ally_cols)
        r_tot = sum(rival_cols)
        state.ally_sector = sectors[max(range(3), key=lambda i: ally_cols[i])] if a_tot >= 4 else "atrás/rotando"
        state.rival_sector = sectors[max(range(3), key=lambda i: rival_cols[i])] if r_tot >= 4 else "distancia media"

        if a_tot > r_tot * 1.35 and a_tot >= 8:
            state.tactical_balance = f"ventaja aliada 2v1 por {state.ally_sector}"
        elif r_tot > a_tot * 1.35 and r_tot >= 8:
            state.tactical_balance = f"presión rival 1v2 por {state.rival_sector}"
        elif state.ally_sector == state.rival_sector and a_tot >= 6:
            state.tactical_balance = f"choque en {state.ally_sector} (riesgo Double Commit)"
        else:
            state.tactical_balance = f"aliado en {state.ally_sector}, rival en {state.rival_sector}"

        state.resource_pct = min(100, max(5, int((hud_res_fill * 185) / max(1, hud_res_tot))))
        state.alert_pct = min(100, int((top_red * 220) / max(1, top_tot)))
        if upper_energy > lower_energy * 1.18:
            state.action_phase = "fase aérea / pared alta"
        elif ally_cols[1] + rival_cols[1] > 28:
            state.action_phase = "duelo cercano en el centro"
        else:
            state.action_phase = "rotación y control de campo"

    def _parse_ocr_lines(
        self,
        lines: list[OCRLine],
        state: LiveHUDState,
        *,
        game_id: str,
    ) -> None:
        all_text_lower = " ".join(ln.text.lower() for ln in lines)
        state.raw_ocr_snippets = [ln.text for ln in lines if len(ln.text) >= 2][:10]

        menu_hits = sum(1 for kw in _MENU_KEYWORDS if kw in all_text_lower)
        training_hits = sum(1 for kw in _TRAINING_KEYWORDS if kw in all_text_lower)
        match_hits = sum(1 for kw in _MATCH_KEYWORDS if kw in all_text_lower)

        for ln in lines:
            txt = ln.text.strip()
            low = txt.lower()

            # 1. Top header: timer & scoreboard (y < 0.20)
            if ln.y < 0.20:
                tm = _TIMER_RE.search(txt)
                if tm and not state.timer_text:
                    state.timer_text = tm.group(1)
                    state.is_overtime = state.timer_text.startswith("+") or "overtime" in all_text_lower or "prórroga" in all_text_lower
                    clean_t = state.timer_text.lstrip("+")
                    try:
                        mm, ss = clean_t.split(":")
                        state.timer_seconds = int(mm) * 60 + int(ss)
                    except ValueError:
                        state.timer_seconds = None
                    # Check if flanking score digits exist around the timer (e.g., "1 01:45 2")
                    before = txt[: tm.start()].strip()
                    after = txt[tm.end() :].strip()
                    b_num = _INT_RE.search(before)
                    a_num = _INT_RE.search(after)
                    if b_num and a_num:
                        state.score_text = f"{b_num.group(1)} - {a_num.group(1)}"

                if not state.score_text:
                    sm = _SCORE_PAIR_RE.search(txt)
                    if sm and not _TIMER_RE.search(txt):
                        state.score_text = f"{sm.group(1)} - {sm.group(2)}"

            # 2. Bottom-right HUD (Boost 0-100 / Ammo)
            if ln.y >= 0.72 and ln.x >= 0.68:
                am = _AMMO_RE.search(txt)
                if am and not state.ammo_text:
                    state.ammo_text = f"{am.group(1)}/{am.group(2)}"
                else:
                    nm = _INT_RE.search(txt)
                    if nm:
                        val = int(nm.group(1))
                        if 0 <= val <= 100 and state.hud_number_br is None:
                            state.hud_number_br = val
                            state.resource_pct = val

            # 3. Bottom-left HUD (HP / Money)
            if ln.y >= 0.72 and ln.x <= 0.34:
                mm_match = _MONEY_RE.search(txt)
                if mm_match and not state.money_text:
                    state.money_text = f"${mm_match.group(1)}"
                else:
                    nm = _INT_RE.search(txt)
                    if nm and state.hud_number_bl is None:
                        val = int(nm.group(1))
                        if 0 <= val <= 200:
                            state.hud_number_bl = val

            # 4. Money anywhere (ROB RUN / CS2 / Valorant)
            if not state.money_text:
                mm_any = _MONEY_RE.search(txt)
                if mm_any:
                    state.money_text = f"${mm_any.group(1)}"

            # 5. Mid-field player / teammate / rival nameplates
            if 0.14 <= ln.y <= 0.74 and 3 <= len(txt) <= 22 and not low.isdigit():
                if ln.team_tint == "ally" and txt not in state.ally_names:
                    state.ally_names.append(txt)
                elif ln.team_tint == "rival" and txt not in state.rival_names:
                    state.rival_names.append(txt)

        # Automatic Mode Classification (Menu vs Training vs Competitive Match)
        if menu_hits >= 2 and not state.timer_text:
            state.detected_mode = "menu"
            return

        if training_hits >= 1:
            state.detected_mode = "training"
            self._consecutive_no_timer_frames = 0
            return

        if state.timer_text or state.score_text or match_hits >= 1 or state.rival_names:
            state.detected_mode = "match"
            self._consecutive_no_timer_frames = 0
            return

        # Rocket League / Shooter heuristic: no match timer visible + full 100 boost/ammo repeatedly -> Free Play / Training
        self._consecutive_no_timer_frames += 1
        if (
            game_id in ("rocket_league", "example_cs2", "example_valorant")
            and self._consecutive_no_timer_frames >= 2
            and (state.hud_number_br == 100 or not state.rival_names)
        ):
            state.detected_mode = "training"
        elif self._prev_state is not None:
            state.detected_mode = self._prev_state.detected_mode
        else:
            state.detected_mode = "match"

    def _compute_live_events(self, state: LiveHUDState) -> None:
        prev = self._prev_state
        events: list[str] = []

        if prev is not None and prev.detected_mode != state.detected_mode:
            if state.detected_mode == "match":
                events.append("CAMBIO DE MODO: Entraste a PARTIDA COMPETITIVA")
            elif state.detected_mode == "training":
                events.append("CAMBIO DE MODO: Entraste a MODO ENTRENAMIENTO / PRÁCTICA")

        if prev is not None and state.score_text and prev.score_text and state.score_text != prev.score_text:
            events.append(f"CAMBIÓ EL MARCADOR AHORA: de {prev.score_text} a {state.score_text}")

        if state.is_overtime:
            events.append(f"PRÓRROGA / OVERTIME EN VIVO ({state.timer_text}): gol de oro, cero errores")
        elif state.timer_seconds is not None and 0 < state.timer_seconds <= 60:
            events.append(f"ÚLTIMO MINUTO DE PARTIDO ({state.timer_text}): cada jugada define")

        if state.hud_number_br is not None and state.hud_number_br <= 18 and state.detected_mode == "match":
            events.append(f"BOOST/RECURSO CRÍTICO ({state.hud_number_br}): necesitás recargar/pads chicos")
        elif (
            prev is not None
            and prev.hud_number_br is not None
            and state.hud_number_br is not None
            and (prev.hud_number_br - state.hud_number_br) >= 45
        ):
            events.append(f"GASTO RÁPIDO DE BOOST/RECURSOS ({prev.hud_number_br} -> {state.hud_number_br})")

        if state.hud_number_bl is not None and 0 < state.hud_number_bl <= 30:
            events.append(f"VIDA BAJA ({state.hud_number_bl} HP): buscá cobertura inmediata")

        if "Double Commit" in state.tactical_balance and state.detected_mode == "match":
            events.append(f"RIESGO DOUBLE COMMIT en {state.ally_sector}")
        elif "1v2" in state.tactical_balance and state.detected_mode == "match":
            events.append(f"PRESIÓN RIVAL 1v2 por {state.rival_sector}")
        elif "2v1" in state.tactical_balance and state.detected_mode == "match":
            events.append(f"OPORTUNIDAD 2v1 con aliado por {state.ally_sector}")

        state.live_events = events
