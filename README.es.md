<p align="center">
  <img src="assets/hudmind-banner.png" alt="HUDMind — Real-Time AI eSports Coach" width="100%" />
</p>

> 🌐 **Idioma / Language:** **[🇪🇸 Español](README.es.md)** | [🇺🇸 English](README.en.md)

# HUDMind (.NET 9 Native) — Coach Táctico de Videojuegos en Tiempo Real

**HUDMind** es un copiloto y coach táctico en tiempo real para **Windows 10/11 (x64)** construido en **.NET 9 + WPF + Win32 puro**, **0% GPU** (`RenderMode.SoftwareOnly`), **$0 costo de infraestructura**, **cero NuGet externos** y **100% pasivo / Anti-Cheat Safe** (captura GDI externa `GetClientRect` + `PROCESS_QUERY_LIMITED_INFORMATION` + `RegisterHotKey` + APIs/logs oficiales de solo lectura, sin inyección ni lectura de memoria).

---

## Características Principales

- **Doble Motor Conversacional e Instantáneo:**
  - **Live Conversacional (WebSocket bidireccional):** audio 16 kHz ↔ voz nativa 24 kHz + video en vivo con supresión de eco y *barge-in* inteligente, compatible con **Gemini Live**, **OpenAI Realtime** y **xAI Grok Voice**.
  - **Turnos Multi-IA (HTTP/2 Keep-Alive + Hedged Vision Racing):** failover automático entre **Gemini**, **Groq (LPU gratis)**, **OpenAI**, **Claude**, **xAI**, **Mistral**, **OpenRouter**, **Pollinations**, **Airforce** y **Ollama** local + modelos de solo texto alimentados con **Windows WinRT OCR + Logs/APIs oficiales** (`Rocket League`, `VALORANT`, `League of Legends` API `127.0.0.1:2999`, `CS2` y `Dota 2` Valve GSI `127.0.0.1:47315`).
- **3 Modos de Comunicación Separados + Chat Escrito:**
  - `🎙 Charla`: micrófono en vivo (**Mic Abierto con supresión de eco** o **Push-to-Talk** por tecla) + voz del coach.
  - `🔊 Solo voz del coach`: micrófono apagado, el coach te habla y podés escribir por chat.
  - `💬 Solo texto`: 100% silencioso, solo chat escrito y overlay.
- **Calibración por Nivel / Rango y Estándar Pro:** adapta profundidad, vocabulario y criterio táctico desde *Principiante* hasta *Pro / GC / Radiant / Faceit 10* en Español o Inglés.
- **Audio Hardware Flexible & Tray:** selector de micrófono y salida de audio WinMM, prueba de micrófono con medición RMS, minimización a **Bandeja del Sistema (Win32 `Shell_NotifyIconW`)**, overlay compacto/escalable **PerMonitorV2** y botón **`📊 Resumen Partida`** al finalizar.

---

## Perfiles Incluidos de Fábrica (`profiles/`)

| Perfil (`game_id`) | Juego | Ejecutable Detectado | Sensores Pasivos Opcionales |
| :--- | :--- | :--- | :--- |
| `universal` | **Cualquier Juego (Universal)** | Cualquier juego en foreground | WinRT OCR + detección automática de fase |
| `rocket_league` | **Rocket League** | `RocketLeague.exe` | Log oficial `Launch.log` (`Freeplay`, `GoalScored`, `LoadMap`) |
| `cs2` | **Counter-Strike 2** | `cs2.exe` | **Valve GSI oficial** (`127.0.0.1:47315`) + WinRT OCR |
| `valorant` | **VALORANT** | `VALORANT-Win64-Shipping.exe` | Log oficial `ShooterGame.log` (`MENU`, `PREGAME`, `INGAME`) |
| `league_of_legends` | **League of Legends** | `League of Legends.exe` | API oficial **Riot Live Client** (`https://127.0.0.1:2999`) |
| `dota2` | **Dota 2** | `dota2.exe` | **Valve GSI oficial** (`127.0.0.1:47315`) |
| `aoe2_de` | **Age of Empires II: DE** | `AoE2DE_s.exe`, `AoE2DE.exe` | WinRT OCR |
| `rob_run` | **ROB RUN** | `ROB RUN.exe` | WinRT OCR |

*(Para CS2 / Dota 2 GSI, podés instalar `gamestate_integration_hudmind.cfg` en 1 clic desde **🎙 Voz y Ajustes → 🎮 Activar conexión CS2 / Dota 2**).*

- **Gestión completa sin tocar JSON:** agregá o **editá** cuentas IA (con `BaseUrl` HTTPS custom y selector de modo `Visión` vs `Solo Texto`), creá o editá **perfiles de juegos** (`EXEs`, `Cooldown`, `pHash` y reglas personales), activá tonos sutiles Win32, abrí tus resúmenes Markdown (`logs/reviews/`) en **`📂 Resúmenes`** y aprovechá diagnóstico automático ante pantallas negras de Fullscreen Exclusivo.
- **100% Portable, Sellado y Auto-Actualizable:** los 7 perfiles incluidos y las reglas base vienen cifrados dentro del vault autenticado AES-256-GCM (`HUDMind.Vault.bin`) en el binario `ReadyToRun` single-file y se auto-crean en `%LocalAppData%\HUDMind\profiles`. Al abrir el `.exe` (y cada 60 min en reposo), HUDMind consulta **GitHub Releases** en segundo plano y muestra un cartel dentro de la ventana cuando hay una versión nueva disponible, con botón **`⬇ Descargar actualización`** y verificación **SHA-256**.

---

## Descarga Rápida (Releases)

