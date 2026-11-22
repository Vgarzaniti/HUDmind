# Base de Conocimiento Táctico — Counter-Strike 2 (CS2)

Este documento proporciona los anclajes visuales del HUD, reglas de economía, heurísticas de combate, manejo de utilidad y elementos de UI a ignorar para evaluar frames de **Counter-Strike 2** con precisión y sin latencia de búsqueda.

---

## 1. Anclajes Visuales del HUD (CS2 Estándar 16:9)

Usa estas referencias espaciales para leer el estado táctico en milisegundos:

- **Esquina Superior Izquierda — Radar / Minimapa:**
  - Puntos **azules/celestes/colores de equipo con flecha**: compañeros vivos y dirección hacia la que miran.
  - Puntos **rojos** o signo de interrogación rojo: enemigos avistados recientemente o última posición conocida.
  - Icono amarillo/rojo de **C4 (bomba)**: si está suelto en el suelo (sin portador) o transportado por un compañero T.
  - Zonas sombreadas **A / B**: bombsites. Si el radar muestra todos los compañeros en un site y un enemigo detectado en el opuesto, alertar rotación o flanco.
- **Centro Superior — Marcador, Reloj y Jugadores Vivos:**
  - Reloj central: duración de ronda (`1:55` inicio; **rojo pulsante / icono de bomba** cuando el C4 fue plantado — temporizador exacto de **40 segundos** hasta detonación).
  - Filas de avatares a izquierda y derecha del reloj: conteo exacto de jugadores vivos (ej. `3v2`, `1v3`, `2v4`).
  - Indicador de ronda / puntuación (MR12: se gana al llegar a **13 rondas**, overtime en `12-12`).
- **Esquina Superior Derecha — Killfeed:**
  - Muestra bajas recientes, arma utilizada (ej. AWP, AK-47, M4A1-S, granada HE), headshots y kills a través de humo/pared.
  - Útil para inferir si cayó el AWPer enemigo o si un site quedó despejado.
- **Esquina Inferior Izquierda — Dinero, Vida (HP) y Chaleco (Armor):**
  - Dinero (`$0` a `$16,000`): visible especialmente en *freeze time* y durante la ronda.
  - **HP (0–100)**: se torna rojo por debajo de `25 HP`.
  - **Armor (0–100)** + icono de casco (Helmet). Si el jugador CT tiene chaleco sin casco contra AK-47, no necesita re-comprar casco; contra eco/SMGs/Glocks el casco es crucial.
- **Centro Inferior — Arma Equipada / Icono de Defuse Kit / C4:**
  - Muestra equipamiento rápido en algunos HUDs minimalistas y tarjetas de estado de ronda.
- **Esquina Inferior Derecha — Munición e Inventario de Utilidad:**
  - Formato `cargador / reserva` (ej. `4 / 90`).
  - Lista vertical de armas y granadas disponibles:
    - **Smoke** (humo), **Flashbang** (cegadora), **HE** (fragmentación), **Molotov / Incendiaria**.
    - **Defuse Kit (alicates)** para CT (reduce defuse de **10s** a **5s**).
    - **C4** para Terroristas.

---

## 2. Reglas de Economía (MR12 Competitivo)

### 2.1 Valores Clave de Compra
- **Full Buy Terrorista (T):** ~`$3,900`–`$4,700` (AK-47 `$2,700` + Kevlar/Helmet `$1,000` + utilidad básica).
- **Full Buy Antiterrorista (CT):** ~`$4,500`–`$5,400` (M4A1-S `$2,900` o M4A4 `$3,000` + Kevlar `$650` + Kit `$400` + utilidad).
- **AWP Full Buy:** `$4,750` (solo arma) + chaleco (`$650`–`$1,000`).

### 2.2 Heurísticas de Freeze Time / Menú de Compra
- **Desincronización económica (CRÍTICO):** Si el equipo está en **Eco** (mayoría con `< $2,000`) y el jugador compra rifle completo quedando en `$0`, intervenir (`medium`): *"Equipo en eco, guardá economía para próxima"*.
- **Full Buy sin utilidad:** Si el jugador tiene `$5,000+` en *freeze time* y `0` granadas (especialmente sin Smoke ni Flash), recordar comprar al menos 1 Smoke y 1 Flash.
- **CT sin Defuse Kit con dinero sobrante:** Si en *freeze time* el jugador CT tiene `$1,500+` sobrantes tras equiparse y no tiene kit, sugerir comprar kit (`$400`).
- **Ronda 12 (última de mitad) o Match Point (12 rondas rivales):** Gastar **todo** el dinero disponible (`$0` restante); nunca ahorrar en la última ronda de mitad o partido.
- **Ronda 2 tras ganar Ronda de Pistolas:** Comprar SMG/rifle + casco (*anti-eco*); nunca quedarse con pistola inicial.

---

## 3. Reglas Tácticas en Combate y Posicionamiento

### 3.1 Gestión de Munición y Recarga
- **Munición baja en cargador:**
  - Rifles (AK/M4/Galil/Famas): si el cargador tiene `<= 6 balas` y no está en intercambio activo en campo abierto, recordar recargar en cobertura.
  - Pistolas / AWP: si quedan `<= 2 balas` en cargador, alerta `high` si hay presión cercana.
- **Prohibición de recarga innecesaria:** No recomendar recargar si todavía tiene `> 12 balas` en un rifle y está holdeando un ángulo caliente.

