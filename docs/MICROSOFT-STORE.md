# Microsoft Store

## Paquete

El paquete x64 usa Windows Desktop y `runFullTrust` para ejecutar WinUI 3. Usa WebView2 Evergreen para navegar. La instalación declara HTTP y HTTPS, y la selección de navegador predeterminado se hace en Configuración de Windows. El mínimo del manifiesto es Windows 10 2004. La ficha y las capturas están en español.

1. En [Partner Center](https://partner.microsoft.com/dashboard), reserva **Flux Browser**. Copia **Package/Identity/Name**, **Package/Identity/Publisher** y **Package/Properties/PublisherDisplayName** desde Identidad del producto.
2. Ejecuta `pwsh -File tools/publish.ps1 -SkipZip` y después `pwsh -File tools/package-store.ps1 -IdentityName "IDENTIDAD" -Publisher "CN=PUBLICADOR" -PublisherDisplayName "NOMBRE VISIBLE"`. Usa los valores exactos de Partner Center. El archivo para subir es `artifacts/FluxBrowser-0.9.0-store-x64.msix`. `-LocalPreview` produce un paquete para pruebas con una identidad distinta.
3. Valida instalación y desinstalación, inicio desde Inicio, apertura de HTTP/HTTPS en una instancia nueva y otra existente, navegación, permisos y descargas. Prueba con el [Windows App Certification Kit](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/resolve-submission-errors). Para la prueba local, usa una máquina de prueba y un certificado cuyo sujeto coincida con el manifiesto.
4. Completa disponibilidad, clasificación IARC, ficha en español y notas de certificación. Adjunta el MSIX con identidad de Partner Center y envía a certificación. Microsoft firma el MSIX al publicarlo tras la aprobación. [Requisitos de empaquetado y firma](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering?pivots=store-installer-msix).

## Ficha en español

**Nombre** · Flux Browser

**Publicador** · World Marketing Labs, sujeto al nombre verificado de Partner Center.

**Sitio web** · https://www.worldmarketinglabs.com/es

**Descripción** · Navegador para Windows con barras de cristal translúcido, pestañas horizontales y verticales, temas claro y oscuro y una página de nueva pestaña configurable. Organiza favoritos en carpetas e impórtalos o expórtalos en HTML. Consulta el historial y controla las descargas desde paneles laterales. Usa ventanas privadas, busca texto dentro de una página y ajusta la prevención de seguimiento.

**Funciones** · Pestañas verticales, favoritos HTML, ventanas privadas, temas claro y oscuro, búsqueda en página, historial y descargas.

**Palabras clave** · navegador web, pestañas verticales, favoritos, Chromium.

**Privacidad** · https://github.com/cbatista96/FluxBrowser/blob/main/docs/PRIVACIDAD.md

**Soporte** · https://github.com/cbatista96/FluxBrowser/issues

**Material gráfico** · Usa el logo de `store/Assets` para el paquete. Para la ficha, prioriza Nueva pestaña, Configuración, Pestañas verticales, Privacidad y Acerca de de [la galería](capturas/README.md). Son PNG de 1600 × 900. Partner Center admite hasta 10 capturas de escritorio. [Requisitos de las capturas](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images).

## Notas para certificación

No se requiere iniciar sesión. Abre la aplicación, escribe `https://example.com` y pulsa Entrar. Prueba los paneles de Favoritos, Historial y Descargas desde la barra. En Configuración → Apariencia puedes cambiar el tema y la disposición de pestañas. El paquete necesita WebView2 Evergreen. `runFullTrust` es necesario para el proceso WinUI 3 de escritorio, la integración de protocolos HTTP/HTTPS y los selectores de archivos. El usuario elige el navegador predeterminado en la configuración del sistema.

