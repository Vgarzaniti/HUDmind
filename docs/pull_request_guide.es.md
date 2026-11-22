> 🌐 **Idioma / Language:** **[🇪🇸 Español](pull_request_guide.es.md)** | [🇺🇸 English](pull_request_guide.en.md)

# Guía Completa para Crear un Pull Request (PR) en HUDMind

La rama `main` está **protegida**: todo cambio debe entrar mediante un **Pull Request** aprobado y con todos los checks de CI en verde. Esta guía detalla la nomenclatura de ramas, los tipos de PR, qué información obligatoria debe incluir la descripción y los criterios de aceptación antes de mergear.

---

## 1. Flujo de Trabajo Paso a Paso

1. **Sincronizá `main` local antes de empezar:**
   ```powershell
   git checkout main
   git pull origin main
   ```
2. **Creá una rama descriptiva** usando el prefijo del tipo de cambio:
   ```powershell
   git checkout -b fix/winmm-mic-reconnect
   ```
   Convención recomendada de ramas:
   - `feat/<tema>` · `fix/<tema>` · `bug/<tema>` · `refactor/<tema>` · `perf/<tema>` · `security/<tema>` · `docs/<tema>` · `test/<tema>` · `ci/<tema>` · `build/<tema>` · `chore/<tema>`
3. **Hacé commits claros y atómicos** siguiendo Conventional Commits:
   `<tipo>(<alcance>): <resumen breve en imperativo>`
   *(Ejemplos: `fix(audio): handle unplugged WinMM mic gracefully`, `feat(sensors): add Apex Legends log sensor`, `docs(pr): add bilingual PR guide`).*
4. **Corré toda la batería de pruebas local** antes de subir la rama (ver sección 4).
5. **Subí tu rama y abrí el PR contra `main`:**
   ```powershell
   git push -u origin fix/winmm-mic-reconnect
   ```
   Completá la plantilla automática [`.github/PULL_REQUEST_TEMPLATE.md`](../.github/PULL_REQUEST_TEMPLATE.md).
6. **Esperá los checks requeridos de CI:**
   - `native-dotnet9-tests` (`ubuntu-latest`)
   - `native-windows-build` (`windows-latest` — compilación Release + Core tests + Windows tests + `--smoke-startup`)
7. **Merge y limpieza:** tras aprobar y pasar el CI, mergeá a `main` (*Squash and merge* recomendado para historial limpio) y eliminá la rama remota.

---

## 2. Clasificación Obligatoria: Tipos de PR

Todo PR debe declarar explícitamente su **tipo** tanto en el título como en la plantilla. Si un PR combina cosas distintas, dividilo en PRs más chicos.

| Tipo | Etiqueta / Prefijo | Cuándo usarlo | Impacto en `vX.Y.Z` | Ejemplo de título |
| :--- | :--- | :--- | :--- | :--- |
| **Feature** | `feat` | Nueva funcionalidad visible para el jugador o nueva capacidad técnica. | **`MINOR`** (`+0.1.0`) | `feat(sensors): add Valorant spike timer parser` |
| **Fix / Bug** | `fix` / `bug` | Corrección de un fallo reproducible, crash, regresión o comportamiento erróneo. | **`PATCH`** (`+0.0.1`) | `fix(capture): avoid GDI handle leak on monitor switch` |
| **Security** | `security` | Parche ante vulnerabilidad o endurecimiento SSRF, TLS, CWE-22, redacción o integridad. | **`PATCH`** *(o `MAJOR` si rompe algo)* | `security(updates): reject non-GitHub redirect hosts` |
| **Refactor** | `refactor` | Reestructuración interna de código sin cambiar comportamiento ni sumar features. | Sin bump / **`PATCH`** | `refactor(ai): unify OpenAI header builder` |
| **Performance** | `perf` | Mejora medible de latencia, CPU, RAM o ancho de banda. | **`PATCH`** / **`MINOR`** | `perf(capture): reuse JPEG encode buffer` |
| **Documentation** | `docs` | Cambios solo en documentación (`.md` en `.es.md` y `.en.md`, diagramas, comentarios). | Sin bump | `docs(versioning): document SemVer vN.N.N rules` |
| **Tests** | `test` | Nuevos tests o mejoras en `HUDMind.Tests` / `HUDMind.Windows.Tests`. | Sin bump | `test(updates): cover SHA-256 mismatch cleanup` |
| **Build** | `build` | Cambios en `.csproj`, propiedades de compilación, `publish` o manifiesto. | **`PATCH`** *(si afecta binario)* | `build(app): embed IsSelfContained metadata` |
| **CI / CD** | `ci` | Cambios en workflows de `.github/workflows/*.yml` o runners. | Sin bump | `ci(release): sign checksums with SHA256SUMS.txt` |
| **Chore** | `chore` | Mantenimiento rutinario: `.gitignore`, Dependabot, plantillas, limpieza. | Sin bump | `chore(repo): add bilingual PR & issue templates` |
| **Revert** | `revert` | Revierte un PR o commit previo que introdujo un problema. | **`PATCH`** *(si ya salió release)* | `revert: undo "feat(overlay): ..."` |
| **Breaking Change** | `!` o `BREAKING CHANGE` | Cualquier tipo que rompa compatibilidad de configs, perfiles o APIs públicas. | **`MAJOR`** (`+1.0.0`) | `feat(config)!: migrate accounts schema to v2` |

