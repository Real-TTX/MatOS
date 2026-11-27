/* MatOS: the behaviour only this app has. Plain JavaScript, no build step.
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
      if (window.MatOSOs) { return; }
      if (!document.hidden) { location.reload(); } else { document.addEventListener('visibilitychange', function () { location.reload(); }, { once: true }); }
    };
    setTimeout(reload, wait);
  }

  // ------------------------------------------------------------------ MatOS updates itself: <div data-await-restart>
  // The page asks for itself every few seconds while MatOS is away (a failed request is the restart) and reloads once it answers without the mark: the new
  // version is up, or the old one was put back. A plain reload would end on the browser's error page while MatOS is away.

  if ($('[data-await-restart]')) {
    var awaitRestart = function () {
      fetch(location.href, { credentials: 'same-origin', cache: 'no-store', headers: { 'X-Requested-With': 'fetch' } })
        .then(function (r) { return r.ok ? r.text() : null; })
        .then(function (html) {
          if (html !== null && html.indexOf('data-await-restart') === -1) { location.reload(); return; }
          setTimeout(awaitRestart, 4000);
        })
        .catch(function () { setTimeout(awaitRestart, 4000); });
    };
    setTimeout(awaitRestart, 4000);
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

    // where the records point (DnsIp.Sentence on the server)
    function dnsIp() {
      var select = $('#DnsProvider', wiz);
      if (!select || !select.value) { return 'nothing is written (no DNS account)'; }
      var answer = $('input[name="DnsIp"]:checked', wiz), fixed = $('[name="DnsFixedIp"]', wiz);
      return answer && answer.value === 'fixed' && fixed && fixed.value.trim() ? 'Your domain points to ' + fixed.value.trim() : 'Your domain follows your internet address';
    }

    function buildSummary(el) {
      var host = $('[name="PublicHost"]', wiz), domain = $('[name="BaseDomain"]', wiz);
      var rows = [
        ['Address of this host', host && host.value ? host.value : 'from the browser'],
        ['Domain for your apps', domain && domain.value ? '*.' + domain.value : 'none (every app on a port of this host, through Matcad)'],
        ['Engines', engines()],
        ['DNS provider', dnsProvider()],
        ['DNS records', dnsIp()]
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

  // ------------------------------------------------------------------ reached from anywhere (_Reach): the status, and "Check again" without a reload

  $$('[data-reach]').forEach(function (box) {
    var alertEl = $('[data-reach-alert]', box), sentence = $('[data-reach-sentence]', box), forward = $('[data-reach-forward]', box);
    var lan = $('[data-reach-lan]', box), dns = $('[data-reach-dns]', box), again = $('[data-reach-again]', box), label = $('[data-reach-label]', box);
    var last = null, waiting = 0, timer = null;

    function tokenOf() {
      var t = $('input[name="__RequestVerificationToken"]');
      return t ? t.value : '';
    }

    function busy(on) {
      if (again) { again.disabled = on; }
      if (label) { label.textContent = on ? 'Checking ...' : 'Check again'; }
    }

    function render(d) {
      if (alertEl) {
        ['success', 'warning', 'info'].forEach(function (t) { alertEl.classList.toggle('mat-alert--' + t, t === d.tone); });
      }
      if (sentence && sentence.textContent !== d.sentence) { sentence.textContent = d.sentence; }
      if (forward) { forward.hidden = !d.forwarding; }
      if (lan && d.lan) { lan.textContent = d.lan; }
      if (dns) { dns.hidden = !d.dnsAccount; }
    }

    // Asks for the status; while a check waits or runs (or until it is newer than the one before "Check again"), again in a few seconds.
    function load() {
      clearTimeout(timer);
      fetch(box.getAttribute('data-reach'), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
        .then(function (d) {
          var pending = d.checking || (waiting > 0 && d.checkedAt === last);
          if (pending && waiting++ < 90) { busy(true); timer = setTimeout(load, 3000); return; }
          busy(false);
          render(d);
          waiting = 0;
          last = d.checkedAt;
        })
        .catch(function () { busy(false); waiting = 0; });
    }

    if (again) {
      again.addEventListener('click', function () {
        busy(true);
        if (sentence) { sentence.textContent = 'Checking whether your apps can be reached from the internet ...'; }
        fetch(box.getAttribute('data-reach-check'), { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': tokenOf(), 'Accept': 'application/json' } })
          .then(function (r) { if (!r.ok) { return Promise.reject(r.status); } waiting = 1; timer = setTimeout(load, 2000); })
          .catch(function () { busy(false); load(); });
      });
    }

    document.addEventListener('mv:reach-refresh', load);
    load();
  });

  // ------------------------------------------------------------------ app view: reload the frame

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-frame-reload]');
    if (!btn) { return; }
    var frame = document.getElementById(btn.getAttribute('data-frame-reload'));
    if (frame) { frame.src = frame.src; }
  });

  // ------------------------------------------------------------------ the name of a free address, checked while typing: <input data-name-check="/api/address/name">
  // The setup assistant and Settings, Address. The answer {available, reason, address, message} goes to the field around the input: a free name marks
  // it valid and says so in the hint, anything else (taken, reserved, invalid, the address service cannot be reached) is the field's error line.
  // The input then raises "name-checked" with the answer, for a page that wants more. The server checks again when the form is sent.

  // The answer is spoken too: the hint and the address after the field describe the input, and the hint is a polite live region (the error line is one already).
  // An address service that cannot be asked is no verdict on the name (data-name-state="unreachable"): "name-recheck" on the input asks again (the first start
  // sends it on Next).

  var nameChecks = 0;
  $$('[data-name-check]').forEach(function (input) {
    var field = input.closest('.mat-field');
    if (!field) { return; }
    var hint = $('.mat-hint', field);
    var error = $('.mat-error', field);
    var addon = $('.mat-input-addon', field);
    var plain = hint ? hint.textContent : '';
    var timer = null;
    var seq = 0;
    var unreachable = 'The name cannot be checked right now. Try again in a moment.';

    nameChecks += 1;
    var described = (input.getAttribute('aria-describedby') || '').split(' ').filter(Boolean);
    [hint, addon].forEach(function (el, i) {
      if (!el) { return; }
      if (!el.id) { el.id = 'name-check-' + nameChecks + '-' + (i === 0 ? 'hint' : 'addon'); }
      if (described.indexOf(el.id) < 0) { described.push(el.id); }
    });
    if (described.length) { input.setAttribute('aria-describedby', described.join(' ')); }
    if (hint) { hint.setAttribute('aria-live', 'polite'); }

    function show(state, text) {
      field.classList.toggle('is-valid', state === 'ok');
      field.classList.toggle('is-invalid', state === 'bad' || state === 'unreachable');
      input.setAttribute('data-name-state', state || 'none');
      if (state === 'bad' || state === 'unreachable') { input.setAttribute('aria-invalid', 'true'); } else { input.removeAttribute('aria-invalid'); }
      if (error) { error.textContent = state === 'bad' || state === 'unreachable' ? text : ''; }
      if (hint) { hint.textContent = state === 'ok' || state === 'busy' ? text : plain; }
    }

    function check() {
      var name = input.value.trim().toLowerCase();
      var mine = ++seq;
      if (name.length < 3) { show('', ''); return; }
      show('busy', 'Checking ' + name + ' ...');
      fetch(input.getAttribute('data-name-check') + '?name=' + encodeURIComponent(name), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (d) {
          if (mine !== seq) { return; }
          if (!d) { show('unreachable', unreachable); return; }
          // data-name-free="{address} is yours.": the words of a free name on this page (the first start says it its own way)
          var free = d.available && input.getAttribute('data-name-free') ? input.getAttribute('data-name-free').replace('{address}', d.address || name) : d.message;
          show(d.available ? 'ok' : d.reason === 'unreachable' ? 'unreachable' : 'bad', free);
          input.dispatchEvent(new CustomEvent('name-checked', { bubbles: true, detail: d }));
        })
        .catch(function () { if (mine === seq) { show('unreachable', unreachable); } });
    }

    input.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(check, 350); });
    input.addEventListener('name-recheck', function () { clearTimeout(timer); check(); });
    if (input.value) { check(); }
  });
})();
