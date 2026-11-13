# Microsoft Store

**English** · [Español](MICROSOFT-STORE.md)

Flux Browser uses a desktop x64 MSIX, WinUI 3, WebView2 Evergreen and the `runFullTrust` desktop capability. Windows lets users select the packaged app for HTTP and HTTPS. The manifest targets Windows 10 2004 and later.

1. Reserve Flux Browser in [Partner Center](https://partner.microsoft.com/dashboard) and copy the package identity, publisher and publisher display name.
2. Run `pwsh -File tools/publish.ps1 -SkipZip`, then `pwsh -File tools/package-store.ps1 -IdentityName "IDENTITY" -Publisher "CN=PUBLISHER" -PublisherDisplayName "DISPLAY NAME"`. Upload the resulting `FluxBrowser-0.9.2-store-x64.msix`. The `-LocalPreview` option creates a package with a test identity.
3. Test install, uninstall, launching from Start, HTTP/HTTPS activation in a new and existing instance, browsing, site permissions and downloads. Run the Windows App Certification Kit on a test machine.
4. Complete availability, IARC, the Store listing and certification notes. Microsoft signs the MSIX after certification.

## English listing

**Name** · Flux Browser

**Publisher** · World Marketing Labs, subject to the verified Partner Center display name.

**Description** · A Windows browser with translucent glass bars, horizontal and vertical tabs, light and dark themes, and a customizable new-tab page. Organize bookmarks into folders and import or export HTML. View history and manage downloads from side panels. Open private windows, find text within a page and adjust tracking prevention.

**Features** · Vertical tabs, HTML bookmarks, private windows, light and dark themes, find in page, history and downloads.

**Keywords** · web browser, vertical tabs, bookmarks, Chromium.

**Website and support** · [World Marketing Labs](https://www.worldmarketinglabs.com/en) · [GitHub Issues](https://github.com/cbatista96/FluxBrowser/issues).

**Privacy** · [Flux Browser privacy policy](https://github.com/cbatista96/FluxBrowser/blob/main/docs/Privacy.md).

**Images** · Use package logos from `store/Assets` and the new tab, settings, vertical tabs, privacy and About captures from the [gallery](capturas/README.md). These captures show the Spanish interface.

## Certification notes

No sign-in required. Launch the app, enter `https://example.com` and press Enter. Try the toolbar panels for bookmarks, history and downloads. Use Settings → Appearance to change the theme and tab layout. WebView2 Evergreen is required. The desktop capability supports WinUI 3, HTTP/HTTPS integration and file pickers. Windows Settings controls the default-browser choice.
