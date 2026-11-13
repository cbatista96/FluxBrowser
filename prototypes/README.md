# Prototipos

Experimentos aislados. No forman parte del navegador ni de su compilación.

- `GlassSpike`: comprueba cómo desenfocar el contenido de WebView2 bajo barras de XAML y mide rendimiento, zoom, menús y páginas sin DOM (resultados en `docs/MIGRACION-WEBVIEW2.md`, secciones 11 y 12; evidencia en `GlassSpike/results`). Ejecutar: `dotnet run --project prototypes/GlassSpike -p:Platform=x64 -- --out <carpeta>`; ejecuta los escenarios solo y termina.
