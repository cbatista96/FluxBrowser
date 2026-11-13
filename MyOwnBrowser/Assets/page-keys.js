// Runs in an ISOLATED world of every frame (Page.addScriptToEvaluateOnNewDocument with a worldName) and reports the browser's own
// shortcuts to the host through the CDP binding __mobKey. That binding exists only in this world, so page scripts can neither call
// it nor forge a shortcut: only genuine key presses (event.isTrusted) get through. A normal chrome.webview message could be
// forged by any site and used to close the user's tabs.
(() => {
  const send = window.__mobKey;
  if (typeof send !== 'function') return;

  const ctrlOnly = new Set(['t', 'w', 'n', 'l', 'f', 'g', 'd', 'h', 'j', 's', 'u', 'r', 'p', 'PageUp', 'PageDown', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '+', '=', '-', 'Tab']);
  const ctrlShift = new Set(['t', 'w', 'n', 'o', 'a', 's', 'i', 'r', 'b', 'm', 'k', 'g', 'Delete', 'Tab']);
  const altOnly = new Set(['f', 'd', 'Home', 'ArrowLeft', 'ArrowRight']);
  const bare = new Set(['F3', 'F5', 'F6', 'F11', 'F12']);

  const isShortcut = (e) => {
    if (!e.isTrusted || e.isComposing || e.repeat || e.metaKey) return false;
    if (e.ctrlKey && e.altKey) return false; // AltGr produces characters on many keyboard layouts
    const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    if (e.ctrlKey) return e.shiftKey ? ctrlShift.has(k) : ctrlOnly.has(k);
    if (e.altKey) return !e.shiftKey && altOnly.has(k);
    return !e.shiftKey && bare.has(k);
  };

  window.addEventListener('keydown', (e) => {
    const escape = e.isTrusted && e.key === 'Escape' && !e.ctrlKey && !e.altKey && !e.shiftKey;
    if (!escape && !isShortcut(e)) return;
    const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    send(JSON.stringify({ key, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey }));
    // Escape also belongs to the page (closing its own dialogs); real shortcuts do not.
    if (!escape) { e.preventDefault(); e.stopImmediatePropagation(); }
  }, true);
})();
