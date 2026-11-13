# Flux Browser

Un navegador de [World Marketing Labs](https://www.worldmarketinglabs.com/es).

Navegador de escritorio de WinUI 3 y Chromium con barras translúcidas que desenfocan el contenido web real. **Vista previa técnica (0.9): todavía no es un lanzamiento estable.**

[Descargar para Windows x64](https://github.com/cbatista96/FluxBrowser/releases/tag/v0.9.1) · [Galería](docs/capturas/README.md) · [Historial de versiones](CHANGELOG.md) · [Fallos conocidos](docs/FALLOS-CONOCIDOS.md) · [Informar un fallo](https://github.com/cbatista96/FluxBrowser/issues/new/choose)

![Nueva pestaña de Flux Browser](docs/capturas/01-nueva-pestana.png)

La versión 0.9.1 incluye un instalador EXE por usuario, un ZIP portable y sumas SHA-256. GitHub atesta la release. El instalador de esta vista previa carece de firma Authenticode. Consulta la [guía de Microsoft Store](docs/MICROSOFT-STORE.md) y la [política de privacidad](docs/PRIVACIDAD.md).

## Motor y seguridad

El motor predeterminado es **WebView2**: Chromium dentro de su **sandbox**, servido y actualizado por el Runtime de WebView2 de Windows. El cristal lo dibuja la propia página (`backdrop-filter`, `Assets\page-host.js`), de modo que la web sigue aislada. Los atajos con la web enfocada viajan por un canal que una página no puede falsificar (`Assets\page-keys.js`, mundo aislado de DevTools), y los permisos de sitios (cámara, micrófono, ubicación…) usan el aviso por sitio del motor.

Si el equipo no tiene el Runtime de WebView2, la aplicación cae a **CEF**, que **no dispone del sandbox de Chromium**: en ese modo se muestra un aviso permanente, bloquea permisos y ventanas emergentes sin gesto del usuario, y conviene usar solo páginas de prueba. `FLUXBROWSER_ENGINE=cef` o `=webview2` fuerza uno u otro; el registro (`logs\app.log`) indica cuál se eligió.

Lo que sigue pendiente antes de un lanzamiento público está en **Estado de lanzamiento**.

## Requisitos y ejecución

- Windows x64, Windows 10 versión 2004 o posterior. Recomendado: el Runtime de WebView2 (incluido en Windows 11 y con Microsoft Edge); sin él se usa CEF.
- SDK de .NET 10 compatible con `global.json`.
- Una GPU y controladores compatibles con Direct3D 11, texturas compartidas NT y fences de Direct3D 11. No hay alternativa de renderizado por CPU en este prototipo.

Desde PowerShell, en la carpeta del proyecto:

```powershell
dotnet build .\FluxBrowser\FluxBrowser.csproj -p:Platform=x64
.\FluxBrowser\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\FluxBrowser.exe
```

La aplicación abre **Nueva pestaña**, con búsqueda y accesos a favoritos, o restaura las pestañas normales guardadas. **Configuración** se abre en una pestaña independiente, sin reemplazar la web activa. La página de inicio del botón Inicio y la página de nuevas pestañas se configuran por separado. La barra de direcciones admite HTTP y HTTPS; el buscador predeterminado es configurable.

Al volver a ejecutar el mismo archivo, la instancia existente se activa y recupera si estaba minimizada; las ventanas normales y privadas adicionales se abren desde sus menús o atajos y comparten el proceso. Las copias ejecutadas desde rutas distintas pueden detectarse como aplicaciones diferentes: si otra copia ya usa el perfil, se muestra un error en lugar de abrir una ventana Chromium. Un inicio fallido no reemplaza la sesión guardada.

El menú principal permite abrir la página local de diagnóstico; también se admite `browser:diagnostics` en la barra de direcciones. En esa página, el botón **Probar desplazamiento** envía una rueda directamente a CEF para observar cómo sus colores y textos se difuminan detrás de las barras. No sustituye una prueba de los dispositivos físicos de entrada.

Para ejecutar las pruebas xUnit (direcciones, marcadores, ajustes, sesión, línea de comandos, almacenamiento y registro del navegador):

```powershell
dotnet test .\FluxBrowser.Tests\FluxBrowser.Tests.csproj
```

Las compilaciones de depuración admiten un guion de interfaz para probar sin enviar teclas ni clics al escritorio: `FLUXBROWSER_TEST_UI="suggest:wiki|find:Windows|zoom:1.25|menu"` ejecuta esos pasos cada pocos segundos (`FLUXBROWSER_TEST_UI_STEP` cambia el intervalo; la lista de pasos está en `MainWindow.DebugScript.cs`), y `FLUXBROWSER_TEST_SLEEP_SECONDS` acorta la espera de las pestañas en suspensión. Úsalo siempre con `FLUXBROWSER_PROFILE` apuntando a una carpeta desechable.

## Distribución portable

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish.ps1
```

El script ejecuta las pruebas, publica la aplicación autocontenida (no requiere instalar .NET) con ReadyToRun, elimina los idiomas que la interfaz no usa, **abre el ejecutable publicado como prueba de humo** (ventana principal y registro sin errores) y genera `artifacts\FluxBrowser-<versión>-win-x64.zip` con su suma SHA-256. Descomprime el zip en cualquier carpeta y ejecuta `FluxBrowser.exe`. Cierra la aplicación antes de lanzarlo (es de instancia única); `-SkipSmokeTest` omite esa comprobación.

La versión se define en `FluxBrowser\FluxBrowser.csproj` (`<Version>`). El icono se regenera con `tools\make-icon.ps1`. **El paquete no está firmado**: Windows SmartScreen avisará al ejecutarlo hasta que se firme con un certificado de firma de código.

### Instalación por usuario

Dentro del zip, `install.ps1` copia la aplicación a `%LOCALAPPDATA%\Programs\FluxBrowser`, crea el acceso directo del menú Inicio, registra el navegador (HTTP/HTTPS) y añade la entrada de «Aplicaciones instaladas»; no requiere administrador:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\FluxBrowser\uninstall.ps1"   # desinstalar (-RemoveData borra también el perfil)
```

Sin instalar, **Configuración → Navegador predeterminado → Registrar en Windows** (o `FluxBrowser.exe --register` / `--unregister`) hace que aparezca en la lista de navegadores de Windows. Windows no permite que una aplicación se fije a sí misma como predeterminada: lo eliges tú en «Aplicaciones predeterminadas».

## Línea de comandos, perfil y registro

- `FluxBrowser.exe https://example.com https://example.org` abre las direcciones HTTP/HTTPS indicadas, también cuando ya hay una ventana abierta (la instancia existente las recibe). Cualquier otro argumento (archivos, otros esquemas, opciones) se ignora.
- La variable de entorno `FLUXBROWSER_PROFILE` (ruta absoluta) sustituye la carpeta del perfil `%LOCALAPPDATA%\FluxBrowser`: sirve para instalaciones portables y para probar sin tocar los datos reales.
- Si Chromium deja de poder dibujar, la barra de avisos ofrece «Reiniciar navegador» (`--restart-after=PID --restore-session` internamente).
- Las pestañas restauradas se cargan al seleccionarlas (con el título guardado): arrancar con muchas pestañas no crea un proceso de renderizado por cada una.
- **Motor:** WebView2 es el predeterminado (con sandbox); `FLUXBROWSER_ENGINE=cef` fuerza el respaldo CEF, `=webview2` fuerza WebView2. `--private` abre la primera ventana como ventana privada. En modo CEF las cookies y la caché están en memoria salvo que actives «Mantener las sesiones iniciadas»; con WebView2 esa opción viene activada en los perfiles nuevos. Detalles y plan en `docs\MIGRACION-WEBVIEW2.md`.
- Los errores inesperados se guardan en `<perfil>\logs\app.log` (rotación a 1 MB). Las excepciones de operaciones asíncronas se registran y se muestran en la barra de avisos en lugar de cerrar la aplicación. Si la aplicación no arranca, el motivo se registra ahí.
- La sesión y el historial se escriben en segundo plano unos 400 ms después del último cambio (y siempre al cerrar), de modo que navegar no bloquea la interfaz con acceso a disco.

## Cómo funciona

CEF entrega cada fotograma en una textura compartida. Durante el callback se copia a una textura propia de Direct3D 11 y se espera un fence GPU antes de devolverla, porque CEF puede reutilizar la textura original. Un hilo de presentación independiente agrupa los fotogramas pendientes y ejecuta el desenfoque y `Present` fuera del callback. Todas las pestañas de una ventana comparten el dispositivo gráfico y el compositor, pero conservan superficies propias para evitar mezclar fotogramas al cambiar de pestaña.

Cuando no hay un popup de Chromium se dibuja directamente desde la textura de la página: se elimina la composición intermedia de pantalla completa. Cuando existe un popup, página y popup se componen primero para que ambos participen en el desenfoque. Las barras y los menús sobre la web utilizan regiones GPU con desenfoque, tinte y, en los menús, una textura pequeña de ruido reutilizada. Los controles WinUI se dibujan encima y permanecen nítidos.

El viewport nativo comienza debajo de las tres franjas superiores: pestañas (36 DIP), navegación (42 DIP) y favoritos (30 DIP, opcional). Un observador de desplazamiento de la página comunica métricas numéricas; el host amplía progresivamente el viewport hasta el borde superior y la página pasa por detrás de las tres franjas. No se insertan márgenes ni se modifica el CSS del sitio. El espacio revelado se limita en documentos cortos para evitar un ciclo entre cambio de tamaño y ajuste de la posición de desplazamiento.

La posición de dibujo y las coordenadas del puntero se calculan a partir de la textura recibida, no de un tamaño solicitado que Chromium todavía no haya dibujado. Popups y menús contextuales utilizan el mismo desplazamiento. Esta ruta necesita composición intermedia mientras exista un espacio superior; una vez revelada toda la superficie vuelve a la ruta directa cuando no hay popups.

**No se confía en `AcrylicBrush` para difuminar `SwapChainPanel`.** Para menús sobre la web el material se compone en GPU y se utiliza un fondo XAML translúcido; para menús sobre paneles XAML se utiliza Acrylic nativo. El contenido XAML de un panel no forma parte de la textura de Chromium.

No se usan capturas de pantalla ni copias continuas de los píxeles a la CPU para crear el efecto. El prototipo necesita una GPU compatible. Si la GPU se pierde, la aplicación ofrece «Reiniciar navegador» (también en Más herramientas): guarda las pestañas normales y las reabre en un proceso nuevo; no intenta reconstruir las superficies de Chromium en caliente.

## Funciones implementadas

- Pestañas horizontales redondeadas y verticales, selector de pestañas, creación, selección, cierre, duplicación, fijado, silencio, reordenación horizontal mediante arrastre o menú, cierre de otras pestañas y reapertura de pestañas normales cerradas. Los iconos se decodifican como imágenes (incluidos SVG y los formatos ICO PNG/DIB de 24/32 bits); si el sitio no publica uno se intenta `/favicon.ico` del mismo origen y, si no está disponible, se muestra un globo.
- Apertura y cierre animados de pestañas, menús, paneles, la barra de búsqueda y las sugerencias, con desvanecimiento, escala y desplazamiento cortos. Con WebView2 estas animaciones se ejecutan en el hilo del compositor (`Motion\Fx.cs`), así que no pierden fotogramas mientras la interfaz construye el contenido; con CEF se siguen calculando fotograma a fotograma para que el material GPU acompañe la opacidad y posición de los menús. Se pueden desactivar en Apariencia y se respeta la preferencia de animaciones de Windows.
- Todos los iconos responden al clic: se encogen al pulsar y hacen un gesto propio al activarse (atrás y adelante empujan, actualizar gira, descargas baja, inicio y nueva pestaña suben, historial y configuración se inclinan). Se anima el icono, no la placa del botón, y funciona igual con ratón, táctil o teclado.
- Indicadores en las pestañas: anillo de carga en lugar del icono mientras la página carga, e icono de altavoz cuando suena o está silenciada (un clic lo silencia o lo activa; también `Ctrl+Mayús+M`).
- Sugerencias en la barra de direcciones: al escribir se ofrecen lo que hará Entrar, las pestañas abiertas («Cambiar a esta pestaña»), los favoritos y el historial que coinciden, ordenado por frecuencia y antigüedad. Se recorren con las flechas, `Esc` las cierra. Se calculan en el equipo: no se envía nada a un buscador hasta que se busca. Se desactivan en Privacidad.
- Pestañas en suspensión (WebView2): una pestaña oculta libera memoria al minuto y su proceso se suspende unos cinco minutos después, salvo que reproduzca sonido, descargue o siga cargando; al volver a ella continúa donde estaba.
- Prevención de seguimiento del motor (WebView2) con tres niveles —básico, equilibrado y estricto— en Privacidad.
- Varias ventanas dentro del mismo proceso; ventanas privadas con contextos de cookies independientes en memoria por pestaña. No guardan historial ni sesión y no se añaden a la lista de pestañas cerradas. Esto no es anonimato ni aislamiento de seguridad.
- Menú principal y menús contextuales de páginas, pestañas y marcadores, con iconos en cada acción, separación por grupos y navegación de teclado. Los submenús nativos de Chromium conservan sus acciones y estados de selección; se recorren con una opción de vuelta sin cancelar el menú antes de elegir una acción. En pestañas verticales también hay un botón de opciones.
- Marcadores persistentes con carpetas anidadas, creación y cambio de nombre de carpetas, edición de marcadores y traslado entre carpetas. Los menús permiten abrir carpetas y sus enlaces en pestañas y abrir las opciones de cada favorito con clic derecho. Eliminar una carpeta y sus descendientes requiere confirmación. Los archivos antiguos sin carpetas siguen siendo válidos.
- Importación y exportación de favoritos HTML de tipo Netscape, desde Perfiles, el panel de Favoritos o el menú de Nueva pestaña. Se conserva la jerarquía, incluidas las carpetas vacías. La importación añade una carpeta fechada sin sustituir los favoritos existentes, informa de los elementos omitidos y solo acepta enlaces HTTP/HTTPS sin credenciales. Límites: 10 MB, 20.000 elementos y 64 niveles.
- Paneles compactos de favoritos, historial y descargas, anclados al extremo derecho. Historial con búsqueda, horas, iconos y pestañas normales cerradas recientemente en esta ventana. Las visitas se comparten entre ventanas normales, con eliminación confirmada y un máximo de 2.000 entradas.
- Descargas con progreso, cancelación, carpeta configurable mediante selector, opción de preguntar dónde guardar y apertura automática configurable del panel. Los nombres automáticos evitan sobrescrituras. El registro de descargas es por ventana y no se conserva al cerrarla; los archivos descargados permanecen en su carpeta. Ningún archivo se abre o ejecuta automáticamente.
- Buscar en la página con contador de coincidencias («3 de 236», WebView2) y `F3`/`Ctrl+G` para la siguiente; indicador de zoom en la barra de direcciones cuando la página no está al 100 % (un clic lo restablece). Zoom, impresión nativa, código fuente de páginas HTTP/HTTPS en una pestaña, DevTools y pantalla completa. Las pestañas de código fuente no se restauran como código fuente.
- Configuración con navegación lateral, búsqueda de opciones y contenido centrado de hasta 810 DIP. En ventanas estrechas la navegación se recoge en un botón. Un indicador se desliza entre categorías y el contenido cambia con una transición; admite clic derecho. Incluye categorías de perfiles, contraseñas/autorrelleno, privacidad, apariencia, navegador predeterminado, inicio, idiomas, descargas, accesibilidad, rendimiento, restablecimiento, extensiones y diagnóstico. Los servicios no implementados se explican sin interruptores ficticios.
- Ajustes persistentes de tema, desenfoque, opacidades, animaciones, botones de herramientas, barra de favoritos, pestañas verticales, aviso antes de cerrar varias pestañas, páginas personalizadas de inicio, buscador, zoom y fuentes. También idiomas preferidos, corrector y JavaScript; estos ajustes del motor se aplican al crear pestañas. Los cambios se guardan automáticamente (sin botón Guardar); los campos numéricos vacíos o inválidos no se guardan hasta corregirse. Restablecer requiere confirmación y conserva favoritos, historial y descargas.
- Tema claro, oscuro o del sistema en barras, pestañas, menús, paneles, diálogos y páginas internas. Se comunica `prefers-color-scheme` a Chromium: los sitios que lo admiten adoptan ese tema, sin invertir imágenes ni forzar CSS sobre páginas que no lo soportan. Los perfiles nuevos usan opacidad 0,90 en menús; se respetan los ajustes guardados de perfiles existentes.
- Disposición tomada de Edge: barra con Atrás, Adelante (solo si hay historial), Actualizar, Inicio, candado/información del sitio, estrella de favoritos y botones de Favoritos, Historial, Descargas y «Configuración y más». La dirección usa texto de 14 DIP centrado verticalmente; el campo se centra en la ventana y ajusta su anchura al texto y al espacio disponible, ampliándose con animación al editar. Menú principal en el orden de Edge con submenús «Más herramientas» y «Ayuda y comentarios», atajos visibles y un icono por opción.
- Nueva pestaña con saludo, buscador, imagen del día de Bing (con su crédito) y accesos rápidos: los sitios más visitados más los fijados, con agregar y quitar. Admite menú contextual tanto en el fondo como en los accesos, también mediante `Mayús+F10`. Barra de favoritos vacía con el texto y el enlace de Edge.
- Acciones de pestañas (`Ctrl+Mayús+A`): alternar pestañas verticales, organizar por sitio, reabrir, buscar entre pestañas abiertas y cerradas recientemente. Donde el menú cubre las barras usa acrílico opaco, para que no se transparenten sin desenfoque.
- Captura de pantalla de la página (`Ctrl+Mayús+S`, se guarda en Imágenes\FluxBrowser y se copia al portapapeles), «Guardar como» (`Ctrl+S`, archivo completo MHTML mediante selector nativo) y eliminación selectiva de datos de navegación (`Ctrl+Mayús+Supr`): historial, cookies, caché y pestañas cerradas.

La interfaz sigue la geometría de Windows 11: esquinas de 4 DIP en controles (botones, filas, campos) y de 8 DIP en superficies que flotan (menús, paneles, tarjetas), que además llevan sombra; texto de menús de 12 DIP, filas de 32 DIP e iconos de 14–16 DIP en una columna común de 16 DIP. Los botones de las barras comparten un único estilo «sutil» (sin placa en reposo ni deshabilitados, la misma placa tenue al pasar el ratón) definido una vez en `Motion\Fx.cs`. Los menús sobre Chromium tienen recorte redondeado y material GPU; sobre superficies XAML se utiliza `AcrylicBrush`. No se incluyen botones ficticios de servicios o extensiones de Microsoft.

Atajos principales: `Ctrl+T`, `Ctrl+W`, `Ctrl+Mayús+T`, `Ctrl+Tab`, `Ctrl+Mayús+Tab`, `Ctrl+1…9`, `Ctrl+N`, `Ctrl+Mayús+N`, `Ctrl+L`, `Ctrl+D`, `Ctrl+H`, `Ctrl+J`, `Ctrl+F`, `Ctrl+P`, `Ctrl+Mayús+B`, `Ctrl+0`, `Ctrl++`, `Ctrl+-`, `F5`, `F6`, `F11`, `F12`, `Ctrl+R`, `Ctrl+U`, `Ctrl+S`, `Ctrl+Mayús+O`, `Ctrl+Mayús+A`, `Ctrl+Mayús+S`, `Ctrl+Mayús+I`, `Ctrl+Mayús+Supr`, `Alt+F`, `Alt+Inicio`, `Alt+D`, `Ctrl+RePág`/`Ctrl+AvPág` (pestaña anterior/siguiente), `F3`/`Ctrl+G` (siguiente coincidencia), `Ctrl+Mayús+M` (silenciar pestaña) y `Ctrl+Mayús+K` (duplicar pestaña). Una pestaña fijada debe desfijarse antes de cerrarla.

## Desplazamiento y rendimiento

La rueda se reenvía a Chromium, que anima cada rendija con su curva de desplazamiento suave, sin temporizadores propios de WinUI. Los touchpads de precisión se envían con `EVENTFLAG_PRECISION_SCROLLING_DELTA`, para que desplacen píxel a píxel sin animación añadida, como en Edge. El ajuste de desplazamiento suave requiere cerrar todas las ventanas y volver a abrir la aplicación.

La página de diagnóstico registra la trayectoria de `scrollY` en cada fotograma tras una rueda y la muestra junto al título. Se comparó con Edge, en la misma página y con el mismo delta de 360:

| Medida | Edge | Flux Browser |
| --- | --- | --- |
| Distancia | 300 px | 300 px |
| 50 % del recorrido | 67 ms | 69 ms |
| 90 % del recorrido | 108 ms | 110 ms |
| Fin del movimiento | 400 ms | 395 ms |

En Edge, la rueda se envió mediante la automatización de Windows; en este navegador, mediante **Probar desplazamiento**, que entrega el mismo delta directamente a CEF. La latencia de la entrada física y la del ratón real no se incluyen en esta medición. La ruta del touchpad está implementada, pero no se ha probado con un dispositivo físico.

Se ofrecen objetivos de 30, 60, 90, 120, 144, 165 y 240 FPS; los perfiles nuevos solicitan 120 FPS. La documentación XML de CefSharp conserva un máximo antiguo de 60 FPS, pero el [código de la revisión instalada de CEF](https://github.com/chromiumembedded/cef/blob/14c5a08/libcef/browser/osr/osr_util.cc) ya no impone ese máximo y el [binding instalado](https://github.com/cefsharp/CefSharp/blob/v154.0.340/CefSharp.Core.Runtime/Internals/CefBrowserHostWrapper.cpp) reenvía el ajuste al motor. **Solicitar una frecuencia no garantiza alcanzarla ni presentarla en el monitor.**

Arranque (WebView2): el proceso del motor se inicia en cuanto se leen los ajustes, en paralelo a la construcción de la ventana; si el perfil debe vaciarse, se renombra y se borra en segundo plano. El registro anota `Arranque: ventana lista a los N ms` y `primera página a los N ms` para poder medirlo.

En **Configuración → Sistema y rendimiento** se activan las pestañas en suspensión (WebView2) o, con CEF, se muestran mediciones y se pausa el renderizado de pestañas ocultas. Con CEF esto no garantiza suspender scripts, audio o descargas, ni liberar toda la memoria de esas pestañas. El compositor permanece esperando un evento cuando no tiene trabajo, sin un bucle de presentación continuo.

Con la página local abierta, **Medir desplazamiento durante 5 segundos** la selecciona, envía ruedas periódicas a CEF y calcula los fotogramas recibidos y los tiempos medios de esa ventana de medición. Los tiempos de copia incluyen la espera del fence y la contención del compositor; el tiempo de dibujo es de CPU/envío de comandos, **no una medición de ejecución GPU ni del refresco físico de pantalla**. El contador puede mostrar cero fotogramas/s en una página inmóvil sin que exista un fallo. Esta medición de carga es distinta de la comparación de trayectorias con Edge descrita arriba y no sustituye una prueba de vídeo/WebGL bajo carga.

En la validación local con una RTX 4060 Ti, dos pestañas abiertas y disposición vertical, el mismo diagnóstico de cinco segundos recibió **58,4 fotogramas/s con objetivo de 60 FPS** y **141,0 fotogramas/s con objetivo de 144 FPS**. La copia y espera promediaron 0,91 y 0,46 ms respectivamente; el dibujo CPU, 0,26 y 0,25 ms. Son dos muestras del caso local, no garantías universales ni mediciones de presentación física o equivalencia con Edge.

## Limitaciones importantes

- **El comportamiento todavía no es equivalente a Safari en sitios arbitrarios.** Se ha corregido el espacio inicial mediante el viewport nativo, pero ampliarlo puede producir reflujo en sitios adaptativos. Los desplazadores internos que no mueven `window.scrollY`, aplicaciones con scroll personalizado, elementos `fixed/sticky`, zoom y restauración de posición necesitan más pruebas de compatibilidad. La página de prueba ya no reserva espacio artificial arriba.
- No es una reproducción completa de Chrome, Edge o Safari ni una copia exacta píxel por píxel de las capturas. El [catálogo oficial de Edge](https://explore.microsoft.com/en-us/edge/features) incluye funciones y servicios que no proporciona CEF por sí mismo. Faltan grupos de pestañas, áreas de trabajo, pantalla dividida, varios perfiles persistentes, sincronización, gestor seguro de contraseñas/passkeys, extensiones, traducción, lector enriquecido, servicios de Copilot/Rewards, protección contra rastreadores y actualizaciones del motor. No se añaden opciones ficticias para simular esas funciones.
- La entrada básica de ratón, rueda y teclado está implementada, pero requiere validación interactiva adicional. IME, entrada táctil, arrastrar y soltar, teclado virtual y accesibilidad del contenido web no están integrados.
- Los objetivos altos del motor no equivalen a una experiencia verificada de 120/144 Hz en todas las máquinas. Faltan mediciones sistemáticas de latencia de entrada, consumo, presentación física, parpadeos y rendimiento bajo carga.
- Faltan pruebas con distintas GPU, varias pantallas y escalas DPI, vídeo, WebGL y páginas complejas. La copia GPU conserva sincronización explícita y puede bloquear temporalmente ante un controlador lento.

## Estado de lanzamiento

Esta compilación es una **vista previa técnica portable**. Con WebView2 como motor predeterminado ya tiene sandbox y actualizaciones del motor a través del Runtime; lo que falta antes de un lanzamiento público:

1. **Pruebas con dispositivos físicos** que aquí no se pudieron hacer: teclado físico, IME, táctil, arrastrar y soltar, varios monitores y escalas de pantalla distintas de 100 %, y un lector de pantalla real. Las comprobaciones hechas fueron automatizadas (UI Automation, DevTools y mensajes dirigidos a la ventana de la propia aplicación).
2. **Firma de código**. El zip y los ejecutables no están firmados (SmartScreen avisará). El instalador por usuario y el registro como navegador existen (ver Distribución portable); Windows sigue exigiendo que el usuario confirme el navegador predeterminado.
3. **Zoom por CSS** (decisión tomada): los sitios con `vw` o anchos fijos se desbordan en horizontal al ampliar. Ctrl+rueda no hace zoom.
4. **Respaldo CEF sin sandbox** (decisión del propietario: se mantiene) para equipos sin el Runtime de WebView2. Muestra un aviso permanente y bloquea permisos y ventanas emergentes sin gesto; el precio es un paquete grande (`libcef.dll` pesa 276 MB sin comprimir).
5. **Licencia del código propio**: pendiente de decisión del propietario. Los avisos de las dependencias se incluyen en [third-party](docs/third-party).

## Datos y privacidad

Los marcadores, ajustes, historial y sesión se guardan en texto claro en `%LOCALAPPDATA%\FluxBrowser` (`bookmarks.json`, `settings.json`, `history.json`, `session.json`); las URL pueden contener información sensible. Las escrituras usan archivos temporales y reemplazo del archivo final. Los archivos inválidos se notifican y no se sustituyen silenciosamente. La caché web está en memoria salvo que actives «Mantener las sesiones iniciadas»; en cualquier caso CEF mantiene archivos de funcionamiento y un registro en `%LOCALAPPDATA%\FluxBrowser\PrototypeCef`. Borrar el historial no elimina marcadores, sesión ni descargas.
