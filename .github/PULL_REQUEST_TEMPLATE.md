> 📖 **Guía de PR / PR Guide:** [🇪🇸 Español](../docs/pull_request_guide.es.md) | [🇺🇸 English](../docs/pull_request_guide.en.md) · **SemVer (`vN.N.N`):** [🇪🇸 ES](../docs/versioning_and_releases.es.md) | [🇺🇸 EN](../docs/versioning_and_releases.en.md)

## 1. Tipo de Cambio / PR Type *(marcá al menos uno / check all that apply)*
- [ ] `feat` — Nueva funcionalidad / New feature *(MINOR)*
- [ ] `fix` / `bug` — Corrección de bug o fallo / Bug fix *(PATCH)*
- [ ] `security` — Parche de seguridad o hardening (SSRF, TLS, CWE-22, redacción, SHA-256)
- [ ] `refactor` — Reestructuración interna sin cambiar comportamiento externo / Internal refactor
- [ ] `perf` — Optimización de latencia, CPU, RAM o red / Performance improvement
- [ ] `docs` — Solo documentación (`.es.md` y `.en.md`) / Documentation only
- [ ] `test` — Pruebas nuevas o actualizadas / Tests (`HUDMind.Tests` / `HUDMind.Windows.Tests`)
- [ ] `build` — Cambios en `.csproj`, publish o `app.manifest` / Build configuration
- [ ] `ci` — Workflows de GitHub Actions / CI & Release pipelines
- [ ] `chore` — Mantenimiento general / Dependabot / plantillas
- [ ] `revert` — Reversión de un cambio anterior / Revert
- [ ] `BREAKING CHANGE` (`!`) — Cambio incompatible hacia atrás *(MAJOR)*

---

## 2. Resumen Técnico ("Qué" y "Por qué") / Technical Summary ("What" & "Why")
<!-- Describí el problema que existía, la solución implementada y cualquier decisión clave. -->
<!-- Describe the problem, your solution, and any key design trade-offs. -->

- **Qué cambia / What changed:**
- **Por qué / Why:**
- **Issue relacionado / Linked issue:** *(ej. `Fixes #12` o `N/A`)*

### Capas afectadas / Affected Layers
- [ ] `HUDMind.Core` (`net9.0`)
- [ ] `HUDMind.Windows` (`net9.0-windows10.0.19041.0`)
- [ ] `HUDMind.App` (WPF / XAML)
- [ ] `profiles/` (Perfiles de juego / Game profiles)
- [ ] `docs/` / `README` (Documentación `.es.md` y `.en.md`)
- [ ] `.github/` (CI / Release / Plantillas)

---

## 3. Impacto SemVer (`vMAJOR.MINOR.PATCH`) / Versioning Impact
- [ ] Ninguno / None *(no requiere release nueva por sí solo)*
- [ ] `PATCH` (`+0.0.1`) — Fix / seguridad / ajuste menor
- [ ] `MINOR` (`+0.1.0`) — Nueva funcionalidad retrocompatible
- [ ] `MAJOR` (`+1.0.0`) — Cambio incompatible (`BREAKING CHANGE`)
- [ ] Versión actualizada en `HUDMind.App.csproj` y `app.manifest` *(si aplica)*: `v___`

---

## 4. Evidencia de Pruebas / Test Evidence
<!-- Pegá resultados reales después de tu último cambio / Paste observed test results after final edit -->
- [ ] `dotnet build native/HUDMind.sln -c Release` → `0 warnings, 0 errors`
- [ ] `dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build` → `___ passed, 0 failed`
- [ ] `dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build` → `___ passed, 0 failed`
- [ ] `dotnet run --project native/HUDMind.UiTests/HUDMind.UiTests.csproj -c Release --no-build` → `___ passed, 0 failed`
- [ ] `HUDMind.Native.exe --smoke-startup` → `exit code 0`
- [ ] Capturas / logs adjuntos si toca UI WPF u Overlay *(sin exponer API keys)*

---

## 5. Checklist de Seguridad, Anti-Cheat y Documentación / Security & Quality Checklist
- [ ] **100% Pasivo / Anti-Cheat Safe:** sin inyección DLL, sin lectura/escritura de memoria de juegos y sin hooks `WH_KEYBOARD_LL` / `WH_MOUSE_LL`.
- [ ] **0% GPU & Zero-Bloat:** sin paquetes NuGet externos ni shaders GPU en WPF.
- [ ] **Sin secretos en el diff:** verificaste que no hay `.env`, `config/accounts.json`, `config/user_config.json`, `config/telemetry.json`, `custom_rules.md` ni `logs/`.
- [ ] **Seguridad:** cambios en red/archivos/logs respetan `BaseUrlSecurityValidator`, CWE-22 y `SecretRedactor`.
- [ ] **Documentación bilingüe:** si cambia comportamiento visible, arquitectura o requisitos, actualizaste tanto `.es.md` como `.en.md`.