### 3.2 Uso de Utilidad (No Morir con Granadas)
- Si el jugador tiene **Smoke + Flash + Molotov** sin usar y está por pushear un choke point peligroso en seco, intervenir (`medium`): *"Tenés flash y smoke, usá utilidad antes de abrir"*.
- En defensa CT ante un rush visible en radar/humos enemigos, priorizar **Molotov / Incendiaria** o **Smoke defensivo** para frenar y dar tiempo a la rotación del equipo.

### 3.3 Gestión de Tiempo de Ronda
- **Lado T sin plantar:**
  - `< 35 segundos`: advertir que deben comprometerse a un site (`medium`).
  - `< 18 segundos`: alerta `high` (*"Quedan menos de 18s, ejecutá site ya"*).
  - Nota: plantar el C4 tarda **3.2 segundos** aproximadamente.
- **Post-Plant (Lado T):**
  - El tiempo juega a favor de Terroristas (40s hasta explosión).
  - Si hay ventaja numérica o 1v1 con bomba plantada y el jugador tiene poca vida (`< 40 HP`), priorizar **jugar tiempo, esconderse y reaccionar al sonido de defuse**, no buscar duelos innecesarios (*"Jugá al tiempo, no peekees"*).
- **Retake (Lado CT):**
  - Sin Defuse Kit necesita **10 segundos**; con Kit necesita **5 segundos**.
  - En desventaja severa (`1v4`, `1v5` o `1v3` con poca vida y bomba avanzada) con rifle/AWP valioso y economía ajustada, sugerir **Save** (guardar el arma para la siguiente ronda).

### 3.4 Situaciones de Clutch y Ventaja Numérica
- **En ventaja (`3v1`, `4v1`, `2v1`):** Nunca regalar duelos 1v1 aislados. Jugar **crossfire** o **trade kill** junto al compañero visible en el radar.
- **En desventaja (`1v2`, `1v3`):** Buscar aislar duelos 1v1 usando ángulos cerrados y humo; no quedarse estático en el mismo lugar tras matar a uno si ya revelaste posición.
- **Radar desprotegido / Flanco:** Si todos los compañeros miran hacia el mismo lado y hay un hueco evidente en la espalda o un punto rojo en radar cerca del flanco, emitir alerta `high` (*"Cuidá espalda, radar abierto atrás"*).

### 3.5 HP Crítico
- Si `HP <= 20`, el jugador muere de un impacto al cuerpo de casi cualquier rifle o HE. Recomendar ángulos de *one-and-done*, jugar con flash/compañero o evitar *dry-peek* en línea recta.

---

## 4. Glosario y Jerga Preferida para Voz/Overlay

Usa terminología corta, clara y natural de CS2:
- `Eco` / `Force` / `Full buy` / `Save`
- `Peekear` / `No peekees` / `Dry-peek`
- `Trade` / `Crossfire` / `Refrag`
- `Rotá` / `Flanco` / `Espalda`
- `Defuse` / `Fake defuse` / `Kit`
- `Post-plant` / `Retake` / `Clutch`
- `Smoke` / `Flash` / `Molo` / `HE`

---

## 5. Modo Entrenamiento / Práctica (`coach_mode = training`)

Cuando el jugador esté en **Modo Entrenamiento** (*Practice con bots*, *Aim Botz*, *Recoil Master* o servidor de práctica de granadas con trayectoria de utilidad activada), **NO apliques la regla de ignorar Warmup/Práctica**. En su lugar, actuá como instructor técnico activo:
- **Crosshair Placement:** Corregir si la mira apunta al piso en lugar de a la altura de la cabeza al limpiar ángulos.
- **Counter-Strafing y Spray Control:** Recordar frenar en seco (`A`/`D` opuesto) antes del primer disparo con AK-47 / M4 y compensar la caída vertical inicial de las primeras 8–10 balas.
- **Práctica de Lineups (Smokes / Flashes / Molos):** Evaluar dónde cae la granada en la cámara de previsualización (*PIP grenade camera*) y sugerir ajustes de referencia o *jump-throw*.

---

## 6. Lista Negra de UI — Cuándo NO Intervenir (En modo `match`)

Debes devolver obligatoriamente `should_intervene = false` en cualquiera de estos casos:
1. **Menú principal, lobby, selección de mapa, veto Premier o pantalla de carga.**
2. **Tabla de puntuaciones (`TAB`)** cubriendo el centro de la pantalla.
3. **Pantalla de espectador tras morir** (excepto si acaba de empezar la siguiente ronda o se ve una clara necesidad de avisar economía global, y aun así solo si el jugador está vivo en *freeze time*).
4. **Warmup / Calentamiento** (reloj de calentamiento o indicador de Warmup visible).
5. **Killcam / Cámara de muerte / Transición entre rondas** cuando la ronda ya terminó (`Counter-Terrorists Win` / `Terrorists Win`) salvo que sea un recordatorio de guardar arma al final de ronda si todavía puede salvarla.
6. **Overlays externos** (Discord, Steam Overlay, contadores de FPS de NVIDIA/MSI Afterburner, chat de texto tóxico o spam de radio).
7. **Situación normal sin anomalías:** El jugador tiene buena vida, munición suficiente, está en posición estándar y no hay amenaza inmediata ni oportunidad táctica destacable.
