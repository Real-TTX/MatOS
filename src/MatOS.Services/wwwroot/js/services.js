/* MatOS Services: the behaviour only the admin area has (everything else is MatUi's matui.js).
   The live list of the page Tunnels: [data-live-tunnels] asks its data-src (same origin, the cookie of the session) every data-every seconds while
   the page is visible and draws the rows again with text only (never HTML from the answer). Without a session it stops (401); a failed request
   is tried again at the next turn. */
(function () {
  'use strict';

  var COLUMNS = [
    { label: 'Name', key: 'name', link: true },
    { label: 'Connected for', key: 'connectedFor' },
    { label: 'Streams now', key: 'streams', cls: 'mat-num' },
    { label: 'This session', key: 'bytes', cls: 'mat-num', title: function (t) { return 'To the visitors ' + t.bytesToVisitor + ', to the installation ' + t.bytesToInstance; } },
    { label: 'Since', key: 'connectedAt', cls: 'mat-hide-sm mat-muted mat-nowrap' },
    { label: 'From', key: 'from', cls: 'mat-hide-sm mat-mono' },
    { label: 'Version', key: 'version', cls: 'mat-hide-sm' },
    { label: 'Streams served', key: 'served', cls: 'mat-num mat-hide-sm' }
  ];

  function row(tunnel) {
    var tr = document.createElement('tr');
    COLUMNS.forEach(function (column) {
      var td = document.createElement('td');
      td.setAttribute('data-label', column.label);
      if (column.cls) { td.className = column.cls; }
      if (column.title) { td.title = column.title(tunnel); }
      var text = String(tunnel[column.key]);
      if (column.link) {
        var name = document.createElement('div');
        name.className = 'mat-table__name';
        var a = document.createElement('a');
        a.href = '/admin/Installations/Details?name=' + encodeURIComponent(text);
        a.textContent = text;
        name.appendChild(a);
        td.appendChild(name);
      } else {
        td.textContent = text;
      }
      tr.appendChild(td);
    });
    return tr;
  }

  function live(section) {
    var src = section.getAttribute('data-src');
    var every = Math.max(2, parseInt(section.getAttribute('data-every'), 10) || 5) * 1000;
    var rows = section.querySelector('[data-live-rows]');
    var table = section.querySelector('[data-live-table]');
    var empty = section.querySelector('[data-live-empty]');
    var at = section.querySelector('[data-live-at]');
    var timer = 0;
    var stopped = false;

    function draw(data) {
      var tunnels = Array.isArray(data.tunnels) ? data.tunnels : [];
      var fragment = document.createDocumentFragment();
      tunnels.forEach(function (t) { fragment.appendChild(row(t)); });
      rows.replaceChildren(fragment);
      table.hidden = tunnels.length === 0;
      empty.hidden = tunnels.length > 0;
      if (at && data.at) { at.textContent = data.at; }
    }

    function next() {
      clearTimeout(timer);
      if (!stopped) { timer = setTimeout(tick, every); }
    }

    function tick() {
      if (document.hidden) { next(); return; }
      fetch(src, { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' } })
        .then(function (response) {
          if (response.status === 401 || response.status === 404) { stopped = true; return null; }
          return response.ok ? response.json() : null;
        })
        .then(function (data) { if (data) { draw(data); } })
        .catch(function () { /* the network: the next turn tries again */ })
        .then(next);
    }

    document.addEventListener('visibilitychange', function () {
      if (!document.hidden && !stopped) { clearTimeout(timer); tick(); }
    });
    next();
  }

  document.querySelectorAll('[data-live-tunnels]').forEach(live);
})();
