# Fallos y limitaciones conocidos

| Área | Estado en 0.9.1 |
| --- | --- |
| Zoom | El zoom CSS puede provocar desplazamiento horizontal en sitios con anchuras fijas o unidades `vw`. Ctrl+rueda no cambia el zoom. |
| CEF | Respaldo experimental de la versión de GitHub. Funciona sin el sandbox de Chromium y necesita una GPU compatible. Usa WebView2 para navegación cotidiana. |
| Accesibilidad y entrada | Pendiente la matriz de teclado físico, IME, táctil, lector de pantalla, pantallas múltiples y escalas distintas de 100 %. |
| Descargas | Los archivos se conservan. El panel de descargas no conserva la lista después de cerrar la ventana. |
| Firma de Windows | El instalador de esta vista previa requiere una firma Authenticode de un publicador con identidad validada. SHA-256 permite comprobar la integridad de la descarga. |

Abre una incidencia desde [GitHub Issues](https://github.com/cbatista96/FluxBrowser/issues/new/choose). Adjunta un fragmento revisado de `%LOCALAPPDATA%\FluxBrowser\logs\app.log` si ayuda a reproducir el fallo. Consulta [CHANGELOG.md](../CHANGELOG.md) para ver las correcciones incluidas en cada versión.

