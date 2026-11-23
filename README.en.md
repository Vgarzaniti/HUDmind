<p align="center">
  <img src="assets/hudmind-banner.png" alt="HUDMind — Real-Time AI eSports Coach" width="100%" />
</p>

> 🌐 **Idioma / Language:** [🇪🇸 Español](README.es.md) | **[🇺🇸 English](README.en.md)**

# HUDMind (.NET 9 Native) — Real-Time Tactical Video Game Coach

**HUDMind** is a real-time tactical co-pilot and coach for **Windows 10/11 (x64)** built on **pure .NET 9 + WPF + Win32**, using **0% GPU** (`RenderMode.SoftwareOnly`), **$0 infrastructure cost**, **zero external NuGet packages**, and **100% passive / Anti-Cheat Safe** observation (external GDI capture `GetClientRect` + `PROCESS_QUERY_LIMITED_INFORMATION` + `RegisterHotKey` + official read-only APIs/logs, with zero memory injection or hooking).

---

## Key Features

- **Dual Conversational & Turn-Based Engines:**
  - **Live Conversational (Bidirectional WebSocket):** 16 kHz mic input ↔ 24 kHz native voice output + live screen frames with acoustic echo suppression and smart *barge-in*, compatible with **Gemini Live**, **OpenAI Realtime**, and **xAI Grok Voice**.
  - **Multi-AI Turn Engine (HTTP/2 Keep-Alive + Hedged Vision Racing):** automatic failover across **Gemini**, **Groq (free LPU)**, **OpenAI**, **Claude**, **xAI**, **Mistral**, **OpenRouter**, **Pollinations**, and **Airforce** + blind text-only models powered by **Windows WinRT OCR + official Logs/APIs** (`Rocket League`, `VALORANT`, `League of Legends` API `127.0.0.1:2999`, `CS2` and `Dota 2` Valve GSI `127.0.0.1:47315`).
- **3 Independent Communication Modes + Text Chat:**
  - `🎙 Charla`: live microphone (**Open Mic with echo suppression** or **Push-to-Talk** key) + coach voice.
  - `🔊 Solo voz del coach`: microphone off, coach speaks while you can send text chat messages.
  - `💬 Solo texto`: 100% silent, written chat + overlay only.
- **Skill Tier / Rank & Pro-Standard Calibration:** adapts tactical depth, terminology, and strictness from *Beginner* to *Pro / GC / Radiant / Faceit 10* in Spanish or English.
- **Flexible Hardware Audio & System Tray:** WinMM microphone and speaker selection, RMS mic test, minimize to **System Tray (Win32 `Shell_NotifyIconW`)**, scalable/compact **PerMonitorV2** click-through overlay, and **`📊 Resumen Partida`** post-match review generator.

---

## Bundled Game Profiles (`profiles/`)

| Profile (`game_id`) | Game | Detected Executable | Optional Passive Sensors |
| :--- | :--- | :--- | :--- |
| `universal` | **Any Game (Universal)** | Any foreground game | WinRT OCR + automatic phase detection |
| `rocket_league` | **Rocket League** | `RocketLeague.exe` | Official `Launch.log` (`Freeplay`, `GoalScored`, `LoadMap`) |
| `cs2` | **Counter-Strike 2** | `cs2.exe` | **Official Valve GSI** (`127.0.0.1:47315`) + WinRT OCR |
| `valorant` | **VALORANT** | `VALORANT-Win64-Shipping.exe` | Official `ShooterGame.log` (`MENU`, `PREGAME`, `INGAME`) |
| `league_of_legends` | **League of Legends** | `League of Legends.exe` | Official **Riot Live Client API** (`https://127.0.0.1:2999`) |
| `dota2` | **Dota 2** | `dota2.exe` | **Official Valve GSI** (`127.0.0.1:47315`) |
| `aoe2_de` | **Age of Empires II: DE** | `AoE2DE_s.exe`, `AoE2DE.exe` | WinRT OCR |
| `rob_run` | **ROB RUN** | `ROB RUN.exe` | WinRT OCR |

*(For CS2 / Dota 2 GSI, install `gamestate_integration_hudmind.cfg` in 1 click from **⚙ Coach, Rango y Voz → 🎮 Instalar GSI Oficial**).*

- **Full GUI Management (No JSON editing):** add or **edit** AI accounts (custom HTTPS `BaseUrl`, `Vision` vs `Text-Only` override), create or edit **game profiles** (`EXEs`, `Cooldown`, `pHash`, and personal rules), toggle subtle Win32 audio cues, open saved Markdown reviews (`logs/reviews/`), and get automatic Exclusive-Fullscreen black-screen diagnostics.
- **100% Portable & In-App Update Alerts:** all 7 bundled profiles are embedded inside the binary and auto-seed into `%LocalAppData%\HUDMind\profiles`. On startup (and every 60 min while idle), HUDMind checks **GitHub Releases** in the background and shows an in-app banner whenever a newer version is available, featuring a **`⬇ Descargar actualización`** button and automatic **SHA-256** verification.

---

## Quick Download (Releases)

