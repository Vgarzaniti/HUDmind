> 🌐 **Idioma / Language:** [🇪🇸 Español](versioning_and_releases.es.md) | **[🇺🇸 English](versioning_and_releases.en.md)**

# Versioning Nomenclature and Release Policy (`vN.N.N`)

**HUDMind Native** follows **Semantic Versioning ([SemVer 2.0.0](https://semver.org/))** using tags formatted as **`vMAJOR.MINOR.PATCH`** (for example, `v1.1.0`). Each digit explicitly communicates change impact to end users, contributors, and the built-in `.exe` update checker.

---

## 1. Meaning of Each Digit (`vX.Y.Z`)

```text
   v 1 . 2 . 3
     │   │   │
     │   │   └── PATCH: bug fixes, security patches, or minor internal improvements (backwards compatible)
     │   └────── MINOR: backwards-compatible new features (resets PATCH to 0)
     └────────── MAJOR: breaking changes or architectural jumps (resets MINOR and PATCH to 0)
```

| Digit | Name | When to increment in HUDMind | Concrete examples |
| :--- | :--- | :--- | :--- |
| **`X`** (`X.0.0`) | **MAJOR** | **Backwards-incompatible** changes or major architectural rewrites. | - Platform/stack migration (e.g., Python → `.NET 9` in `1.0.0`).<br>- Breaking schema changes in `user_config.json`, `accounts.json`, or `manifest.json` without automatic migration.<br>- Removing a core engine or raising minimum supported OS. |
| **`Y`** (`1.Y.0`) | **MINOR** | **New features** that remain 100% compatible with existing configs and profiles. | - New AI provider or Live WebSocket backend.<br>- New bundled game profile or passive telemetry sensor (API/log).<br>- New UI tab, visual control, or major workflow (e.g., in-app update banner in `1.1.0`).<br>- New communication mode or coaching/audio capability. |
| **`Z`** (`1.1.Z`) | **PATCH** | **Bug fixes**, **security hardening**, stability improvements, or minor internal refinements without major new features. | - Fixes in GDI capture, WinMM devices, hotkeys, or DPI.<br>- Security patches (SSRF, secret redaction, `SHA-256` verification).<br>- Update-banner polish, hourly idle check, or workflow/docs fixes bundled with an `.exe` patch. |

### Reset and Monotonicity Rules
- Bumping **`MAJOR`** resets both `MINOR` and `PATCH` to `0` (e.g., `1.4.2` → `2.0.0`).
- Bumping **`MINOR`** resets `PATCH` to `0` (e.g., `1.1.3` → `1.2.0`).
- Never reuse or overwrite an already published tag (`v1.1.0` is immutable once released).

---

## 2. Pre-releases (`-alpha.N`, `-beta.N`, `-rc.N`)

To validate risky changes before pushing them to all users, append a hyphenated pre-release suffix:

- `v1.2.0-alpha.1` → early internal testing (unstable).
- `v1.2.0-beta.1` → public testing with new features subject to change.
- `v1.2.0-rc.1` → *Release Candidate* for final validation right before `v1.2.0`.

**Automatic behavior:**
1. [`.github/workflows/release.yml`](../.github/workflows/release.yml) detects the `-` in the tag and automatically flags the GitHub Release as **Pre-release**.
2. The built-in updater ([`GitHubReleaseUpdateService`](../native/HUDMind.Core/Updates/GitHubReleaseUpdateService.cs)) queries `/releases/latest`, which **ignores drafts and pre-releases**—stable users will never receive beta popups.
3. If someone is running `1.2.0-beta.1` and stable `v1.2.0` ships, the updater recognizes `1.2.0 > 1.2.0-beta.1` and prompts to upgrade to stable.

---

## 3. Practical Decision Examples

| Change made | Current version | Next version | Reason |
| :--- | :--- | :--- | :--- |
| Fix WinMM mic handling + hourly idle update check | `v1.1.0` | **`v1.1.1`** | Fixes/refines existing behavior without breaking changes or big features. |
| Add official Apex Legends profile + new sensor + new UI option | `v1.1.1` | **`v1.2.0`** | Adds backwards-compatible features while keeping existing configs intact. |
| Test major overlay overhaul before calling it stable | `v1.2.0` | **`v1.3.0-beta.1`** | Pre-release for testing; does not notify stable users. |
| Upgrade to `.NET 10` with breaking `accounts.json` schema | `v1.3.0` | **`v2.0.0`** | Breaking change. |

---

## 4. Official Release Process (with Protected `main`)

1. **Work on a branch and open a PR to `main`** (see [Pull Request Guide](pull_request_guide.en.md)):
   - Update `<Version>` in [`native/HUDMind.App/HUDMind.App.csproj`](../native/HUDMind.App/HUDMind.App.csproj) and `<assemblyIdentity version="...">` in [`native/HUDMind.App/app.manifest`](../native/HUDMind.App/app.manifest) to match the upcoming version (e.g., `1.1.1` / `1.1.1.0`).
   - Ensure the PR passes both required CI checks: `native-dotnet9-tests` and `native-windows-build`.
   - Merge the PR into `main`.
2. **Tag and trigger the Release:**
   ```powershell
   git checkout main
   git pull origin main
   git tag v1.1.1
   git push origin v1.1.1
   ```
   *(Alternatively, run **Actions → Release — HUDMind Native (Windows x64) → Run workflow** on GitHub with `v1.1.1`).*
3. **Artifacts automatically produced by CI:**
   - `HUDMind-Setup-win-x64.exe` (Windows per-user installer with Start Menu icon, zero Admin or .NET 9 required)
   - `HUDMind.Native-Standalone-win-x64.exe` (portable self-contained, zero prerequisites)
   - `HUDMind.Native-win-x64.exe` (framework-dependent for PCs with .NET 9 Desktop Runtime)
   - `SHA256SUMS.txt` (integrity checksums automatically verified by the in-app downloader)
