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
  const stackOnDemand = s => (s.containers || []).some(c => c.onDemand);
  const stackRunning  = s => (s.containers || []).some(c => c.running);
  // On-demand apps: start the container on open (via a "Starting…" page that wakes it, then loads
  // the app) and stop it again when the window closes. Centralised here so every launch path is covered.
  function open(opts) {
    if (opts.key && opts.key.startsWith("stack:")) {
      const s = stackData.find(x => x.name === opts.key.slice(6));
      // matOS store apps open through a loader page: it shows a spinner, starts the app if needed,
      // waits until it's up, then either embeds it or (if the app blocks framing) shows our own
      // "open in new tab" message instead of the browser's error. On-demand apps also stop on close.
      if (s && stackApp(s)) {
        if (stackOnDemand(s)) {
          const stop = () => { fetch(`/api/v1/docker/stacks/${encodeURIComponent(s.name)}/stop`, { method: "POST" }).catch(()=>{}); setTimeout(load, 1500); };
          const prev = opts.onClose;
          opts.onClose = () => { try { prev && prev(); } catch(_){} stop(); };
        }
        opts.url = `/apps/starting?stack=${encodeURIComponent(s.name)}&title=${encodeURIComponent(stackTitle(s))}`;
      }
    }
    if (!opts.iconHtml && opts.key) opts.iconHtml = iconForKey(opts.key);
    window.MatWM.open(opts);
  }
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
  let taskbarWidgets = []; // tray widgets [{id,type,order,config}]
  let startFolders = []; // start-menu folders [{id,name,keys:[...]}]
  const startFoldersCollapsed = new Set();
  let labels = {};    // key -> custom display name (per user)
  let taskbarPins = new Set(); // app keys pinned to the taskbar (Windows-11 style — always shown)
  const pendingInstalls = new Map(); // installId -> { appId, name, icon }
  const stackKey = s => "stack:" + s.name;
  const primaryWeb = s => (s.containers || []).find(c => c.hasWebUi && c.running && c.appUrl);
  function folderContainingKey(key){ for (const f of folders) if (f.keys.includes(key)) return f; return null; }

  async function loadDefs() {
    try { const d = await (await fetch("/api/v1/store/catalog")).json(); const m = {}; for (const a of d.apps) m[a.id] = a; appDefs = m; } catch (_) {}
  }
  function appIconHtml(icon) {
    if (!icon) return null;
    if (/^(https?:|data:)/i.test(icon)) return `<img src="${escAttr(icon)}" alt="" draggable="false">`;
    return `<span class="mat-emoji">${esc(icon)}</span>`;
  }
  function stackApp(s) { const c = (s.containers || []).find(x => x.matosApp); return c ? c.matosApp : null; }
  function stackDef(s) { const a = stackApp(s); return a ? appDefs[a] : null; }
  // Pre-disambiguation title: the stored matos.title (the installer now bakes the instance
  // number into it, e.g. "MatCMS 2"), else the app-def name, else the raw stack name.
  function stackBaseTitle(s) {
    const c = (s.containers || []).find(x => x.matosTitle);
    return (c && c.matosTitle) ? c.matosTitle : (stackDef(s)?.name || s.name);
  }
  // What a stack actually shows as its base — a per-user rename wins, so a renamed sibling
  // ("Blog") is never treated as clashing with "MatCMS".
  function effectiveBaseTitle(s) { return labels["stack:" + s.name] || stackBaseTitle(s); }
  // Instance number used only when disambiguating a clash: matos.instance, else a trailing
  // _<n> in the stack name, else "".
  function stackInstance(s) {
    const cc = (s.containers || []).find(x => x.matosInstance);
    return (cc && cc.matosInstance) || (s.name.match(/_(\d+)$/)?.[1]) || "";
  }
  function stackTitle(s) {
    // Per-user custom label always wins.
    const custom = labels["stack:" + s.name]; if (custom) return custom;
    const base = stackBaseTitle(s);
    const app = stackApp(s);
    if (!app) return base;
    // Disambiguate ONLY on a real clash: another install of the same app whose displayed base
    // title is byte-for-byte identical. Installs whose stored title already carries a number
    // ("MatCMS 2") are unique, so they're shown verbatim — never "MatCMS 2 2". Legacy installs
    // whose stored title is still plain "MatCMS" collide and get " <instance>" appended.
    const collides = stackData.some(x => x !== s && stackApp(x) === app && effectiveBaseTitle(x) === base);
    if (!collides) return base;
    const inst = stackInstance(s);
    return inst ? `${base} ${inst}` : base;
  }
  // Custom label for any key (folder / system / stack); used by systemApps renderer too.
  function customLabel(key) { return labels[key] || null; }

  // All properties (App/Stack/Container/Volumes) open in the one unified tabbed window.
  function stackSettingsUrl(s) {
    const cs = s.containers || [];
    const appC = cs.find(c => c.matosApp);
    if (appC) return "/apps/properties?type=app&id=" + encodeURIComponent(appC.id);
    if (s.standalone && cs[0]) return "/apps/properties?type=container&id=" + encodeURIComponent(cs[0].id);
    return "/apps/properties?type=stack&name=" + encodeURIComponent(s.name);
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
    if (!u) { matDialog.alert("This action needs the app's web UI to be running.", { title: "Not available" }); return; }
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
  // First grid slot not already taken by a saved position or an on-screen icon — so a newly
  // installed app never lands on top of another icon.
  function freeSlot() {
    const taken = [];
    for (const p of Object.values(layout)) if (p) taken.push(p);
    for (const el of iconEls.values()) taken.push({ x: parseFloat(el.style.left) || 0, y: parseFloat(el.style.top) || 0 });
    for (let i = 0; i < 500; i++) {
      const s = slotToXY(i);
      if (!taken.some(p => Math.abs(p.x - s.x) < CELL_W && Math.abs(p.y - s.y) < CELL_H)) return s;
    }
    return slotToXY(defaultIndex++);
  }
  function applyPos(el, key) { const p = layout[key] || freeSlot(); const c = clampXY(p.x, p.y); el.style.left = c.x + "px"; el.style.top = c.y + "px"; }

  async function saveIcon(key, x, y) {
    try { await fetch("/api/v1/desktop/icon", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key, x, y }) }); } catch (_) {}
  }
  // ---- Selection state (multi-select via rubber-band or ctrl/shift click) ----
  const selectedKeys = new Set();
  function isSelected(key) { return selectedKeys.has(key); }
  function setSelected(key, on) {
    if (on) selectedKeys.add(key); else selectedKeys.delete(key);
    const el = iconEls.get(key); if (el) el.classList.toggle("selected", on);
  }
  function clearSelection() { for (const k of [...selectedKeys]) setSelected(k, false); }
  function selectionList() { return [...selectedKeys].map(k => ({ key: k, el: iconEls.get(k) })).filter(x => x.el); }

  // True when (x,y) is over the taskbar strip — used to pin an icon by dragging it there.
  function overTaskbar(x, y) {
    const tb = document.getElementById("mat-taskbar");
    if (!tb) return false;
    const r = tb.getBoundingClientRect();
    return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
  }

  function enableDrag(el, key) {
    let sx, sy, ox, oy, moved = false, active = false, groupStart = null;
    el.addEventListener("pointerdown", (e) => {
      if (e.pointerType === "mouse" && e.button !== 0) return;
      // Ctrl/Shift + click toggles the icon in/out of the selection, no drag/launch.
      if (e.ctrlKey || e.metaKey || e.shiftKey) { e.stopPropagation(); setSelected(key, !isSelected(key)); return; }
      // If the clicked icon isn't already in a multi-selection, this starts a fresh single drag.
      if (!isSelected(key)) clearSelection();
      active = true; moved = false; sx = e.clientX; sy = e.clientY;
      ox = parseFloat(el.style.left) || 0; oy = parseFloat(el.style.top) || 0;
      // Snapshot every selected icon's origin so we can move the whole group together.
      groupStart = selectionList().filter(x => x.key !== key).map(x => ({ key: x.key, el: x.el, ox: parseFloat(x.el.style.left) || 0, oy: parseFloat(x.el.style.top) || 0 }));
      try { el.setPointerCapture(e.pointerId); } catch (_) {}
    });
    el.addEventListener("pointermove", (e) => {
      if (!active) return;
      const dx = e.clientX - sx, dy = e.clientY - sy;
      if (!moved && Math.hypot(dx, dy) > 5) { moved = true; dragging = true; el.classList.add("dragging"); for (const g of groupStart) g.el.classList.add("dragging"); }
      if (moved) {
        el.style.left = (ox + dx) + "px"; el.style.top = (oy + dy) + "px";
        for (const g of groupStart) { g.el.style.left = (g.ox + dx) + "px"; g.el.style.top = (g.oy + dy) + "px"; }
        // Highlight the taskbar when hovering it — dropping there pins the app (Windows-style).
        const overTb = overTaskbar(e.clientX, e.clientY);
        const tb = document.getElementById("mat-taskbar");
        if (tb) tb.classList.toggle("drag-pin-target", overTb);
        // Highlight any icon we'd merge into on drop (only when dragging a single icon, not over the taskbar).
        document.querySelectorAll(".mat-app.drop-target").forEach(x => x.classList.remove("drop-target"));
        if (!groupStart.length && !overTb) {
          const t = dropTargetAt(e.clientX, e.clientY, el);
          if (t) t.classList.add("drop-target");
        }
      }
    });
    const end = (e) => {
      if (!active) return; active = false;
      try { el.releasePointerCapture(e.pointerId); } catch (_) {}
      if (moved) {
        el.classList.remove("dragging"); for (const g of groupStart) g.el.classList.remove("dragging");
        document.querySelectorAll(".mat-app.drop-target").forEach(x => x.classList.remove("drop-target"));
        const tb = document.getElementById("mat-taskbar"); if (tb) tb.classList.remove("drag-pin-target");
        dragging = false;

        // Dropped on the taskbar: pin the app(s) there and snap the icon(s) back — the desktop icon stays.
        if (overTaskbar(e.clientX, e.clientY)) {
          const items = [{ key, el, ox, oy }, ...groupStart];
          for (const it of items) {
            if (it.el.dataset.folderId) continue; // folders can't be pinned to the taskbar
            pinToTaskbar(it.key);
            it.el.style.left = it.ox + "px"; it.el.style.top = it.oy + "px"; // revert to its slot
          }
          clearSelection();
          setTimeout(() => { dragging = false; }, 60);
          return;
        }

        // Group drop into a folder: if any icon of the group lands on a folder, move ALL of them there.
        if (groupStart.length) {
          const t = dropTargetAt(e.clientX, e.clientY, el);
          if (t && t.dataset.folderId) {
            const fid = t.dataset.folderId;
            for (const k of [key, ...groupStart.map(g => g.key)]) moveKeyToFolder(k, fid);
            clearSelection(); return;
          }
          // Otherwise: snap every icon in the group to the grid and persist.
          const items = [{ key, el }, ...groupStart];
          for (const it of items) {
            const s = snapXY(parseFloat(it.el.style.left), parseFloat(it.el.style.top));
            it.el.style.left = s.x + "px"; it.el.style.top = s.y + "px";
            layout[it.key] = s; saveIcon(it.key, s.x, s.y);
          }
          setTimeout(() => { dragging = false; }, 60);
          return;
        }
        // Single-icon drop — same behaviour as before.
        const drop = dropTargetAt(e.clientX, e.clientY, el);
        if (drop) {
          const srcFolderId = el.dataset.folderId, dstFolderId = drop.dataset.folderId;
          const srcKey = key, dstKey = drop.dataset.key;
          if (srcFolderId && dstFolderId) mergeFolders(srcFolderId, dstFolderId);
          else if (srcFolderId && !dstFolderId) moveKeyToFolder(dstKey, srcFolderId);
          else if (!srcFolderId && dstFolderId) moveKeyToFolder(srcKey, dstFolderId);
          else mergeIntoFolder(srcKey, dstKey);
          return;
        }
        const s = snapXY(parseFloat(el.style.left), parseFloat(el.style.top));
        el.style.left = s.x + "px"; el.style.top = s.y + "px";
        layout[key] = s; saveIcon(key, s.x, s.y);
        setTimeout(() => { dragging = false; }, 60);
      } else {
        // Plain click: single-select this icon and launch (Windows behaviour).
        clearSelection(); launchEl(el);
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
      const title = customLabel(a.key) || a.title;
      let el = iconEls.get(a.key);
      if (!el) el = makeIcon(a.key, title, a.url, a.w, a.h, "sys", a.iconHtml);
      else { el.dataset.title = title; const lbl = el.querySelector(".mat-app-label"); if (lbl) lbl.textContent = title; }
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
  // Rename an icon (per-user custom label). Blank/empty resets to the default.
  async function renameIcon(key) {
    // Compute current visible name for the prompt default.
    let current = "";
    const el = iconEls.get(key); if (el) current = el.querySelector(".mat-app-label")?.textContent || "";
    const next = await matDialog.prompt("New name for this icon (leave empty to reset):", { value: current, title: "Rename" });
    if (next === null) return;
    const label = next.trim();
    if (label) labels[key] = label; else delete labels[key];
    reconcileDesktop();
    try { await fetch("/api/v1/desktop/rename", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key, label }) }); } catch (_) {}
  }
  function unpin(key) { pins.delete(key); reconcileDesktop(); renderStartMenu(startSearch ? startSearch.value : ""); fetch("/api/v1/desktop/unpin", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key }) }).catch(()=>{}); }

  let hiddenSet = new Set(); // stacks hidden from the desktop (matOS's own stack by default; editable in Settings)
  async function loadHidden() { try { const d = await (await fetch("/api/v1/desktop/hidden")).json(); hiddenSet = new Set(d.hidden || []); } catch (_) {} }
  async function load() {
    try {
      const res = await fetch("/api/v1/docker/stacks", { headers: { "Accept": "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json(); stackData = (data.stacks || []).filter(s => !hiddenSet.has(s.name));
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
    renderTaskbarWidgets();
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
  // Resolve a Start-menu item object (system app or "stack:<name>") for a folder member key.
  function startItemFor(key) {
    if (key && key.startsWith("stack:")) { const s = stackData.find(x => stackKey(x) === key); return s ? { key, title: stackTitle(s), url: stackOpenUrl(s), w: "1024", h: "680", inner: stackInner(s) } : null; }
    const sa = systemApps().find(a => a.key === key);
    return sa ? { key, title: sa.title, url: sa.url, w: sa.w, h: sa.h, iconHtml: sa.iconHtml, sys: true } : null;
  }
  function startFolderHtml(f, q) {
    const members = (f.keys || []).map(startItemFor).filter(Boolean).filter(a => !q || (a.title || "").toLowerCase().includes(q));
    if (q && !members.length) return "";
    const collapsed = startFoldersCollapsed.has(f.id);
    const items = members.map(a => smItem(a, a.sys ? "sys" : "", a.sys ? a.iconHtml : a.inner, pins.has(a.key))).join("");
    return `<div class="sm-folder${collapsed ? ' collapsed' : ''}" data-sfid="${escAttr(f.id)}">
      <button class="sm-folder-head"><span class="sm-folder-chev">▸</span><span class="sm-folder-name">${esc(f.name)}</span><span class="sm-folder-count">${members.length}</span></button>
      <div class="sm-folder-items">${items}</div></div>`;
  }
  function renderStartMenu(filter) {
    if (!startMenu) return;
    const q = (filter || "").trim().toLowerCase();
    const match = t => !q || (t || "").toLowerCase().includes(q);
    const inFolder = new Set(); for (const f of startFolders) for (const k of (f.keys || [])) inFolder.add(k);
    const sys = systemApps().filter(a => match(a.title) && !inFolder.has(a.key));
    smSystem.innerHTML = sys.map(a => smItem(a, "sys", a.iconHtml, pins.has(a.key))).join("");
    if (smSystemTitle) smSystemTitle.style.display = sys.length ? "" : "none";
    // matOS-managed store apps vs plain Docker containers/stacks
    const toItem = s => ({ key: stackKey(s), title: stackTitle(s), url: stackOpenUrl(s), w: "1024", h: "680", inner: stackInner(s) });
    const apps = stackData.filter(s => stackApp(s) && match(stackTitle(s)) && !inFolder.has(stackKey(s))).map(toItem);
    const cons = stackData.filter(s => !stackApp(s) && match(stackTitle(s)) && !inFolder.has(stackKey(s))).map(toItem);
    const pend = [...pendingInstalls.values()].filter(p => match(p.name));
    const pendHtml = pend.map(p => `<div class="sm-item sm-installing"><span class="sm-ico">${appIconHtml(p.icon) || CUBE}</span><span class="sm-label">${esc(p.name)}</span><span class="sm-inst-badge">Installing…</span></div>`).join("");
    const folderHtml = startFolders.map(f => startFolderHtml(f, q)).join("");
    const foldersShown = folderHtml.trim().length > 0;
    smApps.innerHTML = folderHtml + pendHtml + apps.map(a => smItem(a, "", a.inner, pins.has(a.key))).join("");
    smContainers.innerHTML = cons.map(a => smItem(a, "", a.inner, pins.has(a.key))).join("");
    if (smAppsTitle) smAppsTitle.style.display = (apps.length + pend.length || foldersShown) ? "" : "none";
    if (smContainersTitle) smContainersTitle.style.display = cons.length ? "" : "none";
    smEmpty.hidden = (sys.length + apps.length + cons.length + pend.length) > 0 || foldersShown;
  }
  const smRerender = () => renderStartMenu(startSearch ? startSearch.value : "");
  const sfPost = (op, body) => fetch("/api/v1/desktop/startmenu/folders/" + op, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }).catch(() => {});
  async function createStartFolderWith(key) {
    const name = window.prompt("Folder name:", "Folder"); if (name === null) return;
    try { const r = await (await fetch("/api/v1/desktop/startmenu/folders/create", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: name.trim() || "Folder" }) })).json();
      startFolders.push(r.folder); addToStartFolder(key, r.folder.id); } catch (_) {}
  }
  function addToStartFolder(key, folderId) {
    for (const f of startFolders) f.keys = (f.keys || []).filter(k => k !== key);
    const f = startFolders.find(x => x.id === folderId); if (f && !f.keys.includes(key)) f.keys.push(key);
    smRerender(); sfPost("add", { id: folderId, key });
  }
  function removeFromStartFolder(key) {
    let fid = null; for (const f of startFolders) if ((f.keys || []).includes(key)) { fid = f.id; f.keys = f.keys.filter(k => k !== key); }
    smRerender(); if (fid) sfPost("remove", { id: fid, key });
  }
  function renameStartFolder(id) {
    const f = startFolders.find(x => x.id === id); if (!f) return;
    const name = window.prompt("Rename folder:", f.name); if (name === null || !name.trim()) return;
    f.name = name.trim(); smRerender(); sfPost("rename", { id, name: f.name });
  }
  function deleteStartFolder(id) {
    startFolders = startFolders.filter(f => f.id !== id); smRerender(); sfPost("delete", { id });
  }
  // Position a taskbar popup (start menu / notification panel) relative to its anchor button,
  // so it stays correctly attached regardless of taskbar position (top/bottom) or icon
  // alignment (left/center) — a hardcoded corner anchor can't track either of those.
  function positionNearAnchor(el, anchor) {
    if (!el || !anchor) return;
    const ar = anchor.getBoundingClientRect();
    const isTop = document.body.classList.contains("tb-top");
    const margin = 8;
    if (isTop) { el.style.top = (ar.bottom + margin) + "px"; el.style.bottom = "auto"; }
    else { el.style.bottom = (window.innerHeight - ar.top + margin) + "px"; el.style.top = "auto"; }
    const w = el.offsetWidth || 400;
    let left = ar.left + ar.width / 2 - w / 2;
    left = Math.max(8, Math.min(left, window.innerWidth - w - 8));
    el.style.left = left + "px";
    el.style.right = "auto";
    el.style.transformOrigin = (isTop ? "top" : "bottom") + " center";
  }

  function openStart() { if (!startMenu) return; startMenu.hidden = false; positionNearAnchor(startMenu, startBtn); startBtn.classList.add("active"); if (startSearch) { startSearch.value = ""; setTimeout(() => startSearch.focus(), 20); } renderStartMenu(""); }
  function closeStart() { if (!startMenu || startMenu.hidden) return; startMenu.hidden = true; startBtn.classList.remove("active"); startMenu.classList.remove("tb-driven"); }
  // Taskbar search (Windows-11 style): typing forwards into the start menu. When the taskbar
  // search is driving the menu, we hide the menu's own search input (one focused input only —
  // otherwise the user sees two search fields and doesn't know where they're typing).
  const tbSearch = document.getElementById("mat-tb-search-input");
  const tbSearchWrap = document.getElementById("mat-tb-search");
  if (tbSearch) {
    const setDriven = on => { startMenu.classList.toggle("tb-driven", !!on); };
    // Opening via the taskbar search must also anchor the menu — otherwise, before the Start button
    // has ever been clicked (which is what sets the inline position), it pops up at its default spot.
    const openWithQuery = q => { if (startMenu.hidden) { startMenu.hidden = false; positionNearAnchor(startMenu, startBtn); startBtn.classList.add("active"); } setDriven(true); renderStartMenu(q); };
    tbSearch.addEventListener("focus", () => { if (startMenu.hidden) { openWithQuery(tbSearch.value || ""); } else setDriven(true); });
    tbSearch.addEventListener("input", () => openWithQuery(tbSearch.value));
    tbSearch.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { const f = startMenu.querySelector(".sm-item"); if (f) { e.preventDefault(); f.click(); tbSearch.value = ""; } }
      else if (e.key === "Escape") { tbSearch.value = ""; setDriven(false); closeStart(); tbSearch.blur(); }
    });
    if (tbSearchWrap) tbSearchWrap.addEventListener("click", () => tbSearch.focus());
  }

  // Apply personal taskbar prefs by toggling body classes + CSS variables (styling lives in desktop.css).
  function applyTaskbarPrefs(p) {
    if (!p) return;
    const body = document.body;
    body.classList.toggle("tb-top", p.taskbarPosition === "top");
    body.classList.toggle("tb-center", p.taskbarAlign === "center");
    body.classList.toggle("tb-no-labels", p.taskbarLabels === false);
    const search = document.getElementById("mat-tb-search"); if (search) search.style.display = p.taskbarSearch === false ? "none" : "";
  }

  // ---- Taskbar pins (Windows-11 style: always-present buttons, running or not) ----
  const tasksPinnedEl = document.getElementById("mat-tasks-pinned");
  function renderTaskbarPins() {
    if (!tasksPinnedEl) return;
    tasksPinnedEl.innerHTML = "";
    for (const key of taskbarPins) {
      const title = customLabel(key) || labelForKey(key);
      const iconHtml = iconForKey(key) || CUBE;
      const state = window.MatWM ? window.MatWM.getState(key) : null;
      const b = document.createElement("button");
      b.className = "mat-task pinned" + (state ? (state.minimized ? "" : (state.focused ? " active" : " running")) : "");
      b.title = title || ""; b.dataset.key = key;
      b.innerHTML = `<span class="mat-task-ico mat-task-ico-svg">${iconHtml}</span><span class="mat-task-label">${escAttrSafe(title)}</span>`;
      b.addEventListener("click", () => { if (window.MatWM && window.MatWM.isOpen(key)) window.MatWM.toggleFocusOrMinimize(key); else launchByKey(key); });
      tasksPinnedEl.appendChild(b);
    }
  }
  function escAttrSafe(s) { return String(s == null ? "" : s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }
  function isPinnedToTaskbar(key) { return taskbarPins.has(key); }
  function pinToTaskbar(key) {
    if (!key || taskbarPins.has(key)) return;
    taskbarPins.add(key); renderTaskbarPins(); if (window.MatWM) window.MatWM.setPinnedKeys([...taskbarPins]);
    fetch("/api/v1/desktop/taskbar-pin", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key }) }).catch(() => {});
  }
  function unpinFromTaskbar(key) {
    if (!taskbarPins.has(key)) return;
    taskbarPins.delete(key); renderTaskbarPins(); if (window.MatWM) window.MatWM.setPinnedKeys([...taskbarPins]);
    fetch("/api/v1/desktop/taskbar-unpin", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key }) }).catch(() => {});
  }

  // ---- Taskbar (tray) widgets — small app status/launcher chips next to the clock ----
  const trayWidgetsEl = document.getElementById("mat-tray-widgets");
  function renderTaskbarWidgets() {
    if (!trayWidgetsEl) return;
    trayWidgetsEl.innerHTML = "";
    for (const tw of taskbarWidgets) {
      const a = appWidgetDef(tw.type); if (!a || !a.def) continue;
      const running = a.s ? (a.s.running || 0) > 0 : false;
      const icon = iconHtmlOf((a.wdef && a.wdef.icon) ? a.wdef.icon : a.def.icon);
      const b = document.createElement("button");
      b.className = "mat-tray-widget" + (running ? " on" : "");
      b.title = a.def.name || a.stack; b.dataset.id = tw.id;
      b.innerHTML = `<span class="tw-ico">${icon}</span><span class="tw-dot"></span><span class="tw-x" title="Remove">✕</span>`;
      b.addEventListener("click", () => launchByKey("stack:" + a.stack));
      b.querySelector(".tw-x").addEventListener("click", (e) => { e.stopPropagation(); removeTaskbarWidget(tw.id); });
      b.addEventListener("contextmenu", (e) => { e.preventDefault(); showCtx(e.clientX, e.clientY, [
        { label: "Remove from taskbar", danger: true, action: () => removeTaskbarWidget(tw.id) }]); });
      trayWidgetsEl.appendChild(b);
    }
  }
  async function addTaskbarWidget(type) {
    const r = await (await fetch("/api/v1/desktop/taskbar-widgets/add", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ type }) })).json();
    taskbarWidgets.push(r.widget); renderTaskbarWidgets();
  }
  function removeTaskbarWidget(id) {
    taskbarWidgets = taskbarWidgets.filter(w => w.id !== id); renderTaskbarWidgets();
    fetch("/api/v1/desktop/taskbar-widgets/remove", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id }) }).catch(() => {});
  }
  function showAddTaskbarWidgetMenu(x, y) {
    const items = availableAppWidgets("taskbar").map(it => ({ label: it.label, action: () => addTaskbarWidget(it.type) }));
    showCtx(x, y, items.length ? items : [{ label: "No taskbar widgets available", action: () => {} }]);
  }
  // Open Settings and drop the user into a specific panel (creates a new window with a query,
  // or navigates the existing window's iframe to the same URL).
  function openSettingsPanel(name) {
    const sys = systemApps(); const s = sys.find(a => a.key === "settings"); if (!s) return;
    open({ key: "settings", title: s.title, url: s.url + "?panel=" + encodeURIComponent(name), iconHtml: s.iconHtml, width: parseInt(s.w||"1024",10), height: parseInt(s.h||"680",10) });
    // If it was already open, MatWM restored + brought it to front but did not reload — post a message.
    setTimeout(() => {
      document.querySelectorAll("#mat-windows iframe").forEach(f => { if ((f.src||"").includes("/apps/settings")) { try { f.contentWindow.postMessage({ type: "matos:goto-panel", panel: name }, location.origin); } catch (_) {} } });
    }, 60);
  }

  // Windows-11 style Start-button right-click: quick jump to Task Manager / Settings / Files / Backups.
  function openQuickMenu(x, y) {
    const sys = systemApps();
    const jump = k => { const a = sys.find(a => a.key === k); if (a) open({ key: a.key, title: a.title, url: a.url, iconHtml: a.iconHtml, width: parseInt(a.w||"1024",10), height: parseInt(a.h||"680",10) }); };
    const items = [];
    if (sys.find(a => a.key === "task-manager")) items.push({ label: "Task Manager", action: () => jump("task-manager") });
    if (sys.find(a => a.key === "files")) items.push({ label: "Files", action: () => jump("files") });
    if (sys.find(a => a.key === "network")) items.push({ label: "Network", action: () => jump("network") });
    if (sys.find(a => a.key === "backups")) items.push({ label: "Backups", action: () => jump("backups") });
    items.push({ sep: true });
    items.push({ label: "Add taskbar widget…", action: openWidgetPicker });
    items.push({ label: "Taskbar settings", action: () => openSettingsPanel("taskbar") });
    if (sys.find(a => a.key === "settings")) items.push({ label: "Settings", action: () => jump("settings") });
    items.push({ label: "Show desktop", action: () => window.MatWM && window.MatWM.showDesktop && window.MatWM.showDesktop() });
    showCtx(x, y, items);
  }

  // Show-desktop sliver at the far right of the taskbar.
  const showDesk = document.getElementById("mat-show-desktop");
  if (showDesk) showDesk.addEventListener("click", () => { if (window.MatWM && window.MatWM.showDesktop) window.MatWM.showDesktop(); });

  if (startBtn) {
    startBtn.addEventListener("click", (e) => { e.stopPropagation(); startMenu.hidden ? openStart() : closeStart(); });
    startBtn.addEventListener("contextmenu", (e) => { e.preventDefault(); closeStart(); openQuickMenu(e.clientX, e.clientY); });
    document.addEventListener("click", (e) => { if (!startMenu.hidden && !startMenu.contains(e.target) && !startBtn.contains(e.target) && !(tbSearchWrap && tbSearchWrap.contains(e.target))) closeStart(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") { closeStart(); hideCtx(); } });
    startSearch.addEventListener("input", () => renderStartMenu(startSearch.value));
    startSearch.addEventListener("keydown", (e) => { if (e.key === "Enter") { const f = startMenu.querySelector(".sm-item"); if (f) { e.preventDefault(); f.click(); } } });
    startMenu.addEventListener("click", (e) => {
      const head = e.target.closest(".sm-folder-head");
      if (head) { const fid = head.closest(".sm-folder").dataset.sfid; if (startFoldersCollapsed.has(fid)) startFoldersCollapsed.delete(fid); else startFoldersCollapsed.add(fid); renderStartMenu(startSearch ? startSearch.value : ""); return; }
      const it = e.target.closest(".sm-item"); if (it && !it.dataset.suppressClick) { e.preventDefault(); launchEl(it); closeStart(); if (tbSearch) tbSearch.value = ""; } if (it) delete it.dataset.suppressClick;
    });
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
    // Full context menu on start-menu items — same shape as right-clicking a desktop icon,
    // plus Start-menu folder options (add to / remove from / new folder; rename/delete on a header).
    startMenu.addEventListener("contextmenu", (e) => {
      const head = e.target.closest(".sm-folder-head");
      if (head) {
        e.preventDefault(); const fid = head.closest(".sm-folder").dataset.sfid;
        showCtx(e.clientX, e.clientY, [
          { label: "Rename folder", action: () => renameStartFolder(fid) },
          { label: "Delete folder", danger: true, action: () => deleteStartFolder(fid) }
        ]); return;
      }
      const it = e.target.closest(".sm-item"); if (!it) return;
      const key = it.dataset.key; if (!key) return;
      e.preventDefault();
      const items = appMenuItems(key, { after: closeStart });
      const inF = startFolders.find(f => (f.keys || []).includes(key));
      items.push({ sep: true });
      if (inF) items.push({ label: "Remove from folder", action: () => removeFromStartFolder(key) });
      else { for (const f of startFolders) items.push({ label: `Add to “${f.name}”`, action: () => addToStartFolder(key, f.id) }); items.push({ label: "New folder…", action: () => createStartFolderWith(key) }); }
      showCtx(e.clientX, e.clientY, items);
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

  // ---- One right-click menu for an app/stack key, used EVERYWHERE (desktop icon, start
  // menu, folder overlay, taskbar). The core app actions are always identical; `where`
  // only supplies the location-specific extras, so there is a single source of truth. ----
  function stackForKey(key) { return (key && key.startsWith("stack:")) ? stackData.find(x => x.name === key.slice(6)) : null; }
  function openStackSettings(s) { open({ key: "set:" + s.name, title: "Settings · " + stackTitle(s), url: stackSettingsUrl(s), width: 1024, height: 680 }); }
  function appMenuItems(key, where) {
    where = where || {};
    const after = where.after || (() => {});
    const s = stackForKey(key);
    const isOpen = !!(window.MatWM && window.MatWM.isOpen(key));
    const items = [];
    items.push({ label: isOpen ? "Bring to front" : "Open", action: () => { launchByKey(key); after(); } });
    if (s) {
      items.push({ label: "Settings", action: () => { after(); openStackSettings(s); } });
      const def = stackDef(s);
      if (def && def.actions && def.actions.length) { items.push({ sep: true }); for (const ac of def.actions) items.push({ label: ac.label, action: () => { after(); openAction(s, ac); } }); }
      items.push({ sep: true });
      if (s.anyRunning) { items.push({ label: "Stop", action: () => stackAction(s.name, "stop") }, { label: "Restart", action: () => stackAction(s.name, "restart") }); }
      if (!s.allRunning) items.push({ label: "Start", action: () => stackAction(s.name, "start") });
    } else if (isOpen) {
      items.push({ label: "Reset app", action: () => window.MatWM.reset(key) });
    }
    // Forget this app's remembered window size/position and snap it back to the default (if open).
    items.push({ label: "Reset window position", action: () => { if (window.MatWM) window.MatWM.resetGeometry(key); } });
    items.push({ sep: true }, { label: "Rename", action: () => renameIcon(key) });
    if (where.inFolder) {
      items.push({ label: "Remove from folder", danger: true, action: where.onRemoveFromFolder });
    } else {
      items.push({ sep: true });
      items.push(pins.has(key)
        ? { label: "Remove from desktop", danger: true, action: () => { unpin(key); after(); } }
        : { label: "Add to desktop", action: () => { pin(key); after(); } });
      items.push(isPinnedToTaskbar(key)
        ? { label: "Unpin from taskbar", action: () => unpinFromTaskbar(key) }
        : { label: "Pin to taskbar", action: () => pinToTaskbar(key) });
    }
    if (isOpen) items.push({ sep: true }, { label: "Close", danger: true, action: () => window.MatWM.closeKey(key) });
    return items;
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
          { label: "Delete folder", danger: true, action: async () => { if (await matDialog.confirm("Delete this folder? Its apps go back to the desktop.", { title: "Delete folder", danger: true, okLabel: "Delete" })) deleteFolder(fid); } },
        ]);
        return;
      }
      if (iconEl && iconEl.dataset.key) {
        const items = appMenuItems(iconEl.dataset.key, {});
        items.push({ label: "Auto-arrange icons", action: autoArrange });
        showCtx(e.clientX, e.clientY, items);
      } else {
        showCtx(e.clientX, e.clientY, [
          { label: "New folder", action: () => createFolderAt(e.clientX, e.clientY) },
          { label: "Add widget…", action: openWidgetPicker },
          { sep: true }, { label: "Auto-arrange icons", action: autoArrange }, { label: "Refresh", action: load }
        ]);
      }
    });
    document.addEventListener("click", () => hideCtx());
    document.addEventListener("scroll", hideCtx, true);
    window.addEventListener("resize", hideCtx); window.addEventListener("blur", hideCtx);

    // ---- Rubber-band multi-select on the empty desktop ----
    let bandEl = null, bandStart = null;
    layer.addEventListener("pointerdown", (e) => {
      if (e.pointerType === "mouse" && e.button !== 0) return;
      // Only when the click misses every icon.
      if (e.target.closest(".mat-app, .mat-widget")) return;
      if (!(e.ctrlKey || e.metaKey || e.shiftKey)) clearSelection();
      const r = layer.getBoundingClientRect();
      bandStart = { x: e.clientX - r.left, y: e.clientY - r.top, rectLeft: r.left, rectTop: r.top };
      bandEl = document.createElement("div"); bandEl.className = "mat-band";
      Object.assign(bandEl.style, { left: bandStart.x + "px", top: bandStart.y + "px", width: "0px", height: "0px" });
      layer.appendChild(bandEl);
      try { layer.setPointerCapture(e.pointerId); } catch (_) {}
    });
    layer.addEventListener("pointermove", (e) => {
      if (!bandEl || !bandStart) return;
      const x = e.clientX - bandStart.rectLeft, y = e.clientY - bandStart.rectTop;
      const left = Math.min(x, bandStart.x), top = Math.min(y, bandStart.y);
      const w = Math.abs(x - bandStart.x), h = Math.abs(y - bandStart.y);
      Object.assign(bandEl.style, { left: left + "px", top: top + "px", width: w + "px", height: h + "px" });
      // Test every icon's rect against the band, treating client coords consistently.
      const bx1 = left + bandStart.rectLeft, by1 = top + bandStart.rectTop, bx2 = bx1 + w, by2 = by1 + h;
      for (const [key, iconEl] of iconEls) {
        const r = iconEl.getBoundingClientRect();
        const hit = r.right >= bx1 && r.left <= bx2 && r.bottom >= by1 && r.top <= by2;
        setSelected(key, hit || (e.ctrlKey || e.shiftKey ? isSelected(key) : hit));
      }
    });
    const bandEnd = (e) => {
      if (!bandEl) return;
      try { layer.releasePointerCapture(e.pointerId); } catch (_) {}
      bandEl.remove(); bandEl = null; bandStart = null;
    };
    layer.addEventListener("pointerup", bandEnd);
    layer.addEventListener("pointercancel", bandEnd);

    // Escape / Delete: clear selection. Delete unpins each selected app (but not folders).
    document.addEventListener("keydown", (e) => {
      if (e.target && (e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA" || e.target.isContentEditable)) return;
      if (e.key === "Escape" && selectedKeys.size) clearSelection();
      if (e.key === "Delete" && selectedKeys.size) {
        const ks = [...selectedKeys];
        matDialog.confirm(`Remove ${ks.length} icon${ks.length===1?"":"s"} from the desktop?`, { title: "Remove from desktop", okLabel: "Remove" }).then(ok => {
          if (!ok) return;
          clearSelection(); for (const k of ks) if (!k.startsWith("folder:")) unpin(k);
        });
      }
    });
  }

  // ---- taskbar (open windows) context menu ----
  // Right-click on the taskbar. Attach to the whole footer so any empty area works,
  // then decide by what got hit:
  //  - a taskbar app button        → per-window menu (Reset / Close)
  //  - the Start button / search / tray widgets → leave alone (they have their own menus)
  //  - anything else (empty area)  → the Windows-11-style quick-menu
  const taskbar = document.getElementById("mat-taskbar");
  if (taskbar) taskbar.addEventListener("contextmenu", (e) => {
    // Don't hijack right-clicks on the UI widgets that already have their own menus.
    if (e.target.closest("#mat-start-btn, #mat-tb-search, #mat-bell, #mat-user-btn, #mat-user-menu, #mat-show-desktop, .mat-clock")) return;
    e.preventDefault();
    const t = e.target.closest(".mat-task");
    // Both a running-window button (data-winKey) and a pinned button (data-key) are just an
    // app key → the same unified menu as everywhere else.
    if (t && (t.dataset.winKey || t.dataset.key)) {
      showCtx(e.clientX, e.clientY, appMenuItems(t.dataset.winKey || t.dataset.key, {}));
      return;
    }
    // Empty taskbar → Win-11-style quick menu (Task Manager / Files / Network / Backups / Settings / Show desktop).
    openQuickMenu(e.clientX, e.clientY);
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
          showCtx(e.clientX, e.clientY, appMenuItems(k, {
            inFolder: true, after: closeIt,
            onRemoveFromFolder: () => removeFromFolder(f.id, k).then(() => renderGrid())
          })); });
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
  // App‑registered widgets: type "app:<stack>:<widgetId>". Size buckets → grid cells.
  const WG_SIZES = { small: { w: 3, h: 2 }, medium: { w: 4, h: 3 }, large: { w: 6, h: 4 } };
  function iconHtmlOf(ic) { return !ic ? CUBE : (/^(https?:|data:)/i.test(ic) ? `<img src="${escAttr(ic)}" alt="">` : `<span>${esc(ic)}</span>`); }
  function appWidgetDef(type) {
    if (!type || !type.startsWith("app:")) return null;
    const i1 = type.indexOf(":"), i2 = type.indexOf(":", i1 + 1); if (i2 < 0) return null;
    const stack = type.slice(i1 + 1, i2), wid = type.slice(i2 + 1);
    const s = stackData.find(x => x.name === stack); const appId = s ? stackApp(s) : null;
    const def = appId ? appDefs[appId] : null;
    const wdef = def && def.widgets ? def.widgets.find(w => w.id === wid) : null;
    return { stack, wid, s, appId, def, wdef };
  }
  function widgetSpec(type) {
    if (WIDGET_TYPES[type]) return WIDGET_TYPES[type];
    const a = appWidgetDef(type);
    if (a && a.wdef) { const sz = WG_SIZES[a.wdef.size] || WG_SIZES.small; return { title: a.wdef.name, w: sz.w, h: sz.h }; }
    return { w: 2, h: 2 };
  }
  // All widgets an installed app offers for a given surface ("desktop"|"taskbar").
  function availableAppWidgets(surface) {
    const out = [];
    for (const s of stackData) {
      const appId = stackApp(s); if (!appId) continue;
      const def = appDefs[appId]; if (!def || !def.widgets) continue;
      for (const w of def.widgets) {
        if ((w.surface || "desktop") !== surface) continue;
        out.push({ type: `app:${s.name}:${w.id}`, label: `${stackTitle(s)} – ${w.name}` });
      }
    }
    return out;
  }
  const WIDGET_META = {
    clock:      { icon: "🕐", desc: "Time & date" },
    resources:  { icon: "📊", desc: "CPU + memory graph" },
    cpu:        { icon: "🧮", desc: "CPU load" },
    memory:     { icon: "💾", desc: "Memory load" },
    containers: { icon: "📦", desc: "Running containers" },
  };
  // ---- Per-widget settings ----
  // Each widget type can declare a settings schema; the ⚙ on the widget opens a dialog built from it.
  // Values are stored as strings in the widget's Config map (persisted per user).
  const WIDGET_SETTINGS = {
    clock: {
      title: "Clock",
      fields: [
        { key: "face", label: "Watch face", type: "choice", default: "digital", options: [
            { v: "digital", t: "Digital", d: "09:41 + date" },
            { v: "analog",  t: "Analog",  d: "Hands on a dial" },
            { v: "text",    t: "Text",    d: "“Nine Forty One”, one word per line" },
          ] },
        { key: "seconds", label: "Show seconds", type: "bool", default: "false", when: c => (c.face || "digital") !== "text" },
        { key: "hour24",  label: "24-hour time", type: "bool", default: "false", when: c => (c.face || "digital") === "digital" },
      ],
    },
  };
  function widgetSettingsSchema(type){ return WIDGET_SETTINGS[type] || null; }
  function widgetDefaults(type){ const s = WIDGET_SETTINGS[type]; const o = {}; if (s) for (const f of s.fields) o[f.key] = String(f.default); return o; }

  // ---- Clock faces ----
  const NUM_ONES = ["","One","Two","Three","Four","Five","Six","Seven","Eight","Nine","Ten","Eleven","Twelve","Thirteen","Fourteen","Fifteen","Sixteen","Seventeen","Eighteen","Nineteen"];
  const NUM_TENS = ["","","Twenty","Thirty","Forty","Fifty"];
  function minuteWords(m){ if (m < 20) return NUM_ONES[m]; const t = NUM_TENS[Math.floor(m/10)], o = m%10; return o ? t + " " + NUM_ONES[o] : t; }
  // Spoken time in words, e.g. 3:00 → "Three o'Clock", 3:05 → "Three Oh Five", 3:45 → "Three Forty Five".
  function clockWords(d){
    let h = d.getHours() % 12; if (h === 0) h = 12; const m = d.getMinutes();
    if (m === 0) return NUM_ONES[h] + " o'Clock";
    if (m < 10)  return NUM_ONES[h] + " Oh " + NUM_ONES[m];
    return NUM_ONES[h] + " " + minuteWords(m);
  }
  function analogClockSvg(d, showSec){
    const m = d.getMinutes(), s = d.getSeconds(), h = (d.getHours() % 12) + m/60;
    const hand = (ang, len, w, col) => { const r = (ang - 90) * Math.PI/180; const x = 50 + len*Math.cos(r), y = 50 + len*Math.sin(r);
      return `<line x1="50" y1="50" x2="${x.toFixed(2)}" y2="${y.toFixed(2)}" stroke="${col}" stroke-width="${w}" stroke-linecap="round"/>`; };
    let ticks = ""; for (let i = 0; i < 12; i++){ const r = (i*30 - 90) * Math.PI/180;
      const x1 = 50+42*Math.cos(r), y1 = 50+42*Math.sin(r), x2 = 50+46*Math.cos(r), y2 = 50+46*Math.sin(r);
      ticks += `<line x1="${x1.toFixed(1)}" y1="${y1.toFixed(1)}" x2="${x2.toFixed(1)}" y2="${y2.toFixed(1)}" stroke="rgba(255,255,255,0.55)" stroke-width="${i%3===0?2:1}"/>`; }
    return `<svg viewBox="0 0 100 100" class="wg-analog"><circle cx="50" cy="50" r="47" fill="none" stroke="rgba(255,255,255,0.25)" stroke-width="2"/>${ticks}
      ${hand(h*30,26,4,"#fff")}${hand(m*6,38,3,"#fff")}${showSec ? hand(s*6,40,1.4,"var(--accent,#8b5cf6)") : ""}
      <circle cx="50" cy="50" r="2.6" fill="#fff"/></svg>`;
  }
  function renderClock(body, el, cfg){
    cfg = cfg || {}; const face = cfg.face || "digital"; const d = new Date();
    el.classList.toggle("wg-clock-analog", face === "analog");
    el.classList.toggle("wg-clock-text", face === "text");
    if (face === "analog"){ body.innerHTML = `<div class="wg-analog-wrap">${analogClockSvg(d, cfg.seconds === "true")}</div>`; return; }
    if (face === "text"){ const words = clockWords(d).split(" "); body.innerHTML = `<div class="wg-words">${words.map(w => `<span>${esc(w)}</span>`).join("")}</div>`; return; }
    const opts = cfg.hour24 === "true" ? { hour:"2-digit", minute:"2-digit", hour12:false } : { hour:"2-digit", minute:"2-digit" };
    if (cfg.seconds === "true") opts.second = "2-digit";
    body.innerHTML = `<div class="wg-clock">${d.toLocaleTimeString([], opts)}</div>
      <div class="wg-sub">${d.toLocaleDateString([], { weekday:"long", day:"2-digit", month:"long" })}</div>`;
  }

  const KIND_LABEL = { status: "Status", launcher: "Launcher", info: "Live panel", iframe: "Live panel" };
  function appWidgetCard(it) {
    const a = appWidgetDef(it.type);
    const icon = iconHtmlOf(a && a.wdef && a.wdef.icon ? a.wdef.icon : (a && a.def ? a.def.icon : ""));
    const kind = a && a.wdef ? (KIND_LABEL[a.wdef.kind] || a.wdef.kind) : "";
    const appName = a && a.def ? a.def.name : "";
    return { type: it.type, icon, name: (a && a.wdef ? a.wdef.name : it.label), sub: appName + (kind ? " · " + kind : "") };
  }
  // A bold modal gallery of every widget the user can add (built‑ins + each installed app's widgets).
  function openWidgetPicker() {
    document.querySelector(".wgp-back")?.remove();
    const back = document.createElement("div"); back.className = "wgp-back";
    back.innerHTML = `<div class="wgp-card-wrap" role="dialog" aria-label="Add a widget">
        <div class="wgp-head"><div><div class="wgp-eyebrow">Personalise</div><h2>Add a widget</h2>
          <p class="wgp-hint">Place a widget on your desktop or in the taskbar. Remove one anytime with the ✕ on its corner (or right‑click it).</p></div>
          <button class="wgp-x" aria-label="Close">✕</button></div>
        <div class="wgp-body"></div></div>`;
    const body = back.querySelector(".wgp-body");
    const close = () => back.remove();
    back.querySelector(".wgp-x").addEventListener("click", close);
    back.addEventListener("click", (e) => { if (e.target === back) close(); });
    const onEsc = (e) => { if (e.key === "Escape") { close(); document.removeEventListener("keydown", onEsc); } };
    document.addEventListener("keydown", onEsc);
    const section = (label, cards, onPick) => {
      if (!cards.length) return;
      const h = document.createElement("div"); h.className = "wgp-sec"; h.textContent = label; body.appendChild(h);
      const grid = document.createElement("div"); grid.className = "wgp-grid";
      for (const c of cards) {
        const b = document.createElement("button"); b.className = "wgp-card";
        b.innerHTML = `<span class="wgp-ico">${c.icon}</span><span class="wgp-tt"><span class="wgp-name">${esc(c.name)}</span>${c.sub ? `<span class="wgp-sub">${esc(c.sub)}</span>` : ""}</span>`;
        b.addEventListener("click", () => { onPick(c.type); close(); });
        grid.appendChild(b);
      }
      body.appendChild(grid);
    };
    const builtins = Object.entries(WIDGET_TYPES).map(([k, v]) => ({ type: k, icon: (WIDGET_META[k] || {}).icon || "🔧", name: v.title, sub: (WIDGET_META[k] || {}).desc || "System widget" }));
    section("Desktop", builtins.concat(availableAppWidgets("desktop").map(appWidgetCard)), addWidget);
    section("Taskbar", availableAppWidgets("taskbar").map(appWidgetCard), addTaskbarWidget);
    document.body.appendChild(back);
    requestAnimationFrame(() => back.classList.add("open"));
  }
  // Rolling history for the resource-monitor widget (aggregate across all running containers).
  const wgCpuHist = [], wgMemHist = []; const WG_HIST = 40;
  // ONE aggregated SSE stream for all resource widgets. Opening a stream per container would use up
  // the browser's ~6-connections-per-host budget (HTTP/1.1) and stall every other request — that's
  // what made opening app windows hang on a blank page once enough containers were running.
  let wgAggSrc = null;
  let wgAgg = { cpu: 0, mem: 0, memLim: 0 };
  function wgSyncStreams(){
    const want = widgets.some(w => w.type === "resources" || w.type === "cpu" || w.type === "memory");
    if (!want) { if (wgAggSrc) { try { wgAggSrc.close(); } catch (_) {} wgAggSrc = null; } wgAgg = { cpu: 0, mem: 0, memLim: 0 }; return; }
    if (wgAggSrc) return; // already streaming
    const src = new EventSource("/api/v1/docker/stats/stream");
    src.addEventListener("stat", e => { try { const s = JSON.parse(e.data); wgAgg = { cpu: +s.cpuPercent||0, mem: +s.memoryBytes||0, memLim: +s.memoryLimitBytes||0 }; } catch (_) {} });
    src.onerror = () => {};
    wgAggSrc = src;
  }
  function wgAggregate(){ return wgAgg; }
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
      const size = widgetSpec(w.type);
      el.style.width  = (size.w * CELL_W) + "px";
      el.style.height = (size.h * CELL_H - 8) + "px";
      el.style.left = (w.x || MARGIN) + "px"; el.style.top = (w.y || MARGIN) + "px";
    }
    widgetTick();
  }
  function buildWidget(w){
    const el = document.createElement("div"); el.dataset.id = w.id;
    const a = appWidgetDef(w.type);
    const kind = a && a.wdef ? a.wdef.kind : "";
    el.className = "mat-widget " + (a ? ("mat-widget-app mat-widget-" + (kind || "status")) : ("mat-widget-" + w.type));
    const settings = widgetSettingsSchema(w.type);
    el.innerHTML = `<div class="mat-widget-tools">${settings ? `<button class="mat-widget-gear" title="Widget settings" aria-label="Widget settings">⚙</button>` : ""}<button class="mat-widget-x" title="Remove widget" aria-label="Remove widget">✕</button></div><div class="mat-widget-body" data-body></div>`;
    el.querySelector(".mat-widget-x").addEventListener("click", (e) => { e.stopPropagation(); removeWidget(w.id); });
    const gearBtn = el.querySelector(".mat-widget-gear");
    if (gearBtn) gearBtn.addEventListener("click", (e) => { e.stopPropagation(); openWidgetSettings(w); });
    enableWidgetDrag(el, w);
    // iframe app widgets (and info widgets with a URL) embed the app once — not re-rendered each tick.
    if (a && a.wdef && a.wdef.url && (kind === "iframe" || kind === "info")) {
      el.querySelector("[data-body]").innerHTML =
        `<iframe class="wg-frame" src="/apps/starting?stack=${encodeURIComponent(a.stack)}&path=${encodeURIComponent(a.wdef.url)}" referrerpolicy="no-referrer"></iframe>`;
    }
    // Declarative app widgets (status/launcher) open the app on click.
    if (a && a.wdef && (kind === "status" || kind === "launcher")) {
      el.style.cursor = "pointer";
      el.addEventListener("click", (e) => { if (e.target.closest("button,a")) return; if (!el.classList.contains("dragging")) launchByKey("stack:" + a.stack); });
    }
    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      showCtx(e.clientX, e.clientY, [{ label: "Remove widget", danger: true, action: () => removeWidget(w.id) }]);
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
        renderClock(body, el, w.config || {});
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
      } else if (w.type.startsWith("app:")) {
        const a = appWidgetDef(w.type);
        if (!a || !a.def) { body.innerHTML = `<div class="wg-lbl">App not installed</div>`; continue; }
        const kind = a.wdef ? a.wdef.kind : "status";
        if (a.wdef && a.wdef.url && (kind === "iframe" || kind === "info")) continue; // iframe built in buildWidget
        const running = a.s ? (a.s.running || 0) : 0, total = a.s ? (a.s.total || 0) : 0;
        const icon = iconHtmlOf((a.wdef && a.wdef.icon) ? a.wdef.icon : a.def.icon);
        const title = a.def.name || a.stack;
        if (kind === "launcher") {
          body.innerHTML = `<div class="wg-launch"><span class="wg-ico">${icon}</span><span class="wg-name">${esc(title)}</span></div>`;
        } else {
          const on = running > 0;
          body.innerHTML = `<div class="wg-status"><span class="wg-ico">${icon}</span>
            <div class="wg-st-txt"><span class="wg-name">${esc(title)}</span>
              <span class="wg-pill ${on ? 'on' : 'off'}">${on ? 'running' : 'stopped'}${total > 1 ? ` · ${running}/${total}` : ''}</span></div></div>`;
        }
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
  // Clock widgets update every second (analog second-hand / digital seconds / minute rollover).
  function tickClocks(){ for (const w of widgets){ if (w.type !== "clock") continue; const el = widgetEls.get(w.id); if (!el) continue; const body = el.querySelector("[data-body]"); if (body) renderClock(body, el, w.config || {}); } }
  setInterval(tickClocks, 1000);

  // ---- Widget settings dialog ----
  function openWidgetSettings(w){
    const schema = widgetSettingsSchema(w.type); if (!schema) return;
    document.querySelector(".wgp-back")?.remove();
    const cfg = Object.assign(widgetDefaults(w.type), w.config || {});
    const back = document.createElement("div"); back.className = "wgp-back";
    back.innerHTML = `<div class="wgp-card-wrap wgs-wrap" role="dialog" aria-label="Widget settings">
        <div class="wgp-head"><div><div class="wgp-eyebrow">Widget</div><h2>${esc(schema.title)} settings</h2>
          <p class="wgp-hint">Personalise this widget — changes apply instantly.</p></div>
          <button class="wgp-x" aria-label="Close">✕</button></div>
        <div class="wgp-body"><div class="wgs-form"></div></div></div>`;
    const form = back.querySelector(".wgs-form");
    const close = () => { back.remove(); document.removeEventListener("keydown", onEsc); };
    const onEsc = (e) => { if (e.key === "Escape") close(); };
    back.querySelector(".wgp-x").addEventListener("click", close);
    back.addEventListener("click", (e) => { if (e.target === back) close(); });
    document.addEventListener("keydown", onEsc);

    async function persist(){
      w.config = Object.assign({}, cfg);
      const el = widgetEls.get(w.id);
      if (el){ const body = el.querySelector("[data-body]"); if (w.type === "clock" && body) renderClock(body, el, w.config); else widgetTick(); }
      try { await fetch("/api/v1/desktop/widgets/config", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id: w.id, config: w.config }) }); } catch(_){}
    }
    function draw(){
      form.innerHTML = "";
      for (const f of schema.fields){
        if (f.when && !f.when(cfg)) continue;
        const row = document.createElement("div"); row.className = "wgs-row";
        if (f.type === "choice"){
          row.innerHTML = `<div class="wgs-lbl">${esc(f.label)}</div><div class="wgs-choices"></div>`;
          const wrap = row.querySelector(".wgs-choices");
          for (const o of f.options){
            const b = document.createElement("button"); b.type = "button"; b.className = "wgs-choice" + (String(cfg[f.key]) === o.v ? " sel" : "");
            b.innerHTML = `<span class="wgs-choice-t">${esc(o.t)}</span>${o.d ? `<span class="wgs-choice-d">${esc(o.d)}</span>` : ""}`;
            b.addEventListener("click", () => { cfg[f.key] = o.v; draw(); persist(); });
            wrap.appendChild(b);
          }
        } else if (f.type === "bool"){
          row.classList.add("wgs-toggle");
          const on = String(cfg[f.key]) === "true";
          row.innerHTML = `<span class="wgs-lbl">${esc(f.label)}</span>`;
          const t = document.createElement("button"); t.type = "button"; t.className = "wgs-switch" + (on ? " on" : "");
          t.setAttribute("role","switch"); t.setAttribute("aria-checked", on ? "true" : "false"); t.innerHTML = "<span></span>";
          t.addEventListener("click", () => { cfg[f.key] = on ? "false" : "true"; draw(); persist(); });
          row.appendChild(t);
        }
        form.appendChild(row);
      }
    }
    draw();
    document.body.appendChild(back);
    requestAnimationFrame(() => back.classList.add("open"));
  }
  async function addWidget(type){
    const spec = widgetSpec(type);
    // Place it near the top-right corner of the layer, but never off-screen
    const lw = (layer && layer.clientWidth) || window.innerWidth;
    const x = Math.max(MARGIN, lw - spec.w*CELL_W - MARGIN);
    const y = MARGIN;
    const r = await (await fetch("/api/v1/desktop/widgets/add", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ type, x, y, w: spec.w, h: spec.h }) })).json();
    widgets.push(r.widget); reconcileDesktop();
  }
  async function removeWidget(id) {
    widgets = widgets.filter(x => x.id !== id); reconcileDesktop();
    fetch("/api/v1/desktop/widgets/remove", { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify({ id }) }).catch(()=>{});
  }
  function showAddWidgetMenu(x, y){
    const items = Object.entries(WIDGET_TYPES).map(([k, v]) => ({ label: v.title, action: () => addWidget(k) }));
    const appWidgets = availableAppWidgets("desktop");
    if (appWidgets.length) { items.push({ sep: true }); for (const it of appWidgets) items.push({ label: it.label, action: () => addWidget(it.type) }); }
    showCtx(x, y, items);
  }

  // ---- init ----
  async function init() {
    try { const r = await fetch("/api/v1/desktop/layout", { headers: { Accept: "application/json" } }); if (r.ok) { const d = await r.json(); layout = d.positions || {}; pins = new Set(d.pins || []); folders = d.folders || []; widgets = d.widgets || []; labels = d.labels || {}; taskbarPins = new Set(d.taskbarPins || []); taskbarWidgets = d.taskbarWidgets || []; startFolders = d.startFolders || []; } }
    catch (_) { layout = {}; }
    // Personal preferences (wallpaper/theme fallback + taskbar).
    try { const p = await (await fetch("/api/v1/desktop/prefs")).json(); applyTaskbarPrefs(p); } catch (_) {}
    await loadDefs();
    buildSystem(); await loadHidden(); await load(); setInterval(load, 15000);
    if (window.MatWM) { window.MatWM.setPinnedKeys([...taskbarPins]); window.MatWM.onChange(renderTaskbarPins); }
    renderTaskbarPins(); renderTaskbarWidgets();
  }
  init();

  // ---- messages from app windows ----
  window.addEventListener("message", (e) => {
    if (e.origin !== location.origin) return;
    const m = e.data; if (!m) return;
    if (m.type === "matos:wallpaper" && m.wallpaper) {
      const wp = document.getElementById("mat-wallpaper");
      if (wp) { const style = m.style || (wp.className.match(/wps-(\S+)/) || [])[1] || "fill"; wp.className = "wp-" + m.wallpaper + " wps-" + style; }
    }
    if (m.type === "matos:accent") {
      const hex = m.accent || "";
      const props = ["--accent", "--accent-2", "--accent-grad", "--mat-accent", "--mat-accent-2"];
      const apply = (root) => { if (hex) props.forEach(p => root.style.setProperty(p, hex)); else props.forEach(p => root.style.removeProperty(p)); };
      apply(document.documentElement);
      // Live-propagate into every currently open (same-origin, internal) window too.
      document.querySelectorAll("#mat-windows iframe").forEach(f => { try { if (f.contentDocument) apply(f.contentDocument.documentElement); } catch (_) {} });
    }
    if (m.type === "matos:taskbar" && m.prefs) applyTaskbarPrefs(m.prefs);
    if (m.type === "matos:theme" && m.theme) {
      const html = document.documentElement; const t = String(m.theme).toLowerCase();
      if (t === "auto") { html.setAttribute("data-theme-user", "auto"); const dark = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches; html.setAttribute("data-theme", dark?"dark":"light"); }
      else if (t === "light" || t === "dark") { html.setAttribute("data-theme-user", t); html.setAttribute("data-theme", t); }
    }
    if (m.type === "matos:open" && m.url) {
      const opts = { key: m.key || m.url, title: m.title || "App", url: m.url, width: m.width || 1024, height: m.height || 680 };
      if (m.ephemeralId) opts.onClose = () => { try { fetch("/api/v1/store/close-ephemeral", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id: m.ephemeralId }) }); } catch (_) {} };
      open(opts);
    }
    if (m.type === "matos:close" && m.key) { window.MatWM.closeKey(m.key); }
    if (m.type === "matos:refresh") { burstReload(); broadcast({ type: "matos:reload" }); }
    if (m.type === "matos:hidden-changed") { loadHidden().then(load); }
    // The install wizard fires this the moment the user clicks Install: show a
    // placeholder icon on the desktop immediately, iOS-style.
    if (m.type === "matos:installing" && m.appId) {
      pendingInstalls.set(m.installId, { appId: m.appId, name: m.name || m.appId, icon: m.icon || "" });
      // A rough placement near the top-left free area, so the user sees it right away.
      const key = "pending:" + m.installId;
      layout[key] = freeSlot();
      reconcileDesktop(); renderStartMenu(startSearch ? startSearch.value : "");
    }
    if (m.type === "matos:install-failed" && m.installId) {
      pendingInstalls.delete(m.installId);
      const key = "pending:" + m.installId; delete layout[key];
      reconcileDesktop(); renderStartMenu(startSearch ? startSearch.value : "");
    }
  });

  // relay a message to every open app window (so e.g. the Store refreshes after an install)
  function broadcast(msg) {
    document.querySelectorAll("#mat-windows iframe").forEach(f => { try { f.contentWindow.postMessage(msg, location.origin); } catch (_) {} });
  }

  // ---- Notifications ----
  const bellBtn = document.getElementById("mat-bell");
  const bellBadge = document.getElementById("mat-bell-badge");
  const notifyPanel = document.getElementById("mat-notify");
  const notifyList = document.getElementById("mat-notify-list");
  const toastLayer = document.getElementById("mat-toasts");
  let notifyLastId = null;

  function escN(s) { return String(s == null ? "" : s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }
  function fmtWhen(iso) { try { return new Date(iso).toLocaleString(); } catch (_) { return ""; } }
  function iconFor(kind) { return { info: "ℹ", success: "✓", warning: "▲", error: "✕" }[kind] || "ℹ"; }

  function showToast({ id, kind, title, body }) {
    if (!toastLayer) return;
    const el = document.createElement("div"); el.className = "mat-toast"; if (id) el.dataset.id = id;
    el.innerHTML = `<span class="k ${escN(kind)}">${iconFor(kind)}</span>
      <div><div class="t">${escN(title)}</div>${body ? `<div class="b">${escN(body)}</div>` : ""}</div>
      <button class="x" title="Dismiss">×</button>`;
    el.querySelector(".x").addEventListener("click", () => el.remove());
    toastLayer.appendChild(el);
    setTimeout(() => el.remove(), 6000);
  }

  function renderNotifications(items, unread) {
    if (!notifyList) return;
    if (!items.length) { notifyList.innerHTML = `<div class="mat-notify-empty">No notifications yet.</div>`; }
    else notifyList.innerHTML = items.map(n => `<div class="mat-notify-item ${n.read ? "" : "unread"}" data-id="${n.id}">
      <span class="k ${escN(String(n.kind || "info").toLowerCase())}">${iconFor(String(n.kind || "info").toLowerCase())}</span>
      <div><div class="t">${escN(n.title)}</div>${n.body ? `<div class="b">${escN(n.body)}</div>` : ""}<div class="w">${escN(fmtWhen(n.createdUtc))}${n.source ? " · " + escN(n.source) : ""}</div></div>
      <button class="x" title="Dismiss">×</button></div>`).join("");
    notifyList.querySelectorAll(".x").forEach(b => b.addEventListener("click", async ev => {
      ev.stopPropagation();
      const id = b.closest(".mat-notify-item").dataset.id;
      try { await fetch("/api/v1/notifications/dismiss", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id }) }); } catch (_) {}
      loadNotifications();
    }));
    if (unread > 0) { bellBadge.hidden = false; bellBadge.textContent = unread > 99 ? "99+" : String(unread); }
    else bellBadge.hidden = true;
  }

  async function loadNotifications() {
    try {
      const d = await (await fetch("/api/v1/notifications/list")).json();
      const items = d.items || [];
      // Toast the newest one if it's new since we last checked.
      if (items.length && items[0].id !== notifyLastId) {
        // Skip toasting on the very first load — only for genuinely new arrivals.
        if (notifyLastId !== null && !items[0].read) showToast({ id: items[0].id, kind: String(items[0].kind || "info").toLowerCase(), title: items[0].title, body: items[0].body });
        notifyLastId = items[0].id;
      } else if (!items.length) notifyLastId = null;
      renderNotifications(items, d.unread || 0);
    } catch (_) {}
  }

  if (bellBtn) {
    bellBtn.addEventListener("click", async (e) => {
      e.stopPropagation();
      const opening = notifyPanel.hidden;
      notifyPanel.hidden = !opening;
      if (opening) { positionNearAnchor(notifyPanel, bellBtn); await loadNotifications();
        // Auto-mark-all read after opening the panel.
        try { await fetch("/api/v1/notifications/mark-all-read", { method: "POST" }); } catch (_) {}
        // Refresh the badge (should be 0 now) without wiping the list.
        try { const d = await (await fetch("/api/v1/notifications/list")).json(); renderNotifications(d.items || [], d.unread || 0); } catch (_) {}
      }
    });
    document.addEventListener("click", (e) => { if (!notifyPanel.hidden && !notifyPanel.contains(e.target) && e.target !== bellBtn && !bellBtn.contains(e.target)) notifyPanel.hidden = true; });
    document.getElementById("mat-notify-mark-all").addEventListener("click", async () => { try { await fetch("/api/v1/notifications/mark-all-read", { method: "POST" }); } catch (_) {} loadNotifications(); });
    document.getElementById("mat-notify-clear").addEventListener("click", async () => { if (!await matDialog.confirm("Clear all notifications?", { title: "Clear notifications", okLabel: "Clear" })) return; try { await fetch("/api/v1/notifications/clear", { method: "POST" }); } catch (_) {} loadNotifications(); });
    loadNotifications(); setInterval(loadNotifications, 15000);
  }

  // Anyone (any child app window) can post a toast via matos:toast.
  window.addEventListener("message", (e) => {
    if (e.origin !== location.origin) return;
    const m = e.data; if (!m) return;
    if (m.type === "matos:toast") showToast({ kind: (m.kind || "info").toLowerCase(), title: m.title || "", body: m.body || "" });
  });
})();
