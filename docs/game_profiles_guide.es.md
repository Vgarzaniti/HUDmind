> 🌐 **Idioma / Language:** **[🇪🇸 Español](game_profiles_guide.es.md)** | [🇺🇸 English](game_profiles_guide.en.md)

# Guía de Perfiles de Juego (`profiles/`)

**HUDMind Native** utiliza una arquitectura modular de "cartuchos" por juego en `profiles/`. Para agregar soporte para un nuevo título **no es necesario modificar código C#**: podés crear el perfil directamente desde la interfaz o creando una carpeta estándar.

Perfiles incluidos de serie (embebidos dentro del `.exe`):
- `aoe2_de` (Age of Empires II: Definitive Edition)
- `cs2` (Counter-Strike 2 — con soporte oficial Valve GSI `127.0.0.1:47315`)
- `dota2` (Dota 2 — con soporte oficial Valve GSI `127.0.0.1:47315`)
- `league_of_legends` (League of Legends — con soporte Riot Live Client API `127.0.0.1:2999`)
- `rob_run` (ROB RUN)
- `rocket_league` (Rocket League — con sensor pasivo `Launch.log`)
- `valorant` (VALORANT — con sensor pasivo `ShooterGame.log`)
- `universal` (fallback dinámico en memoria cuando el juego no tiene carpeta propia)

---

## 1. Estructura de un Perfil

```text
profiles/
└── mi_nuevo_juego/
    ├── manifest.json         # Metadatos técnicos y detección del ejecutable (.exe)
    ├── system_prompt.md      # Personalidad base y reglas tácticas resumidas
    ├── knowledge_base.md     # (Opcional) Glosario, anclajes de HUD y pantallas a ignorar
    └── custom_rules.md       # (Opcional, gestionado en GUI) Reglas personales hasta 600 chars
```

> **Tip Rápido desde la GUI:** En la pestaña **`Juegos y Memoria`** completá **`+ Nuevo Juego`** (`Nombre` y `Archivo (.exe)` — el ID se deriva de forma segura) y hacé clic en **`➕ Agregar`**. El sistema valida el ID (sin rutas `..` ni `universal`), normaliza automáticamente `.exe`, escribe `manifest.json` + `system_prompt.md` y lo deja listo para editar sus reglas personales (`custom_rules.md`).

---

## 2. Esquema de `manifest.json`

Ejemplo real basado en [`profiles/cs2/manifest.json`](../profiles/cs2/manifest.json):

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

### Referencia de Campos de `manifest.json` (`GameManifest`)

| Campo | Tipo | Descripción |
| :--- | :--- | :--- |
| `game_id` | `string` | Identificador único en minúsculas (`^[a-z0-9_\-]{1,64}$`, distinto de `universal`). |
| `display_name` | `string` | Nombre visible en overlay, selector de juegos y resúmenes post-partida. |
| `process_names` | `string[]` | Lista de ejecutables `.exe` (insensible a mayúsculas) que activan este perfil al tomar foco. |
| `capture_fps` | `double` | Frecuencia objetivo de captura en Hz (`1.0` a `2.0` típico). |
| `phash_threshold` | `int` | Distancia de Hamming perceptual (`0`–`64`) para omitir frames estáticos. |
| `cooldown_seconds` | `double` | Enfriamiento base entre consejos (en modo `training` se reduce automáticamente a 65%, mínimo `2.8s`). |
| `max_resolution` | `[int, int]` | Metadato reservado del perfil; el escalado real en runtime sigue a `TokenEfficiencyMode` (`eco`: `960×540`, `balanced`: `1280×720`, `high`: `1440×810`). |
| `language` | `string` | Idioma base del perfil (`"es"` o `"en"`). |
| `notes` | `string` | Resumen táctico corto usado como fallback compacto si `system_prompt.md` está vacío. |

---

## 3. Buenas Prácticas de Redacción

1. **`system_prompt.md`:** Mantener instrucciones concretas, orientadas a consejos cortos (`7–12` palabras), evitando alucinaciones fuera de lo visible en HUD o sensores.
2. **`custom_rules.md` (GUI — máximo `600` caracteres):** Ideal para recordatorios personales en tiempo real (ej.: *"Priorizar backpost en defensa"*, *"Avisarme solo si bajé de 45% vida o si hay Spike plantada"*). Cada cambio en GUI se inyecta en vivo al coach.
3. **Resúmenes post-partida (`logs/reviews/`):** Al terminar o detener una partida, el botón **`📊 Resumen Partida`** genera un análisis con tus errores repetidos y 1 drill práctico, y lo guarda automáticamente en `logs/reviews/yyyyMMdd_HHmmss_<juego>.md` accesible desde **`📂 Resúmenes`**.
