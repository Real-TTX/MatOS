/* matOS desktop controller — free-placement icons, taskbar + Start menu */
(function () {
  "use strict";

  const CUBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#c7cffc" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05"/><path d="M12 22.08V12"/></svg>';
  const GLOBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#9fe8ff" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.7 2.5 15.3 0 18M12 3c-2.5 2.7-2.5 15.3 0 18"/></svg>';

  const CELL_W = 100, CELL_H = 116, MARGIN = 16, ICON_W = 88;

  function esc(s) { return String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }
  function escAttr(s) { return String(s).replace(/"/g, "&quot;"); }

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
  function open(opts) { window.MatWM.open(opts); }
  function launchEl(el) {
    open({
      key: el.dataset.key || el.dataset.url, title: el.dataset.title || "App", icon: el.dataset.icon || null,
      url: el.dataset.url, width: parseInt(el.dataset.w || "1024", 10), height: parseInt(el.dataset.h || "680", 10)
    });
  }

  // [data-open-url] controls (tray menu items, start-menu footer)
  document.addEventListener("click", (e) => {
    const opener = e.target.closest("[data-open-url]");
    if (opener) {
      open({ key: opener.dataset.openKey || opener.dataset.openUrl, title: opener.dataset.openTitle || "App", url: opener.dataset.openUrl, width: 1040, height: 680 });
      closeStart(); if (userMenu) userMenu.classList.remove("open");
    }
  });

  // ---- shared container helpers ----
  let containerData = [];
  function displayName(c) { return c.name || c.shortId; }
  function containerUrl(c) { return (c.hasWebUi && c.running && c.appUrl) ? c.appUrl : ("/apps/task-manager?focus=" + encodeURIComponent(c.id)); }
  function containerInner(c) {
    const dot = c.running ? "on" : "off";
    const web = c.hasWebUi ? `<span class="mat-web-badge" title="Has web UI">${GLOBE}</span>` : "";
    return `${CUBE}<span class="mat-state-dot ${dot}" title="${escAttr(c.state)}"></span>${web}`;
  }

  // ---- desktop free-icon field ----
  const layer = document.getElementById("mat-desktop-icons");
  const iconEls = new Map();      // key -> element
  const systemKeys = new Set();
  let layout = {};                // key -> {x,y}
  let defaultIndex = 0;
  let dragging = false;

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
    const col = Math.floor(i / perCol), row = i % perCol;
    return { x: MARGIN + col * CELL_W, y: MARGIN + row * CELL_H };
  }

  function clampXY(x, y) {
    const lw = layer.clientWidth || window.innerWidth, lh = layer.clientHeight || window.innerHeight;
    return { x: Math.max(0, Math.min(x, Math.max(0, lw - ICON_W))), y: Math.max(0, Math.min(y, Math.max(0, lh - CELL_H))) };
  }
  function snapXY(x, y) {
    const c = clampXY(x, y);
    return {
      x: Math.max(0, MARGIN + Math.round((c.x - MARGIN) / CELL_W) * CELL_W),
      y: Math.max(0, MARGIN + Math.round((c.y - MARGIN) / CELL_H) * CELL_H)
    };
  }

  function applyPos(el, key) {
    const p = layout[key] || slotToXY(defaultIndex++);
    const c = clampXY(p.x, p.y);
    el.style.left = c.x + "px"; el.style.top = c.y + "px";
  }

  async function saveIcon(key, x, y) {
    try {
      await fetch("/api/v1/desktop/icon", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ key, x, y })
      });
    } catch (_) { /* best effort */ }
  }

  function enableDrag(el, key) {
    let sx, sy, ox, oy, moved = false, active = false;
    el.addEventListener("pointerdown", (e) => {
      if (e.pointerType === "mouse" && e.button !== 0) return; // let right-click open ctx menu
      active = true; moved = false;
      sx = e.clientX; sy = e.clientY;
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
      } else {
        launchEl(el); // treated as a click
      }
    };
    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", end);
  }

  function makeIcon(key, title, url, w, h, iconClass, inner) {
    const el = document.createElement("button");
    el.className = "mat-app";
    el.dataset.key = key; el.dataset.title = title; el.dataset.url = url; el.dataset.w = w; el.dataset.h = h;
    el.innerHTML = `<div class="mat-app-icon ${iconClass}">${inner}</div><span class="mat-app-label">${esc(title)}</span>`;
    layer.appendChild(el);
    iconEls.set(key, el);
    applyPos(el, key);
    enableDrag(el, key);
    return el;
  }

  function buildSystem() {
    for (const a of systemApps()) { systemKeys.add(a.key); makeIcon(a.key, a.title, a.url, a.w, a.h, "sys", a.iconHtml); }
  }

  function reconcileContainers() {
    if (dragging) return;
    const wanted = new Map(containerData.map(c => [c.id, c]));
    for (const [key, el] of iconEls) {
      if (systemKeys.has(key)) continue;
      if (!wanted.has(key)) { el.remove(); iconEls.delete(key); }
    }
    for (const c of containerData) {
      const el = iconEls.get(c.id);
      if (!el) { makeIcon(c.id, displayName(c), containerUrl(c), "1024", "680", "", containerInner(c)); }
      else {
        el.dataset.url = containerUrl(c);
        const ico = el.querySelector(".mat-app-icon"); if (ico) ico.innerHTML = containerInner(c);
      }
    }
  }

  async function autoArrange() {
    try { await fetch("/api/v1/desktop/reset", { method: "POST" }); } catch (_) {}
    layout = {}; defaultIndex = 0;
    for (const [key, el] of iconEls) applyPos(el, key);
  }

  // ---- container fetch loop ----
  async function load() {
    try {
      const res = await fetch("/api/v1/docker/containers", { headers: { "Accept": "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json();
      containerData = data.containers || [];
    } catch (_) { containerData = []; }
    reconcileContainers();
    renderStartMenu(startSearch ? startSearch.value : "");
  }

  // ---- Start menu ----
  const startBtn = document.getElementById("mat-start-btn");
  const startMenu = document.getElementById("mat-startmenu");
  const startSearch = document.getElementById("mat-start-search");
  const smSystem = document.getElementById("sm-system");
  const smContainers = document.getElementById("sm-containers");
  const smContainerTitle = document.getElementById("sm-container-title");
  const smEmpty = document.getElementById("sm-empty");

  function smItem(a, iconClass, inner) {
    return `<button class="sm-item" data-key="${escAttr(a.key)}" data-title="${escAttr(a.title)}" data-url="${escAttr(a.url)}" data-w="${a.w}" data-h="${a.h}">
      <span class="sm-ico ${iconClass}">${inner}</span><span class="sm-label">${esc(a.title)}</span></button>`;
  }
  function renderStartMenu(filter) {
    if (!startMenu) return;
    const q = (filter || "").trim().toLowerCase();
    const match = (t) => !q || (t || "").toLowerCase().includes(q);
    const sys = systemApps().filter(a => match(a.title));
    smSystem.innerHTML = sys.map(a => smItem(a, "sys", a.iconHtml)).join("");
    const cons = containerData.filter(c => match(displayName(c))).map(c => ({
      key: c.id, title: displayName(c), url: containerUrl(c), w: "1024", h: "680", inner: containerInner(c)
    }));
    smContainers.innerHTML = cons.map(a => smItem(a, "", a.inner)).join("");
    smContainerTitle.style.display = cons.length ? "" : "none";
    smEmpty.hidden = (sys.length + cons.length) > 0;
  }
  function openStart() {
    if (!startMenu) return;
    startMenu.hidden = false; startBtn.classList.add("active");
    if (startSearch) { startSearch.value = ""; setTimeout(() => startSearch.focus(), 20); }
    renderStartMenu("");
  }
  function closeStart() {
    if (!startMenu || startMenu.hidden) return;
    startMenu.hidden = true; startBtn.classList.remove("active");
  }
  if (startBtn) {
    startBtn.addEventListener("click", (e) => { e.stopPropagation(); startMenu.hidden ? openStart() : closeStart(); });
    document.addEventListener("click", (e) => { if (!startMenu.hidden && !startMenu.contains(e.target) && !startBtn.contains(e.target)) closeStart(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") { closeStart(); hideCtx(); } });
    startSearch.addEventListener("input", () => renderStartMenu(startSearch.value));
    startSearch.addEventListener("keydown", (e) => { if (e.key === "Enter") { const f = startMenu.querySelector(".sm-item"); if (f) { e.preventDefault(); f.click(); } } });
    startMenu.addEventListener("click", (e) => { const it = e.target.closest(".sm-item"); if (it) { e.preventDefault(); launchEl(it); closeStart(); } });
  }

  // ---- Right-click context menu ----
  const ctx = document.createElement("div");
  ctx.id = "mat-ctx"; ctx.hidden = true; document.body.appendChild(ctx);
  function hideCtx() { ctx.hidden = true; ctx.innerHTML = ""; }
  function showCtx(x, y, items) {
    ctx.innerHTML = "";
    for (const it of items) {
      if (it.sep) { const s = document.createElement("div"); s.className = "mat-ctx-sep"; ctx.appendChild(s); continue; }
      const b = document.createElement("button");
      b.className = "mat-ctx-item" + (it.danger ? " danger" : "");
      b.textContent = it.label;
      b.addEventListener("click", () => { hideCtx(); it.action(); });
      ctx.appendChild(b);
    }
    ctx.hidden = false;
    const w = ctx.offsetWidth, h = ctx.offsetHeight;
    ctx.style.left = Math.min(x, window.innerWidth - w - 8) + "px";
    ctx.style.top = Math.min(y, window.innerHeight - h - 8) + "px";
  }
  async function containerAction(id, action) {
    try { await fetch(`/api/v1/docker/containers/${id}/${action}`, { method: "POST" }); } catch (_) {}
    setTimeout(load, 600);
  }
  if (layer) {
    layer.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      const iconEl = e.target.closest(".mat-app");
      if (iconEl) {
        const key = iconEl.dataset.key;
        const items = [{ label: "Open", action: () => launchEl(iconEl) }];
        if (!systemKeys.has(key)) {
          const c = containerData.find(x => x.id === key);
          items.push({ sep: true });
          if (c && c.running) {
            items.push({ label: "Stop", action: () => containerAction(key, "stop") });
            items.push({ label: "Restart", action: () => containerAction(key, "restart") });
          } else {
            items.push({ label: "Start", action: () => containerAction(key, "start") });
          }
          items.push({ label: "Open Task Manager", action: () => open({ key: "task-manager", title: "Task Manager", url: "/apps/task-manager?focus=" + encodeURIComponent(key), width: 1040, height: 680 }) });
        }
        items.push({ sep: true }, { label: "Auto-arrange icons", action: autoArrange });
        showCtx(e.clientX, e.clientY, items);
      } else {
        showCtx(e.clientX, e.clientY, [
          { label: "Auto-arrange icons", action: autoArrange },
          { label: "Refresh", action: load }
        ]);
      }
    });
    document.addEventListener("click", () => hideCtx());
    document.addEventListener("scroll", hideCtx, true);
    window.addEventListener("resize", hideCtx);
    window.addEventListener("blur", hideCtx);
  }

  // ---- init ----
  async function init() {
    try {
      const r = await fetch("/api/v1/desktop/layout", { headers: { Accept: "application/json" } });
      if (r.ok) { const d = await r.json(); layout = d.positions || {}; }
    } catch (_) { layout = {}; }
    buildSystem();
    await load();
    setInterval(load, 15000);
  }
  init();

  // ---- live wallpaper preview from the Settings window ----
  window.addEventListener("message", (e) => {
    if (e.origin !== location.origin) return;
    const m = e.data;
    if (m && m.type === "matos:wallpaper" && m.wallpaper) {
      const wp = document.getElementById("mat-wallpaper");
      if (wp) wp.className = "wp-" + m.wallpaper;
    }
  });
})();
