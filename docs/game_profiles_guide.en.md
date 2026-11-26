> 🌐 **Idioma / Language:** [🇪🇸 Español](game_profiles_guide.es.md) | **[🇺🇸 English](game_profiles_guide.en.md)**

# Game Profiles Guide (`profiles/`)

**HUDMind Native** uses a modular per-game "cartridge" architecture under `profiles/`. Adding support for a new title **does not require modifying C# code**: you can create a profile directly from the GUI or by creating a standard profile folder.

Built-in profiles (embedded inside the `.exe`):
- `aoe2_de` (Age of Empires II: Definitive Edition)
- `cs2` (Counter-Strike 2 — with official Valve GSI `127.0.0.1:47315`)
- `dota2` (Dota 2 — with official Valve GSI `127.0.0.1:47315`)
- `league_of_legends` (League of Legends — with Riot Live Client API `127.0.0.1:2999`)
- `rob_run` (ROB RUN)
- `rocket_league` (Rocket League — with passive `Launch.log` sensor)
- `valorant` (VALORANT — with passive `ShooterGame.log` sensor)
- `universal` (dynamic in-memory fallback when the foreground game has no dedicated folder)

---

## 1. Profile Folder Structure

```text
profiles/
└── my_new_game/
    ├── manifest.json         # Technical metadata and executable (.exe) detection
    ├── system_prompt.md      # Base coaching persona and concise tactical rules
    ├── knowledge_base.md     # (Optional) Glossary, HUD anchors, and screens to ignore
    └── custom_rules.md       # (Optional, GUI-managed) Personal rules up to 600 chars
```

> **Quick GUI Tip:** In the **`Juegos y Memoria`** tab, fill out **`+ Nuevo Juego`** (`Name` and `Executable (.exe)` — ID is derived cleanly automatically) and click **`➕ Agregar`**. HUDMind validates the ID (blocking `..` traversal and `universal`), automatically appends `.exe` if omitted, writes `manifest.json` + `system_prompt.md`, and opens it for editing your personal `custom_rules.md`.

---

## 2. `manifest.json` Schema

Real example based on [`profiles/cs2/manifest.json`](../profiles/cs2/manifest.json):

```json
{
  "game_id": "cs2",
  "display_name": "Counter-Strike 2",
  "process_names": ["cs2.exe"],
  "capture_fps": 1.0,
  "phash_threshold": 8,
  "cooldown_seconds": 10.0,
  "max_resolution": [1280, 720],
  "language": "es",
  "notes": "Perfil táctico competitivo 5v5 para Counter-Strike 2 (Premier / Faceit / Competitivo, con soporte Valve GSI oficial)."
}
```

### `manifest.json` Field Reference (`GameManifest`)

| Field | Type | Description |
| :--- | :--- | :--- |
| `game_id` | `string` | Unique lowercase identifier (`^[a-z0-9_\-]{1,64}$`, cannot be `universal`). |
| `display_name` | `string` | Display name shown in the overlay, profile picker, and post-match reviews. |
| `process_names` | `string[]` | Case-insensitive list of `.exe` filenames that activate this profile when focused. |
| `capture_fps` | `double` | Target capture frequency in Hz (`1.0` to `2.0` typical). |
| `phash_threshold` | `int` | Perceptual Hamming distance (`0`–`64`) used to skip static frames. |
| `cooldown_seconds` | `double` | Base cooldown between callouts (automatically reduced to 65% in `training` mode, min `2.8s`). |
| `max_resolution` | `[int, int]` | Reserved profile metadata hint; runtime capture scaling follows `TokenEfficiencyMode` (`eco`: `960×540`, `balanced`: `1280×720`, `high`: `1440×810`). |
| `language` | `string` | Base profile language (`"es"` or `"en"`). |
| `notes` | `string` | Short tactical summary used as a compact fallback if `system_prompt.md` is empty. |

---

## 3. Authoring Best Practices

1. **`system_prompt.md`:** Keep instructions concrete and focused on short callouts (`7–12` words), forbidding guesses outside what is visible on the HUD or passive sensors.
2. **`custom_rules.md` (GUI — max `600` characters):** Ideal for real-time personal reminders (e.g., *"Prioritize backpost on defense"*, *"Warn me only below 45% HP or when Spike is planted"*). Edits apply live to the active coach.
3. **Post-match reviews (`logs/reviews/`):** After a match, clicking **`📊 Resumen Partida`** generates an analysis of repeated mistakes plus 1 practice drill, saved automatically to `logs/reviews/yyyyMMdd_HHmmss_<game>.md` and accessible via **`📂 Resúmenes`**.
