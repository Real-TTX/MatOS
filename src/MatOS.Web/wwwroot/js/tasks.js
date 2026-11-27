/* MatOS: the Task Manager (Pages/Tasks/Index.cshtml, css/tasks.css). Plain JavaScript, no build step.
   Every three seconds it asks the page for its numbers (<div data-tasks="/Tasks?handler=Live">, with the query of the list) and puts them in place:
   the cards and their charts, the line under them, and the numbers of every app and container (data-f="...", by data-key). The rows follow the order
   of the answer unless the keyboard is in the list. When the rows themselves change (an app came or went, a container stopped: data-signature), the
   list and the notes of the page are fetched again and exchanged, the toolkit's groups keep what was open. It waits while the tab is hidden or a
   question is open, and says so when MatOS does not answer. */
(function () {
  'use strict';

  var root = document.querySelector('[data-tasks]');
  if (!root) { return; }

  var EVERY = 3000;
  var live = root.getAttribute('data-tasks');
  var busy = false;

  function $(sel, scope) { return (scope || document).querySelector(sel); }
  function $$(sel, scope) { return Array.prototype.slice.call((scope || document).querySelectorAll(sel)); }

  function field(scope, name) { return scope ? $('[data-f="' + name + '"]', scope) : null; }

  function text(scope, name, value) {
    var el = field(scope, name);
    if (el && typeof value === 'string' && el.textContent !== value) { el.textContent = value; }
  }

  function meter(el, value, tone) {
    if (!el || value === null || value === undefined) { return; }
    var v = Math.max(0, Math.min(100, Math.round(value)));
    if (el.value !== v) { el.value = v; }
    if (tone) { el.setAttribute('data-tone', tone); }
  }

  function url() {
    var q = window.location.search.replace(/^\?/, '');
    return live + (q ? '&' + q : '');
  }

  function byKey(selector) {
    var map = {};
    $$(selector, root).forEach(function (el) { map[el.getAttribute('data-key')] = el; });
    return map;
  }

  function cards(data) {
    (data.cards || []).forEach(function (c) {
      var card = $('[data-card="' + c.key + '"]', root);
      if (!card) { return; }
      text(card, 'value', c.value);
      text(card, 'sub', c.sub);
      var line = field(card, 'line');
      if (line) {
        line.setAttribute('d', c.line || '');
        field(card, 'area').setAttribute('d', c.area || '');
        $('svg', card).hidden = !c.line;
        var empty = field(card, 'empty');
        if (empty) { empty.hidden = !!c.line; }
      }
      meter(field(card, 'meter'), c.meter, c.tone);
    });
    text(root, 'facts', data.facts);
  }

  function numbers(scope, item) {
    text(scope, 'cpu', item.cpu);
    meter(field(scope, 'cpuMeter'), item.cpuMeter === null ? 0 : item.cpuMeter, item.cpuTone);
    text(scope, 'memory', item.memory);
    text(scope, 'memorySub', item.memorySub);
    text(scope, 'net', item.net);
    text(scope, 'disk', item.disk);
  }

  function rows(data) {
    var groups = byKey('tbody[data-key]');
    var lines = byKey('tr[data-key]');
    (data.groups || []).forEach(function (g) {
      var body = groups[g.key];
      if (!body) { return; }
      var head = $('.mat-group__head', body);
      numbers(head, g);
      text(head, 'state', g.stateText);
      var dot = field(head, 'dot');
      if (dot) { dot.setAttribute('data-state', g.state); }
      g.rows.forEach(function (r) {
        var tr = lines[r.key];
        if (tr) { numbers(tr, r); text(tr, 'status', r.status); } else { text(head, 'status', r.status); }
      });
    });
  }

  // The order of the answer, section by section: an app after the heading of its part, its containers after its heading row.
  function order(data) {
    var table = $('#tm-list table', root);
    if (!table || table.contains(document.activeElement)) { return; }
    var groups = byKey('tbody[data-key]');
    var lines = byKey('tr[data-key]');
    (data.sections || []).forEach(function (s) {
      var anchor = $('tbody.mv-tm-section[data-section="' + s.key + '"]', table);
      s.groups.forEach(function (key) {
        var body = groups[key];
        if (!body || !anchor) { return; }
        if (anchor.nextElementSibling !== body) { anchor.after(body); }
        anchor = body;
      });
    });
    (data.groups || []).forEach(function (g) {
      var body = groups[g.key];
      var after = body && $('.mat-group__head', body);
      g.rows.forEach(function (r) {
        var tr = lines[r.key];
        if (!tr || !after) { return; }
        if (after.nextElementSibling !== tr) { after.after(tr); }
        after = tr;
      });
    });
  }

  // The rows themselves changed: the list and the notes as the page draws them now.
  function reload() {
    return fetch(window.location.href, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { return r.ok && !r.redirected ? r.text() : null; })
      .then(function (html) {
        if (!html) { return; }
        var fresh = new DOMParser().parseFromString(html, 'text/html');
        ['tm-list', 'tm-alerts'].forEach(function (id) {
          var now = document.getElementById(id);
          var next = fresh.getElementById(id);
          if (!now || !next) { return; }
          now.innerHTML = next.innerHTML;
          if (next.hasAttribute('data-signature')) { now.setAttribute('data-signature', next.getAttribute('data-signature')); }
          if (window.MatUi && window.MatUi.refresh) { window.MatUi.refresh(now); }
        });
      });
  }

  function stale(on) {
    var el = field(root, 'stale');
    if (el) { el.hidden = !on; }
  }

  function tick() {
    if (busy || document.hidden || $('dialog[open]')) { return schedule(); }
    busy = true;
    fetch(url(), { credentials: 'same-origin', headers: { 'Accept': 'application/json', 'X-Requested-With': 'fetch' } })
      .then(function (r) {
        var json = (r.headers.get('Content-Type') || '').indexOf('application/json') >= 0;
        if (!r.ok || r.redirected || !json) { throw new Error('not live'); }
        return r.json();
      })
      .then(function (data) {
        stale(false);
        cards(data);
        var list = document.getElementById('tm-list');
        var known = document.getElementById('tm-alerts');
        var measured = known ? known.children.length === 0 : true;
        if ((list && list.getAttribute('data-signature') !== data.signature) || measured !== (data.measured && !data.error)) {
          return reload();
        }
        rows(data);
        order(data);
      })
      .catch(function () { stale(true); })
      .then(function () { busy = false; schedule(); });
  }

  var timer = null;
  function schedule() {
    window.clearTimeout(timer);
    timer = window.setTimeout(tick, EVERY);
  }

  document.addEventListener('visibilitychange', function () { if (!document.hidden) { schedule(); } });
  schedule();
})();
