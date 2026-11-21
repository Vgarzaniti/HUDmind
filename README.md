# HUDMind — Coach Táctico de Videojuegos en Tiempo Real

Un copiloto / coach de videojuegos en tiempo real para **Windows** impulsado por **Google Gemini (`google-genai`)**. Observa tu pantalla de forma ligera en segundo plano, detecta automáticamente qué juego estás jugando, aplica un pipeline de filtrado en CPU para no desperdiciar tokens ni FPS, y entrega recomendaciones tácticas concisas mediante:

- **Overlay transparente *click-through*** (*always-on-top*, sin robar foco ni bloquear el mouse).
- **Voz asíncrona de baja latencia (TTS)** con soporte intercambiable para `edge-tts` (neural) y `pyttsx3` (offline).
- **Context Caching de Gemini** (*Zero Search Latency*) con *fallback* automático cuando la base de conocimiento aún no supera el umbral mínimo de tokens de la API.

---

## Requisitos Previos

- **Sistema Operativo:** Windows 10 / 11 (para detección de ventana activa vía `pywin32` y overlay *click-through* con `WS_EX_TRANSPARENT`).
- **Python:** 3.11 o superior.
- **API Key de Gemini:** Obtenela gratis o con plan pago en [Google AI Studio](https://aistudio.google.com/).

---

## Instalación Rápida

1. **Clonar o abrir el proyecto** y crear un entorno virtual:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
```

2. **Instalar dependencias:**

```powershell
pip install -r requirements.txt
```

3. **Configurar variables de entorno:**

Copia `.env.example` a `.env` y agrega tu clave real:

```powershell
Copy-Item .env.example .env
```

Edita `.env`:

```env
GEMINI_API_KEY=tu_api_key_real_de_gemini
DEFAULT_MODEL=gemini-2.5-flash
ENABLE_VOICE=true
ENABLE_OVERLAY=true
```

> **Seguridad:** `.env` está incluido en `.gitignore`. Nunca publiques ni subas tu `GEMINI_API_KEY`.

---

## Cómo Ejecutar

### 0. Panel Gráfico Ligero (GUI — Recomendado)

Hacé doble clic en [HUDMind.bat](HUDMind.bat) o ejecutá:

```powershell
.\.venv\Scripts\hudmind.exe --gui
# o: python -m src.main --gui
```

Desde el panel gráfico podés:
- **Asociar múltiples cuentas IA con rotación automática por cuota/tokens** (varias cuentas de **Google Gemini**, **ChatGPT / OpenAI**, **Anthropic Claude**, **Groq gratis**, **OpenRouter `:free`**, **Pollinations gratis**, **Ollama Local 100% gratis sin login** y servidores **OpenAI-compatible / LM Studio sin login**).
- **Cambiar idioma de texto y audio por separado** (`Español` / `English`) y probar la voz al instante.
- **Personalizar el estilo del overlay** (tema visual, transparencia/opacidad, posición, tipografía, tamaño, ancho, duración) con **vista previa click-through en pantalla**.
- Usar **"Iniciar y Cerrar GUI (Mínimo RAM)"** para que la interfaz consuma **0 MB** mientras jugás.

### 1. Modo Normal por Consola (Detección Automática de Juego)

Ejecuta el copiloto desde la raíz del proyecto:

```powershell
python -m src.main
```

El sistema quedará en espera escuchando el proceso en primer plano. Cuando abras un juego configurado en `profiles/` (por ejemplo `cs2.exe`), cargará automáticamente su `manifest.json`, `system_prompt.md` y `knowledge_base.md`, creará el **Context Cache** en Gemini y comenzará a asistirte.

### 2. Modo de Prueba (`--test-mode`) — Ideal para probar con YouTube

¿Querés probar el copiloto sin abrir un juego pesado? Abrí un gameplay de CS2 en YouTube en pantalla completa (o cualquier ventana) y ejecutá:

```powershell
python -m src.main --test-mode --profile example_cs2
```

En `--test-mode`:
- Se omite el filtro de ventana activa (`FocusFilter`).
- Se fuerza el perfil indicado (`example_cs2` por defecto).
- Podés calibrar en vivo el umbral de `pHash`, la frecuencia de captura y la cadencia de voz/overlay.

### 3. Controles en Partida (Hotkeys Nativos Windows)

Sin hacer `Alt+Tab` y usando la API oficial Win32 `RegisterHotKey` (sin hooks intrusivos de teclado):
- **`F9`**: Pausar / Reanudar el coach al instante (muestra confirmación discreta en el overlay).
- **`F10`**: Pedir lectura táctica inmediata (*on-demand*), salteando `pHash` y `cooldown`.
- **Memoria anti-repetición:** El coach recuerda las últimas 3 intervenciones (TTL 90s) para no repetir el mismo consejo seguido.

### 4. Otras Opciones Útiles de CLI y Calidad (ISO/IEEE)

```powershell
# Diagnóstico integral de entorno (perfiles, Win32, captura, overlay click-through, audio)
python -m src.main --health-check

# Probar captura + filtros en CPU SIN gastar tokens ni necesitar API key
python -m src.main --test-mode --dry-run

# Elegir perfil de Valorant o CS2
python -m src.main --test-mode --profile example_valorant

# Probar con una carpeta de capturas de pantalla o un video (VideoMockCaptureStrategy)
python -m src.main --test-mode --mock-source ./capturas_prueba --dry-run

# Desactivar voz, overlay o hotkeys temporalmente
python -m src.main --test-mode --no-voice --no-hotkeys
python -m src.main --test-mode --no-overlay

# Ejecutar suite de calidad (pytest + ruff + mypy)
pytest
ruff check src tests
mypy src tests
```

Para detener limpiamente el copiloto en cualquier momento, presioná `Ctrl + C`. El sistema liberará el caché de Gemini, cerrará la ventana de overlay y detendrá el audio de forma ordenada.

---

## Estructura del Proyecto

```text
.
├── .env.example                 # Plantilla de variables de entorno
├── .gitignore                   # Ignora .env, config/accounts.json, venvs y logs
├── HUDMind.bat                  # Lanzador directo en Windows para la GUI
├── LICENSE                      # Licencia MIT
├── pyproject.toml               # Empaquetado CLI (hudmind) y configuración QA (ruff, mypy, pytest)
├── requirements.txt             # Dependencias Python
├── README.md                    # Esta guía
├── .github/workflows/ci.yml     # Pipeline CI automatizado (GitHub Actions)
├── docs/
│   ├── srs.md                   # Especificación de Requisitos (ISO/IEC/IEEE 29148)
│   ├── architecture.md          # Arquitectura Event-Driven y ADRs (ISO/IEC/IEEE 42010)
│   ├── test_plan_ieee29119.md   # Plan de pruebas, RTM y benchmarks (ISO/IEC/IEEE 29119)
│   └── game_profiles_guide.md   # Cómo crear nuevos "cartuchos" de juegos
├── profiles/
│   ├── example_cs2/             # Perfil completo Counter-Strike 2
│   └── example_valorant/        # Perfil completo VALORANT
├── src/
│   ├── main.py                  # Orquestador principal asyncio, diagnóstico y CLI
│   ├── config/
│   │   ├── settings.py          # Configuración tipada, redacción de secretos y telemetría
│   │   ├── user_store.py        # Persistencia local atómica (0o600) de cuentas IA y preferencias
│   │   └── hotkeys.py           # Hotkeys globales Win32 RegisterHotKey (F9 / F10)
│   ├── capture/
│   │   ├── base.py              # Interfaz CaptureStrategy y modelos de frame
│   │   ├── mss_capture.py       # MSSCaptureStrategy (zero-copy) + VideoMock + DXCam
│   │   └── observer.py          # ScreenObserver (Pub/Sub Drop-Oldest + compuerta pre-captura)
│   ├── pipeline/
│   │   ├── filters.py           # Chain of Responsibility (Focus, Cooldown, pHash, Resize/Eco)
│   │   └── schemas.py           # Structured Outputs, State Machine y memoria anti-repetición
│   ├── profiles/
│   │   └── loader.py            # Detector de .exe ((HWND, PID) cache) y cargador de perfiles
│   ├── ai/
│   │   ├── gemini_client.py     # Context Caching, thinking_budget=0 e inferencia Gemini
│   │   └── providers.py         # Enrutador Multi-IA (Gemini, ChatGPT, Claude, Groq, OpenRouter, Ollama)
│   ├── output/
│   │   ├── overlay.py           # Overlay transparente click-through (5 temas + DPI Aware v2)
│   │   └── tts.py               # Motor TTS desacoplado (EdgeTTS / Pyttsx3 / Piper)
│   └── ui/
│       └── gui.py               # Centro de Control Gráfico nativo ultra-ligero (Tkinter/ttk)
└── tests/                       # Suite de 26 pruebas unitarias, seguridad e integración
```

---

## Documentación Adicional (Estándares ISO / IEEE)

- [docs/srs.md](docs/srs.md): Especificación de Requisitos de Software formal (**ISO/IEC/IEEE 29148** + requisitos **ISO/IEC 25010** y **27001**).
- [docs/architecture.md](docs/architecture.md): Descripción de Arquitectura (**ISO/IEC/IEEE 42010**), diagramas, patrones, concurrencia y ADRs.
- [docs/test_plan_ieee29119.md](docs/test_plan_ieee29119.md): Plan y reporte de pruebas (**ISO/IEC/IEEE 29119 / IEEE 829**), matriz de trazabilidad y benchmarks medidos.
- [docs/game_profiles_guide.md](docs/game_profiles_guide.md): Guía paso a paso para agregar nuevos videojuegos y diseñar bases de conocimiento efectivas para el coach.

---

## Solución de Problemas Frecuentes

- **El juego está en Pantalla Completa Exclusiva y el overlay no se ve:** Cambiá el modo de video del juego a **Ventana sin bordes (*Borderless Windowed / Fullscreen Windowed*)**. Windows restringe las ventanas superpuestas sobre contextos DirectX en modo exclusivo.
- **Advertencia sobre Context Caching (`fallback` a modo estándar):** La API de Gemini exige un mínimo de tokens (~1,024 a 4,096 según el modelo) para crear un caché explícito. Si tu `knowledge_base.md` es corto, el cliente detecta el error automáticamente y continúa en modo estándar sin interrumpir la sesión.
- **Sin audio con `edge-tts`:** Verificá tu conexión a internet o cambiá `TTS_ENGINE=pyttsx3` en `.env` para usar síntesis offline instantánea.
