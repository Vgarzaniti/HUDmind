> 🌐 **Idioma / Language:** [🇪🇸 Guía IA (`AGENTS.es.md`)](../AGENTS.es.md) | [🇺🇸 AI Guide (`AGENTS.en.md`)](../AGENTS.en.md)

# GitHub Copilot Repository Instructions — HUDMind

Follow [AGENTS.md](../AGENTS.md) (full details in [AGENTS.en.md](../AGENTS.en.md) / [AGENTS.es.md](../AGENTS.es.md)):

- **Architecture:** `.NET 9` split across `HUDMind.Core` (`net9.0` cross-platform), `HUDMind.Windows` (`net9.0-windows10.0.19041.0`), and `HUDMind.App` (WPF `RenderMode.SoftwareOnly`, 0% GPU).
- **Zero External NuGet Packages:** Do not introduce external NuGet dependencies or Python runtime dependencies.
- **Anti-Cheat & Security:** Capture/telemetry must remain 100% passive (external GDI + read-only official APIs/logs, no DLL injection, no game memory reads, no `WH_KEYBOARD_LL`/`WH_MOUSE_LL`). Preserve `BaseUrlSecurityValidator`, `SecretRedactor`, `ConfigRepository` path/symlink checks, and `GitHubReleaseUpdateService` redirect/SHA-256 verification.
- **Verification Required for Every PR:**
  1. `dotnet build native/HUDMind.sln -c Release` (0 warnings, 0 errors)
  2. `dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build`
  3. `dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build`
  4. `HUDMind.Native.exe --smoke-startup` exits with code `0`
- **PR & Docs Standard:** `main` is protected; target PRs via [.github/PULL_REQUEST_TEMPLATE.md](PULL_REQUEST_TEMPLATE.md) following [docs/pull_request_guide.en.md](../docs/pull_request_guide.en.md) / [docs/pull_request_guide.es.md](../docs/pull_request_guide.es.md). Keep documentation pairs in both `.es.md` and `.en.md` with cross-links.
