# Plan: migrar de CefSharp a WebView2 para obtener el sandbox de Chromium

Estado: **WebView2 implementado y predeterminado (sección 13); CEF queda como respaldo; la Fase 6 (retirar CEF) no se ha hecho**. Fecha: 2026-11-03.

## 1. Objetivo

Que cada pestaña se ejecute dentro del sandbox de Chromium y que el motor reciba parches de seguridad sin que tengamos que publicar una versión nueva, **conservando el rasgo distintivo del producto: barras translúcidas que desenfocan la web real**.

Hoy el desenfoque se consigue porque CEF en modo fuera de pantalla entrega cada fotograma como textura GPU y nosotros lo componemos. Ese mismo diseño es el que impide el sandbox: CefSharp usa un subproceso administrado que no puede enlazar el sandbox de CEF. No hay ajuste ni parche que lo cambie.

## 2. Qué está confirmado y qué no

**Confirmado en la documentación de Microsoft** (fuentes al final):
- WebView2 admite *alojamiento en composición*: `CoreWebView2CompositionController` conecta el árbol visual del motor a un visual de la aplicación (`RootVisualTarget`: `IDCompositionVisual` o `Windows.UI.Composition.ContainerVisual`) y se dimensiona con `Bounds`.
- En ese modo la aplicación reenvía la entrada: `SendMouseInput` (con coordenadas de cliente y eventos `LEAVE` explícitos), `SendPointerInput` para táctil y lápiz, `MoveFocus`/`MoveFocusRequested` para el foco, y `CursorChanged` para el cursor.
- `CompositionBackdropBrush` muestrea lo que hay detrás de un visual y admite un efecto `GaussianBlurEffect` sobre él.

**No confirmado — es el riesgo central:** que un pincel de desenfoque pueda muestrear el visual que WebView2 aloja. La documentación no lo cubre y `AcrylicBrush` de WinUI solo difumina contenido XAML. Además, la superficie de WebView2 es un elemento nativo aparte, con el conocido problema de «airspace» (XAML no se mezcla con ella). **Nadie debería comprometerse con la migración hasta resolver esto con un prototipo (Fase 0).**

## 3. Arquitectura propuesta

Introducir una interfaz `IWebEngineSession` con lo que hoy expone `BrowserSession` (navegar, atrás/adelante, zoom, buscar, imprimir, silenciar, tema, descargas, menú contextual, eventos de título/dirección/carga/favicon/desplazamiento). La interfaz se extrae **antes** de tocar el motor, con CEF todavía detrás; así la migración se hace por sustitución y se puede abandonar sin perder lo ya hecho.

Se reutiliza sin cambios: pestañas, menús, ajustes, favoritos, historial, panel de descargas, `FrameLoop`/`Fx`, `ViewportLayout` (y sus pruebas), `AddressResolver`, `BookmarkStore`/`BookmarkHtml`, `ProfileStorage`, instalador y registro del navegador, `AppLog`, restauración perezosa y reinicio.

## 4. Fases y puertas de decisión

### Fase 0 — Prototipo del cristal (puerta de la migración)
Proyecto aparte, sin tocar el navegador. Una ventana WinUI 3 con WebView2 alojado en composición y una barra translúcida encima. Se prueban, en este orden, y se detiene al primero que funcione:
1. **A. Desenfoque en la composición**: `CompositionBackdropBrush` o `CompositionVisualSurface` con `GaussianBlurEffect` sobre el visual de WebView2.
2. **B. Captura de visual en GPU**: `GraphicsCaptureItem.CreateFromVisual` sobre el visual web, alimentando el desenfoque Direct2D que ya tenemos (hipótesis a verificar: requiere visuales de `Windows.UI.Composition`, puede añadir un fotograma de latencia y afectar al borde de captura). Esto contradice hoy el principio «sin capturas»; habría que reformularlo (captura de nuestro propio visual, sin CPU).
3. **C. Sin desenfoque de la web**: barras con acrílico del sistema sobre el escritorio.

