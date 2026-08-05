/* matOS window manager — vanilla JS, no dependencies.
   Manages draggable/resizable windows that host an iframe (app UI or internal page). */
(function () {
  "use strict";

  const MIN_W = 320, MIN_H = 220, BAR_H = 44;
  let z = 20, seq = 0;
  const wins = new Map(); // key/id -> state

  const layer = () => document.getElementById("mat-windows");
  const dock = () => document.getElementById("mat-tasks");

  function bringToFront(w) {
    w.el.style.zIndex = String(++z);
    for (const other of wins.values()) other.el.classList.toggle("focused", other === w);
    syncDock();
  }

  // A click inside an app's iframe doesn't bubble a pointerdown to the window (the iframe swallows
  // it), so clicking the content wouldn't raise the window — only the title bar/chrome would. When
  // focus moves into an iframe the parent window blurs and document.activeElement becomes that
  // iframe, so raise whichever window owns it.
  window.addEventListener("blur", () => {
    setTimeout(() => {
      const ae = document.activeElement;
      if (!ae || ae.tagName !== "IFRAME") return;
      for (const w of wins.values())
        if (w.el.querySelector(".mat-frame") === ae && !w.minimized) { bringToFront(w); break; }
    }, 0);
  });

  function nextPosition() {
    const n = wins.size % 8;
    const baseX = Math.max(24, (window.innerWidth - 900) / 2);
    const baseY = Math.max(64, (window.innerHeight - 600) / 3);
    return { x: baseX + n * 32, y: baseY + n * 28 };
  }

  function open(opts) {
    const key = opts.key || ("w" + (++seq));
    if (wins.has(key)) { const w = wins.get(key); restore(w); bringToFront(w); return w; }

    const pos = nextPosition();
    const width = Math.min(opts.width || 960, window.innerWidth - 48);
    const height = Math.min(opts.height || 620, window.innerHeight - 96);
    // External web apps (a container's own origin) may block iframe embedding
    // (X-Frame-Options / CSP frame-ancestors) -> give them an address bar with
    // a reliable "open in new tab". Internal matOS pages ("/apps/...") embed fine.
    const isExternal = !!opts.url && (/^https?:\/\//i.test(opts.url) || opts.url.startsWith("//"));

    const el = document.createElement("section");
    el.className = "mat-window";
    el.style.left = pos.x + "px";
    el.style.top = pos.y + "px";
    el.style.width = width + "px";
    el.style.height = height + "px";
    el.setAttribute("role", "dialog");
    el.setAttribute("aria-label", opts.title || "Window");

    const iconMarkup = opts.iconHtml
      ? `<span class="mat-win-icon mat-win-icon-svg">${opts.iconHtml}</span>`
      : opts.icon
        ? `<img class="mat-win-icon" src="${opts.icon}" alt="" />`
        : `<span class="mat-win-icon mat-win-icon-fallback">${(opts.title || "?").slice(0, 1).toUpperCase()}</span>`;

    el.innerHTML = `
      <header class="mat-titlebar">
        ${iconMarkup}
        <span class="mat-title" title="${escapeAttr(opts.title || "")}">${escapeHtml(opts.title || "")}</span>
        <div class="mat-win-actions">
          ${!isExternal ? `<button class="mat-win-btn mat-refresh" title="Refresh" aria-label="Refresh">&#8635;</button>` : ""}
          ${isExternal ? `<button class="mat-win-btn mat-addr-toggle" title="Show address bar" aria-label="Toggle address bar">&#128279;</button><button class="mat-win-btn mat-open-ext" title="Open in new tab" aria-label="Open in new tab">&#8599;</button>` : ""}
          <button class="mat-win-btn mat-min" title="Minimize" aria-label="Minimize">&#8211;</button>
          <button class="mat-win-btn mat-max" title="Maximize" aria-label="Maximize">&#9723;</button>
          <button class="mat-win-btn mat-close" title="Close" aria-label="Close">&#10005;</button>
        </div>
      </header>
      <div class="mat-win-body">
        ${isExternal ? `<div class="mat-appbar" hidden>
          <span class="mat-appbar-url" title="${escapeAttr(opts.url)}">${escapeHtml(opts.url)}</span>
          <button class="mat-appbar-open" title="Open in new tab">Open in new tab &#8599;</button>
        </div>` : ""}
        <div class="mat-frame-area">
          <div class="mat-win-loading"><span class="mat-spinner"></span></div>
          <iframe class="mat-frame" src="${escapeAttr(opts.url || "about:blank")}"
                  referrerpolicy="no-referrer" allow="clipboard-read; clipboard-write"></iframe>
          <div class="mat-frame-shield" hidden></div>
        </div>
      </div>
      ${resizeHandles()}
    `;

    layer().appendChild(el);

    const w = { key, el, opts, maximized: false, minimized: false, rect: null };
    wins.set(key, w);

    // frame load handling
    const frame = el.querySelector(".mat-frame");
    const loading = el.querySelector(".mat-win-loading");
    frame.addEventListener("load", () => { loading.style.display = "none"; }, { once: false });
    setTimeout(() => { loading.style.display = "none"; }, 6000); // safety for X-Frame-blocked apps

    // wire controls
    el.querySelector(".mat-close").addEventListener("click", () => close(w));
    el.querySelector(".mat-min").addEventListener("click", () => minimize(w));
    el.querySelector(".mat-max").addEventListener("click", () => toggleMax(w));
    const refreshBtn = el.querySelector(".mat-refresh");
    if (refreshBtn) refreshBtn.addEventListener("click", () => reset(w.key));
    el.querySelectorAll(".mat-open-ext, .mat-appbar-open").forEach(b =>
      b.addEventListener("click", () => window.open(opts.url, "_blank", "noopener")));
    const addrToggle = el.querySelector(".mat-addr-toggle"), appbar = el.querySelector(".mat-appbar");
    if (addrToggle && appbar) addrToggle.addEventListener("click", () => { appbar.hidden = !appbar.hidden; addrToggle.classList.toggle("on", !appbar.hidden); });

    el.addEventListener("pointerdown", () => bringToFront(w), true);
    makeDraggable(w);
    makeResizable(w);

    const bar = el.querySelector(".mat-titlebar");
    bar.addEventListener("dblclick", (e) => { if (!e.target.closest(".mat-win-btn") && !e.target.closest(".mat-win-icon")) toggleMax(w); });
    // Double-clicking the window icon (far left) closes the window, like the Windows system menu.
    const winIcon = el.querySelector(".mat-win-icon");
    if (winIcon) winIcon.addEventListener("dblclick", (e) => { e.stopPropagation(); close(w); });

    bringToFront(w);
    syncDock();
    return w;
  }

  function close(w) {
    w.el.remove();
    wins.delete(w.key);
    if (typeof w.opts.onClose === "function") { try { w.opts.onClose(); } catch (_) {} }
    syncDock();
  }

  function minimize(w) {
    w.minimized = true;
    w.el.classList.add("minimized");
    syncDock();
  }

  function restore(w) {
    w.minimized = false;
    w.el.classList.remove("minimized");
  }

  function toggleMax(w) {
    if (w.maximized) {
      w.maximized = false;
      w.el.classList.remove("maximized");
      if (w.rect) Object.assign(w.el.style, { left: w.rect.left, top: w.rect.top, width: w.rect.width, height: w.rect.height });
    } else {
      w.rect = { left: w.el.style.left, top: w.el.style.top, width: w.el.style.width, height: w.el.style.height };
      w.maximized = true;
      w.el.classList.add("maximized");
    }
  }

  function shield(on) { for (const w of wins.values()) w.el.querySelector(".mat-frame-shield").hidden = !on; }

  function makeDraggable(w) {
    const bar = w.el.querySelector(".mat-titlebar");
    let sx, sy, ox, oy, dragging = false;
    bar.addEventListener("pointerdown", (e) => {
      if (e.target.closest(".mat-win-btn") || w.maximized) return;
      dragging = true; shield(true);
      sx = e.clientX; sy = e.clientY;
      ox = parseFloat(w.el.style.left); oy = parseFloat(w.el.style.top);
      bar.setPointerCapture(e.pointerId);
    });
    bar.addEventListener("pointermove", (e) => {
      if (!dragging) return;
      let nx = ox + (e.clientX - sx), ny = oy + (e.clientY - sy);
      nx = Math.min(Math.max(nx, -w.el.offsetWidth + 120), window.innerWidth - 80);
      ny = Math.min(Math.max(ny, 0), window.innerHeight - 40);
      w.el.style.left = nx + "px"; w.el.style.top = ny + "px";
    });
    const end = (e) => { if (dragging) { dragging = false; shield(false); try { bar.releasePointerCapture(e.pointerId); } catch (_) {} } };
    bar.addEventListener("pointerup", end);
    bar.addEventListener("pointercancel", end);
  }

  function makeResizable(w) {
    w.el.querySelectorAll(".mat-resize").forEach((h) => {
      const dir = h.dataset.dir;
      let sx, sy, sw, sh, sl, st, active = false;
      h.addEventListener("pointerdown", (e) => {
        if (w.maximized) return;
        active = true; shield(true); e.stopPropagation();
        sx = e.clientX; sy = e.clientY;
        sw = w.el.offsetWidth; sh = w.el.offsetHeight;
        sl = parseFloat(w.el.style.left); st = parseFloat(w.el.style.top);
        h.setPointerCapture(e.pointerId);
      });
      h.addEventListener("pointermove", (e) => {
        if (!active) return;
        const dx = e.clientX - sx, dy = e.clientY - sy;
        if (dir.includes("e")) w.el.style.width = Math.max(MIN_W, sw + dx) + "px";
        if (dir.includes("s")) w.el.style.height = Math.max(MIN_H, sh + dy) + "px";
        if (dir.includes("w")) { const nw = Math.max(MIN_W, sw - dx); w.el.style.width = nw + "px"; w.el.style.left = (sl + (sw - nw)) + "px"; }
        if (dir.includes("n")) { const nh = Math.max(MIN_H, sh - dy); w.el.style.height = nh + "px"; w.el.style.top = (st + (sh - nh)) + "px"; }
      });
      const end = (e) => { if (active) { active = false; shield(false); try { h.releasePointerCapture(e.pointerId); } catch (_) {} } };
      h.addEventListener("pointerup", end);
      h.addEventListener("pointercancel", end);
    });
  }

  let pinnedKeys = new Set();
  const changeListeners = [];
  function notifyChange() { for (const cb of changeListeners) { try { cb(); } catch (_) {} } }

  function syncDock() {
    const d = dock();
    if (!d) return;
    d.innerHTML = "";
    for (const w of wins.values()) {
      if (pinnedKeys.has(w.key)) continue; // pinned apps render their own (always-present) button
      const b = document.createElement("button");
      b.className = "mat-task" + (w.el.classList.contains("focused") && !w.minimized ? " active" : "");
      b.title = w.opts.title || "";
      b.dataset.winKey = w.key;
      b.innerHTML = (w.opts.iconHtml ? `<span class="mat-task-ico mat-task-ico-svg">${w.opts.iconHtml}</span>`
                    : w.opts.icon ? `<img src="${w.opts.icon}" alt=""/>`
                    : `<span class="mat-task-ico">${(w.opts.title || "?").slice(0,1)}</span>`) +
                    `<span class="mat-task-label">${escapeHtml(w.opts.title || "")}</span>`;
      b.addEventListener("click", () => toggleFocusOrMinimize(w.key));
      d.appendChild(b);
    }
    notifyChange();
  }

  // ---- Taskbar-pin support: state queries + the same click behaviour regular buttons use ----
  function setPinnedKeys(keys) { pinnedKeys = new Set(keys || []); syncDock(); }
  function isOpen(key) { return wins.has(key); }
  function getState(key) { const w = wins.get(key); return w ? { minimized: w.minimized, focused: w.el.classList.contains("focused") } : null; }
  function toggleFocusOrMinimize(key) {
    const w = wins.get(key); if (!w) return;
    if (w.minimized) { restore(w); bringToFront(w); }
    else if (w.el.classList.contains("focused")) { minimize(w); }
    else { bringToFront(w); }
  }
  function onChange(cb) { if (typeof cb === "function") changeListeners.push(cb); }

  function resizeHandles() {
    return ["n","s","e","w","ne","nw","se","sw"]
      .map(d => `<div class="mat-resize mat-resize-${d}" data-dir="${d}"></div>`).join("");
  }

  function escapeHtml(s) { return String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }
  function escapeAttr(s) { return String(s).replace(/"/g, "&quot;"); }

  function closeKey(key) { const w = wins.get(key); if (w) close(w); }

  // "Show desktop": if any window is currently visible, minimize them all; otherwise restore.
  let restoreSnapshot = null;
  function showDesktop() {
    const visible = [...wins.values()].filter(w => !w.minimized);
    if (visible.length) {
      restoreSnapshot = visible.map(w => w.key);
      for (const w of visible) minimize(w);
    } else if (restoreSnapshot) {
      for (const key of restoreSnapshot) { const w = wins.get(key); if (w) { restore(w); bringToFront(w); } }
      restoreSnapshot = null;
    }
  }

  // Reset an app: reload its iframe (works for internal and external/cross-origin apps).
  function reset(key) {
    const w = wins.get(key); if (!w) return;
    const frame = w.el.querySelector(".mat-frame"); if (!frame) return;
    const loading = w.el.querySelector(".mat-win-loading"); if (loading) loading.style.display = "";
    const src = frame.src;
    frame.src = "about:blank";
    setTimeout(() => { frame.src = src; }, 30);
    restore(w); bringToFront(w);
  }

  window.MatWM = { open, close, closeKey, reset, showDesktop, setPinnedKeys, isOpen, getState, toggleFocusOrMinimize, onChange };
})();
