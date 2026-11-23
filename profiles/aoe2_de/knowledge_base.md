# Base de Conocimiento Táctico — Age of Empires II: Definitive Edition

## 1. Anclajes Visuales del HUD (AoE2: DE Estándar)

- **Esquina Superior Izquierda — Barra de Recursos y Población:**
  - **Madera (Wood):** Icono de troncos.
  - **Alimento (Food):** Icono de carne/maíz.
  - **Oro (Gold):** Icono de lingotes amarillos.
  - **Piedra (Stone):** Icono de roca gris.
  - **Población (`Actual / Máxima`, ej. `24/25`):** Si está a `<= 2` del límite o parpadea en amarillo/rojo, el jugador está por quedar **encasillado (*Housed*)**.
  - **Botón/Contador de Aldeanos Inactivos (`!` o icono de aldeano con número):** Si es `>= 1`, hay aldeanos sin trabajar.
- **Esquina Superior Izquierda (Debajo de recursos) — Cola Global de Producción:**
  - Muestra aldeanos, unidades militares y tecnologías en entrenamiento/investigación. Si **no aparece el icono de Aldeano** durante Alta Edad Media / Feudal / Castillos (con `< 120` aldeanos y sin estar avanzando de edad), el Centro Urbano (TC) está inactivo.
- **Centro Superior — Edad Actual e Indicador de Avance:**
  - Muestra la edad (*Alta Edad Media / Dark Age*, *Edad Feudal*, *Edad de los Castillos*, *Edad Imperial*) o el porcentaje de avance.
- **Esquina Inferior Derecha — Minimapa:**
  - Puntos brillantes del color enemigo acercándose a las líneas de madera, bayas u oro del jugador indican un *raid* (Scout rush, Archer rush, Men-at-Arms).

## 2. Reglas de Macroeconomía (Prioridad Máxima)

1. **Producción Continua de Aldeanos y Cero Inactivos:**
   - Regla #1 de AoE2: el Centro Urbano nunca debe estar parado hasta alcanzar `~110–130` aldeanos.
   - Si ves contador de **Aldeanos Inactivos > 0**, intervenir (`high` si `>= 3`, `medium` si `1–2`): *"Tenés aldeanos inactivos, asignalos a recursos"*.
2. **Alerta de Encasillado (*Housed*):**
   - En Alta Edad Media / Feudal: alertar cuando falten **2 de población** para el límite (`18/20`, `23/25`).
   - En Castillos / Imperial (con múltiples TCs o edificios militares): alertar cuando falten **5 de población** (`65/70`): *"Falta población, construí dos casas ya"*.
3. **Control de Flotación de Recursos (*Floating Resources*):**
   - **Madera > 600 en Feudal:** *"Sobra madera: sembrá granjas o levantá Herrería/Mercado"*.
   - **Alimento >= 500 en Alta Edad Media:** *"Tenés 500 de alimento, clickeá a Edad Feudal"*.
   - **Alimento >= 800 y Oro >= 200 en Feudal:** *"Recursos listos para subir a Edad de los Castillos"*.
   - **Oro o Alimento > 1,200 en Castillos (sin estar subiendo a Imperial):** Recomendar agregar edificios militares para gastar recursos o invertir en mejoras de Herrería/Universidad.
4. **Mejoras Económicas Obligatorias al Cambiar de Edad:**
   - Al llegar a **Feudal:** investigar **Hacha de Doble Filo (*Double-Bit Axe*)** en campamento maderero y **Collera (*Horse Collar*)** en molino.
   - Al llegar a **Castillos:** investigar **Sierra de Arco (*Bow Saw*)** y agregar **2 Centros Urbanos adicionales** (`275 Madera + 100 Piedra` c/u) si juega macro/boom.

## 3. Triángulo de Counters Militares

- **Contra Caballería (Scouts / Jinetes / Caballeros):** **Lanceros / Piqueros / Alabarderos** o **Camellos** / **Monjes**.
- **Contra Arqueros (Arqueros a pie / Artilleros):** **Guerrilleros (*Skirmishers*)**, **Mangoneles (*Onagros*)** o **Caballería con armadura anti-proyectil (+2 en Herrería)**.
- **Contra Infantería (Hombres de Armas / Campeones / Piqueros):** **Arqueros**, **Artilleros Manuales** o **Escorpiones**.
- **Contra Armas de Asedio (Mangoneles / Lanzapiedras / Cañones):** **Caballería Ligera / Húsares** o **Bombardas**.

## 4. Modo Entrenamiento / Práctica (`coach_mode = training`)

Cuando el usuario esté en **Modo Entrenamiento** (*El Arte de la Guerra / Art of War*, *Escaramuza de práctica de Build Order* o *Scenario Editor*), guiá activamente la ejecución técnica:
- **Build Order Estándar (Alta Edad Media — primeros 21 aldeanos):**
  - Aldeanos `1–6` a **Ovejas** bajo el TC (construir 2 casas iniciales al segundo `0:00`).
  - Aldeanos `7–10` (o `7–9`) a **Madera** (campamento maderero pegado al bosque).
  - Aldeano `11` atrae el **primer Jabalí (*Boar*)** hacia debajo del TC.
  - Aldeanos `12–15` a **Bayas** (molino) y atraer el **segundo Jabalí** cuando el primero baje a `< 140` de alimento.
  - Desarrollar **Telar (*Loom*)** antes de clickear a Feudal si hay riesgo o justo al final de la cola de aldeanos.
- **Evaluación en Entrenamiento:** Corregí en vivo cualquier segundo de TC inactivo (*Idle TC time*), exceso de aldeanos golpeando animales lejos del TC, campamentos madereros mal pegados al bosque o retraso en explorar en círculos con el Scout.

## 5. Lectura Predictiva (Anticipar Timing y Raids)
- Si estás cerca de `500` comida sin telar ni casas libres -> avisar **antes** de quedar *housed* o retrasar Feudal. Si el Scout enemigo rodea madera/oro -> prevenir *quick-wall* o lanzas antes de que lleguen Scouts.

## 6. Lista Negra de UI (En modo `match`)
- Menú principal, lobby multijugador, árbol tecnológico a pantalla completa tapando el mapa, pantalla de estadísticas post-partida o pausa de partida.
