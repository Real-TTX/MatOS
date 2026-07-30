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

    const el = document.createElement("section");
    el.className = "mat-window";
    el.style.left = pos.x + "px";
    el.style.top = pos.y + "px";
    el.style.width = width + "px";
    el.style.height = height + "px";
    el.setAttribute("role", "dialog");
    el.setAttribute("aria-label", opts.title || "Window");

    const iconHtml = opts.icon
      ? `<img class="mat-win-icon" src="${opts.icon}" alt="" />`
      : `<span class="mat-win-icon mat-win-icon-fallback">${(opts.title || "?").slice(0, 1).toUpperCase()}</span>`;

    el.innerHTML = `
      <header class="mat-titlebar">
        ${iconHtml}
        <span class="mat-title" title="${escapeAttr(opts.title || "")}">${escapeHtml(opts.title || "")}</span>
        <div class="mat-win-actions">
          ${opts.url ? `<button class="mat-win-btn mat-open-ext" title="Open in new tab" aria-label="Open in new tab">&#8599;</button>` : ""}
          <button class="mat-win-btn mat-min" title="Minimize" aria-label="Minimize">&#8211;</button>
          <button class="mat-win-btn mat-max" title="Maximize" aria-label="Maximize">&#9723;</button>
          <button class="mat-win-btn mat-close" title="Close" aria-label="Close">&#10005;</button>
        </div>
      </header>
      <div class="mat-win-body">
        <div class="mat-win-loading"><span class="mat-spinner"></span></div>
        <iframe class="mat-frame" src="${escapeAttr(opts.url || "about:blank")}"
                referrerpolicy="no-referrer" allow="clipboard-read; clipboard-write"></iframe>
        <div class="mat-frame-shield" hidden></div>
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
    const ext = el.querySelector(".mat-open-ext");
    if (ext) ext.addEventListener("click", () => window.open(opts.url, "_blank", "noopener"));

    el.addEventListener("pointerdown", () => bringToFront(w), true);
    makeDraggable(w);
    makeResizable(w);

    const bar = el.querySelector(".mat-titlebar");
    bar.addEventListener("dblclick", (e) => { if (!e.target.closest(".mat-win-btn")) toggleMax(w); });

    bringToFront(w);
    syncDock();
    return w;
  }

  function close(w) {
    w.el.remove();
    wins.delete(w.key);
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
      ny = Math.min(Math.max(ny, BAR_H), window.innerHeight - 40);
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

  function syncDock() {
    const d = dock();
    if (!d) return;
    d.innerHTML = "";
    for (const w of wins.values()) {
      const b = document.createElement("button");
      b.className = "mat-task" + (w.el.classList.contains("focused") && !w.minimized ? " active" : "");
      b.title = w.opts.title || "";
      b.innerHTML = (w.opts.icon ? `<img src="${w.opts.icon}" alt=""/>` : `<span class="mat-task-ico">${(w.opts.title || "?").slice(0,1)}</span>`) +
                    `<span class="mat-task-label">${escapeHtml(w.opts.title || "")}</span>`;
      b.addEventListener("click", () => {
        if (w.minimized) { restore(w); bringToFront(w); }
        else if (w.el.classList.contains("focused")) { minimize(w); }
        else { bringToFront(w); }
      });
      d.appendChild(b);
    }
  }

  function resizeHandles() {
    return ["n","s","e","w","ne","nw","se","sw"]
      .map(d => `<div class="mat-resize mat-resize-${d}" data-dir="${d}"></div>`).join("");
  }

  function escapeHtml(s) { return String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }
  function escapeAttr(s) { return String(s).replace(/"/g, "&quot;"); }

  window.MatWM = { open, close };
})();
