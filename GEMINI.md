> 🌐 **Idioma / Language:** [🇪🇸 Guía IA (`AGENTS.es.md`)](AGENTS.es.md) | [🇺🇸 AI Guide (`AGENTS.en.md`)](AGENTS.en.md)

# GEMINI.md

Before working on **HUDMind**, read [AGENTS.md](AGENTS.md) and the full guide in [AGENTS.es.md](AGENTS.es.md) / [AGENTS.en.md](AGENTS.en.md).

Key non-negotiable constraints:
- **Stack:** `.NET 9 + WPF + Win32` (`native/HUDMind.sln`), **0% GPU** (`RenderMode.SoftwareOnly`), **zero external NuGet packages**, **100% passive anti-cheat safe** (no injection, no memory reads, no low-level hooks).
- **Cross-platform Core:** Keep `HUDMind.Core` strictly `net9.0`; keep `archive/legacy_python/` untouched and `profiles/*/*.md` filenames unchanged.
- **Documentation & PRs:** `main` is protected. Work in a typed branch (`feat/`, `fix/`, `refactor/`, etc.), run all Core (`61`) + Windows (`20`) + `--smoke-startup` checks, update both `.es.md` and `.en.md` docs if applicable, and use [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md).
