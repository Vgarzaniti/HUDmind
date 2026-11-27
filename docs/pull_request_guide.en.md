> 🌐 **Idioma / Language:** [🇪🇸 Español](pull_request_guide.es.md) | **[🇺🇸 English](pull_request_guide.en.md)**

# Complete Guide to Creating a Pull Request (PR) in HUDMind

The `main` branch is **protected**: every change must go through a reviewed **Pull Request** with all CI checks passing. This guide covers branch naming, PR types, mandatory PR contents, and acceptance criteria before merging.

---

## 1. Step-by-Step Workflow

1. **Sync local `main` before starting:**
   ```powershell
   git checkout main
   git pull origin main
   ```
2. **Create a descriptive branch** prefixed with the change type:
   ```powershell
   git checkout -b fix/winmm-mic-reconnect
   ```
   Recommended branch prefixes:
   - `feat/<topic>` · `fix/<topic>` · `bug/<topic>` · `refactor/<topic>` · `perf/<topic>` · `security/<topic>` · `docs/<topic>` · `test/<topic>` · `ci/<topic>` · `build/<topic>` · `chore/<topic>`
3. **Write clear, atomic commits** following Conventional Commits:
   `<type>(<scope>): <short imperative summary>`
   *(Examples: `fix(audio): handle unplugged WinMM mic gracefully`, `feat(sensors): add Apex Legends log sensor`, `docs(pr): add bilingual PR guide`).*
4. **Run the full local test suite** before pushing (see Section 4).
5. **Push your branch and open the PR targeting `main`:**
   ```powershell
   git push -u origin fix/winmm-mic-reconnect
   ```
   Fill out every applicable section of [`.github/PULL_REQUEST_TEMPLATE.md`](../.github/PULL_REQUEST_TEMPLATE.md).
6. **Wait for all required CI checks:**
   - `native-dotnet9-tests` (`ubuntu-latest`)
   - `native-windows-build` (`windows-latest` — Release build + Core tests + Windows tests + `--smoke-startup`)
7. **Merge and clean up:** once approved and green, merge into `main` (*Squash and merge* recommended for clean history) and delete the feature branch.

---

## 2. Mandatory Classification: PR Types

Every PR must explicitly declare its **type** in both the title and the PR template. If a PR mixes unrelated work, split it into smaller PRs.

| Type | Tag / Prefix | When to use it | `vX.Y.Z` Impact | Title example |
| :--- | :--- | :--- | :--- | :--- |
| **Feature** | `feat` | New user-facing functionality or new technical capability. | **`MINOR`** (`+0.1.0`) | `feat(sensors): add Valorant spike timer parser` |
| **Fix / Bug** | `fix` / `bug` | Fixes a reproducible defect, crash, regression, or wrong behavior. | **`PATCH`** (`+0.0.1`) | `fix(capture): avoid GDI handle leak on monitor switch` |
| **Security** | `security` | Vulnerability fix or hardening for SSRF, TLS, CWE-22, secret redaction, or integrity. | **`PATCH`** *(or `MAJOR` if breaking)* | `security(updates): reject non-GitHub redirect hosts` |
| **Refactor** | `refactor` | Internal code restructuring with zero external behavior change or new features. | None / **`PATCH`** | `refactor(ai): unify OpenAI header builder` |
| **Performance** | `perf` | Measurable improvement to latency, CPU, RAM, or network overhead. | **`PATCH`** / **`MINOR`** | `perf(capture): reuse JPEG encode buffer` |
| **Documentation** | `docs` | Documentation-only updates (`.es.md` & `.en.md` files, diagrams, comments). | None | `docs(versioning): document SemVer vN.N.N rules` |
| **Tests** | `test` | New tests or improvements in `HUDMind.Tests` / `HUDMind.Windows.Tests`. | None | `test(updates): cover SHA-256 mismatch cleanup` |
| **Build** | `build` | Changes to `.csproj`, MSBuild properties, publish flags, or app manifest. | **`PATCH`** *(if binary changes)* | `build(app): embed IsSelfContained metadata` |
| **CI / CD** | `ci` | Changes to `.github/workflows/*.yml` or CI runner configuration. | None | `ci(release): sign checksums with SHA256SUMS.txt` |
| **Chore** | `chore` | Routine maintenance: `.gitignore`, Dependabot, templates, cleanup. | None | `chore(repo): add bilingual PR & issue templates` |
| **Revert** | `revert` | Cleanly reverts a previous PR or commit that caused an issue. | **`PATCH`** *(if already released)* | `revert: undo "feat(overlay): ..."` |
| **Breaking Change** | `!` or `BREAKING CHANGE` | Any type breaking backwards compatibility of configs, profiles, or public APIs. | **`MAJOR`** (`+1.0.0`) | `feat(config)!: migrate accounts schema to v2` |