1. Download **`HUDMind.Native-Standalone-win-x64.exe`** from **[Releases](https://github.com/Vgarzaniti/HUDmind/releases/latest)** (no .NET 9 installation needed), or **`HUDMind.Native-win-x64.exe`** (~26 MB) if you already have the .NET 9 Desktop Runtime.
2. Run the `.exe` and paste a free **Groq** or **Gemini** API key in **🔑 Cuentas IA**.

---

## Build, Test & Run (`.NET 9`)

```powershell
# 1. Build entire solution (0 warnings / 0 errors)
dotnet build native\HUDMind.sln -c Release

# 2. Cross-platform Core suite (36 tests) + Native Windows suite (8 tests) + WPF smoke
dotnet run --project native\HUDMind.Tests\HUDMind.Tests.csproj -c Release --no-build
dotnet run --project native\HUDMind.Windows.Tests\HUDMind.Windows.Tests.csproj -c Release --no-build
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 3. Publish single-file executable (x64, 0% GPU)
dotnet publish native\HUDMind.App\HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist\native

# Optional for PCs without .NET 9 Desktop Runtime installed (--self-contained):
dotnet publish native\HUDMind.App\HUDMind.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist\native-standalone
```

You can also launch directly via [HUDMind.bat](HUDMind.bat).

---

## Complete Documentation (Español / English)

| Document | Español (`.es.md`) | English (`.en.md`) |
| :--- | :--- | :--- |
| Main Guide (README) | [README.es.md](README.es.md) | [README.en.md](README.en.md) |
| AI Agent Context (`AGENTS`/`CLAUDE`/`GEMINI`) | [AGENTS.es.md](AGENTS.es.md) | [AGENTS.en.md](AGENTS.en.md) |
| Pull Request & Contribution Guide | [docs/pull_request_guide.es.md](docs/pull_request_guide.es.md) · [CONTRIBUTING.es.md](CONTRIBUTING.es.md) | [docs/pull_request_guide.en.md](docs/pull_request_guide.en.md) · [CONTRIBUTING.en.md](CONTRIBUTING.en.md) |
| Versioning `vN.N.N` & Release Policy | [docs/versioning_and_releases.es.md](docs/versioning_and_releases.es.md) | [docs/versioning_and_releases.en.md](docs/versioning_and_releases.en.md) |
| Technical Architecture (IEEE 42010) | [docs/architecture.es.md](docs/architecture.es.md) | [docs/architecture.en.md](docs/architecture.en.md) |
| Software Requirements (SRS IEEE 29148) | [docs/srs.es.md](docs/srs.es.md) | [docs/srs.en.md](docs/srs.en.md) |
| Test Plan & Report (IEEE 29119) | [docs/test_plan_ieee29119.es.md](docs/test_plan_ieee29119.es.md) | [docs/test_plan_ieee29119.en.md](docs/test_plan_ieee29119.en.md) |
| Game Profiles Guide | [docs/game_profiles_guide.es.md](docs/game_profiles_guide.es.md) | [docs/game_profiles_guide.en.md](docs/game_profiles_guide.en.md) |
| Security Policy | [SECURITY.es.md](SECURITY.es.md) | [SECURITY.en.md](SECURITY.en.md) |

---

## Repository Structure

```text
.
├── .cursor/rules/                  # Automatic rules for Cursor AI
├── .github/
│   ├── ISSUE_TEMPLATE/             # Bilingual Bug Report & Feature Request templates
│   ├── PULL_REQUEST_TEMPLATE.md    # Mandatory PR checklist (type, CI checks, SemVer, security)
│   ├── copilot-instructions.md     # Automatic instructions for GitHub Copilot
│   ├── dependabot.yml              # Monthly GitHub Actions updates
│   └── workflows/
│       ├── ci.yml                  # .NET 9 CI (ubuntu-latest core + windows-latest build/tests/smoke)
│       └── release.yml             # Automated Windows x64 Release (tags v*.*.* or manual + SHA256SUMS)
├── AGENTS.md / CLAUDE.md / GEMINI.md # Instant multi-AI context (+ AGENTS.es.md / AGENTS.en.md)
├── .gitignore                      # Protects .env, config/*.json, custom_rules.md, logs/, bin/obj & dist/
├── HUDMind.bat                     # Windows launcher for the native app
├── native/
│   ├── HUDMind.sln                 # .NET 9 solution
│   ├── HUDMind.Core/               # Models, Config/SSRF/Redaction, Embedded Profiles, Updates, Wire Protocol, Engines
│   ├── HUDMind.Windows/            # GDI Capture, WinMM Audio/Devices, WinRT Natural TTS + OneCore, GSI, OCR, KnownFolders
│   ├── HUDMind.App/                # WPF UI (0% GPU), PerMonitorV2 Overlay, Win32 Tray Icon & Update Banner
│   ├── HUDMind.Tests/              # Cross-platform Core test suite (36 tests)
│   └── HUDMind.Windows.Tests/      # Win32, GSI, TLS, Logs, Audio & KnownFolders test suite (8 tests)
├── profiles/                       # Game profiles and tactical prompts
├── docs/                           # Bilingual technical documentation (*.es.md and *.en.md)
└── archive/legacy_python/          # Archived legacy Python code after the .NET 9 migration
```