Criterios de salida: desenfoque correcto con la web desplazándose, sin parpadeos, a 60 y 120 Hz, con 4K/DPI 150 %, y coste de GPU medido. **Resultado A o B → continuar. Solo C → decisión de producto tuya** (perder el rasgo distintivo a cambio de seguridad, o conservar CEF como «laboratorio»).

### Fase 1 — Extraer `IWebEngineSession` (con CEF detrás)
Sin cambios de comportamiento. Salida: la app funciona igual, las 89 pruebas pasan y la prueba de humo sigue verde.

### Fase 2 — Motor WebView2 básico
Navegación, título, dirección, carga, favicon, zoom, atrás/adelante, recarga, pestañas privadas (`IsInPrivateModeEnabled`), perfil persistente. Salida: se navega por 5 sitios distintos y se restauran pestañas.

### Fase 3 — Entrada, foco y cursor
Ratón, rueda, táctil/lápiz, teclado e IME, arrastrar y soltar, foco entre la barra de direcciones y la web, atajos que no deben llegar a la página. Es la fase con más riesgo de errores sutiles. Salida: lista de comprobación manual en teclado físico, touchpad de precisión y pantalla táctil.

### Fase 4 — Funciones del navegador
Descargas (`DownloadStarting`, progreso, cancelar, ruta propia y confirmación de ejecutables), menú contextual propio (`ContextMenuRequested` con los elementos del motor), permisos (`PermissionRequested`: pasar de «bloquear todo» a preguntar por sitio), ventanas nuevas, buscar en la página, imprimir, código fuente, captura (`CapturePreviewAsync`), MHTML (`Page.captureSnapshot` por DevTools), tema claro/oscuro, idiomas, corrector.

### Fase 5 — Cristal definitivo
Integración de la técnica elegida en la Fase 0 con el viewport dinámico (la web pasa detrás de las barras), menús y paneles con material, y los efectos actuales.

### Fase 6 — Retirada de CEF y cierre
Eliminar `Rendering/`, `CefRenderHandler`, `GpuCompositor`, manejadores CEF y el paquete CefSharp; quitar de Configuración los ajustes que ya no aplican (frecuencia de fotogramas, mediciones OSR, «pausar renderizado»); actualizar README, instalador, prueba de humo y la guía de requisitos (Runtime de WebView2). Salida: publicación con sandbox verificado.

## 5. Mapa de cambios

| Zona | Acción |
|---|---|
| `Rendering/GpuCompositor.cs`, `CefRenderHandler.cs` | Se reescriben según la Fase 0 o se eliminan |
| `Browser/BrowserSession.cs` | Pasa a ser una de dos implementaciones de la interfaz |
| `Browser/PrototypeHandlers.cs`, `BrowserDownloadHandler.cs`, `WebContextMenu.cs`, `BrowserDisplayHandler.cs` | Se reescriben sobre eventos de WebView2 |
| `MainWindow.xaml.cs` (ratón, teclado, rueda) | La entrada deja de simularse hacia CEF; en composición se reenvía con `SendMouseInput`/`SendPointerInput`; la lógica de atajos se conserva |
| `MainWindow.Motion.cs`, `Edge`, `Menus`, `Tabs`, `Pages`, `SettingsEditor` | Cambios menores (llamadas a la interfaz, ajustes que desaparecen) |
| `App.xaml.cs` | Sustituye `Cef.Initialize` por el entorno de WebView2 (carpeta de datos, idioma, opciones) |
| `Browser/ViewportLayout.cs` y pruebas | Se conservan |

## 6. Qué se gana y qué se pierde

