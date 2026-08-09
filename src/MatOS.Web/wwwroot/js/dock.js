/* macOS dock proximity magnification — active only when data-os="macos".
   Scales dock items by how close the cursor is, like the real Dock. No-op otherwise. */
(function () {
  "use strict";
  const html = document.documentElement;
  const active = () => html.getAttribute("data-os") === "macos";
  const MAX = 1.6, MIN = 1.0, R = 120; // peak scale, base scale, influence radius (px)

  function items() {
    const c = document.getElementById("mat-tb-cluster");
    return c ? [...c.querySelectorAll("#mat-start-btn, .mat-task")] : [];
  }
  function reset() { for (const el of items()) el.style.transform = ""; }
  function magnify(cx) {
    for (const el of items()) {
      const r = el.getBoundingClientRect();
      const d = Math.abs(cx - (r.left + r.width / 2));
      let s = MIN;
      if (d < R) { const t = 1 - d / R; s = MIN + (MAX - MIN) * t * t; }
      el.style.transform = s > 1.001 ? `scale(${s.toFixed(3)}) translateY(${(-(s - 1) * 20).toFixed(1)}px)` : "";
    }
  }
  document.addEventListener("pointermove", (e) => {
    if (!active()) return;
    const tb = document.getElementById("mat-taskbar");
    if (!tb) return;
    const r = tb.getBoundingClientRect();
    if (e.clientY < r.top - 40 || e.clientY > r.bottom + 12) { reset(); return; }
    magnify(e.clientX);
  }, { passive: true });
  document.addEventListener("pointerleave", () => { if (active()) reset(); });
  document.addEventListener("pointerdown", () => { if (active()) setTimeout(reset, 220); });
})();
