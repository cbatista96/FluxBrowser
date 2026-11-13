// Injected into every top-level document by the host (AddScriptToExecuteOnDocumentCreated).
// The page draws the glass: Chromium composites backdrop-filter on the GPU inside the sandboxed renderer.
// The host sends rectangles in DIP plus its own rasterization scale; devicePixelRatio = scale * zoom, so
// css px per DIP = scale / devicePixelRatio, which keeps the glass aligned and the blur constant at any page zoom.
(() => {
  if (window.top !== window || !window.chrome || !window.chrome.webview) return;
  const post = (m) => window.chrome.webview.postMessage(m);
  let host = null, root = null, regions = [], scale = 1, blurDip = 12;
  const nodes = [];

  const ensure = () => {
    if (host && host.isConnected) return true;
    if (!document.documentElement) return false;
    host = document.createElement('div');
    host.id = '__mob_glass';
    host.style.cssText = 'all:initial;position:fixed;left:0;top:0;width:100%;height:100%;pointer-events:none;z-index:2147483647;';
    root = host.attachShadow({ mode: 'closed' });
    document.documentElement.appendChild(host);
    nodes.length = 0;
    return true;
  };

  const layout = () => {
    if (!ensure()) return;
    const k = scale / window.devicePixelRatio;
    while (nodes.length < regions.length) {
      const n = document.createElement('div');
      n.style.position = 'absolute';
      root.appendChild(n);
      nodes.push(n);
    }
    while (nodes.length > regions.length) nodes.pop().remove();
    regions.forEach((r, i) => {
      const s = nodes[i].style;
      s.left = r.x * k + 'px'; s.top = r.y * k + 'px';
      s.width = r.w * k + 'px'; s.height = r.h * k + 'px';
      s.borderRadius = (r.r || 0) * k + 'px';
      s.opacity = r.v === undefined ? 1 : r.v;
      const f = `blur(${blurDip * k}px) saturate(1.15)`;
      s.backdropFilter = f; s.webkitBackdropFilter = f;
      s.background = r.tint || 'rgba(255,255,255,0.3)';
    });
  };

  const scrollTest = (seconds, pxPerSecond) => {
    const deltas = []; let last = 0; const start = performance.now(); let y = window.scrollY;
    const step = (t) => {
      if (last) deltas.push(t - last);
      const dt = last ? (t - last) / 1000 : 0; last = t;
      y += pxPerSecond * dt; window.scrollTo(0, y % 20000);
      if (t - start < seconds * 1000) requestAnimationFrame(step);
      else {
        deltas.sort((a, b) => a - b);
        const avg = deltas.reduce((a, b) => a + b, 0) / deltas.length;
        post({ type: 'perf', frames: deltas.length, avgMs: avg, p50Ms: deltas[deltas.length >> 1], p95Ms: deltas[Math.floor(deltas.length * 0.95)],
               maxMs: deltas[deltas.length - 1], over20: deltas.filter(d => d > 20).length, over34: deltas.filter(d => d > 34).length });
      }
    };
    requestAnimationFrame(step);
  };

  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    if (m.type === 'regions') {
      regions = m.regions || []; scale = m.scale || 1; blurDip = m.blur || 12;
      layout();
      post({ type: 'ack', id: m.id });
    } else if (m.type === 'measure') {
      const k = window.devicePixelRatio;
      post({ type: 'measure', id: m.id, dpr: k, rects: nodes.map(n => { const b = n.getBoundingClientRect(); return { x: b.left * k, y: b.top * k, w: b.width * k, h: b.height * k }; }) });
    } else if (m.type === 'scrollTest') scrollTest(m.seconds, m.speed);
  });
  window.addEventListener('resize', layout);
  if (document.documentElement) ensure();
  else new MutationObserver((_, o) => { if (document.documentElement) { o.disconnect(); ensure(); } }).observe(document, { childList: true });
})();