> **Diferencia práctica entre `fix` y `bug`:** usá **`fix`** en el título/commit (estándar Conventional Commits) y podés marcar la etiqueta/casilla **`fix` / `bug`** en la plantilla del PR si el PR corrige un issue reportado con `[BUG]`.

---

## 3. Qué debe contener obligatoriamente la descripción de un PR

Un PR incompleto o sin evidencia puede ser rechazado hasta completarse. Debe incluir:

1. **Título descriptivo:** `<tipo>(<alcance>): <qué cambia>` (ej: `fix(ui): keep update banner hidden after dismiss`).
2. **Tipo de cambio marcado:** exactamente cuál de la tabla anterior aplica (`feat`, `fix`/`bug`, `security`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`, `revert`) y si introduce **Breaking Change**.
3. **Contexto y Resumen ("Qué" y "Por qué"):**
   - Qué problema existía o qué necesidad resuelve.
   - Cómo se implementó y qué archivos/capas toca (`HUDMind.Core`, `HUDMind.Windows`, `HUDMind.App`, `profiles/`, `docs/`, `.github/`).
4. **Referencia a Issue / Discusión (si existe):** `Fixes #12`, `Closes #15` o `N/A`.
5. **Impacto en Versionado SemVer ([ver guía](versioning_and_releases.es.md)):** indicar si corresponde `Ninguno`, `PATCH`, `MINOR` o `MAJOR` (y si actualiza `<Version>` en `HUDMind.App.csproj` y `app.manifest`).
6. **Evidencia de Pruebas y Verificación:**
   - Resultado exacto de compilación, tests Core, tests Windows y `--smoke-startup`.
   - Capturas de pantalla o GIF si modifica UI WPF / `OverlayWindow` (sin mostrar API keys en pantalla).
7. **Checklist de Seguridad, Anti-Cheat y Documentación:**
   - Confirmación explícita de cero inyección, cero hooks globales de teclado/mouse y cero NuGet externos.
   - Confirmación de que **no** se subieron `.env`, `config/accounts.json`, `config/user_config.json`, `config/telemetry.json`, `custom_rules.md` ni `logs/`.
   - Si el cambio afecta arquitectura, requisitos, perfiles o uso, confirmar que se actualizaron **ambas versiones de la documentación (`.es.md` y `.en.md`)**.

---

## 4. Verificación Local Obligatoria Antes de Enviar el PR

```powershell
# 1. Compilar solución completa (debe terminar con 0 warnings y 0 errores)
dotnet build native/HUDMind.sln -c Release

# 2. Suite multiplataforma Core
dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release --no-build

# 3. Suite nativa Windows (Win32, GSI, TLS, Logs, Audio, KnownFolders)
dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release --no-build

# 4. Smoke test real de la ventana WPF
.\native\HUDMind.App\bin\Release\net9.0-windows10.0.19041.0\HUDMind.Native.exe --smoke-startup

# 5. Revisar que no haya archivos privados ni binarios en git status
git status
```

> Si agregaste lógica nueva en `Core` o `Windows`, agregá o actualizá su caso correspondiente en `HUDMind.Tests` / `HUDMind.Windows.Tests` y en [`test_plan_ieee29119.es.md`](test_plan_ieee29119.es.md) + [`test_plan_ieee29119.en.md`](test_plan_ieee29119.en.md).
