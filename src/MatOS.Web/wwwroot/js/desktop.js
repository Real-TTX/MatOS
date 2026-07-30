/* matOS desktop controller — stacks-as-apps, free-placement icons, taskbar + Start menu */
(function () {
  "use strict";

  const CUBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#c7cffc" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05"/><path d="M12 22.08V12"/></svg>';
  const GLOBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#9fe8ff" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.7 2.5 15.3 0 18M12 3c-2.5 2.7-2.5 15.3 0 18"/></svg>';
  const CELL_W = 100, CELL_H = 116, MARGIN = 16, ICON_W = 88;

  function esc(s) { return String(s == null ? "" : s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }
  function escAttr(s) { return String(s == null ? "" : s).replace(/"/g, "&quot;"); }

  // ---- clock (tray) ----
  const clock = document.getElementById("mat-clock");
  function tick() {
    if (!clock) return;
    const d = new Date();
    clock.innerHTML = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) +
      "<br>" + d.toLocaleDateString([], { day: "2-digit", month: "2-digit", year: "numeric" });
  }
  tick(); setInterval(tick, 15000);

  // ---- user menu (tray) ----
  const userBtn = document.getElementById("mat-user-btn");
  const userMenu = document.getElementById("mat-user-menu");
  if (userBtn) {
    userBtn.addEventListener("click", (e) => { e.stopPropagation(); userMenu.classList.toggle("open"); });
    document.addEventListener("click", (e) => { if (!userMenu.contains(e.target) && e.target !== userBtn) userMenu.classList.remove("open"); });
  }

  // ---- launch helpers ----
  function iconForKey(key) {
    if (!key) return null;
    if (key.startsWith("stack:") || key.startsWith("container:") || key.startsWith("set:")) return CUBE;
    const s = systemApps().find(a => a.key === key);
    return s ? s.iconHtml : null;
  }
  function open(opts) { if (!opts.iconHtml && opts.key) opts.iconHtml = iconForKey(opts.key); window.MatWM.open(opts); }
  function launchEl(el) {
    open({ key: el.dataset.key || el.dataset.url, title: el.dataset.title || "App", icon: el.dataset.icon || null,
      url: el.dataset.url, width: parseInt(el.dataset.w || "1024", 10), height: parseInt(el.dataset.h || "680", 10) });
  }
  document.addEventListener("click", (e) => {
    const opener = e.target.closest("[data-open-url]");
    if (opener) {
      open({ key: opener.dataset.openKey || opener.dataset.openUrl, title: opener.dataset.openTitle || "App", url: opener.dataset.openUrl, width: 1040, height: 680 });
      closeStart(); if (userMenu) userMenu.classList.remove("open");
    }
  });

  // ---- stacks (a stack = the app) ----
  let stackData = [];
  const stackKey = s => "stack:" + s.name;
  const primaryWeb = s => (s.containers || []).find(c => c.hasWebUi && c.running && c.appUrl);
  function stackSettingsUrl(s) {
    return (s.standalone && s.containers[0]) ? "/apps/container/" + encodeURIComponent(s.containers[0].id)
                                             : "/apps/stack/" + encodeURIComponent(s.name);
  }
  function stackOpenUrl(s) { const w = primaryWeb(s); return w ? w.appUrl : stackSettingsUrl(s); }
  function stackInner(s) {
    const dot = s.anyRunning ? "on" : "off";
    const web = primaryWeb(s) ? `<span class="mat-web-badge" title="Has web UI">${GLOBE}</span>` : "";
    const count = (!s.standalone && s.total > 1) ? `<span class="mat-count-badge" title="${s.running}/${s.total} running">${s.running}/${s.total}</span>` : "";
    return `${CUBE}<span class="mat-state-dot ${dot}"></span>${web}${count}`;
  }

  // ---- free-placement icon field ----
  const layer = document.getElementById("mat-desktop-icons");
  const iconEls = new Map();
  const systemKeys = new Set();
  let layout = {}, defaultIndex = 0, dragging = false;

  function systemApps() {
    return Array.from(document.querySelectorAll("#mat-system-apps .mat-app")).map(el => ({
      key: el.dataset.key, title: el.dataset.title, url: el.dataset.url,
      w: el.dataset.w || "1040", h: el.dataset.h || "680",
      iconHtml: (el.querySelector(".mat-app-icon") || {}).innerHTML || "", sys: true
    }));
  }
  function slotToXY(i) {
    const h = layer.clientHeight || (window.innerHeight - 60);
    const perCol = Math.max(1, Math.floor((h - MARGIN) / CELL_H));
    return { x: MARGIN + Math.floor(i / perCol) * CELL_W, y: MARGIN + (i % perCol) * CELL_H };
  }
  function clampXY(x, y) {
    const lw = layer.clientWidth || window.innerWidth, lh = layer.clientHeight || window.innerHeight;
    return { x: Math.max(0, Math.min(x, Math.max(0, lw - ICON_W))), y: Math.max(0, Math.min(y, Math.max(0, lh - CELL_H))) };
  }
  function snapXY(x, y) {
    const c = clampXY(x, y);
    return { x: Math.max(0, MARGIN + Math.round((c.x - MARGIN) / CELL_W) * CELL_W),
             y: Math.max(0, MARGIN + Math.round((c.y - MARGIN) / CELL_H) * CELL_H) };
  }
  function applyPos(el, key) { const p = layout[key] || slotToXY(defaultIndex++); const c = clampXY(p.x, p.y); el.style.left = c.x + "px"; el.style.top = c.y + "px"; }

  async function saveIcon(key, x, y) {
    try { await fetch("/api/v1/desktop/icon", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key, x, y }) }); } catch (_) {}
  }
  function enableDrag(el, key) {
    let sx, sy, ox, oy, moved = false, active = false;
    el.addEventListener("pointerdown", (e) => {
      if (e.pointerType === "mouse" && e.button !== 0) return;
      active = true; moved = false; sx = e.clientX; sy = e.clientY;
      ox = parseFloat(el.style.left) || 0; oy = parseFloat(el.style.top) || 0;
      try { el.setPointerCapture(e.pointerId); } catch (_) {}
    });
    el.addEventListener("pointermove", (e) => {
      if (!active) return;
      const dx = e.clientX - sx, dy = e.clientY - sy;
      if (!moved && Math.hypot(dx, dy) > 5) { moved = true; dragging = true; el.classList.add("dragging"); }
      if (moved) { el.style.left = (ox + dx) + "px"; el.style.top = (oy + dy) + "px"; }
    });
    const end = (e) => {
      if (!active) return; active = false;
      try { el.releasePointerCapture(e.pointerId); } catch (_) {}
      if (moved) {
        el.classList.remove("dragging");
        const s = snapXY(parseFloat(el.style.left), parseFloat(el.style.top));
        el.style.left = s.x + "px"; el.style.top = s.y + "px";
        layout[key] = s; saveIcon(key, s.x, s.y);
        setTimeout(() => { dragging = false; }, 60);
      } else { launchEl(el); }
    };
    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", end);
  }
  function makeIcon(key, title, url, w, h, iconClass, inner) {
    const el = document.createElement("button");
    el.className = "mat-app";
    el.dataset.key = key; el.dataset.title = title; el.dataset.url = url; el.dataset.w = w; el.dataset.h = h;
    el.innerHTML = `<div class="mat-app-icon ${iconClass}">${inner}</div><span class="mat-app-label">${esc(title)}</span>`;
    layer.appendChild(el); iconEls.set(key, el);
    applyPos(el, key); enableDrag(el, key);
    return el;
  }
  function buildSystem() { for (const a of systemApps()) { systemKeys.add(a.key); makeIcon(a.key, a.title, a.url, a.w, a.h, "sys", a.iconHtml); } }

  function reconcileStacks() {
    if (dragging) return;
    const wanted = new Map(stackData.map(s => [stackKey(s), s]));
    for (const [key, el] of iconEls) { if (systemKeys.has(key)) continue; if (!wanted.has(key)) { el.remove(); iconEls.delete(key); } }
    for (const s of stackData) {
      const key = stackKey(s); let el = iconEls.get(key);
      if (!el) { el = makeIcon(key, s.name, stackOpenUrl(s), "1024", "680", "", stackInner(s)); el.dataset.stackName = s.name; }
      else { el.dataset.url = stackOpenUrl(s); const ico = el.querySelector(".mat-app-icon"); if (ico) ico.innerHTML = stackInner(s); }
    }
  }
  async function autoArrange() {
    try { await fetch("/api/v1/desktop/reset", { method: "POST" }); } catch (_) {}
    layout = {}; defaultIndex = 0; for (const [key, el] of iconEls) applyPos(el, key);
  }

  async function load() {
    try {
      const res = await fetch("/api/v1/docker/stacks", { headers: { "Accept": "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json(); stackData = data.stacks || [];
    } catch (_) { stackData = []; }
    reconcileStacks();
    renderStartMenu(startSearch ? startSearch.value : "");
  }
  async function stackAction(name, action) { try { await fetch(`/api/v1/docker/stacks/${encodeURIComponent(name)}/${action}`, { method: "POST" }); } catch (_) {} setTimeout(load, 700); }

  // ---- Start menu ----
  const startBtn = document.getElementById("mat-start-btn");
  const startMenu = document.getElementById("mat-startmenu");
  const startSearch = document.getElementById("mat-start-search");
  const smSystem = document.getElementById("sm-system");
  const smApps = document.getElementById("sm-containers");
  const smAppsTitle = document.getElementById("sm-container-title");
  const smEmpty = document.getElementById("sm-empty");
  if (smAppsTitle) smAppsTitle.textContent = "Apps";

  function smItem(a, iconClass, inner) {
    return `<button class="sm-item" data-key="${escAttr(a.key)}" data-title="${escAttr(a.title)}" data-url="${escAttr(a.url)}" data-w="${a.w}" data-h="${a.h}">
      <span class="sm-ico ${iconClass}">${inner}</span><span class="sm-label">${esc(a.title)}</span></button>`;
  }
  function renderStartMenu(filter) {
    if (!startMenu) return;
    const q = (filter || "").trim().toLowerCase();
    const match = t => !q || (t || "").toLowerCase().includes(q);
    const sys = systemApps().filter(a => match(a.title));
    smSystem.innerHTML = sys.map(a => smItem(a, "sys", a.iconHtml)).join("");
    const apps = stackData.filter(s => match(s.name)).map(s => ({ key: stackKey(s), title: s.name, url: stackOpenUrl(s), w: "1024", h: "680", inner: stackInner(s) }));
    smApps.innerHTML = apps.map(a => smItem(a, "", a.inner)).join("");
    smAppsTitle.style.display = apps.length ? "" : "none";
    smEmpty.hidden = (sys.length + apps.length) > 0;
  }
  function openStart() { if (!startMenu) return; startMenu.hidden = false; startBtn.classList.add("active"); if (startSearch) { startSearch.value = ""; setTimeout(() => startSearch.focus(), 20); } renderStartMenu(""); }
  function closeStart() { if (!startMenu || startMenu.hidden) return; startMenu.hidden = true; startBtn.classList.remove("active"); }
  if (startBtn) {
    startBtn.addEventListener("click", (e) => { e.stopPropagation(); startMenu.hidden ? openStart() : closeStart(); });
    document.addEventListener("click", (e) => { if (!startMenu.hidden && !startMenu.contains(e.target) && !startBtn.contains(e.target)) closeStart(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") { closeStart(); hideCtx(); } });
    startSearch.addEventListener("input", () => renderStartMenu(startSearch.value));
    startSearch.addEventListener("keydown", (e) => { if (e.key === "Enter") { const f = startMenu.querySelector(".sm-item"); if (f) { e.preventDefault(); f.click(); } } });
    startMenu.addEventListener("click", (e) => { const it = e.target.closest(".sm-item"); if (it) { e.preventDefault(); launchEl(it); closeStart(); } });
  }

  // ---- Right-click context menu ----
  const ctx = document.createElement("div"); ctx.id = "mat-ctx"; ctx.hidden = true; document.body.appendChild(ctx);
  function hideCtx() { ctx.hidden = true; ctx.innerHTML = ""; }
  function showCtx(x, y, items) {
    ctx.innerHTML = "";
    for (const it of items) {
      if (it.sep) { const s = document.createElement("div"); s.className = "mat-ctx-sep"; ctx.appendChild(s); continue; }
      const b = document.createElement("button"); b.className = "mat-ctx-item" + (it.danger ? " danger" : ""); b.textContent = it.label;
      b.addEventListener("click", () => { hideCtx(); it.action(); }); ctx.appendChild(b);
    }
    ctx.hidden = false;
    ctx.style.left = Math.min(x, window.innerWidth - ctx.offsetWidth - 8) + "px";
    ctx.style.top = Math.min(y, window.innerHeight - ctx.offsetHeight - 8) + "px";
  }
  if (layer) {
    layer.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      const iconEl = e.target.closest(".mat-app");
      if (iconEl && !systemKeys.has(iconEl.dataset.key)) {
        const s = stackData.find(x => x.name === iconEl.dataset.stackName);
        const items = [{ label: "Open", action: () => launchEl(iconEl) }];
        if (s) {
          items.push({ label: "Settings", action: () => open({ key: "set:" + s.name, title: "Settings · " + s.name, url: stackSettingsUrl(s), width: 1024, height: 680 }) });
          items.push({ sep: true });
          if (s.anyRunning) { items.push({ label: "Stop", action: () => stackAction(s.name, "stop") }); items.push({ label: "Restart", action: () => stackAction(s.name, "restart") }); }
          if (!s.allRunning) items.push({ label: "Start", action: () => stackAction(s.name, "start") });
        }
        items.push({ sep: true }, { label: "Auto-arrange icons", action: autoArrange });
        showCtx(e.clientX, e.clientY, items);
      } else if (iconEl) {
        showCtx(e.clientX, e.clientY, [{ label: "Open", action: () => launchEl(iconEl) }, { sep: true }, { label: "Auto-arrange icons", action: autoArrange }]);
      } else {
        showCtx(e.clientX, e.clientY, [{ label: "Auto-arrange icons", action: autoArrange }, { label: "Refresh", action: load }]);
      }
    });
    document.addEventListener("click", () => hideCtx());
    document.addEventListener("scroll", hideCtx, true);
    window.addEventListener("resize", hideCtx); window.addEventListener("blur", hideCtx);
  }

  // ---- init ----
  async function init() {
    try { const r = await fetch("/api/v1/desktop/layout", { headers: { Accept: "application/json" } }); if (r.ok) { const d = await r.json(); layout = d.positions || {}; } }
    catch (_) { layout = {}; }
    buildSystem(); await load(); setInterval(load, 15000);
  }
  init();

  // ---- messages from app windows ----
  window.addEventListener("message", (e) => {
    if (e.origin !== location.origin) return;
    const m = e.data; if (!m) return;
    if (m.type === "matos:wallpaper" && m.wallpaper) { const wp = document.getElementById("mat-wallpaper"); if (wp) wp.className = "wp-" + m.wallpaper; }
    if (m.type === "matos:open" && m.url) { open({ key: m.key || m.url, title: m.title || "App", url: m.url, width: m.width || 1024, height: m.height || 680 }); }
  });
})();