> **Practical note on `fix` vs `bug`:** use **`fix`** in the commit/PR title (Conventional Commits standard) and check the **`fix` / `bug`** type in the PR template when resolving a `[BUG]` issue.

---

## 3. Mandatory Contents of Every PR Description

An incomplete PR or one without test evidence may be held until updated. Every PR must include:

1. **Descriptive Title:** `<type>(<scope>): <what changes>` (e.g., `fix(ui): keep update banner hidden after dismiss`).
2. **Selected Change Type:** exact classification from the table above (`feat`, `fix`/`bug`, `security`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`, `revert`) plus explicit **Breaking Change** flag.
3. **Context & Technical Summary ("What" and "Why"):**
   - What problem existed or what need is addressed.
   - How it was implemented and which layers are affected (`HUDMind.Core`, `HUDMind.Windows`, `HUDMind.App`, `profiles/`, `docs/`, `.github/`).
4. **Linked Issue / Reference (if any):** `Fixes #12`, `Closes #15`, or `N/A`.
5. **SemVer Impact ([see policy](versioning_and_releases.en.md)):** state whether the change warrants `None`, `PATCH`, `MINOR`, or `MAJOR` (and whether `<Version>` in `HUDMind.App.csproj` / `app.manifest` was bumped).
6. **Test & Verification Evidence:**
   - Exact output/counts of Release build, Core tests, Windows tests, and `--smoke-startup`.
   - Screenshots or logs (with secrets redacted) when modifying WPF UI or `OverlayWindow`.
7. **Security, Anti-Cheat & Documentation Checklist:**
   - Explicit confirmation of zero DLL injection, zero global keyboard/mouse hooks, and zero external NuGet packages.
   - Confirmation that **no** `.env`, `config/accounts.json`, `config/user_config.json`, `config/telemetry.json`, `custom_rules.md`, or `logs/` are included.
   - Confirmation that **both language versions (`.es.md` and `.en.md`)** were updated whenever architecture, requirements, profiles, or user-facing behavior changed.

---

## 4. Mandatory Local Verification Before Opening a PR

```powershell
# 1. Build entire solution (must finish with 0 warnings and 0 errors)
dotnet build native/HUDMind.sln -c Release

# 2. Cross-platform Core test suite
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Native Windows test suite (Win32, GSI, TLS, Logs, Audio, KnownFolders)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Live WPF UI Automation E2E test suite (7 real scenarios on isolated HUDMind.Native.exe)
dotnet run --project native/HUDMind.UiTests/HUDMind.UiTests.csproj -c Release --no-build

# 5. Real WPF startup smoke test
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 6. Ensure no private config files or build binaries are staged
git status
```

> If you added new logic in `Core` or `Windows`, also add/update matching test cases in `HUDMind.Tests` / `HUDMind.Windows.Tests` and update both [`test_plan_ieee29119.es.md`](test_plan_ieee29119.es.md) and [`test_plan_ieee29119.en.md`](test_plan_ieee29119.en.md).