**Se gana:** sandbox y aislamiento por sitio, actualizaciones del motor con el Runtime de WebView2, sesiones persistentes seguras (se puede activar por defecto con cifrado del perfil), menos código propio (sin composición de fotogramas, sin sincronización por *fence*), cadencia de pantalla nativa, y un paquete mucho más pequeño (libcef.dll son 276 MB de los 639 MB actuales; estimación).

**Se pierde o cambia:** el control del renderizado fotograma a fotograma (selector de FPS, mediciones propias, pausa de renderizado), la posibilidad de entregar a CEF la entrada exacta que queramos, y la independencia de Edge: hace falta el Runtime de WebView2 (viene con Windows 11 y con Edge; en equipos sin él se puede incluir el instalador *bootstrapper*).

## 7. Riesgos

| Riesgo | Mitigación |
|---|---|
| El cristal no es posible sobre WebView2 | Fase 0 antes que nada, con la puerta de decisión descrita |
| IME, táctil y foco en alojamiento por composición | Fase 3 dedicada; si fuera inviable, usar modo HWND con la barra superior fuera de la web |
| El modo composición cambia entre versiones del SDK de WebView2 | Fijar versión, pruebas de humo automáticas y revisión del Dependabot |
| Pérdida de funciones del modo OSR (arrastrar y soltar, vídeo bajo carga) | Lista de regresión por función antes de retirar CEF |
| Dos motores a la vez durante la transición aumentan el tamaño y la complejidad | Mantenerlos solo hasta el final de la Fase 5 |

## 8. Pruebas

- Mantener las 89 pruebas xUnit y ampliarlas con la lógica nueva (por ejemplo la traducción de comandos del menú contextual).
- Pruebas de interfaz con UI Automation sobre la propia ventana (ya usadas aquí para el reinicio y la restauración perezosa).
- La prueba de humo de `publish.ps1` pasa a comprobar también que el sandbox está activo (procesos de renderizado con nivel de integridad bajo o *AppContainer*).
- Matriz manual: Windows 10 y 11, 100 %/150 %/200 % de escala, dos monitores, teclado físico, touchpad de precisión y pantalla táctil.

## 9. Estimación (orientativa, un desarrollador)

| Fase | Duración estimada |
|---|---|
| 0 Prototipo del cristal | 2–3 días |
| 1 Interfaz | 3–4 días |
| 2 Motor básico | 5–7 días |
| 3 Entrada y foco | 3–5 días |
| 4 Funciones | 4–6 días |
| 5 Cristal definitivo | 4–8 días (depende de la Fase 0) |
| 6 Retirada y cierre | 2–3 días |
| **Total** | **unas 4 a 7 semanas** |

Son estimaciones mías, sin medir contra el SDK real; la Fase 0 es la que más las puede mover.

## 10. Decisiones que necesito de ti

1. ¿Autorizas la Fase 0 (prototipo aislado, no toca el navegador actual)?
2. Si el resultado fuera solo la opción C (sin desenfoque de la web): ¿prefieres seguridad sin cristal, conservar CEF como edición «laboratorio» con aviso, o decidirlo con el prototipo delante?
3. ¿Windows 10 es un objetivo? Condiciona el Runtime de WebView2 y la técnica de cristal.

## 11. Resultado de la Fase 0 (ejecutada el 2026-11-02)

Prototipo en [`prototypes/GlassSpike`](../prototypes/GlassSpike) (WinUI 3, Windows App SDK 2.6.4, control `WebView2` con una página de franjas de colores y cuatro barras encima). Se capturó la ventana y se comparó:

| Franja | Técnica | Resultado |
|---|---|---|
| A | `AcrylicBrush` de XAML | **Falla.** Pinta un gris plano; no muestrea la web |
| B | `CompositionBackdropBrush` + `GaussianBlurEffect` (Win2D) | **Falla.** Muestrea el fondo de la ventana y el XAML, no la web |
| C | XAML translúcido sin efecto | La web **se ve a través**: el XAML se dibuja encima de WebView2 y **no hay «airspace»** en este SDK |
| D | `backdrop-filter` inyectado en la página (`AddScriptToExecuteOnDocumentCreatedAsync`) | **Funciona.** Las franjas aparecen realmente desenfocadas, con el texto XAML nítido encima |

