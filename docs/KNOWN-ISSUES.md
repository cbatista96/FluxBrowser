# Known issues

| Area | Status |
| --- | --- |
| Zoom | CSS zoom can cause horizontal scrolling on sites with fixed widths or `vw` units. Ctrl+wheel does not change zoom. |
| CEF | Experimental fallback in the GitHub build. It runs without Chromium's sandbox and requires a compatible GPU. Use WebView2 for everyday browsing. |
| Input and accessibility | Physical keyboard, IME, touch, screen reader, multi-monitor and display scaling still need broader testing. |
| Downloads | Files stay on disk. The download panel clears its list when the window closes. |
| Windows signing | The preview EXE awaits Authenticode signing. GitHub attestation and SHA-256 are available for downloads. |

[Report an issue](https://github.com/cbatista96/FluxBrowser/issues/new/choose). If helpful, attach a reviewed excerpt of `%LOCALAPPDATA%\FluxBrowser\logs\app.log`. See the [changelog](../CHANGELOG.en.md) for fixes by version.
