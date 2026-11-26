> 🌐 **Idioma / Language:** **[🇪🇸 Español](versioning_and_releases.es.md)** | [🇺🇸 English](versioning_and_releases.en.md)

# Nomenclatura de Versiones y Política de Releases (`vN.N.N`)

**HUDMind Native** adopta **Versionado Semántico ([SemVer 2.0.0](https://semver.org/))** con tags en formato **`vMAJOR.MINOR.PATCH`** (por ejemplo `v1.1.0`). Cada dígito comunica de forma explícita el impacto del cambio para usuarios finales, colaboradores y el actualizador integrado en el `.exe`.

---

## 1. Significado de cada dígito (`vX.Y.Z`)

```text
   v 1 . 2 . 3
     │   │   │
     │   │   └── PATCH: correcciones de bugs, parches de seguridad o ajustes internos (sin romper nada)
     │   └────── MINOR: nuevas funciones retrocompatibles (resetea PATCH a 0)
     └────────── MAJOR: cambios incompatibles o salto de arquitectura (resetea MINOR y PATCH a 0)
```

| Dígito | Nombre | Cuándo incrementarlo en HUDMind | Ejemplos concretos |
| :--- | :--- | :--- | :--- |
| **`X`** (`X.0.0`) | **MAJOR** | Cambios **incompatibles hacia atrás** o rediseños estructurales mayores. | - Migración de plataforma/stack (ej: Python → `.NET 9` en `1.0.0`).<br>- Cambio incompatible en el esquema de `user_config.json`, `accounts.json` o `manifest.json` que requiera reconfigurar.<br>- Eliminación de un motor principal o aumento del SO mínimo requerido. |
| **`Y`** (`1.Y.0`) | **MINOR** | **Nuevas funcionalidades** que mantienen compatibilidad total con configs y perfiles previos. | - Nuevo proveedor IA o backend Live WebSocket.<br>- Nuevo perfil de juego oficial o nuevo sensor pasivo (API/log).<br>- Nueva pestaña, control visual o flujo mayor (ej: verificador de actualizaciones in-app en `1.1.0`).<br>- Nuevo modo de comunicación o capacidad de coaching/audio. |
| **`Z`** (`1.1.Z`) | **PATCH** | **Correcciones de errores**, hardening de **seguridad**, mejoras de estabilidad o ajustes menores sin nuevas features grandes. | - Fix de captura GDI, dispositivos WinMM, hotkeys o DPI.<br>- Parches de seguridad (SSRF, redacción de secretos, validación `SHA-256`).<br>- Ajustes en el banner de updates, chequeo horario en reposo o correcciones en workflows/docs que acompañen un fix. |

### Reglas de reinicio y monotonicidad
- Al subir **`MAJOR`**, `MINOR` y `PATCH` vuelven a `0` (ej. `1.4.2` → `2.0.0`).
- Al subir **`MINOR`**, `PATCH` vuelve a `0` (ej. `1.1.3` → `1.2.0`).
- Nunca se reutiliza ni se sobrescribe un tag ya publicado (`v1.1.0` es inmutable una vez publicada la Release).

---

## 2. Pre-releases (`-alpha.N`, `-beta.N`, `-rc.N`)

Para probar cambios riesgosos antes de liberarlos a todos los usuarios se usa sufijo con guion:

- `v1.2.0-alpha.1` → prueba interna temprana (inestable).
- `v1.2.0-beta.1` → prueba abierta con funciones nuevas sujetas a ajuste.
- `v1.2.0-rc.1` → *Release Candidate* lista para validación final antes de `v1.2.0`.

**Comportamiento automático:**
1. El workflow [`.github/workflows/release.yml`](../.github/workflows/release.yml) detecta el `-` en el tag y marca la release en GitHub como **Pre-release** automáticamente.
2. El verificador integrado ([`GitHubReleaseUpdateService`](../native/HUDMind.Core/Updates/GitHubReleaseUpdateService.cs)) consulta `/releases/latest`, por lo que **ignora drafts y pre-releases**: a quienes usan una versión estable **no** les salta el cartel con betas.
3. Si alguien está probando `1.2.0-beta.1` y luego se publica la estable `v1.2.0`, el actualizador sí detecta `1.2.0 > 1.2.0-beta.1` y ofrece pasar a la versión estable.

---

## 3. Ejemplos prácticos de decisión

| Cambio realizado | Versión actual | Próxima versión | Motivo |
| :--- | :--- | :--- | :--- |
| Fix en micrófono WinMM + chequeo horario en reposo | `v1.1.0` | **`v1.1.1`** | Solo corrige/mejora algo existente sin romper ni sumar gran feature. |
| Agregar perfil oficial de Apex Legends + sensor nuevo + modo nuevo en UI | `v1.1.1` | **`v1.2.0`** | Suma funcionalidades nuevas manteniendo configs anteriores. |
| Probar refactor grande de overlay antes de darlo por estable | `v1.2.0` | **`v1.3.0-beta.1`** | Pre-release de prueba; no dispara aviso a usuarios estables. |
| Pasar a `.NET 10` cambiando formato de `accounts.json` sin retrocompatibilidad | `v1.3.0` | **`v2.0.0`** | Cambio incompatible (*breaking change*). |

---

## 4. Proceso oficial para publicar una Release (con `main` protegida)

1. **Trabajar en una rama y abrir PR hacia `main`** (ver [Guía de Pull Requests](pull_request_guide.es.md)):
   - Actualizar `<Version>` en [`native/HUDMind.App/HUDMind.App.csproj`](../native/HUDMind.App/HUDMind.App.csproj) y `<assemblyIdentity version="...">` en [`native/HUDMind.App/app.manifest`](../native/HUDMind.App/app.manifest) acorde al próximo número (ej. `1.1.1` / `1.1.1.0`).
   - Verificar que el PR pase los dos checks requeridos del CI: `native-dotnet9-tests` y `native-windows-build`.
   - Mergear el PR en `main`.
2. **Etiquetar y disparar la Release:**
   ```powershell
   git checkout main
   git pull origin main
   git tag v1.1.1
   git push origin v1.1.1
   ```
   *(Alternativamente, desde GitHub → **Actions → Release — HUDMind Native (Windows x64) → Run workflow** ingresando `v1.1.1`).*
3. **Artefactos generados automáticamente por CI:**
   - `HUDMind-Setup-win-x64.exe` (instalador para Windows con ícono en Menú Inicio, sin permisos de Admin ni .NET 9)
   - `HUDMind.Native-Standalone-win-x64.exe` (portable self-contained, sin prerrequisitos)
   - `HUDMind.Native-win-x64.exe` (framework-dependent para PCs con .NET 9 Desktop Runtime)
   - `SHA256SUMS.txt` (firmas de integridad verificadas automáticamente al descargar desde la app)