1. Descargá **`HUDMind.Native-Standalone-win-x64.exe`** desde **[Releases](https://github.com/Vgarzaniti/HUDmind/releases/latest)** (no requiere instalar .NET 9 aparte), o **`HUDMind.Native-win-x64.exe`** (~26 MB) si ya tenés .NET 9 Desktop Runtime.
2. Ejecutá el `.exe` y pegá una API Key gratuita de **Groq** o **Gemini** en **🤖 Proveedores IA**.

---

## Compilación, Tests y Ejecución (`.NET 9`)

```powershell
# 1. Compilar solución completa (0 warnings / 0 errors)
dotnet build native\HUDMind.sln -c Release

# 2. Suite Core multiplataforma (50 pruebas) + suite nativa Windows (15 pruebas) + smoke WPF
dotnet run --project native\HUDMind.Tests\HUDMind.Tests.csproj -c Release --no-build
dotnet run --project native\HUDMind.Windows.Tests\HUDMind.Windows.Tests.csproj -c Release --no-build
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 3. Publicar ejecutable single-file (x64, 0% GPU)
dotnet publish native\HUDMind.App\HUDMind.App.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -o dist\native

# Opcional para PCs sin .NET 9 Desktop Runtime instalado (--self-contained):
dotnet publish native\HUDMind.App\HUDMind.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist\native-standalone
```

También podés iniciar directamente con [HUDMind.bat](HUDMind.bat).

---

## Documentación Completa (Español / English)

| Documento | Español (`.es.md`) | English (`.en.md`) |
| :--- | :--- | :--- |
| Guía Principal (README) | [README.es.md](README.es.md) | [README.en.md](README.en.md) |
| Contexto para IAs (`AGENTS`/`CLAUDE`/`GEMINI`) | [AGENTS.es.md](AGENTS.es.md) | [AGENTS.en.md](AGENTS.en.md) |
| Guía de Pull Requests y Contribución | [docs/pull_request_guide.es.md](docs/pull_request_guide.es.md) · [CONTRIBUTING.es.md](CONTRIBUTING.es.md) | [docs/pull_request_guide.en.md](docs/pull_request_guide.en.md) · [CONTRIBUTING.en.md](CONTRIBUTING.en.md) |
| Nomenclatura `vN.N.N` y Releases | [docs/versioning_and_releases.es.md](docs/versioning_and_releases.es.md) | [docs/versioning_and_releases.en.md](docs/versioning_and_releases.en.md) |
| Arquitectura Técnica (IEEE 42010) | [docs/architecture.es.md](docs/architecture.es.md) | [docs/architecture.en.md](docs/architecture.en.md) |
| Requisitos de Software (SRS IEEE 29148) | [docs/srs.es.md](docs/srs.es.md) | [docs/srs.en.md](docs/srs.en.md) |
| Plan y Reporte de Pruebas (IEEE 29119) | [docs/test_plan_ieee29119.es.md](docs/test_plan_ieee29119.es.md) | [docs/test_plan_ieee29119.en.md](docs/test_plan_ieee29119.en.md) |
| Guía de Perfiles de Juegos | [docs/game_profiles_guide.es.md](docs/game_profiles_guide.es.md) | [docs/game_profiles_guide.en.md](docs/game_profiles_guide.en.md) |
| Política de Seguridad | [SECURITY.es.md](SECURITY.es.md) | [SECURITY.en.md](SECURITY.en.md) |

---

## Estructura del Repositorio

```text
.
├── .cursor/rules/                  # Reglas automáticas para Cursor AI
├── .github/
│   ├── ISSUE_TEMPLATE/             # Plantillas bilingües de Bug Report y Feature Request
│   ├── PULL_REQUEST_TEMPLATE.md    # Plantilla obligatoria de PR (tipo, checks, SemVer, seguridad)
│   ├── copilot-instructions.md     # Instrucciones automáticas para GitHub Copilot
│   ├── dependabot.yml              # Actualizaciones mensuales de GitHub Actions
│   └── workflows/
│       ├── ci.yml                  # CI .NET 9 (ubuntu-latest core + windows-latest build/tests/smoke)
│       └── release.yml             # Release automática Windows x64 (tags v*.*.* o manual + SHA256SUMS)
├── AGENTS.md / CLAUDE.md / GEMINI.md # Contexto inmediato multi-IA (+ AGENTS.es.md / AGENTS.en.md)
├── .gitignore                      # Protege .env, config/*.json, custom_rules.md, logs/, bin/obj y dist/
├── HUDMind.bat                     # Lanzador Windows de la app nativa
├── native/
│   ├── HUDMind.sln                 # Solución .NET 9
│   ├── HUDMind.Core/               # Modelos, Config/SSRF/Redaction, Embedded Profiles, Updates, Wire Protocol, Motores
│   ├── HUDMind.Windows/            # GDI Capture, WinMM Audio/Devices, WinRT Natural TTS + OneCore, GSI, OCR, KnownFolders
│   ├── HUDMind.App/                # UI WPF (0% GPU), PerMonitorV2 Overlay, Tray Icon Win32 y Banner de Updates
│   ├── HUDMind.Packer/             # Compilador build-time del vault AES-256-GCM (Perfiles + ProtectedAssets -> Vault.bin)
│   ├── HUDMind.Tests/              # Suite de pruebas Core multiplataforma (50 casos)
│   └── HUDMind.Windows.Tests/      # Suite de pruebas Win32, WASAPI, GSI, TLS, Logs, Audio y KnownFolders (15 casos)
├── profiles/                       # Perfiles de juegos y prompts tácticos
├── docs/                           # Documentación técnica bilingüe (*.es.md y *.en.md)
└── archive/legacy_python/          # Código Python legado archivado tras la migración a .NET 9
```
