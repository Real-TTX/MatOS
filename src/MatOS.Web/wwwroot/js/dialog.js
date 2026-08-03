// Themed, promise-based replacements for the browser's native alert/confirm/prompt.
// Loaded in both the desktop shell (_DesktopLayout) and every system-app window
// (_WindowLayout) so the whole app shares one modal style (matches the UI's CI).
//
//   await matDialog.alert("Saved.")                       -> resolves undefined
//   if (await matDialog.confirm("Delete?", {danger:true})) ...   -> true / false
//   const name = await matDialog.prompt("Rename to:", {value:"old"})  -> string / null
(function () {
  if (window.matDialog) return;

  function esc(s) { return String(s == null ? "" : s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c])); }

  function open(opts) {
    const o = opts || {};
    const kind = o.kind || "alert";
    return new Promise(resolve => {
      const overlay = document.createElement("div");
      overlay.className = "matdlg-overlay";
      const box = document.createElement("div");
      box.className = "matdlg";
      box.setAttribute("role", "dialog");
      box.setAttribute("aria-modal", "true");

      let html = "";
      if (o.title) html += `<div class="matdlg-title">${esc(o.title)}</div>`;
      if (o.message) html += `<div class="matdlg-msg">${esc(o.message)}</div>`;
      if (kind === "prompt") html += `<input class="matdlg-input" type="text" />`;
      html += `<div class="matdlg-actions"></div>`;
      box.innerHTML = html;

      const actions = box.querySelector(".matdlg-actions");
      const input = box.querySelector(".matdlg-input");
      if (input) { input.value = o.value || ""; if (o.placeholder) input.placeholder = o.placeholder; }

      // Cancel value differs per kind so callers can branch naturally.
      const cancelValue = kind === "prompt" ? null : (kind === "confirm" ? false : undefined);

      function close(result) {
        document.removeEventListener("keydown", onKey, true);
        overlay.remove();
        resolve(result);
      }
      function onKey(e) {
        if (e.key === "Escape") { e.preventDefault(); close(cancelValue); }
        else if (e.key === "Enter") { e.preventDefault(); okBtn.click(); }
      }

      if (kind !== "alert") {
        const cancelBtn = document.createElement("button");
        cancelBtn.className = "matdlg-btn";
        cancelBtn.textContent = o.cancelLabel || "Cancel";
        cancelBtn.addEventListener("click", () => close(cancelValue));
        actions.appendChild(cancelBtn);
      }
      const okBtn = document.createElement("button");
      okBtn.className = "matdlg-btn primary" + (o.danger ? " danger" : "");
      okBtn.textContent = o.okLabel || "OK";
      okBtn.addEventListener("click", () => close(kind === "prompt" ? input.value : (kind === "confirm" ? true : undefined)));
      actions.appendChild(okBtn);

      overlay.addEventListener("mousedown", e => { if (e.target === overlay) close(cancelValue); });

      overlay.appendChild(box);
      document.body.appendChild(overlay);
      document.addEventListener("keydown", onKey, true);
      setTimeout(() => { (input || okBtn).focus(); if (input) input.select(); }, 30);
    });
  }

  window.matDialog = {
    alert: (message, opts) => open(Object.assign({ kind: "alert", message }, opts)),
    confirm: (message, opts) => open(Object.assign({ kind: "confirm", message }, opts)),
    prompt: (message, opts) => open(Object.assign({ kind: "prompt", message }, opts)),
  };
})();
