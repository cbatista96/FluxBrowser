# Privacidad de My Own Browser

My Own Browser guarda el perfil en el equipo. El perfil puede incluir ajustes, marcadores, historial, pestañas abiertas, cookies, caché y un registro de diagnóstico. El registro se rota a partir de 1 MB y puede contener rutas o datos de navegación. La aplicación no incorpora telemetría propia ni envía automáticamente el perfil o el registro a su desarrollador.

Los sitios que abres reciben los datos propios de la conexión y pueden guardar cookies o solicitar permisos. Las búsquedas se envían al proveedor que elijas al ejecutarlas. Si la imagen del día está activa, Nueva pestaña consulta a Bing para descargar el fondo y su crédito. WebView2 se distribuye y actualiza mediante Microsoft y está sujeto a su [declaración de privacidad](https://privacy.microsoft.com/privacystatement).

Las ventanas privadas no añaden entradas al historial ni restauran sus pestañas al reiniciar. Los archivos descargados, las capturas y los favoritos que guardes expresamente pueden permanecer en el equipo. Los sitios visitados y los servicios de red pueden observar sus propias conexiones.

En Configuración → Privacidad, búsqueda y servicios puedes borrar datos de navegación. Para eliminar el perfil local completo en la versión de GitHub, cierra el navegador y elimina `%LOCALAPPDATA%\MyOwnBrowser` o la carpeta que hayas configurado mediante `MYOWNBROWSER_PROFILE`. Desinstalar el EXE conserva el perfil. Si envías un informe voluntario a GitHub, revisa antes las capturas y registros que adjuntes.

Contacto de privacidad a través de [un informe privado de GitHub](https://github.com/cbatista96/MyOwnBrowser/security/advisories/new).