**Conclusión:** la opción A del plan no es viable y la B queda descartada por ahora (la captura de pantalla propia añadiría realimentación y latencia). Aparece una **opción D**: el desenfoque lo hace Chromium dentro del proceso de renderizado aislado, en GPU, y las barras XAML solo ponen el tinte y los controles. Para los menús y paneles, el host enviaría a la página los rectángulos de cristal (`PostWebMessageAsJson`) y un script los pintaría con `backdrop-filter` y esquinas redondeadas.

**Lo que la prueba no demostró** (se midió después; ver la sección 12):
- Rendimiento real con desplazamiento, en 4K y con zoom; solo se capturó un fotograma estático.
- Páginas sin DOM propio: visor de PDF, páginas de error de Chromium y `view-source:` (ahí no se ejecuta el script). Habría que caer a un tinte sólido o acrílico.
- Capa superior de la página (diálogos modales, pantalla completa, vídeo): el elemento inyectado queda por debajo.
- Sitios con `transform` en `<html>` (rompe `position: fixed`) y la interacción con el zoom (píxeles CSS frente a DIP).
- Los rectángulos de menús llegan con 1–2 fotogramas de retraso respecto a su animación.
- **Cambia una afirmación del producto:** hoy el README dice que no se toca el DOM ni el CSS de los sitios. Con D se añade **un elemento** a cada página, que un sitio podría detectar o ocultar.

**Efecto en el plan:** la Fase 5 se simplifica mucho (desaparece el compositor propio, los *fences* y la copia de fotogramas) y el control de WebView2 se coloca directamente en el árbol XAML. La estimación total baja a **unas 3 a 5 semanas**. El acoplamiento medido hoy con CEF es de unas 110 referencias en ~3.300 líneas de interfaz (entrada, compositor, menú contextual y `BrowserTab.Source`), por lo que la Fase 1 (interfaz) es obligatoria antes de empezar.

## 12. Resultado de la ampliación del prototipo (2026-11-02)

El prototipo ejecuta solo los escenarios y guarda informe y capturas (`prototypes/GlassSpike/results/`). Máquina de prueba: RTX 4060 Ti, pantalla de 2560×1440 a escala 100 %, rejilla a 120 Hz.

### Lo que se confirmó

| Pregunta | Resultado medido |
|---|---|
| ¿Rinde el cristal en la página al desplazarse? | **Sí.** En 3 s de desplazamiento a 1.500 px/s, el tiempo medio por fotograma es 8,33 ms (120 Hz) con cristal apagado, con barra, con barra + menú y a pantalla completa (2560×1369): p95 de 8,4 ms y **0–1 fotogramas de más de 20 ms** en cada caso. |
| ¿Latencia al actualizar regiones (menús)? | La ida y vuelta del mensaje al script tarda 0,23 ms de media (máximo 0,49 ms). Quedaría el retraso propio de 1–2 fotogramas de la página, no de la mensajería. |
| ¿Funciona en páginas «sin DOM»? | **Sí, en las cuatro probadas:** visor de PDF, página de error de Chromium, `view-source:` y una web normal dibujan el cristal (capturas). La previsión de la sección 11 era demasiado pesimista; el respaldo XAML sigue siendo útil por si alguna otra página no ejecuta el script. |
| ¿Y los diálogos modales (capa superior)? | El diálogo **tapa el cristal** como se preveía. La barra XAML sigue encima y se ve bien; el efecto solo se pierde bajo el propio diálogo. |
| ¿Se alinea con el zoom de página real? | **Sí, con error de 0 px a 100 %, 150 %, 75 % y 200 %**, usando un controlador propio con `ZoomFactor`: `devicePixelRatio` = escala × zoom y la fórmula `css px por DIP = escala / devicePixelRatio` coloca el cristal exactamente. Solo se probó con escala de pantalla del 100 %. |

