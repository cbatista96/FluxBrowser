# Publicación en GitHub

1. Ejecuta `pwsh -File tools/publish.ps1` para probar, compilar y generar el ZIP autocontenido.
2. Compila el instalador con Inno Setup: `ISCC.exe /DAppVersion=0.9.2 installer/FluxBrowser.iss`. El EXE instala por usuario y registra HTTP y HTTPS. Windows pide al usuario confirmar el navegador predeterminado.
3. Para una publicación con firma Authenticode, usa un certificado de firma de código reconocido por Windows. Firma los ejecutables propios antes de construir el instalador y comprimir el ZIP. Firma el instalador después de compilarlo, con SHA-256 y un sello de tiempo RFC 3161. Verifica con `signtool verify /pa /v archivo.exe`. Consulta [opciones de firma](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options).
4. Calcula SHA-256 sobre los archivos finales. Crea una etiqueta `v<versión>`, crea un borrador en GitHub Releases y adjunta el instalador, ZIP y `SHA256SUMS.txt` antes de publicar. Detalla los cambios en [CHANGELOG.md](../CHANGELOG.md). GitHub firma una atestación para cada release inmutable. Verifica los archivos con `gh release verify-asset v0.9.2 archivo.exe -R cbatista96/FluxBrowser`. [Verificación de integridad](https://docs.github.com/en/enterprise-cloud@latest/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/verify-release-integrity).

La licencia del código queda pendiente de decisión del propietario.

Para preparar las notas en los dos idiomas, añade la misma versión a `CHANGELOG.en.md` y `CHANGELOG.md`. Ejecuta `pwsh -File tools/release-notes.ps1 -Version 0.9.2`, usa `artifacts/release-notes.md` como cuerpo de la release y adjunta `release-notes.en.md` y `release-notes.es.md`.

