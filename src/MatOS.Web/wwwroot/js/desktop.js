/* matOS desktop controller — taskbar + Start menu (Windows-style) */
(function () {
  "use strict";

  const CUBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#c7cffc" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05"/><path d="M12 22.08V12"/></svg>';
  const GLOBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#9fe8ff" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.7 2.5 15.3 0 18M12 3c-2.5 2.7-2.5 15.3 0 18"/></svg>';

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
      key: el.dataset.key || el.dataset.url,
      title: el.dataset.title || "App",
      icon: el.dataset.icon || null,
      url: el.dataset.url,
      width: parseInt(el.dataset.w || "1024", 10),
      height: parseInt(el.dataset.h || "680", 10)
    });
  }

  // Desktop icons + any [data-open-url] control (tray menu items, start-menu footer)
  document.addEventListener("click", (e) => {
    const app = e.target.closest(".mat-app");
    if (app && !e.target.closest("#mat-startmenu")) { launchEl(app); return; }
    const opener = e.target.closest("[data-open-url]");
    if (opener) {
      open({ key: opener.dataset.openKey || opener.dataset.openUrl, title: opener.dataset.openTitle || "App", url: opener.dataset.openUrl, width: 1040, height: 680 });
      closeStart(); if (userMenu) userMenu.classList.remove("open");
    }
  });

  // ---- desktop-grid container apps ----
  const grid = document.getElementById("mat-container-apps");
  const section = document.getElementById("mat-container-section");
  let containerData = [];

  function displayName(c) { return c.name || c.shortId; }
  function containerUrl(c) { return (c.hasWebUi && c.running && c.appUrl) ? c.appUrl : ("/apps/task-manager?focus=" + encodeURIComponent(c.id)); }
  function containerInner(c) {
    const dot = c.running ? "on" : "off";
    const web = c.hasWebUi ? `<span class="mat-web-badge" title="Has web UI">${GLOBE}</span>` : "";
    return `${CUBE}<span class="mat-state-dot ${dot}" title="${escAttr(c.state)}"></span>${web}`;
  }

  function renderDesktopContainers() {
    if (!grid) return;
    if (!containerData.length) {
      grid.innerHTML = `<div class="mat-empty-hint">No containers found. Once Docker is reachable, your containers appear here as apps.</div>`;
      return;
    }
    grid.innerHTML = "";
    for (const c of containerData) {
      const btn = document.createElement("button");
      btn.className = "mat-app";
      btn.dataset.title = displayName(c);
      btn.dataset.key = c.id; btn.dataset.w = "1024"; btn.dataset.h = "680";
      btn.dataset.url = containerUrl(c);
      btn.innerHTML = `<div class="mat-app-icon">${containerInner(c)}</div><span class="mat-app-label">${esc(displayName(c))}</span>`;
      grid.appendChild(btn);
    }
  }

  async function load() {
    try {
      const res = await fetch("/api/v1/docker/containers", { headers: { "Accept": "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json();
      containerData = data.containers || [];
    } catch (_) {
      containerData = [];
      if (grid) grid.innerHTML = `<div class="mat-empty-hint">Docker is not reachable yet.</div>`;
    }
    renderDesktopContainers();
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

  function systemApps() {
    return Array.from(document.querySelectorAll("#mat-system-apps .mat-app")).map(el => ({
      key: el.dataset.key, title: el.dataset.title, url: el.dataset.url,
      w: el.dataset.w || "1040", h: el.dataset.h || "680",
      iconHtml: (el.querySelector(".mat-app-icon") || {}).innerHTML || "", sys: true
    }));
  }

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
    document.addEventListener("click", (e) => {
      if (!startMenu.hidden && !startMenu.contains(e.target) && !startBtn.contains(e.target)) closeStart();
    });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeStart(); });
    startSearch.addEventListener("input", () => renderStartMenu(startSearch.value));
    startSearch.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { const first = startMenu.querySelector(".sm-item"); if (first) { e.preventDefault(); first.click(); } }
    });
    startMenu.addEventListener("click", (e) => {
      const it = e.target.closest(".sm-item");
      if (it) { e.preventDefault(); launchEl(it); closeStart(); }
    });
  }

  load(); setInterval(load, 15000);

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
