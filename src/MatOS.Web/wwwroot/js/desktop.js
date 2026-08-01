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
    if (key.startsWith("stack:")) { const s = stackData.find(x => x.name === key.slice(6)); const d = s ? stackDef(s) : null; return (d && appIconHtml(d.icon)) || CUBE; }
    if (key.startsWith("container:") || key.startsWith("set:") || key.startsWith("act:")) return CUBE;
    const s = systemApps().find(a => a.key === key);
    return s ? s.iconHtml : null;
  }
  function open(opts) { if (!opts.iconHtml && opts.key) opts.iconHtml = iconForKey(opts.key); window.MatWM.open(opts); }
  function launchEl(el) {
    if (el.dataset.folderId) { openFolderOverlay(el); return; }
    open({ key: el.dataset.key || el.dataset.url, title: el.dataset.title || "App", icon: el.dataset.icon || null,
      url: el.dataset.url, width: parseInt(el.dataset.w || "1024", 10), height: parseInt(el.dataset.h || "680", 10) });
  }
  // Launch an app by its key (from inside the folder overlay etc.)
  function launchByKey(key) {
    if (key.startsWith("stack:")) {
      const s = stackData.find(x => x.name === key.slice(6)); if (!s) return;
      open({ key, title: stackTitle(s), url: stackOpenUrl(s), width: 1024, height: 680 });
    } else {
      const a = systemApps().find(x => x.key === key); if (!a) return;
      open({ key: a.key, title: a.title, url: a.url, width: parseInt(a.w||"1024",10), height: parseInt(a.h||"680",10), iconHtml: a.iconHtml });
    }
  }
  document.addEventListener("click", (e) => {
    const opener = e.target.closest("[data-open-url]");
    if (opener) {
      open({ key: opener.dataset.openKey || opener.dataset.openUrl, title: opener.dataset.openTitle || "App", url: opener.dataset.openUrl, width: 1040, height: 680 });
      closeStart(); if (userMenu) userMenu.classList.remove("open");
    }
  });

  // ---- stacks (a stack = the app) + app definitions ----
  let stackData = [];
  let appDefs = {}; // id -> app definition (icon, name, actions)
  let pins = new Set(); // app keys pinned to the desktop (nothing shows by default; user pins from Start menu)
  let folders = [];   // [{id,name,keys:[...]}]
  let widgets = [];   // [{id,type,x,y,w,h,config}]
  const pendingInstalls = new Map(); // installId -> { appId, name, icon }
  const stackKey = s => "stack:" + s.name;
  const primaryWeb = s => (s.containers || []).find(c => c.hasWebUi && c.running && c.appUrl);
  function folderContainingKey(key){ for (const f of folders) if (f.keys.includes(key)) return f; return null; }

  async function loadDefs() {
    try { const d = await (await fetch("/api/v1/store/catalog")).json(); const m = {}; for (const a of d.apps) m[a.id] = a; appDefs = m; } catch (_) {}
  }
  function appIconHtml(icon) {
    if (!icon) return null;
    if (/^(https?:|data:)/i.test(icon)) return `<img src="${escAttr(icon)}" alt="">`;
    return `<span class="mat-emoji">${esc(icon)}</span>`;
  }
  function stackApp(s) { const c = (s.containers || []).find(x => x.matosApp); return c ? c.matosApp : null; }
  function stackDef(s) { const a = stackApp(s); return a ? appDefs[a] : null; }
  function stackTitle(s) {
    const c = (s.containers || []).find(x => x.matosTitle);
    if (c && c.matosTitle) return c.matosTitle;
    const d = stackDef(s); return d ? d.name : s.name;
  }

  function stackSettingsUrl(s) {
    const first = (s.containers || [])[0];
    if (stackApp(s) && first) return "/apps/app/" + encodeURIComponent(first.id); // matOS app -> App settings
    return (s.standalone && first) ? "/apps/container/" + encodeURIComponent(first.id)
                                   : "/apps/stack/" + encodeURIComponent(s.name);
  }
  function stackOpenUrl(s) { const w = primaryWeb(s); return w ? w.appUrl : stackSettingsUrl(s); }
  function stackInner(s) {
    const d = stackDef(s);
    const base = (d && appIconHtml(d.icon)) || CUBE;
    const dot = s.anyRunning ? "on" : "off";
    const web = primaryWeb(s) ? `<span class="mat-web-badge" title="Has web UI">${GLOBE}</span>` : "";
    const count = (!s.standalone && s.total > 1) ? `<span class="mat-count-badge" title="${s.running}/${s.total} running">${s.running}/${s.total}</span>` : "";
    return `${base}<span class="mat-state-dot ${dot}"></span>${web}${count}`;
  }
  function resolveActionUrl(s, ac) {
    if (/^https?:/i.test(ac.url) || ac.url.startsWith("//")) return ac.url;
    const w = primaryWeb(s); if (!w) return null;
    return w.appUrl.replace(/\/+$/, "") + (ac.url.startsWith("/") ? ac.url : "/" + ac.url);
  }
  function openAction(s, ac) {
    const u = resolveActionUrl(s, ac);
    if (!u) { alert("This action needs the app's web UI to be running."); return; }
    open({ key: "act:" + s.name + ":" + ac.label, title: stackTitle(s) + " · " + ac.label, url: u, width: 1024, height: 680 });
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
      if (moved) {
        el.style.left = (ox + dx) + "px"; el.style.top = (oy + dy) + "px";
        // Highlight any icon we'd merge into on drop.
        const t = dropTargetAt(e.clientX, e.clientY, el);
        document.querySelectorAll(".mat-app.drop-target").forEach(x => x.classList.remove("drop-target"));
        if (t) t.classList.add("drop-target");
      }
    });
    const end = (e) => {
      if (!active) return; active = false;
      try { el.releasePointerCapture(e.pointerId); } catch (_) {}
      if (moved) {
        el.classList.remove("dragging");
        document.querySelectorAll(".mat-app.drop-target").forEach(x => x.classList.remove("drop-target"));
        // Detect drop target — decide by which side is a folder.
        const drop = dropTargetAt(e.clientX, e.clientY, el);
        if (drop) {
          const srcFolderId = el.dataset.folderId, dstFolderId = drop.dataset.folderId;
          const srcKey = key, dstKey = drop.dataset.key;
          // Clear the dragging flag BEFORE dispatching so the optimistic reconcileDesktop()
          // that these helpers call actually runs (it's a no-op while dragging is true).
          dragging = false;
          if (srcFolderId && dstFolderId) {
            mergeFolders(srcFolderId, dstFolderId);
          } else if (srcFolderId && !dstFolderId) {
            // Folder dragged onto an app: put the app into the folder.
            moveKeyToFolder(dstKey, srcFolderId);
          } else if (!srcFolderId && dstFolderId) {
            // App dragged onto a folder: add the app to the folder.
            moveKeyToFolder(srcKey, dstFolderId);
          } else {
            // App onto app: create a new folder containing both.
            mergeIntoFolder(srcKey, dstKey);
          }
          return;
        }
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
  // Track which keys are system apps (for context-menu behaviour) — but they only render on
  // the desktop when the user has pinned them, exactly like store apps.
  function buildSystem() { for (const a of systemApps()) systemKeys.add(a.key); }

  // Rebuild the full desktop: pinned system apps + pinned stacks (that aren't in a folder)
  // + folder icons + widgets. Called whenever pins/folders/widgets/stacks change.
  function reconcileDesktop() {
    if (dragging) return;
    const inFolder = new Set(); for (const f of folders) for (const k of f.keys) inFolder.add(k);
    const wantedKeys = new Set();

    // pinned system apps
    for (const a of systemApps()) if (pins.has(a.key) && !inFolder.has(a.key)) {
      wantedKeys.add(a.key);
      let el = iconEls.get(a.key);
      if (!el) el = makeIcon(a.key, a.title, a.url, a.w, a.h, "sys", a.iconHtml);
    }
    // pinned stack apps
    for (const s of stackData) {
      const key = stackKey(s);
      if (!pins.has(key) || inFolder.has(key)) continue;
      wantedKeys.add(key);
      let el = iconEls.get(key);
      if (!el) { el = makeIcon(key, stackTitle(s), stackOpenUrl(s), "1024", "680", "", stackInner(s)); el.dataset.stackName = s.name; }
      else {
        el.dataset.title = stackTitle(s); el.dataset.url = stackOpenUrl(s);
        const ico = el.querySelector(".mat-app-icon"); if (ico) ico.innerHTML = stackInner(s);
        const lbl = el.querySelector(".mat-app-label"); if (lbl) lbl.textContent = stackTitle(s);
      }
    }
    // folders
    for (const f of folders) {
      const key = "folder:" + f.id; wantedKeys.add(key);
      let el = iconEls.get(key);
      if (!el) el = makeIcon(key, f.name, "", "0", "0", "folder", folderInner(f));
      else { el.dataset.title = f.name; const ico = el.querySelector(".mat-app-icon"); if (ico) ico.innerHTML = folderInner(f); const lbl = el.querySelector(".mat-app-label"); if (lbl) lbl.textContent = f.name; }
      el.dataset.folderId = f.id;
    }
    // pending install placeholders (iOS-style icon with a progress ring)
    for (const [instId, p] of pendingInstalls) {
      const key = "pending:" + instId; wantedKeys.add(key);
      let el = iconEls.get(key);
      const inner = `${appIconHtml(p.icon) || CUBE}<span class="mat-app-progress" aria-label="Installing"></span>`;
      if (!el) el = makeIcon(key, p.name || p.appId, "", "0", "0", "installing", inner);
      else { el.dataset.title = p.name || p.appId; const lbl = el.querySelector(".mat-app-label"); if (lbl) lbl.textContent = p.name || p.appId; }
      el.classList.add("installing");
    }

    // Remove any icon that's no longer wanted
    for (const [key, el] of iconEls) if (!wantedKeys.has(key)) { el.remove(); iconEls.delete(key); }

    reconcileWidgets();
  }
  // Compat alias for older callers
  const reconcileStacks = reconcileDesktop;

  // 2×2 preview of the folder's first 4 apps
  function folderInner(f){
    const previews = f.keys.slice(0, 4).map(k => {
      const html = iconForKey(k) || CUBE;
      return `<span class="mat-folder-mini">${html}</span>`;
    }).join("");
    return `<div class="mat-folder-preview">${previews}</div>`;
  }
  async function autoArrange() {
    try { await fetch("/api/v1/desktop/reset", { method: "POST" }); } catch (_) {}
    layout = {}; defaultIndex = 0; for (const [key, el] of iconEls) applyPos(el, key);
  }
  function pin(key)   { pins.add(key);    reconcileDesktop(); renderStartMenu(startSearch ? startSearch.value : ""); fetch("/api/v1/desktop/pin",   { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key }) }).catch(()=>{}); }
  function unpin(key) { pins.delete(key); reconcileDesktop(); renderStartMenu(startSearch ? startSearch.value : ""); fetch("/api/v1/desktop/unpin", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key }) }).catch(()=>{}); }

  async function load() {
    try {
      const res = await fetch("/api/v1/docker/stacks", { headers: { "Accept": "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json(); stackData = data.stacks || [];
    } catch (_) { stackData = []; }
    const needed = new Set();
    for (const s of stackData) for (const c of (s.containers || [])) if (c.matosApp) needed.add(c.matosApp);
    if ([...needed].some(id => !(id in appDefs))) await loadDefs();
    // Resolve pending install placeholders once the real stack shows up:
    // remove the placeholder, pin the real key at the placeholder's position.
    for (const [instId, p] of [...pendingInstalls]) {
      const stack = stackData.find(s => (s.containers || []).some(c => c.matosApp === p.appId));
      if (!stack) continue;
      const pendingKey = "pending:" + instId, realKey = "stack:" + stack.name;
      if (layout[pendingKey]) { layout[realKey] = layout[pendingKey]; delete layout[pendingKey]; saveIcon(realKey, layout[realKey].x, layout[realKey].y); }
      pendingInstalls.delete(instId);
      if (!pins.has(realKey)) { pins.add(realKey); try { fetch("/api/v1/desktop/pin", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ key: realKey }) }); } catch(_){} }
    }
    reconcileDesktop();
    renderStartMenu(startSearch ? startSearch.value : "");
  }
  // Poll a few times right after an install so the placeholder resolves quickly.
  function burstReload(){ [200, 700, 1500, 3000, 6000].forEach(t => setTimeout(load, t)); }
  async function stackAction(name, action) { try { await fetch(`/api/v1/docker/stacks/${encodeURIComponent(name)}/${action}`, { method: "POST" }); } catch (_) {} setTimeout(load, 700); }

  // ---- Start menu ----
  const startBtn = document.getElementById("mat-start-btn");
  const startMenu = document.getElementById("mat-startmenu");
  const startSearch = document.getElementById("mat-start-search");
  const smSystem = document.getElementById("sm-system");
  const smSystemTitle = document.getElementById("sm-system-title");
  const smApps = document.getElementById("sm-apps");
  const smAppsTitle = document.getElementById("sm-apps-title");
  const smContainers = document.getElementById("sm-containers");
  const smContainersTitle = document.getElementById("sm-containers-title");
  const smEmpty = document.getElementById("sm-empty");

  function smItem(a, iconClass, inner, pinned) {
    return `<button class="sm-item" data-key="${escAttr(a.key)}" data-title="${escAttr(a.title)}" data-url="${escAttr(a.url)}" data-w="${a.w}" data-h="${a.h}">
      <span class="sm-ico ${iconClass}">${inner}${pinned ? '<span class="sm-pin" title="On desktop">✓</span>' : ''}</span><span class="sm-label">${esc(a.title)}</span></button>`;
  }
  function renderStartMenu(filter) {
    if (!startMenu) return;
    const q = (filter || "").trim().toLowerCase();
    const match = t => !q || (t || "").toLowerCase().includes(q);
    const sys = systemApps().filter(a => match(a.title));
    smSystem.innerHTML = sys.map(a => smItem(a, "sys", a.iconHtml, pins.has(a.key))).join("");
    if (smSystemTitle) smSystemTitle.style.display = sys.length ? "" : "none";
    // matOS-managed store apps vs plain Docker containers/stacks
    const toItem = s => ({ key: stackKey(s), title: stackTitle(s), url: stackOpenUrl(s), w: "1024", h: "680", inner: stackInner(s) });
    const apps = stackData.filter(s => stackApp(s) && match(stackTitle(s))).map(toItem);
    const cons = stackData.filter(s => !stackApp(s) && match(stackTitle(s))).map(toItem);
    smApps.innerHTML = apps.map(a => smItem(a, "", a.inner, pins.has(a.key))).join("");
    smContainers.innerHTML = cons.map(a => smItem(a, "", a.inner, pins.has(a.key))).join("");
    if (smAppsTitle) smAppsTitle.style.display = apps.length ? "" : "none";
    if (smContainersTitle) smContainersTitle.style.display = cons.length ? "" : "none";
    smEmpty.hidden = (sys.length + apps.length + cons.length) > 0;
  }
  function openStart() { if (!startMenu) return; startMenu.hidden = false; startBtn.classList.add("active"); if (startSearch) { startSearch.value = ""; setTimeout(() => startSearch.focus(), 20); } renderStartMenu(""); }
  function closeStart() { if (!startMenu || startMenu.hidden) return; startMenu.hidden = true; startBtn.classList.remove("active"); }
  if (startBtn) {
    startBtn.addEventListener("click", (e) => { e.stopPropagation(); startMenu.hidden ? openStart() : closeStart(); });
    document.addEventListener("click", (e) => { if (!startMenu.hidden && !startMenu.contains(e.target) && !startBtn.contains(e.target)) closeStart(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") { closeStart(); hideCtx(); } });
    startSearch.addEventListener("input", () => renderStartMenu(startSearch.value));
    startSearch.addEventListener("keydown", (e) => { if (e.key === "Enter") { const f = startMenu.querySelector(".sm-item"); if (f) { e.preventDefault(); f.click(); } } });
    startMenu.addEventListener("click", (e) => { const it = e.target.closest(".sm-item"); if (it && !it.dataset.suppressClick) { e.preventDefault(); launchEl(it); closeStart(); } if (it) delete it.dataset.suppressClick; });
    // Drag a start-menu item onto the desktop to pin it there (with a chosen position).
    startMenu.addEventListener("pointerdown", (e) => {
      const it = e.target.closest(".sm-item"); if (!it) return;
      if (e.pointerType === "mouse" && e.button !== 0) return;
      const startX = e.clientX, startY = e.clientY; let ghost = null, dragged = false;
      const onMove = (ev) => {
        const dx = ev.clientX - startX, dy = ev.clientY - startY;
        if (!dragged && Math.hypot(dx, dy) > 8) {
          dragged = true;
          ghost = it.cloneNode(true); ghost.classList.add("sm-ghost");
          ghost.style.position = "fixed"; ghost.style.pointerEvents = "none"; ghost.style.zIndex = "80"; ghost.style.opacity = "0.9";
          document.body.appendChild(ghost);
        }
        if (dragged) { ghost.style.left = (ev.clientX - 30) + "px"; ghost.style.top = (ev.clientY - 30) + "px"; }
      };
      const onUp = async (ev) => {
        window.removeEventListener("pointermove", onMove); window.removeEventListener("pointerup", onUp);
        if (!dragged || !ghost) return;
        ghost.remove();
        it.dataset.suppressClick = "1";  // prevent the click from launching after a drag
        // If the pointer landed on the desktop layer (not the start menu), pin + place there.
        const overStart = startMenu.contains(document.elementFromPoint(ev.clientX, ev.clientY));
        if (!overStart) {
          const key = it.dataset.key;
          const rect = layer.getBoundingClientRect();
          const s = snapXY(ev.clientX - rect.left - 34, ev.clientY - rect.top - 34);
          layout[key] = s;
          if (!pins.has(key)) await pin(key);
          saveIcon(key, s.x, s.y);
          const el = iconEls.get(key); if (el) { el.style.left = s.x + "px"; el.style.top = s.y + "px"; }
          closeStart();
        }
      };
      window.addEventListener("pointermove", onMove); window.addEventListener("pointerup", onUp, { once: true });
    });
    startMenu.addEventListener("contextmenu", (e) => {
      const it = e.target.closest(".sm-item"); if (!it) return;
      const key = it.dataset.key; if (!key) return;
      e.preventDefault();
      showCtx(e.clientX, e.clientY, [pins.has(key)
        ? { label: "Remove from desktop", action: () => unpin(key) }
        : { label: "Add to desktop", action: () => pin(key) }]);
    });
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
      // Folder icon
      if (iconEl && iconEl.dataset.folderId) {
        const fid = iconEl.dataset.folderId;
        showCtx(e.clientX, e.clientY, [
          { label: "Open", action: () => openFolderOverlay(iconEl) },
          { label: "Rename", action: () => openFolderOverlay(iconEl) },
          { sep: true },
          { label: "Delete folder", danger: true, action: () => { if (confirm("Delete this folder? Its apps go back to the desktop.")) deleteFolder(fid); } },
        ]);
        return;
      }
      if (iconEl) {
        const key = iconEl.dataset.key;
        const s = stackData.find(x => x.name === iconEl.dataset.stackName);
        const items = [{ label: "Open", action: () => launchEl(iconEl) }];
        if (s) {
          items.push({ label: "Settings", action: () => open({ key: "set:" + s.name, title: "Settings · " + stackTitle(s), url: stackSettingsUrl(s), width: 1024, height: 680 }) });
          const def = stackDef(s);
          if (def && def.actions && def.actions.length) { items.push({ sep: true }); for (const ac of def.actions) items.push({ label: ac.label, action: () => openAction(s, ac) }); }
          items.push({ sep: true });
          if (s.anyRunning) { items.push({ label: "Stop", action: () => stackAction(s.name, "stop") }); items.push({ label: "Restart", action: () => stackAction(s.name, "restart") }); }
          if (!s.allRunning) items.push({ label: "Start", action: () => stackAction(s.name, "start") });
        }
        // Move to folder (any icon — system or store)
        items.push({ sep: true });
        items.push({ label: "New folder from this app", action: () => moveKeyToFolder(key, null) });
        for (const f of folders) if (!f.keys.includes(key)) items.push({ label: 'Move to "' + f.name + '"', action: () => moveKeyToFolder(key, f.id) });
        items.push({ sep: true }, { label: "Remove from desktop", action: () => unpin(key) }, { label: "Auto-arrange icons", action: autoArrange });
        showCtx(e.clientX, e.clientY, items);
      } else {
        showCtx(e.clientX, e.clientY, [
          { label: "Add apps…", action: openStart },
          { label: "New folder", action: () => createFolderAt(e.clientX, e.clientY) },
          { label: "Add widget…", action: () => showAddWidgetMenu(e.clientX, e.clientY) },
          { sep: true }, { label: "Auto-arrange icons", action: autoArrange }, { label: "Refresh", action: load }
        ]);
      }
    });
    document.addEventListener("click", () => hideCtx());
    document.addEventListener("scroll", hideCtx, true);
    window.addEventListener("resize", hideCtx); window.addEventListener("blur", hideCtx);
  }

  // ---- taskbar (open windows) context menu ----
  const dock = document.getElementById("mat-tasks");
  if (dock) dock.addEventListener("contextmenu", (e) => {
    e.preventDefault();
    const t = e.target.closest(".mat-task");
    if (t && t.dataset.winKey) {
      const key = t.dataset.winKey;
      showCtx(e.clientX, e.clientY, [
        { label: "Reset app", action: () => window.MatWM.reset(key) },
        { sep: true },
        { label: "Close", danger: true, action: () => window.MatWM.closeKey(key) },
      ]);
    } else {
      // Right-click on the empty part of the taskbar
      const sys = systemApps();
      const tm = sys.find(a => a.key === "task-manager" || a.title === "Task Manager");
      const st = sys.find(a => a.key === "settings" || a.title === "Settings");
      const items = [];
      if (tm) items.push({ label: "Task Manager", action: () => open({ key: tm.key, title: tm.title, url: tm.url, iconHtml: tm.iconHtml, width: parseInt(tm.w||"1024",10), height: parseInt(tm.h||"680",10) }) });
      if (st) items.push({ label: "Settings", action: () => open({ key: st.key, title: st.title, url: st.url, iconHtml: st.iconHtml, width: parseInt(st.w||"1024",10), height: parseInt(st.h||"680",10) }) });
      if (items.length) showCtx(e.clientX, e.clientY, items);
    }
  });

  // ---- Folders: iOS-style overlay with scale-up animation ----
  function openFolderOverlay(iconEl){
    const fid = iconEl.dataset.folderId; const f = folders.find(x => x.id === fid); if (!f) return;
    const r = iconEl.getBoundingClientRect();
    const backdrop = document.createElement("div"); backdrop.className = "mat-folder-backdrop";
    const panel = document.createElement("div"); panel.className = "mat-folder-panel";
    panel.style.setProperty("--ox", (r.left + r.width/2) + "px");
    panel.style.setProperty("--oy", (r.top + r.height/2) + "px");
    panel.innerHTML = `<div class="mat-folder-head"><span class="mat-folder-name" contenteditable spellcheck="false">${esc(f.name)}</span>
        <button class="mat-folder-close" title="Close">&#10005;</button></div>
      <div class="mat-folder-grid"></div>`;
    backdrop.appendChild(panel); document.body.appendChild(backdrop);
    requestAnimationFrame(() => backdrop.classList.add("open"));

    function renderGrid(){
      const grid = panel.querySelector(".mat-folder-grid"); grid.innerHTML = "";
      for (const k of f.keys) {
        const html = iconForKey(k) || CUBE;
        const btn = document.createElement("button"); btn.className = "mat-folder-item"; btn.dataset.key = k;
        btn.innerHTML = `<span class="mat-folder-item-ico">${html}</span><span class="mat-folder-item-lbl">${esc(labelForKey(k))}</span>`;
        btn.addEventListener("click", (e) => { if (btn.dataset.suppressClick) { delete btn.dataset.suppressClick; return; } launchByKey(k); closeIt(); });
        btn.addEventListener("contextmenu", (e) => { e.preventDefault();
          showCtx(e.clientX, e.clientY, [{ label: "Remove from folder", danger:true, action: () => removeFromFolder(f.id, k).then(() => renderGrid()) }]); });
        // Drag an item out of the folder onto the desktop
        btn.addEventListener("pointerdown", (e) => {
          if (e.pointerType === "mouse" && e.button !== 0) return;
          const startX = e.clientX, startY = e.clientY; let ghost = null, dragged = false;
          const onMove = (ev) => {
            const dx = ev.clientX - startX, dy = ev.clientY - startY;
            if (!dragged && Math.hypot(dx, dy) > 8) {
              dragged = true;
              ghost = btn.cloneNode(true); ghost.classList.add("sm-ghost");
              ghost.style.position = "fixed"; ghost.style.pointerEvents = "none"; ghost.style.zIndex = "80"; ghost.style.opacity = "0.9";
              ghost.style.width = "76px"; ghost.style.height = "auto";
              document.body.appendChild(ghost);
            }
            if (dragged) { ghost.style.left = (ev.clientX - 38) + "px"; ghost.style.top = (ev.clientY - 30) + "px"; }
          };
          const onUp = async (ev) => {
            window.removeEventListener("pointermove", onMove); window.removeEventListener("pointerup", onUp);
            if (!dragged) return;
            if (ghost) ghost.remove();
            btn.dataset.suppressClick = "1";
            // Drop outside the overlay panel? -> remove from folder + place at drop position on the desktop
            const overPanel = panel.contains(document.elementFromPoint(ev.clientX, ev.clientY));
            if (overPanel) return;
            const rect = layer.getBoundingClientRect();
            const pos = snapXY(ev.clientX - rect.left - 34, ev.clientY - rect.top - 34);
            layout[k] = pos;
            await removeFromFolder(f.id, k);
            if (!pins.has(k)) await pin(k); else reconcileDesktop();
            saveIcon(k, pos.x, pos.y);
            const iconEl = iconEls.get(k); if (iconEl) { iconEl.style.left = pos.x + "px"; iconEl.style.top = pos.y + "px"; }
            closeIt();
          };
          window.addEventListener("pointermove", onMove); window.addEventListener("pointerup", onUp, { once: true });
        });
        grid.appendChild(btn);
      }
      if (!f.keys.length) grid.innerHTML = `<div class="mat-folder-empty">Empty folder — drag an icon here, or right-click an app on the desktop → Move to folder.</div>`;
    }
    renderGrid();

    const nameEl = panel.querySelector(".mat-folder-name");
    nameEl.addEventListener("blur", async () => { const n = nameEl.textContent.trim(); if (n && n !== f.name) { f.name = n; iconEl.querySelector(".mat-app-label").textContent = n;
      try { await fetch("/api/v1/desktop/folders/rename", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: f.id, name: n }) }); } catch(_){} } });
    nameEl.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); nameEl.blur(); } });

    function closeIt(){ backdrop.classList.remove("open"); backdrop.classList.add("closing"); setTimeout(() => backdrop.remove(), 220); }
    panel.querySelector(".mat-folder-close").addEventListener("click", closeIt);
    backdrop.addEventListener("click", (e) => { if (e.target === backdrop) closeIt(); });
    document.addEventListener("keydown", function onKey(e){ if (e.key === "Escape") { closeIt(); document.removeEventListener("keydown", onKey); } });
  }
  function labelForKey(k){
    if (k.startsWith("stack:")) { const s = stackData.find(x => x.name === k.slice(6)); return s ? stackTitle(s) : k.slice(6); }
    const a = systemApps().find(x => x.key === k); return a ? a.title : k;
  }
  async function createFolderAt(x, y){
    // Optimistic: draw the folder immediately with a temporary id, then reconcile with the server id.
    const tempId = "tmp" + Math.random().toString(36).slice(2, 8);
    const optimistic = { id: tempId, name: "New Folder", keys: [] };
    folders.push(optimistic);
    const tempKey = "folder:" + tempId;
    if (x != null && y != null) {
      const rect = layer.getBoundingClientRect();
      const pos = snapXY(x - rect.left - 34, y - rect.top - 34);
      layout[tempKey] = pos;
    }
    reconcileDesktop();
    try {
      const r = await (await fetch("/api/v1/desktop/folders/create", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ name: "New Folder" }) })).json();
      // Swap the temp folder for the real one; carry over the position.
      const idx = folders.findIndex(f => f.id === tempId);
      if (idx >= 0) folders.splice(idx, 1, r.folder);
      const realKey = "folder:" + r.folder.id;
      if (layout[tempKey]) { layout[realKey] = layout[tempKey]; delete layout[tempKey]; saveIcon(realKey, layout[realKey].x, layout[realKey].y); }
      // Rename any live icon key without a full teardown
      const el = iconEls.get(tempKey); if (el) { iconEls.delete(tempKey); iconEls.set(realKey, el); el.dataset.folderId = r.folder.id; el.dataset.key = realKey; }
    } catch (_) {
      // Roll back: remove the optimistic folder
      folders = folders.filter(f => f.id !== tempId);
      delete layout[tempKey];
      reconcileDesktop();
    }
  }
  async function moveKeyToFolder(key, folderId){
    // Fast path: existing folder → update local state + rerender immediately, then persist.
    if (folderId) {
      for (const f of folders) f.keys = f.keys.filter(k => k !== key);
      const target = folders.find(f => f.id === folderId);
      if (target && !target.keys.includes(key)) target.keys.push(key);
      reconcileDesktop();
      try { await fetch("/api/v1/desktop/folders/add", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: folderId, key }) }); } catch(_){}
      return;
    }
    // New folder: paint a temp one at the source icon's position immediately, then swap ids.
    const src = iconEls.get(key); const pos = src ? { x: parseFloat(src.style.left)||0, y: parseFloat(src.style.top)||0 } : null;
    const tempId = "tmp" + Math.random().toString(36).slice(2, 8);
    const optimistic = { id: tempId, name: labelForKey(key), keys: [key] };
    folders.push(optimistic);
    const tempKey = "folder:" + tempId;
    if (pos) { layout[tempKey] = pos; }
    reconcileDesktop();
    try {
      const r = await (await fetch("/api/v1/desktop/folders/create", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ name: optimistic.name }) })).json();
      const idx = folders.findIndex(f => f.id === tempId); if (idx >= 0) folders.splice(idx, 1, { ...r.folder, keys: [key] });
      const realKey = "folder:" + r.folder.id;
      if (layout[tempKey]) { layout[realKey] = layout[tempKey]; delete layout[tempKey]; saveIcon(realKey, layout[realKey].x, layout[realKey].y); }
      const el = iconEls.get(tempKey); if (el) { iconEls.delete(tempKey); iconEls.set(realKey, el); el.dataset.folderId = r.folder.id; el.dataset.key = realKey; }
      await fetch("/api/v1/desktop/folders/add", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: r.folder.id, key }) });
    } catch (_) {
      folders = folders.filter(f => f.id !== tempId); delete layout[tempKey]; reconcileDesktop();
    }
  }
  // Find a droppable icon (app or folder) under the pointer, excluding the drag source.
  function dropTargetAt(x, y, sourceEl){
    const before = sourceEl.style.pointerEvents; sourceEl.style.pointerEvents = "none";
    const t = document.elementFromPoint(x, y);
    sourceEl.style.pointerEvents = before;
    if (!t) return null;
    const a = t.closest(".mat-app"); if (!a || a === sourceEl) return null;
    return a;
  }
  // Merge two app icons into a new folder (iOS-style). Both args must be plain app keys.
  // Optimistic: paint the folder in place of the target immediately, then persist.
  async function mergeIntoFolder(dragKey, targetKey){
    if (!dragKey || !targetKey || dragKey === targetKey) return;
    if (dragKey.startsWith("folder:") || targetKey.startsWith("folder:")) return; // safety net
    const tempId = "tmp" + Math.random().toString(36).slice(2, 8);
    const optimistic = { id: tempId, name: labelForKey(targetKey), keys: [targetKey, dragKey] };
    folders.push(optimistic);
    // Take the target icon's position for the new folder (iOS-style).
    const tgtEl = iconEls.get(targetKey);
    const tempKey = "folder:" + tempId;
    if (tgtEl) layout[tempKey] = { x: parseFloat(tgtEl.style.left)||0, y: parseFloat(tgtEl.style.top)||0 };
    reconcileDesktop();
    try {
      const r = await (await fetch("/api/v1/desktop/folders/create", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ name: optimistic.name }) })).json();
      const idx = folders.findIndex(f => f.id === tempId); if (idx >= 0) folders.splice(idx, 1, { ...r.folder, keys: [targetKey, dragKey] });
      const realKey = "folder:" + r.folder.id;
      if (layout[tempKey]) { layout[realKey] = layout[tempKey]; delete layout[tempKey]; saveIcon(realKey, layout[realKey].x, layout[realKey].y); }
      const el = iconEls.get(tempKey); if (el) { iconEls.delete(tempKey); iconEls.set(realKey, el); el.dataset.folderId = r.folder.id; el.dataset.key = realKey; }
      for (const k of [targetKey, dragKey]) {
        fetch("/api/v1/desktop/folders/add", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: r.folder.id, key: k }) }).catch(()=>{});
      }
    } catch (_) {
      folders = folders.filter(f => f.id !== tempId); delete layout[tempKey]; reconcileDesktop();
    }
  }

  // Fold one folder into another: move all children, delete the source folder. Optimistic.
  async function mergeFolders(srcId, dstId){
    if (!srcId || !dstId || srcId === dstId) return;
    const src = folders.find(f => f.id === srcId); const dst = folders.find(f => f.id === dstId);
    if (!src || !dst) return;
    const movedKeys = [...src.keys];
    for (const k of movedKeys) if (!dst.keys.includes(k)) dst.keys.push(k);
    folders = folders.filter(f => f.id !== srcId);
    reconcileDesktop();
    // Persist in the background — folders/add moves the key out of the old folder server-side.
    for (const k of movedKeys) fetch("/api/v1/desktop/folders/add", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: dstId, key: k }) }).catch(()=>{});
    fetch("/api/v1/desktop/folders/delete", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: srcId }) }).catch(()=>{});
  }

  async function removeFromFolder(fid, key){
    const f = folders.find(x => x.id === fid); if (f) f.keys = f.keys.filter(k => k !== key);
    reconcileDesktop();
    try { await fetch("/api/v1/desktop/folders/remove", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: fid, key }) }); } catch(_){}
  }
  async function deleteFolder(fid){
    folders = folders.filter(f => f.id !== fid); reconcileDesktop();
    try { await fetch("/api/v1/desktop/folders/delete", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: fid }) }); } catch(_){}
  }

  // ---- Widgets ----
  const WIDGET_TYPES = {
    clock:       { title: "Clock",              w: 3, h: 2 },
    resources:   { title: "Resource monitor",   w: 4, h: 3 },
    cpu:         { title: "CPU load",           w: 3, h: 2 },
    memory:      { title: "Memory load",        w: 3, h: 2 },
    containers:  { title: "Containers status",  w: 2, h: 2 },
  };
  // Rolling history for the resource-monitor widget (aggregate across all running containers).
  const wgCpuHist = [], wgMemHist = []; const WG_HIST = 40;
  const wgStreams = new Map();  // containerId -> EventSource
  const wgLatest = new Map();   // containerId -> { cpu, mem, lim }
  function wgSyncStreams(){
    const wantResourceWidgets = widgets.some(w => w.type === "resources" || w.type === "cpu" || w.type === "memory");
    if (!wantResourceWidgets) { for (const [id, s] of wgStreams) { try { s.close(); } catch (_) {} } wgStreams.clear(); wgLatest.clear(); return; }
    const running = new Set(stackData.flatMap(s => (s.containers||[]).filter(c => c.running).map(c => c.id)));
    for (const [id, s] of wgStreams) if (!running.has(id)) { try { s.close(); } catch (_) {} wgStreams.delete(id); wgLatest.delete(id); }
    for (const id of running) {
      if (wgStreams.has(id)) continue;
      const src = new EventSource(`/api/v1/docker/containers/${id}/stats/stream`);
      src.addEventListener("stat", e => { try { const s = JSON.parse(e.data); wgLatest.set(id, { cpu: +s.cpuPercent||0, mem: +s.memoryBytes||0, lim: +s.memoryLimitBytes||0 }); } catch (_) {} });
      src.onerror = () => {};
      wgStreams.set(id, src);
    }
  }
  function wgAggregate(){
    let cpu = 0, mem = 0, memLim = 0;
    for (const [, v] of wgLatest) { cpu += v.cpu||0; mem += v.mem||0; memLim = Math.max(memLim, v.lim||0); }
    return { cpu, mem, memLim };
  }
  function wgAreaSvg(data, color, max){
    const W = 100, H = 30, n = data.length; if (n < 2) return `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" style="width:100%;height:100%"></svg>`;
    const step = W/(n-1); const pts = data.map((v,i)=>[i*step, H - Math.min(1,(v||0)/(max||1))*(H-2) - 1]);
    const line = pts.map((p,i)=>(i?"L":"M")+p[0].toFixed(1)+" "+p[1].toFixed(1)).join(" ");
    const area = "M0 "+H+" "+pts.map(p=>"L"+p[0].toFixed(1)+" "+p[1].toFixed(1)).join(" ")+" L"+W+" "+H+" Z";
    return `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" style="width:100%;height:100%">
      <path d="${area}" fill="${color}" fill-opacity="0.2"/><path d="${line}" fill="none" stroke="${color}" stroke-width="1.5" vector-effect="non-scaling-stroke"/></svg>`;
  }
  const widgetEls = new Map();
  function reconcileWidgets(){
    const wanted = new Set(widgets.map(w => w.id));
    for (const [id, el] of widgetEls) if (!wanted.has(id)) { el.remove(); widgetEls.delete(id); }
    for (const w of widgets) {
      let el = widgetEls.get(w.id);
      if (!el) { el = buildWidget(w); widgetEls.set(w.id, el); layer.appendChild(el); }
      const size = WIDGET_TYPES[w.type] || { w: 2, h: 2 };
      el.style.width  = (size.w * CELL_W) + "px";
      el.style.height = (size.h * CELL_H - 8) + "px";
      el.style.left = (w.x || MARGIN) + "px"; el.style.top = (w.y || MARGIN) + "px";
    }
    widgetTick();
  }
  function buildWidget(w){
    const el = document.createElement("div"); el.className = "mat-widget mat-widget-" + w.type; el.dataset.id = w.id;
    el.innerHTML = `<div class="mat-widget-body" data-body></div>`;
    enableWidgetDrag(el, w);
    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      showCtx(e.clientX, e.clientY, [
        { label: "Remove widget", danger: true, action: async () => {
          await fetch("/api/v1/desktop/widgets/remove", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: w.id }) });
          widgets = widgets.filter(x => x.id !== w.id); reconcileDesktop();
        } }
      ]);
    });
    return el;
  }
  function enableWidgetDrag(el, w){
    let sx, sy, ox, oy, moved = false, active = false;
    el.addEventListener("pointerdown", (e) => {
      if (e.pointerType === "mouse" && e.button !== 0) return;
      // Don't grab drags from interactive descendants
      if (e.target.closest("button, a, input, [contenteditable]")) return;
      active = true; moved = false; sx = e.clientX; sy = e.clientY;
      ox = parseFloat(el.style.left)||0; oy = parseFloat(el.style.top)||0;
      try { el.setPointerCapture(e.pointerId); } catch(_){}
    });
    el.addEventListener("pointermove", (e) => {
      if (!active) return;
      const dx = e.clientX - sx, dy = e.clientY - sy;
      if (!moved && Math.hypot(dx,dy) > 5) { moved = true; dragging = true; el.classList.add("dragging"); }
      if (moved) { el.style.left = (ox + dx) + "px"; el.style.top = (oy + dy) + "px"; }
    });
    const end = (e) => { if (!active) return; active = false;
      try { el.releasePointerCapture(e.pointerId); } catch(_){}
      if (moved) { el.classList.remove("dragging");
        const nx = Math.max(0, parseFloat(el.style.left)), ny = Math.max(0, parseFloat(el.style.top));
        w.x = nx; w.y = ny;
        fetch("/api/v1/desktop/widgets/move", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: w.id, x: nx, y: ny }) });
        setTimeout(() => { dragging = false; }, 60);
      }
    };
    el.addEventListener("pointerup", end); el.addEventListener("pointercancel", end);
  }
  // Live widget content
  function widgetTick(){
    for (const w of widgets) {
      const el = widgetEls.get(w.id); if (!el) continue;
      const body = el.querySelector("[data-body]"); if (!body) continue;
      if (w.type === "clock") {
        const d = new Date();
        body.innerHTML = `<div class="wg-clock">${d.toLocaleTimeString([],{hour:"2-digit",minute:"2-digit"})}</div>
          <div class="wg-sub">${d.toLocaleDateString([], { weekday:"long", day:"2-digit", month:"long" })}</div>`;
      } else if (w.type === "containers") {
        const running = stackData.reduce((n,s)=>n+(s.running||0),0);
        const total = stackData.reduce((n,s)=>n+(s.total||0),0);
        body.innerHTML = `<div class="wg-num">${running}<span class="wg-sub"> / ${total}</span></div><div class="wg-lbl">Containers running</div>`;
      } else if (w.type === "resources") {
        const a = wgAggregate(); const runningCount = stackData.reduce((n,s)=>n+(s.running||0),0);
        body.innerHTML = `<div class="wg-mon-head">Resource monitor <span class="wg-sub" style="margin-left:auto">${runningCount} running</span></div>
          <div class="wg-mon-row"><div class="wg-mon-lbl">CPU</div><div class="wg-mon-val">${a.cpu.toFixed(1)}%</div><div class="wg-mon-spark">${wgAreaSvg(wgCpuHist,"#8b5cf6",Math.max(100,...wgCpuHist))}</div></div>
          <div class="wg-mon-row"><div class="wg-mon-lbl">MEM</div><div class="wg-mon-val">${fmtB(a.mem)}</div><div class="wg-mon-spark">${wgAreaSvg(wgMemHist,"#22b8ff",Math.max(1,...wgMemHist))}</div></div>`;
      } else if (w.type === "cpu") {
        const a = wgAggregate();
        body.innerHTML = `<div class="wg-num">${a.cpu.toFixed(1)}%</div><div class="wg-lbl">CPU</div>
          <div style="height:38px;margin-top:0.3rem">${wgAreaSvg(wgCpuHist,"#8b5cf6",Math.max(100,...wgCpuHist))}</div>`;
      } else if (w.type === "memory") {
        const a = wgAggregate();
        body.innerHTML = `<div class="wg-num">${fmtB(a.mem)}</div><div class="wg-lbl">Memory</div>
          <div style="height:38px;margin-top:0.3rem">${wgAreaSvg(wgMemHist,"#22b8ff",Math.max(1,...wgMemHist))}</div>`;
      }
    }
  }
  const fmtB = b => { if (b==null||b<0) return "—"; const u=["B","KB","MB","GB","TB"]; let i=0,n=b; while(n>=1024&&i<u.length-1){ n/=1024; i++; } return n.toFixed(n<10&&i>0?1:0)+" "+u[i]; };
  // Sample the aggregated CPU/mem every 2s so widgets show live history.
  setInterval(() => {
    if (!widgets.length) return;
    wgSyncStreams();
    const a = wgAggregate();
    wgCpuHist.push(a.cpu); wgMemHist.push(a.mem);
    if (wgCpuHist.length > WG_HIST) wgCpuHist.shift();
    if (wgMemHist.length > WG_HIST) wgMemHist.shift();
    widgetTick();
  }, 2000);
  setInterval(widgetTick, 30000);
  setInterval(() => { const cw = widgetEls; for (const [id, el] of cw) { const w = widgets.find(x => x.id === id); if (w && w.type === "clock") widgetTick(); break; } }, 15000);
  async function addWidget(type){
    const spec = WIDGET_TYPES[type] || { w:2, h:2 };
    // Place it near the top-right corner of the layer, but never off-screen
    const lw = (layer && layer.clientWidth) || window.innerWidth;
    const x = Math.max(MARGIN, lw - spec.w*CELL_W - MARGIN);
    const y = MARGIN;
    const r = await (await fetch("/api/v1/desktop/widgets/add", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ type, x, y, w: spec.w, h: spec.h }) })).json();
    widgets.push(r.widget); reconcileDesktop();
  }
  function showAddWidgetMenu(x, y){
    showCtx(x, y, Object.entries(WIDGET_TYPES).map(([k, v]) => ({ label: v.title, action: () => addWidget(k) })));
  }

  // ---- init ----
  async function init() {
    try { const r = await fetch("/api/v1/desktop/layout", { headers: { Accept: "application/json" } }); if (r.ok) { const d = await r.json(); layout = d.positions || {}; pins = new Set(d.pins || []); folders = d.folders || []; widgets = d.widgets || []; } }
    catch (_) { layout = {}; }
    await loadDefs();
    buildSystem(); await load(); setInterval(load, 15000);
  }
  init();

  // ---- messages from app windows ----
  window.addEventListener("message", (e) => {
    if (e.origin !== location.origin) return;
    const m = e.data; if (!m) return;
    if (m.type === "matos:wallpaper" && m.wallpaper) { const wp = document.getElementById("mat-wallpaper"); if (wp) wp.className = "wp-" + m.wallpaper; }
    if (m.type === "matos:open" && m.url) {
      const opts = { key: m.key || m.url, title: m.title || "App", url: m.url, width: m.width || 1024, height: m.height || 680 };
      if (m.ephemeralId) opts.onClose = () => { try { fetch("/api/v1/store/close-ephemeral", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id: m.ephemeralId }) }); } catch (_) {} };
      open(opts);
    }
    if (m.type === "matos:close" && m.key) { window.MatWM.closeKey(m.key); }
    if (m.type === "matos:refresh") { burstReload(); broadcast({ type: "matos:reload" }); }
    // The install wizard fires this the moment the user clicks Install: show a
    // placeholder icon on the desktop immediately, iOS-style.
    if (m.type === "matos:installing" && m.appId) {
      pendingInstalls.set(m.installId, { appId: m.appId, name: m.name || m.appId, icon: m.icon || "" });
      // A rough placement near the top-left free area, so the user sees it right away.
      const key = "pending:" + m.installId;
      layout[key] = slotToXY(defaultIndex++);
      reconcileDesktop();
    }
    if (m.type === "matos:install-failed" && m.installId) {
      pendingInstalls.delete(m.installId);
      const key = "pending:" + m.installId; delete layout[key];
      reconcileDesktop();
    }
  });

  // relay a message to every open app window (so e.g. the Store refreshes after an install)
  function broadcast(msg) {
    document.querySelectorAll("#mat-windows iframe").forEach(f => { try { f.contentWindow.postMessage(msg, location.origin); } catch (_) {} });
  }
})();