### Lo que apareció y no estaba en el plan

1. **El control XAML de WebView2 no permite zoom programático.** No tiene `ZoomFactor`. Tampoco sirve `RasterizationScale`: se acepta, pero la página no cambia (`devicePixelRatio` e `innerWidth` siguen iguales). La emulación de métricas por DevTools tampoco reproduce el zoom. Sin zoom programático se pierden la fila de zoom del menú, el zoom predeterminado y el zoom por pestaña.
2. **El `CoreWebView2CompositionController` sí se puede crear** en este entorno y expone la API completa (`ZoomFactor`, `SetBoundsAndZoomFactor`, `SendMouseInput`, `SendPointerInput`, `MoveFocus`, `Cursor`…), **pero `RootVisualTarget` rechaza un visual de WinUI** (`Microsoft.UI.Composition`: `InvalidCastException`); acepta visuales de `Windows.UI.Composition` o `IDCompositionVisual`. Falta un puente hacia el árbol de WinUI que no he podido demostrar.
3. **Un controlador en ventana (HWND) dentro de la ventana WinUI no se ve** (la isla XAML lo tapa), así que el alojamiento clásico no es una alternativa.
4. **Ventaja inesperada del control XAML:** el XAML se dibuja encima sin «airspace» y el control recibe los eventos de entrada de XAML (puntero, teclado, caracteres, arrastrar y soltar). Si se confirma con teclado físico, IME y táctil, **la Fase 3 (entrada y foco) se reduce mucho**, porque no habría que reenviar la entrada a mano.

### Veredicto

- **Cristal: viable.** El rendimiento y la alineación están demostrados.
- **Riesgo restante: el zoom.** Hay tres salidas, de menos a más coste:
  1. Control XAML + zoom por `zoom` de CSS inyectado. Es barato, pero no es zoom de navegador: afecta a `vh`/`vw`, a las consultas de medios y a elementos `fixed`. El cristal no se ve afectado (el DPR no cambia).
  2. Control XAML + zoom nativo de Chromium (Ctrl+rueda, Ctrl++/−) sin interfaz propia de zoom: no se puede leer ni fijar desde la aplicación.
  3. Alojamiento por composición con puente a WinUI (`ZoomFactor` real). Hay que demostrar el puente (visual de `Windows.UI.Composition` o DirectComposition dentro del árbol de WinUI) en otro prototipo corto.
- **Decisión de producto pendiente:** qué salida de zoom aceptas. No bloquea la Fase 1 (extraer la interfaz del motor).

### Estimación revisada
Con el control XAML y zoom por CSS: **unas 3 semanas**. Si hace falta el puente de composición: **4–5 semanas**.

## 13. Estado de la implementación (2026-11-03, zoom por CSS elegido)

**WebView2 es ya el motor predeterminado** (CEF queda como respaldo si no hay Runtime o se fuerza con `FLUXBROWSER_ENGINE=cef`). Todo lo siguiente se comprobó ejecutando la aplicación, incluida la compilación publicada; la prueba de humo de `publish.ps1` arranca ambos motores.

