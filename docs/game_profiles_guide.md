# Guía de Perfiles de Juego (`profiles/`)

**HUDMind** utiliza una arquitectura de "cartuchos" desacoplados. Para agregar soporte a un nuevo videojuego **no necesitas modificar código Python**: solo creas una carpeta dentro de `profiles/` con tres archivos estándar.

---

## 1. Estructura de un Perfil

```text
profiles/
└── mi_nuevo_juego/
    ├── manifest.json         # Configuración técnica y detección del proceso .exe
    ├── system_prompt.md      # Personalidad, reglas de brevedad y criterios del coach
    ├── knowledge_base.md     # Conocimiento táctico, anclajes del HUD, modo entrenamiento y lista negra de UI
    └── custom_rules.md       # (Opcional, gestionado desde la GUI) Reglas personales del usuario
```

> **Tip Rápido desde la GUI:** Podés crear el esqueleto completo de un juego nuevo sin tocar archivos haciendo clic en **`+ Nuevo Perfil de Juego`** en la pestaña **`Control`** de `HUDMind.bat`, y agregar tus propias reglas o recordatorios personales desde la pestaña **`Reglas por Juego`** (con medidor semáforo de impacto en tokens).

---

## 2. Paso a Paso para Crear un Nuevo Perfil

### Paso 1: Identificar el ejecutable (`.exe`) del juego
1. Abre el juego en Windows.
2. Abre el **Administrador de Tareas** (`Ctrl + Shift + Esc`) → pestaña **Detalles**.
3. Anota el nombre exacto del ejecutable (por ejemplo: `VALORANT-Win64-Shipping.exe`, `League of Legends.exe`, `cs2.exe`, `eldenring.exe`).

### Paso 2: Configurar `manifest.json`

```json
{
  "game_id": "valorant",
  "display_name": "VALORANT",
  "process_names": ["VALORANT-Win64-Shipping.exe"],
  "capture_fps": 1.0,
  "phash_threshold": 8,
  "cooldown_seconds": 12.0,
  "max_resolution": [1280, 720],
  "language": "es",
  "notes": "Perfil táctico competitivo 5v5"
}
```

#### Referencia de Campos de `manifest.json`

| Campo | Tipo | Descripción y Recomendaciones |
| :--- | :--- | :--- |
| `game_id` | `string` | Identificador único en minúsculas (coincide con el nombre de la carpeta). |
| `display_name` | `string` | Nombre legible mostrado en logs y en el encabezado del overlay. |
| `process_names` | `list[string]` | Lista de `.exe` (insensible a mayúsculas) que activan este perfil automáticamente. |
| `capture_fps` | `float` | Frames capturados por segundo. Juegos tácticos/MOBA: `0.5`–`1.0`. Shooters rápidos: `1.0`–`2.0`. |
| `phash_threshold` | `int` | Distancia de Hamming mínima (`0`–`64`) para considerar que la pantalla cambió. Valores típicos: `6`–`12`. Más alto = menos llamadas a la IA. |
| `cooldown_seconds` | `float` | Segundos mínimos entre intervenciones reales del coach (`should_intervene=true`). Recomendado: `8.0`–`15.0`. |
| `max_resolution` | `[int, int]` | Resolución máxima `[ancho, alto]` antes de enviar a Gemini. `[1280, 720]` mantiene legible el HUD con bajo peso en tokens. |
| `language` | `string` | Idioma de las recomendaciones (`"es"`, `"en"`, etc.). |

### Paso 3: Redactar `system_prompt.md`

Define **quién es el coach, cómo habla y cuándo debe callarse**. Reglas de oro:
- **Silencio por defecto:** Exige explícitamente que devuelva `should_intervene = false` en la mayoría de los frames si no hay una oportunidad táctica clara o un error evitable.
- **Brevedad extrema:** Un jugador en combate no puede leer un párrafo. Limita `overlay_text` a <= 8–10 palabras y `voice_line` a <= 12 palabras (máximo 2–3 segundos de locución).
- **Cero alucinaciones:** Indica que solo comente elementos claramente visibles en el HUD o la escena actual.

### Paso 4: Construir `knowledge_base.md` (Buenas Prácticas)

Un buen `knowledge_base.md` evita que el modelo pierda contexto buscando dónde mirar en la imagen. Estructúralo siempre en **4 bloques**:

1. **Anclajes Visuales del HUD (*HUD Anchors*):**
   - Describe coordenadas aproximadas de la pantalla (esquina superior izquierda, centro inferior, etc.) donde aparecen recursos clave: vida, minimapa, temporizador, economía, habilidades o inventario.
   - *Ejemplo:* *"Esquina inferior izquierda: barra de HP (blanco) y Escudo (azul). Si HP < 35 y el jugador tiene una curación lista en la barra inferior central, priorizar retirada."*

2. **Reglas Tácticas y Heurísticas de Decisión:**
   - Umbrales concretos (economía de rondas, tiempos de respawn de objetivos, ventanas de cooldown, posicionamiento post-plant o *wave management*).
   - Condiciones exactas de urgencia (`high` = peligro inmediato/objetivo crítico, `medium` = ajuste táctico/economía, `low` = recordatorio estratégico).

3. **Jerga del Juego y Glosario:**
   - Términos cortos que el jugador reconoce instantáneamente (`eco`, `save`, `trade`, `rotate`, `baron`, `flank`, `ult`).

4. **Lista Negra de UI (*Qué Ignorar*):**
   - Enumera explícitamente pantallas y estados donde el coach **DEBE** devolver `should_intervene = false`:
     - Menús principales, pantallas de carga, lobbies de matchmaking.
     - Tabla de puntuaciones (`TAB`) tapando la pantalla.
     - Pantalla de muerte / espectador prolongado sin decisiones activas (salvo consejo de economía antes de la siguiente ronda).
     - Chat de texto, notificaciones de Steam/Discord/overlay de FPS.

---

## 3. Nota sobre Context Caching y Tamaño del `knowledge_base.md`

La API de **Context Caching** de Gemini requiere un mínimo de tokens en el contenido cacheado (usualmente entre **1,024 y 4,096 tokens** según el modelo `gemini-2.5-flash` o `gemini-2.5-flash-lite`).

- Si tu `knowledge_base.md` es extenso y detallado (mapas, callouts, tablas de economía, counters de personajes/armas), aprovecharás el caché con **descuento en costo de tokens y menor latencia**.
- Si estás empezando con un borrador corto, **no te preocupes**: el sistema incluye un **fallback automático** que detecta el límite y funciona en modo estándar sin errores hasta que amplíes tu guía.

---

## 4. Cómo Probar tu Nuevo Perfil

1. Ejecuta primero en modo seco para calibrar `phash_threshold` sin gastar API:
   ```powershell
   python -m src.main --test-mode --profile mi_nuevo_juego --dry-run
   ```
2. Reproduce un gameplay del juego en YouTube en pantalla completa y valida con Gemini real:
   ```powershell
   python -m src.main --test-mode --profile mi_nuevo_juego
   ```
3. Ajusta `phash_threshold` y `cooldown_seconds` en `manifest.json` hasta que la frecuencia de consejos sea útil y no invasiva.
