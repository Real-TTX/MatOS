/* Mattiverse: the behaviour only this app has. Plain JavaScript, no build step.
   The shell, the theme, dialogs (data-confirm), forms, validation, copy and the dependent fields (data-show-if) are the toolkit's: wwwroot/matui/matui.js.
   Every behaviour here is switched on by a data-* attribute, so several of the same control can live on one page. */
(function () {
  'use strict';

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  // ------------------------------------------------------------------ a select that applies at once: <form data-autosubmit>

  $$('form[data-autosubmit]').forEach(function (form) {
    form.addEventListener('change', function (e) { if (e.target.matches('select')) { form.submit(); } });
  });

  // ------------------------------------------------------------------ a secret (credentials): shown on a click, hidden again on the next

  document.addEventListener('click', function (e) {
    var reveal = e.target.closest('[data-reveal]');
    if (!reveal) { return; }
    var target = document.getElementById(reveal.getAttribute('data-reveal'));
    if (!target) { return; }
    var shown = target.getAttribute('data-shown') === '1';
    target.textContent = shown ? '••••••••••' : target.getAttribute('data-secret');
    target.setAttribute('data-shown', shown ? '0' : '1');
    reveal.setAttribute('aria-pressed', shown ? 'false' : 'true');
  });

  // ------------------------------------------------------------------ pages that wait for something refresh themselves

  var auto = $('[data-autorefresh]');
  if (auto) {
    var wait = parseInt(auto.getAttribute('data-autorefresh'), 10) || 5000;
    var reload = function () {
      // The home screen refreshes itself in place (js/os.js): a reload would take its windows with it.
      if (window.MattiverseOs) { return; }
      if (!document.hidden) { location.reload(); } else { document.addEventListener('visibilitychange', function () { location.reload(); }, { once: true }); }
    };
    setTimeout(reload, wait);
  }

  // ------------------------------------------------------------------ numbers that move: <section id="..." data-live="5000">
  // The page is fetched again and only the marked parts are exchanged, so the window, the scroll position and the tools stay as they are (the Task Manager).

  var live = $$('[data-live][id]');
  if (live.length) {
    var every = parseInt(live[0].getAttribute('data-live'), 10) || 5000;
    var busy = false;
    setInterval(function () {
      // not behind the back of somebody who is answering a question, has a menu open or holds a button of the list
      if (busy || document.hidden || $('dialog[open]') || live.some(function (el) { return el.contains(document.activeElement); })) { return; }
      busy = true;
      fetch(location.href, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
        .then(function (r) { return r.ok && !r.redirected ? r.text() : null; })
        .then(function (html) {
          if (!html) { return; }
          var fresh = new DOMParser().parseFromString(html, 'text/html');
          live.forEach(function (el) {
            var next = fresh.getElementById(el.id);
            if (next && next.innerHTML !== el.innerHTML) { el.innerHTML = next.innerHTML; }
          });
        })
        .catch(function () { /* the next round asks again */ })
        .then(function () { busy = false; });
    }, every);
  }

  // ------------------------------------------------------------------ job log viewer: <pre data-job-log data-job-id="12">

  function pillFor(status) {
    var map = { Queued: ['mat-pill--busy', 'Queued'], Running: ['mat-pill--busy', 'Running'], Succeeded: ['mat-pill--ok', 'Done'], Failed: ['mat-pill--error', 'Failed'] };
    return map[status] || ['', status];
  }

  $$('[data-job-log]').forEach(function (pre) {
    var id = pre.getAttribute('data-job-id');
    var offset = 0;
    var stopped = false;
    var root = pre.closest('[data-job]') || document;
    var statusEl = $('[data-job-status]', root);
    var doneEls = $$('[data-job-done]', root);
    var errorEl = $('[data-job-error]', root);

    function poll() {
      if (stopped) { return; }
      fetch('/api/jobs/' + id + '/log?offset=' + offset, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); })
        .then(function (d) {
          var atBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 40;
          if (d.text) { pre.textContent += d.text; }
          offset = d.offset;
          if (atBottom) { pre.scrollTop = pre.scrollHeight; }
          if (statusEl) { var p = pillFor(d.status); statusEl.className = ('mat-pill ' + p[0]).trim(); statusEl.textContent = p[1]; }
          if (d.finished) {
            stopped = true;
            doneEls.forEach(function (el) { el.hidden = false; });
            if (errorEl && d.error) { errorEl.querySelector('.mat-alert__body').textContent = d.error; errorEl.hidden = false; }
            pre.dispatchEvent(new CustomEvent('job-finished', { bubbles: true, detail: d }));
            return;
          }
          setTimeout(poll, 1000);
        })
        .catch(function () { setTimeout(poll, 2500); });
    }
    poll();
  });

  // ------------------------------------------------------------------ wizard: steps in one page, the bar above shows where you are

  $$('[data-wizard]').forEach(function (wiz) {
    var panels = $$('[data-step]', wiz);
    var bars = $$('.mat-steps li', wiz);
    var label = $('[data-step-label]', wiz);
    var index = 0;

    function show(i, scroll) {
      index = Math.max(0, Math.min(panels.length - 1, i));
      panels.forEach(function (p, n) { p.classList.toggle('is-active', n === index); });
      bars.forEach(function (b, n) {
        b.classList.toggle('is-active', n === index);
        b.classList.toggle('is-done', n < index);
        if (n === index) { b.setAttribute('aria-current', 'step'); } else { b.removeAttribute('aria-current'); }
      });
      if (label) { label.textContent = 'Step ' + (index + 1) + ' of ' + panels.length; }
      var summary = $('[data-wizard-summary]', wiz);
      if (summary && index === panels.length - 1) { buildSummary(summary); }
      if (scroll) { wiz.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
    }

    function engines() {
      var names = [];
      $$('[data-engine]', wiz).forEach(function (card) {
        var box = card.querySelector('input[type=checkbox]');
        var name = card.querySelector('[data-engine-name]');
        if (box && box.checked && name) { names.push(name.textContent); }
      });
      return names.join(', ') || 'none';
    }

    function dnsProvider() {
      var select = $('#DnsProvider', wiz);
      return select && select.value ? select.options[select.selectedIndex].text : 'none (you manage DNS yourself)';
    }

    function buildSummary(el) {
      var host = $('[name="PublicHost"]', wiz), domain = $('[name="BaseDomain"]', wiz);
      var rows = [
        ['Address of this host', host && host.value ? host.value : 'from the browser'],
        ['Domain for your apps', domain && domain.value ? '*.' + domain.value : 'none (every app on a port of this host, through Matcad)'],
        ['Engines', engines()],
        ['DNS provider', dnsProvider()]
      ];
      el.innerHTML = '';
      rows.forEach(function (r) {
        var dt = document.createElement('dt'); dt.textContent = r[0];
        var dd = document.createElement('dd'); dd.textContent = r[1];
        el.appendChild(dt); el.appendChild(dd);
      });
    }

    wiz.addEventListener('click', function (e) {
      if (e.target.closest('[data-next]')) { e.preventDefault(); show(index + 1, true); }
      else if (e.target.closest('[data-prev]')) { e.preventDefault(); show(index - 1, true); }
    });
    // Enter in a field goes to the next step; only the last step sends the form.
    wiz.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && index < panels.length - 1 && e.target.matches('input:not([type=checkbox]):not([type=radio]), select')) { e.preventDefault(); show(index + 1, true); }
    });
    show(0, false);
  });

  // ------------------------------------------------------------------ app view: reload the frame

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-frame-reload]');
    if (!btn) { return; }
    var frame = document.getElementById(btn.getAttribute('data-frame-reload'));
    if (frame) { frame.src = frame.src; }
  });
})();
