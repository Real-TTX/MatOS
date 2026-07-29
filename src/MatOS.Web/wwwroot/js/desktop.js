/* matOS desktop controller */
(function () {
  "use strict";

  // ---- clock ----
  const clock = document.getElementById("mat-clock");
  function tick() {
    if (!clock) return;
    const d = new Date();
    clock.textContent = d.toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" }) +
      "  " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }
  tick(); setInterval(tick, 15000);

  // ---- user menu ----
  const userBtn = document.getElementById("mat-user-btn");
  const userMenu = document.getElementById("mat-user-menu");
  if (userBtn) {
    userBtn.addEventListener("click", (e) => { e.stopPropagation(); userMenu.classList.toggle("open"); });
    document.addEventListener("click", () => userMenu.classList.remove("open"));
    userMenu.addEventListener("click", (e) => e.stopPropagation());
  }

  // ---- open apps (delegation) ----
  function openFrom(el) {
    const url = el.dataset.url;
    if (!url) return;
    window.MatWM.open({
      key: el.dataset.key || url,
      title: el.dataset.title || "App",
      icon: el.dataset.icon || null,
      url,
      width: parseInt(el.dataset.w || "980", 10),
      height: parseInt(el.dataset.h || "640", 10)
    });
  }
  document.addEventListener("click", (e) => {
    const app = e.target.closest(".mat-app");
    if (app) openFrom(app);
    const item = e.target.closest("[data-open-url]");
    if (item) window.MatWM.open({ key: item.dataset.openKey || item.dataset.openUrl, title: item.dataset.openTitle || "App", url: item.dataset.openUrl, width: 980, height: 640 });
  });

  // ---- container apps ----
  const CUBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#c7cffc" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05"/><path d="M12 22.08V12"/></svg>';
  const GLOBE = '<svg viewBox="0 0 24 24" fill="none" stroke="#9fe8ff" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.7 2.5 15.3 0 18M12 3c-2.5 2.7-2.5 15.3 0 18"/></svg>';

  const grid = document.getElementById("mat-container-apps");
  const section = document.getElementById("mat-container-section");

  function containerIcon(c) {
    const dot = c.running ? "on" : "off";
    const web = c.hasWebUi ? `<span class="mat-web-badge" title="Has web UI">${GLOBE}</span>` : "";
    return `
      <div class="mat-app-icon">${CUBE}
        <span class="mat-state-dot ${dot}" title="${c.state}"></span>${web}
      </div>
      <span class="mat-app-label">${escapeHtml(displayName(c))}</span>`;
  }

  function displayName(c) { return c.name || c.shortId; }

  function render(containers) {
    if (!grid) return;
    grid.innerHTML = "";
    if (!containers.length) {
      section.style.display = "";
      grid.innerHTML = `<div class="mat-empty-hint">No containers found. Once Docker is reachable, your containers appear here as apps.</div>`;
      return;
    }
    section.style.display = "";
    for (const c of containers) {
      const btn = document.createElement("button");
      btn.className = "mat-app";
      btn.dataset.title = displayName(c);
      btn.dataset.key = c.id;
      btn.dataset.w = "1024"; btn.dataset.h = "680";
      btn.dataset.url = (c.hasWebUi && c.running && c.appUrl) ? c.appUrl : ("/apps/task-manager?focus=" + encodeURIComponent(c.id));
      btn.innerHTML = containerIcon(c);
      grid.appendChild(btn);
    }
    applySearch();
  }

  async function load() {
    if (!grid) return;
    try {
      const res = await fetch("/api/v1/docker/containers", { headers: { "Accept": "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json();
      render(data.containers || []);
    } catch (_) {
      grid.innerHTML = `<div class="mat-empty-hint">Docker is not reachable yet.</div>`;
    }
  }
  load(); setInterval(load, 15000);

  // ---- search filter ----
  const search = document.getElementById("mat-search");
  function applySearch() {
    const q = (search && search.value || "").trim().toLowerCase();
    document.querySelectorAll(".mat-app").forEach((a) => {
      const hit = !q || (a.dataset.title || "").toLowerCase().includes(q);
      a.style.display = hit ? "" : "none";
    });
  }
  if (search) search.addEventListener("input", applySearch);

  function escapeHtml(s) { return String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }

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