**Hecho y verificado:**
- **Fase 1:** `IWebSession` extraída; CEF intacto (89 pruebas xUnit y restauración perezosa).
- **Fase 2:** `WebView2Session` con navegación, título, dirección, carga, favicon, atrás/adelante, recarga, silencio, tema, descargas, nuevas ventanas con criterio de gesto, captura, impresión, DevTools, MHTML, borrado de datos y **buscar en la página nativo** (`CoreWebView2.Find`, con resaltado).
- **Cristal en la página** (`page-host.js`): barra, menús y paneles; la página reserva el hueco bajo la barra con `padding` en la raíz (la traslación de composición del control se descartó: el WebView2 no la refresca de forma fiable y desalineaba el cristal, que llega por otro canal); las barras pasan de opacas a cristal según `scrollTop` dentro del mismo fotograma; la barra de desplazamiento se dibuja en la página para que nazca bajo las barras; sin compositor GPU ni capa de entrada de CEF en este modo.
- **Zoom por CSS** con el cristal compensado.
- **Atajos con la web enfocada** por un canal en mundo aislado (solo teclas de confianza; `Page.enable` imprescindible; receptores de DevTools referenciados). Una página hostil (servida de verdad desde localhost) lanzando eventos sintéticos y mensajes falsos durante 16 s no abre ni cierra pestañas y ve el binding como `undefined`. *Nota:* una primera versión de esa prueba no cargaba la página (servidor mal montado) y era inválida; se repitió bien.
- **Accesibilidad:** la barra, las pestañas y los botones están en el árbol de UI Automation (visto con `TreeWalker`); el control web tiene nombre accesible. `FindAll` desde la ventana se corta en la frontera de la isla de WebView2.
- **Pestañas y ventanas privadas:** perfil InPrivate del motor (`IsInPrivateModeEnabled`) y `--private`. Comprobado con un servidor local: la primera visita no lleva cookie, una ventana privada con el mismo perfil **no ve la cookie** guardada por la ventana normal, y la normal posterior sí la ve.
- **Menú contextual propio** (`ContextMenuRequested`, `MainWindow.NativeMenu.cs`): acciones de vínculo e imagen, elementos del motor, Actualizar, Guardar como, Captura, Buscar, código fuente e Inspeccionar; elegir «Abrir vínculo en una pestaña nueva» abre la pestaña.
- **Permisos:** aviso por sitio del propio motor (con memoria), en lugar de denegar todo.
- **Sesiones:** viene activada en perfiles nuevos con WebView2; sin ella el perfil del motor se vacía en cada arranque.
- **Ajustes:** la sección de rendimiento de CEF se sustituye por una nota en este modo.
- **Restauración perezosa** también en WebView2 (1 motor al arrancar con 4 pestañas guardadas, 2 tras seleccionar la segunda).

**Huecos conocidos:**
1. **Zoom CSS:** páginas con `vw` o anchos fijos se desbordan al ampliar; Ctrl+rueda inactivo.
2. **Sin probar con dispositivos físicos:** teclado físico, IME, táctil, arrastrar y soltar, escalas ≠ 100 %, varios monitores, lector de pantalla real y foco dentro de `iframe` entre orígenes.
3. **Guardar enlace como** navega a la dirección; **idiomas:** solo el primero llega al motor.
4. **Decisión: se mantiene el respaldo CEF** (2026-11-03). Sigue empaquetado: el zip pesa 269 MB y `libcef.dll` son 276 MB sin comprimir. Retirarlo (Fase 6) reduciría el paquete, pero dejaría sin navegador a los equipos sin el Runtime de WebView2; queda como mejora futura si se exige el Runtime en el instalador.

## Fuentes consultadas

- [ICoreWebView2CompositionController (referencia Win32)](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2compositioncontroller)
- [CoreWebView2CompositionController.RootVisualTarget (.NET)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2compositioncontroller.rootvisualtarget)
- [CoreWebView2CompositionController (.NET)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2compositioncontroller)
- [Compositor.CreateBackdropBrush (Windows App SDK)](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositor.createbackdropbrush?view=windows-app-sdk-2.0-experimental)
- [Compositor.CreateHostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)
- [Acrílico en la aplicación (AcrylicBrush solo difumina contenido XAML)](https://learn.microsoft.com/en-us/WINDOWS/APPS/develop/ui/in-app-acrylic)
- [Diez cosas sobre WebView (problema de «airspace»)](https://learn.microsoft.com/en-us/archive/blogs/wsdevsol/ten-things-you-need-to-know-about-webview)
