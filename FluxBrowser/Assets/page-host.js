// Injected by the host into every top-level document (AddScriptToExecuteOnDocumentCreated).
//  - Glass: the page draws the blur behind the host's translucent bars and menus with backdrop-filter, so Chromium composites it
//    on the GPU inside the sandboxed renderer. The host sends rectangles in DIP plus its rasterization scale;
//    devicePixelRatio = scale * browser zoom, so css px per DIP = scale / devicePixelRatio keeps it aligned at any zoom.
//  - Gap: when the web view fills the whole window (content scrolls under the bars) the page keeps its first `gap` DIP free with
//    padding on the root element. Page, glass and scrollbar live in the same renderer frame, so nothing can drift apart while scrolling.
//  - Scrollbar: the native root scrollbar always spans the full view, so it is hidden and redrawn from `gap` down to the bottom edge.
//  - Zoom: CSS zoom on the root element (the host's WebView2 control exposes no real page zoom).
//  - Scroll metrics: the host decides between "full view" and "below the bars" from the natural document height.
// Everything the page can post is treated as untrusted by the host.
(() => {
  if (window.top !== window || !window.chrome || !window.chrome.webview) return;
  const post = (m) => window.chrome.webview.postMessage(m);
  let st = { zoom: 1, scale: 1, blur: 12, dark: true, gap: 0, regions: [] };
  const seed = /*MOB_INIT*/null;
  if (seed && typeof seed === 'object') st = Object.assign(st, seed);

  const BAR = 14;        // scrollbar track width, DIP
  const THUMB = 6;       // idle thumb width, DIP
  const THUMB_ON = 10;   // thumb width while hovered or dragged
  const MIN_THUMB = 32;  // shortest thumb, DIP

  let host = null, root = null, track = null, thumb = null;
  const nodes = new Map();

  // css px (before page zoom) per DIP. The host lives inside the zoomed root, so the zoom is divided back out.
  const k = () => st.scale / window.devicePixelRatio / st.zoom;
  // css px of the viewport per DIP (what getBoundingClientRect / clientY use).
  const v = () => st.scale / window.devicePixelRatio;
  const scroller = () => document.scrollingElement || document.documentElement;

  // ---------- Glass ----------

  const ensure = () => {
    if (host && host.isConnected) return true;
    if (!document.documentElement) return false;
    host = document.createElement('div');
    host.id = '__mob_glass';
    host.style.cssText = 'all:initial;position:fixed;left:0;top:0;width:100%;height:100%;pointer-events:none;z-index:2147483647;';
    root = host.attachShadow({ mode: 'closed' });
    document.documentElement.appendChild(host);
    nodes.clear();
    track = thumb = null;
    return true;
  };

  // The bars are opaque while the page sits at the very top (nothing but the gap is behind them) and turn into glass as the content
  // moves under them: the tint's alpha follows the scroll position inside the same frame.
  const paintTint = (n) => {
    const m = /^(rgba\([^)]*,\s*)([\d.]+)\)$/.exec(n._tint || '');
    if (!m) { n.style.background = n._tint || 'rgba(255,255,255,0.3)'; return; }
    let a = parseFloat(m[2]);
    if (n._reveal && st.gap > 0) {
      const t = Math.min(1, Math.max(0, (scroller().scrollTop * st.zoom) / (st.gap * v())));
      a = a + (1 - a) * (1 - t);
    }
    n.style.background = m[1] + a + ')';
  };

  const layoutGlass = () => {
    const live = new Set();
    for (const r of st.regions) {
      const id = String(r.id || '');
      live.add(id);
      let n = nodes.get(id);
      const fresh = !n;
      if (fresh) {
        n = document.createElement('div');
        n.style.cssText = 'position:absolute;'; // present from the document's first frame: a fade-in would flash the bars on every navigation
        root.insertBefore(n, track);
        nodes.set(id, n);
      }
      const s = n.style, f = k();
      s.left = r.x * f + 'px'; s.top = r.y * f + 'px';
      s.width = r.w * f + 'px'; s.height = r.h * f + 'px';
      s.borderRadius = (r.r || 0) * f + 'px';
      const blur = `blur(${st.blur * f}px) saturate(1.15)`;
      s.backdropFilter = blur; s.webkitBackdropFilter = blur;
      n._tint = r.tint; n._reveal = !!r.reveal;
      paintTint(n);
      s.opacity = r.v === undefined ? 1 : r.v;
    }
    for (const [id, n] of nodes) if (!live.has(id)) { n.remove(); nodes.delete(id); }
  };

  const paintReveal = () => { for (const n of nodes.values()) if (n._reveal) paintTint(n); };

  // ---------- Root layout: gap, zoom ----------

  const sheet = (() => { try { return new CSSStyleSheet(); } catch { return null; } })();
  let sheetRule = '';
  const applySheet = () => {
    if (!sheet) return;
    const g = st.gap * k();
    const rule = 'html{scrollbar-width:none!important;' + (st.gap > 0
      ? `padding-top:${g}px!important;box-sizing:border-box!important;scroll-padding-top:${g}px!important;` : '') + '}';
    // Constructed stylesheets are exempt from the page's inline-style CSP. An unchanged rule is left alone: rewriting it restyles the page.
    if (rule !== sheetRule) { sheetRule = rule; sheet.replaceSync(rule); }
    if (!document.adoptedStyleSheets.includes(sheet)) document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
  };

  const applyZoom = () => {
    const el = document.documentElement;
    if (!el) return;
    if (st.zoom === 1) { el.style.removeProperty('zoom'); el.style.removeProperty('width'); return; }
    el.style.zoom = String(st.zoom);
    el.style.removeProperty('width');
    // Chromium's current zoom model already lays the root out at viewport/zoom. The older one lays it out at the viewport's width and
    // draws it larger, so the root has to be narrowed by hand; measure instead of assuming which model this runtime uses.
    if (el.getBoundingClientRect().width > window.innerWidth * 1.02) el.style.width = (100 / st.zoom) + '%';
  };

  // ---------- Scrollbar ----------

  let hover = false, drag = null, repeat = null;

  // A neutral grey reads on light and dark pages alike (the page's own colours are not known here); the faint ring keeps it visible on black.
  const colors = () => ({ thumb: 'rgba(128,128,128,.62)', thumbOn: 'rgba(104,104,104,.9)', track: 'rgba(128,128,128,.16)' });

  const buildBar = () => {
    if (track && track.isConnected) return;
    track = document.createElement('div');
    track.style.cssText = 'position:fixed;right:0;pointer-events:auto;touch-action:none;user-select:none;-webkit-user-select:none;display:none;transition:background-color .12s;';
    thumb = document.createElement('div');
    thumb.style.cssText = 'position:absolute;right:0;border-radius:999px;box-shadow:0 0 0 1px rgba(255,255,255,.14);transition:width .12s,background-color .12s;';
    track.appendChild(thumb);
    root.appendChild(track);
    track.addEventListener('pointerenter', () => { hover = true; paintBar(); });
    track.addEventListener('pointerleave', () => { hover = false; paintBar(); });
    track.addEventListener('pointerdown', onDown);
    track.addEventListener('pointermove', onMove);
    track.addEventListener('pointerup', onUp);
    track.addEventListener('pointercancel', onUp);
    track.addEventListener('lostpointercapture', onUp);
  };

  const paintBar = () => {
    if (!thumb) return;
    const c = colors(), f = 1 / st.zoom, active = hover || drag;
    track.style.backgroundColor = active ? c.track : 'transparent';
    thumb.style.backgroundColor = active ? c.thumbOn : c.thumb;
    const w = (active ? THUMB_ON : THUMB) * v() * f;
    thumb.style.width = w + 'px';
    thumb.style.marginRight = ((BAR - (active ? THUMB_ON : THUMB)) / 2) * v() * f + 'px';
  };

  const layoutBar = () => {
    if (!ensure()) return;
    buildBar();
    const se = scroller(), sh = se.scrollHeight, ch = se.clientHeight;
    if (!(sh > ch + 1)) { track.style.display = 'none'; return; }
    const f = 1 / st.zoom, s = v();
    const top = st.gap * s;                       // viewport css px
    const room = window.innerHeight - top;        // track length, viewport css px
    if (room < 40 * s) { track.style.display = 'none'; return; }
    const len = Math.min(room, Math.max(MIN_THUMB * s, room * ch / sh));
    const pos = (se.scrollTop / (sh - ch)) * (room - len);
    track.style.display = 'block';
    track.style.top = top * f + 'px';
    track.style.width = BAR * s * f + 'px';
    track.style.height = room * f + 'px';
    thumb.style.height = len * f + 'px';
    thumb.style.top = Math.max(0, Math.min(room - len, pos)) * f + 'px';
    paintBar();
  };

  const onDown = (e) => {
    if (e.button !== 0) return;
    e.preventDefault(); e.stopPropagation();
    const t = thumb.getBoundingClientRect();
    if (e.target === thumb) {
      drag = { y: e.clientY, top: scroller().scrollTop };
      track.setPointerCapture(e.pointerId);
      paintBar();
      return;
    }
    // Track click: page by page toward the pointer, repeating while the button stays down.
    const dir = e.clientY < t.top ? -1 : 1;
    const y = e.clientY;
    const step = () => {
      const se = scroller(), cur = thumb.getBoundingClientRect();
      if (y >= cur.top && y <= cur.bottom) return stop();
      se.scrollTop += dir * se.clientHeight * 0.875;
    };
    const stop = () => { if (repeat) { clearTimeout(repeat.t); clearInterval(repeat.i); repeat = null; } };
    stop();
    track.setPointerCapture(e.pointerId);
    step();
    repeat = { t: setTimeout(() => { repeat.i = setInterval(step, 90); }, 380), i: 0 };
  };

  const onMove = (e) => {
    if (!drag) return;
    const se = scroller(), tr = track.getBoundingClientRect(), th = thumb.getBoundingClientRect();
    const room = tr.height - th.height;
    if (room <= 0) return;
    se.scrollTop = drag.top + ((e.clientY - drag.y) / room) * (se.scrollHeight - se.clientHeight);
  };

  const onUp = () => {
    if (repeat) { clearTimeout(repeat.t); clearInterval(repeat.i); repeat = null; }
    if (drag) { drag = null; paintBar(); }
  };

  // ---------- Top-anchored elements stay below the bars ----------
  // Fixed or sticky headers, side rails and menus pinned at top:0 are positioned against the viewport, which here starts under the bars.
  // Left alone they would sit hidden behind the glass (and unclickable), even with the page at its very top. Each one that carries
  // links or controls is moved down by the gap, so it stays where a browser with opaque bars would draw it.

  const shifted = new Map(); // element -> { top: original px, value/priority: its inline top before we touched it }
  let scanTimer = 0, lastScan = 0, scanToken = 0;

  const inScrollContainer = (el) => {
    for (let p = el.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
      const cs = getComputedStyle(p);
      if (cs.overflowY !== 'visible' || cs.overflowX !== 'visible') return true;
    }
    return false;
  };

  // A fixed element inside a transformed, filtered or contained ancestor is positioned against that ancestor, not the viewport.
  const viewportAnchored = (el) => {
    for (let p = el.parentElement; p && p !== document.documentElement; p = p.parentElement) {
      const cs = getComputedStyle(p);
      if (cs.transform !== 'none' || cs.filter !== 'none' || cs.perspective !== 'none' || cs.backdropFilter && cs.backdropFilter !== 'none' ||
          /paint|layout|strict|content/.test(cs.contain) || /transform|filter/.test(cs.willChange)) return false;
    }
    return true;
  };

  // The page's own layout puts these at "top" px from the viewport, which here starts under the bars: all of them move down by the gap,
  // so the page keeps its own geometry (a side rail at top:56px still meets the header that sits at top:0) and nothing it pins is
  // hidden or mis-stacked behind the glass. Tops the page writes inline at run time are usually computed from other elements' positions
  // (pop-ups, menus), which already include the gap, so only an inline top of 0 counts.
  const wantsShift = (el, cs) => {
    const pos = cs.position;
    if (pos !== 'fixed' && pos !== 'sticky' && pos !== 'absolute') return false;
    if (cs.top === 'auto') return false;
    const top = parseFloat(cs.top);
    const inlineTop = el.style.getPropertyValue('top') !== '';
    const limit = pos === 'absolute' || inlineTop ? 2 : innerHeight * 0.4;
    if (!(top >= 0 && top <= limit)) return false;
    if (cs.display === 'none' || cs.visibility === 'hidden') return false;
    const r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) return false;
    // Modal backdrops and full-screen layers cover the bars on purpose.
    if (pos !== 'absolute' && r.width >= innerWidth * 0.95 && r.height >= innerHeight * 0.95) return false;
    if (pos === 'absolute') {
      // Only page-level boxes: positioned against the initial containing block, so they start under the bars too. That is a header
      // or navigation bar, or the app's whole root (sites such as YouTube lay the page out in one absolutely positioned element).
      const op = el.offsetParent;
      if (op && op !== document.body && op !== document.documentElement) return false;
      if (op === document.body && getComputedStyle(document.body).position !== 'static') return false;
      const tag = el.tagName;
      const landmark = tag === 'HEADER' || tag === 'NAV' || tag === 'ASIDE' || el.getAttribute('role') === 'banner' || el.getAttribute('role') === 'navigation';
      const appRoot = r.width >= innerWidth * 0.6 && r.height >= innerHeight * 0.6;
      return landmark || appRoot;
    }
    if (pos === 'sticky') return !inScrollContainer(el);
    return viewportAnchored(el);
  };

  const shiftValue = (o, g) => `calc(${o.top}px + ${g}px)`;

  const saved = (el, prop) => ({ value: el.style.getPropertyValue(prop), priority: el.style.getPropertyPriority(prop) });
  const put = (el, prop, s) => { el.style.removeProperty(prop); if (s.value) el.style.setProperty(prop, s.value, s.priority); };

  // A rail that runs down to the bottom edge (a side menu sized "100vh - header") would lose its last `gap` px below the window once
  // moved down: it is stretched between its new top and the bottom edge instead.
  const fill = (el) => { el.style.setProperty('bottom', '0px', 'important'); el.style.setProperty('height', 'auto', 'important'); };

  const restoreShifted = () => {
    for (const [el, o] of shifted) {
      put(el, 'top', o);
      if (o.fill) { put(el, 'bottom', o.bottom); put(el, 'height', o.height); }
    }
    shifted.clear();
  };

  const scanTop = () => {
    scanTimer = 0;
    lastScan = Date.now();
    if (st.gap <= 0) { restoreShifted(); return; }
    const g = st.gap * k();
    for (const [el, o] of shifted) {
      if (!el.isConnected) shifted.delete(el);
      else { el.style.setProperty('top', shiftValue(o, g), 'important'); if (o.fill) fill(el); }
    }
    // The page may have thousands of elements: look at them in short slices so the page never stalls.
    const token = ++scanToken;
    const all = document.getElementsByTagName('*');
    let i = 0;
    const slice = (deadline) => {
      if (token !== scanToken || st.gap <= 0) return;
      const until = Date.now() + 8;
      while (i < all.length && (Date.now() < until || (deadline && deadline.timeRemaining && deadline.timeRemaining() > 1))) {
        const el = all[i++];
        if (shifted.has(el) || (host && (el === host || host.contains(el)))) continue;
        const cs = getComputedStyle(el);
        if (cs.position === 'static' || cs.position === 'relative') continue;
        if (!wantsShift(el, cs)) continue;
        const o = { top: parseFloat(cs.top), ...saved(el, 'top') };
        const r = el.getBoundingClientRect();
        if (cs.position === 'fixed' && r.bottom >= innerHeight - 2 && r.height >= innerHeight * 0.5) {
          o.fill = true; o.bottom = saved(el, 'bottom'); o.height = saved(el, 'height');
          fill(el);
        }
        shifted.set(el, o);
        el.style.setProperty('top', shiftValue(o, g), 'important');
      }
      if (i < all.length) (window.requestIdleCallback || ((f) => setTimeout(f, 16)))(slice);
    };
    slice();
  };

  const scheduleScan = (delay = 350) => {
    if (scanTimer) return;
    scanTimer = setTimeout(scanTop, Math.max(delay, 800 - (Date.now() - lastScan)));
  };

  let scanKey = '';
  const rescanIfNeeded = () => {
    const key = st.gap + '|' + st.zoom + '|' + st.scale;
    if (key === scanKey) return;
    scanKey = key;
    if (st.gap <= 0) restoreShifted(); else scheduleScan(60);
  };

  // ---------- Host messages and metrics ----------

  // The bars are drawn by the host over the page. Wherever the host lets a pointer through (a gap between its own controls), the page
  // must not get it: content scrolled under the glass would otherwise be clicked, hovered or right-clicked through the bars.
  // The wheel still reaches the document, so scrolling over the bars keeps working.
  let shield = null;
  const layoutShield = () => {
    if (!shield || !shield.isConnected) {
      shield = document.createElement('div');
      shield.style.cssText = 'position:absolute;left:0;top:0;width:100%;pointer-events:auto;background:transparent;';
      for (const type of ['click', 'auxclick', 'dblclick', 'contextmenu', 'mousedown', 'mouseup', 'pointerdown', 'pointerup'])
        shield.addEventListener(type, (e) => { e.preventDefault(); e.stopPropagation(); });
      root.insertBefore(shield, root.firstChild);
    }
    const h = st.gap * k();
    shield.style.height = h + 'px';
    shield.style.display = h > 0 ? 'block' : 'none';
  };

  const layout = () => {
    if (!ensure()) return;
    applySheet();
    layoutShield();
    layoutGlass();
    layoutBar();
    rescanIfNeeded();
  };

  let pending = false;
  const sendMetrics = () => {
    pending = false;
    const el = document.documentElement;
    // The scroll height includes the gap the page reserves itself; the host wants the natural height of the content.
    const natural = Math.max(el.scrollHeight - (st.gap > 0 ? st.gap * k() : 0), (document.body && document.body.scrollHeight) || 0);
    post({ kind: 'viewport', y: window.scrollY, height: natural });
  };
  const schedule = () => { if (!pending) { pending = true; requestAnimationFrame(sendMetrics); } };

  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    if (!m || typeof m !== 'object' || m.kind !== 'state') return;
    st = {
      zoom: typeof m.zoom === 'number' && m.zoom > 0.1 && m.zoom < 10 ? m.zoom : 1,
      scale: m.scale || 1, blur: m.blur || 12, dark: m.dark !== false,
      gap: typeof m.gap === 'number' && m.gap >= 0 && m.gap < 1000 ? m.gap : 0,
      regions: Array.isArray(m.regions) ? m.regions : []
    };
    applyZoom();
    layout();
    schedule();
  });

  const onScroll = () => { layoutBar(); paintReveal(); schedule(); };
  window.addEventListener('resize', () => { layout(); schedule(); });
  window.addEventListener('scroll', onScroll, { passive: true });

  const start = () => { ensure(); applyZoom(); layout(); };
  applySheet();
  if (document.documentElement) start();
  else new MutationObserver((_, o) => { if (document.documentElement) { o.disconnect(); start(); } }).observe(document, { childList: true });
  document.addEventListener('DOMContentLoaded', () => {
    new ResizeObserver(() => { layoutBar(); schedule(); }).observe(document.documentElement);
    // Headers are often built or restyled after load: look again when the page changes, but never more than about once a second.
    new MutationObserver((records) => {
      if (st.gap > 0 && records.some((r) => !(r.type === 'attributes' && r.attributeName === 'style' && shifted.has(r.target)))) scheduleScan(450);
    }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden'] });
    layout(); schedule(); scheduleScan(120);
  });
  window.addEventListener('load', () => { layoutBar(); schedule(); scheduleScan(200); });
  post({ kind: 'hello' });
})();
