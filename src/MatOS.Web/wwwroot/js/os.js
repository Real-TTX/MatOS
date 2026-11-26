/* MatOS: the behaviour of the home screen (Pages/Shared/_Launcher.cshtml, css/os.css). Plain JavaScript, no build step.

   WINDOWS, like a desktop. Anything with data-window opens that page in a window of its own (an iframe; a page of MatOS drops its menu there,
   js/window-mode.js). Several windows are open side by side:
   - the bar moves a window, its edges and corners size it, a click anywhere in it brings it to the front;
   - red closes it, yellow puts it away (the app stays as it is), green fills the screen; a double click on the bar fills the screen too;
   - dragged to the top of the screen it fills it, dragged to the left or right edge it takes that half; dragged away it has its own size again;
   - the dock is the taskbar: a dot under an app that has a window, an icon for every window that has none there; a click brings the window to the
     front, a click on the one in front puts it away. The logo in the status bar shows the desktop;
   - every app remembers where its window was and how large (this browser), and a reload of the home screen brings the windows back (this tab).
   The address keeps the page of the window in front (#/Store). On a phone a window fills the screen and the bar at its bottom goes home.

   An installed app opens in a window too; its menu (a right click, a long press) opens it in a new tab instead, and can make that the default for
   this app (kept in this browser).

   The person arranges the home screen, and the account keeps it (PUT /api/home: pages, what is off the screen, the sizes of the widgets):
   - a right click (a long press on a touch screen) gives everything its menu: an app, a widget, the dock, a window's bar, the empty screen;
     Shift with the right click is the browser's own menu, and a field keeps the browser's menu (to paste);
   - a thing is dragged with the mouse to another place; at the edge of the screen, or over a dot, the page turns (past the last one a new page begins);
   - "Edit the home screen" makes the things wiggle: a finger drags them too, a minus takes one off the screen, "Show again" brings it back;
   - the pages turn with a swipe, the dots below them, or the arrow keys.
   The home screen refreshes itself without a reload (refresh(): the things are fetched and put in place), so the windows stay.
   The search narrows the apps of all pages (the key / puts the focus there, Enter opens the first one). The clocks tick here. */
(function () {
  'use strict';

  var layer = document.getElementById('os-windows');
  var template = document.getElementById('os-window-template');
  if (!layer || !template) { return; }
  // The home screen inside one of its own windows (a page that led back to it) is not a second desktop (js/window-mode.js closes that window).
  try { if (window.top !== window.self && window.top.location.origin === location.origin) { return; } } catch (e) { /* framed by someone else */ }

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
  var html = document.documentElement;
  var body = document.body;
  var dock = $('.os-dock');
  var statusBar = $('.os-status');
  var activeName = $('[data-os-active]');
  var snapEl = $('[data-os-snap]');
  var instance = html.getAttribute('data-os') || 'MatOS';
  var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var phone = window.matchMedia('(max-width: 700px)');
  var PREFS = 'matos.os.window.';
  var GEO = 'matos.os.geo.';
  var OPEN = 'matos.os.windows';
  var PAGE = 'matos.os.page';
  var LEGACY = 'mattiverse.os.';

  // An icon an app serves itself (its launcher, <img data-fallback>) comes from the app's address, which this browser may not reach (yet): before the
  // certificate is there, or from a device at home without the internet. Then the icon of the catalog takes its place, once. An image that failed
  // before this ran is caught too; the error of an image does not bubble, so it is heard on the way down.
  function iconFallback(img) {
    var other = img.getAttribute('data-fallback');
    img.removeAttribute('data-fallback');
    if (other && img.getAttribute('src') !== other) { img.src = other; }
  }
  document.addEventListener('error', function (e) {
    if (e.target && e.target.tagName === 'IMG' && e.target.hasAttribute('data-fallback')) { iconFallback(e.target); }
  }, true);
  $$('img[data-fallback]').forEach(function (img) { if (img.complete && img.naturalWidth === 0) { iconFallback(img); } });

  // MatOS was called Mattiverse until 2026-11 and kept the places of the windows (localStorage) and the open windows of a tab (sessionStorage) under
  // mattiverse.os.*: they move to matos.os.* once (a key that is there already wins), so the windows come back where they were. The browser keeps them per
  // address: this helps where the address stays (host:port); at matos.<domain> nothing of mattiverse.<domain> can be read (docs/ARCHITECTURE.md).
  function moveLegacyKeys(storage) {
    try {
      var store = storage();
      var old = [];
      for (var i = 0; i < store.length; i++) {
        var key = store.key(i);
        if (key && key.indexOf(LEGACY) === 0) { old.push(key); }
      }
      old.forEach(function (key) {
        var now = 'matos.os.' + key.slice(LEGACY.length);
        if (store.getItem(now) === null) { store.setItem(now, store.getItem(key)); }
        store.removeItem(key);
      });
    } catch (e) { /* private mode, or no storage */ }
  }
  moveLegacyKeys(function () { return window.localStorage; });
  moveLegacyKeys(function () { return window.sessionStorage; });
  var MINW = 380;
  var MINH = 260;

  // ------------------------------------------------------------------ the windows

  var wins = [];
  var zTop = 10;
  var seq = 0;
  var mv = null;
  var restoring = false;

  function centre(el) {
    var r = el.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  }
  function byEl(el) { return wins.filter(function (w) { return w.el === el; })[0] || null; }
  function byId(id) { return wins.filter(function (w) { return String(w.id) === String(id); })[0] || null; }
  function windowOf(app) { return wins.filter(function (w) { return w.app === app && !w.leaving; })[0] || null; }
  // The window in front: the topmost one that is on the screen.
  function active() {
    var top = null;
    wins.forEach(function (w) { if (!w.min && !w.leaving && (!top || w.z > top.z)) { top = w; } });
    return top;
  }
  function pinned(app) {
    return dock ? $$('.os-dock__app[data-window]', dock).filter(function (a) { return a.getAttribute('data-window') === app; })[0] || null : null;
  }
  function sourceOf(app) {
    return pinned(app) || $$('.os-app[data-window]').filter(function (a) { return a.getAttribute('data-window') === app; })[0] || null;
  }
  // A page with no icon of its own on the home screen: the details of an app take the app's icon, a page further in its section's (/Settings/System the one of /Settings).
  function kinOf(url) {
    if (!url) { return null; }
    var app = $$('.os-app[data-details]').filter(function (a) { return a.getAttribute('data-details') === url; })[0];
    if (app) { return app; }
    var section = /^\/[^/?#]+/.exec(url);
    return section && section[0] !== url ? sourceOf(section[0]) : null;
  }

  // Where windows may be: below the status bar, and (filling the screen, or a half) above the dock.
  function area() {
    var top = statusBar ? statusBar.getBoundingClientRect().bottom : 0;
    var bottom = window.innerHeight;
    if (dock && !phone.matches) {
      var d = dock.getBoundingClientRect();
      if (d.height && d.top > top + MINH) { bottom = d.top - 10; }
    }
    return { top: top, right: window.innerWidth, bottom: bottom };
  }
  function modeRect(mode) {
    var a = area();
    var h = a.bottom - a.top;
    var half = Math.round(a.right / 2);
    if (mode === 'max') { return { x: 0, y: a.top, w: a.right, h: h }; }
    if (mode === 'left') { return { x: 8, y: a.top + 8, w: half - 12, h: h - 8 }; }
    if (mode === 'right') { return { x: half + 4, y: a.top + 8, w: a.right - half - 12, h: h - 8 }; }
    return null;
  }
  function defaultRect() {
    var a = area();
    var w = Math.min(1180, a.right - 64);
    var h = Math.min(800, a.bottom - a.top - 28);
    return { x: Math.round((a.right - w) / 2), y: Math.round(a.top + Math.max(12, (a.bottom - a.top - h) / 2)), w: w, h: h };
  }
  // A window keeps its bar on the screen, whatever the size of the browser.
  function clampRect(r) {
    var a = area();
    r.w = Math.round(Math.max(MINW, Math.min(r.w, a.right)));
    r.h = Math.round(Math.max(MINH, Math.min(r.h, window.innerHeight - a.top)));
    r.x = Math.round(Math.max(140 - r.w, Math.min(r.x, a.right - 140)));
    r.y = Math.round(Math.max(a.top, Math.min(r.y, window.innerHeight - 48)));
    return r;
  }
  function validRect(r) {
    return r && [r.x, r.y, r.w, r.h].every(function (n) { return typeof n === 'number' && isFinite(n); }) ? { x: r.x, y: r.y, w: r.w, h: r.h } : null;
  }
  function validMode(m) { return m === 'max' || m === 'left' || m === 'right' ? m : 'normal'; }
  function remembered(app) {
    try {
      var g = JSON.parse(localStorage.getItem(GEO + app) || 'null');
      var r = validRect(g);
      return r ? { rect: r, mode: validMode(g.mode) } : null;
    } catch (e) { return null; }
  }
  function remember(w) {
    try { localStorage.setItem(GEO + w.app, JSON.stringify({ x: w.rect.x, y: w.rect.y, w: w.rect.w, h: w.rect.h, mode: w.mode })); } catch (e) { /* private mode */ }
  }

  // Puts the window where its state says (its own place, the whole screen, a half). On a phone the stylesheet fills the screen with it.
  function place(w, glide) {
    var r = modeRect(w.mode) || w.rect;
    var el = w.el;
    if (glide && !reduced && !w.min) {
      el.classList.add('is-settling');
      clearTimeout(w.settling);
      w.settling = setTimeout(function () { el.classList.remove('is-settling'); }, 280);
    }
    el.style.left = r.x + 'px';
    el.style.top = r.y + 'px';
    el.style.width = r.w + 'px';
    el.style.height = r.h + 'px';
    el.classList.toggle('is-max', w.mode === 'max');
    el.classList.toggle('is-tiled', w.mode === 'left' || w.mode === 'right');
    var max = $('[data-win-max]', el);
    if (max) { max.setAttribute('aria-pressed', w.mode === 'max' ? 'true' : 'false'); }
  }

  // A window grows out of its icon and shrinks back into it; without one it fades.
  function animateWin(w, name, from, done) {
    if (w.anim) { w.anim(); }
    if (reduced) { if (done) { done(); } return; }
    var el = w.el;
    if (from) {
      var r = el.getBoundingClientRect();
      el.style.transformOrigin = (from.x - r.left) + 'px ' + (from.y - r.top) + 'px';
    }
    el.classList.add(name);
    var timer = null;
    var onEnd = function (e) { if (e.target === el) { end(); } };
    function end() {
      clearTimeout(timer);
      el.removeEventListener('animationend', onEnd);
      w.anim = null;
      el.classList.remove(name);
      el.style.transformOrigin = '';
      if (done) { done(); }
    }
    el.addEventListener('animationend', onEnd);
    timer = setTimeout(end, 700);
    w.anim = end;
  }
  function originOf(w) {
    var el = w.entry || pinned(w.app) || w.source;
    return el && body.contains(el) && el.getClientRects().length ? centre(el) : null;
  }

  function setIcon(w, source) {
    var picOf = function (el) { return el ? el.querySelector('.os-app__icon > .mv-appicon, .os-app__icon > .os-ico, :scope > .os-ico, :scope > .mv-appicon') : null; };
    // A window opened from a widget, a page or the address has no icon of its own: the one of the same page in the dock or on the home screen.
    var pic = picOf(source) || picOf(sourceOf(w.app)) || (w.url !== w.app ? picOf(sourceOf(w.url)) : null) || picOf(kinOf(w.url));
    var make = function () {
      if (pic) { return pic.cloneNode(true); }
      var blank = document.createElement('span');
      blank.className = 'os-ico os-ico--window';
      blank.innerHTML = icon('monitor');
      return blank;
    };
    w.iconEl.innerHTML = '';
    w.iconEl.appendChild(make());
    if (w.entry) { w.entry.innerHTML = ''; w.entry.appendChild(make()); }
  }
  function setTitle(w, text) {
    w.title = text || '';
    w.titleEl.textContent = w.title;
    w.el.setAttribute('aria-label', w.title || 'Window');
    w.frame.setAttribute('title', w.title || 'Window');
    if (w.entry) {
      w.entry.setAttribute('aria-label', w.title || 'Window');
      w.entry.setAttribute('data-title', w.title || 'Window');
      w.entry.setAttribute('data-tip', w.title || 'Window');
    }
    if (activeName && w === active()) { activeName.textContent = w.title; }
  }

  // The dock is the taskbar: a window whose app has no icon there gets one for as long as it is open.
  var dockOpen = null;
  var dockSep = null;
  function addEntry(w) {
    if (!dock) { return; }
    if (!dockOpen) {
      dockSep = document.createElement('span');
      dockSep.className = 'os-dock__sep';
      dockSep.setAttribute('aria-hidden', 'true');
      dockOpen = document.createElement('div');
      dockOpen.className = 'os-dock__open';
      dock.appendChild(dockSep);
      dock.appendChild(dockOpen);
    }
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'os-dock__app os-dock__win is-running';
    b.setAttribute('data-win-id', String(w.id));
    dockOpen.appendChild(b);
    w.entry = b;
    dockSep.hidden = false;
    if (!reduced && !restoring && b.animate) { b.animate([{ transform: 'scale(.3)', opacity: 0 }, { transform: 'none', opacity: 1 }], { duration: 240, easing: 'cubic-bezier(.2, .9, .25, 1)' }); }
  }
  function removeEntry(w) {
    if (w.entry) { w.entry.remove(); w.entry = null; }
    if (dockSep) { dockSep.hidden = !dockOpen.firstElementChild; }
  }

  function markRunning(top) {
    var open = {};
    wins.forEach(function (w) { if (!w.leaving) { open[w.app] = true; } });
    $$('[data-window]').forEach(function (el) {
      if (!el.classList.contains('os-app') && !el.classList.contains('os-dock__app')) { return; }
      var app = el.getAttribute('data-window');
      el.classList.toggle('is-running', !!open[app]);
      el.classList.toggle('is-front', !!top && top.app === app);
    });
  }

  function pathOf(w) {
    try {
      var loc = w.frame.contentWindow && w.frame.contentWindow.location;
      if (loc && loc.href !== 'about:blank' && loc.origin === location.origin) { return loc.pathname + loc.search; }
    } catch (e) { /* another origin */ }
    return null;
  }

  // The address names the page of the window in front; without one it is the home screen. The first window adds a step for the browser's Back
  // (which shows the desktop again); after that the address only follows.
  function address(top, push) {
    var want = top ? '#' + (pathOf(top) || top.start) : '';
    if (location.hash === want) { return; }
    if (!want) { history.replaceState(null, '', location.pathname + location.search); return; }
    if (push && !location.hash) { history.pushState({ os: 1 }, '', want); } else { history.replaceState({ os: 1 }, '', want); }
  }

  // The windows of this tab, so that a reload of the home screen brings them back.
  function persist() {
    if (restoring) { return; }
    try {
      sessionStorage.setItem(OPEN, JSON.stringify(wins.filter(function (w) { return !w.leaving; }).map(function (w) {
        return { app: w.app, start: w.start, url: pathOf(w) || w.url, title: w.title, rect: w.rect, mode: w.mode, min: w.min, z: w.z };
      })));
    } catch (e) { /* private mode */ }
  }

  // After every change: which window is in front, the dots in the dock, the address, what is kept.
  function sync(push) {
    var top = active();
    wins.forEach(function (w) {
      w.el.classList.toggle('is-active', w === top);
      if (w.entry) { w.entry.classList.toggle('is-front', w === top); }
    });
    body.classList.toggle('os-has-window', !!top);
    if (activeName) { activeName.textContent = top ? top.title : ''; }
    markRunning(top);
    address(top, push);
    persist();
  }

  // The theme of the home screen is the theme of the windows.
  function syncTheme(w) {
    var doc;
    try { doc = w.frame.contentDocument; } catch (e) { return; }
    if (!doc || !doc.documentElement || !pathOf(w)) { return; }
    var t = html.getAttribute('data-theme');
    if (t) { doc.documentElement.setAttribute('data-theme', t); } else { doc.documentElement.removeAttribute('data-theme'); }
  }
  new MutationObserver(function () { wins.forEach(syncTheme); }).observe(html, { attributes: true, attributeFilter: ['data-theme'] });

  function create(app, url, opts) {
    var el = template.content.firstElementChild.cloneNode(true);
    var w = {
      id: ++seq, app: app, start: opts.start || url, url: url, el: el,
      frame: $('.os-window__frame', el), titleEl: $('[data-win-title]', el), iconEl: $('[data-win-icon]', el), backBtn: $('[data-win-back]', el),
      stack: [], tab: null, source: opts.source || null, title: '', min: false, leaving: false, z: 0, entry: null, anim: null,
      mode: 'normal', rect: null,
    };
    var kept = opts.rect ? { rect: opts.rect, mode: opts.mode } : remembered(app);
    w.rect = kept ? kept.rect : defaultRect();
    w.mode = kept ? validMode(kept.mode) : 'normal';
    // A window never opens exactly on another one: a step down and to the right, like a stack of paper.
    if (!opts.rect && w.mode === 'normal') {
      for (var n = 0; n < 8 && wins.some(function (o) { return !o.min && !o.leaving && o.mode === 'normal' && Math.abs(o.rect.x - w.rect.x) < 10 && Math.abs(o.rect.y - w.rect.y) < 10; }); n++) {
        w.rect.x += 30;
        w.rect.y += 30;
      }
      // A new window that was stepped down ends above the dock, as the first one does (a place the person chose is kept as it was).
      var room = area();
      if (!kept && w.rect.y + w.rect.h > room.bottom) { w.rect.h = Math.max(MINH, room.bottom - w.rect.y); }
    }
    clampRect(w.rect);
    el.id = 'os-window-' + w.id;
    w.frame.setAttribute('name', 'os-window-' + w.id);
    var first = !windowOf(app);
    wins.push(w);
    layer.appendChild(el);
    if (!(first && pinned(app))) { addEntry(w); }
    setTitle(w, opts.title || '');
    setIcon(w, opts.source || sourceOf(app));
    place(w);

    // Every page that loads in the window: its title, whether Back has somewhere to go, the address, the theme.
    w.frame.addEventListener('load', function () {
      var path = pathOf(w);
      if (!path) { return; }
      var s = w.stack;
      if (s.length > 1 && s[s.length - 2] === path) { s.pop(); } else if (s[s.length - 1] !== path) { s.push(path); }
      w.backBtn.disabled = s.length < 2;
      var doc = w.frame.contentDocument;
      if (doc) {
        setTitle(w, (doc.title || '').split(' · ')[0].replace(instance, '').trim() || w.title);
        var ext = doc.querySelector('[data-window-tab]');
        w.tab = ext ? ext.getAttribute('data-window-tab') : null;
        syncTheme(w);
        // A press inside the page brings its window to the front.
        doc.addEventListener('pointerdown', function () { closeMenus(); if (active() !== w && !w.min && !w.leaving) { show(w); } }, true);
      }
      if (w === active()) { address(w, false); }
      persist();
      if (!restoring && s.length > 1) { refreshIn(1500); }
    });
    w.frame.setAttribute('src', url);

    if (opts.min) {
      w.min = true;
      el.hidden = true;
    } else {
      w.z = opts.z || (zTop += 2);
      zTop = Math.max(zTop, w.z);
      el.style.zIndex = String(w.z);
      if (!restoring) {
        var from = opts.source && body.contains(opts.source) && opts.source.getClientRects().length ? centre(opts.source) : originOf(w);
        animateWin(w, from ? 'is-opening' : 'is-appearing', from);
      }
    }
    return w;
  }

  // Brings a window to the front (back on the screen when it was put away).
  function show(w, opts) {
    var was = w.min;
    w.min = false;
    w.el.hidden = false;
    w.z = (zTop += 2);
    w.el.style.zIndex = String(w.z);
    if (was) {
      place(w);
      var from = originOf(w);
      animateWin(w, from ? 'is-opening' : 'is-appearing', from);
    }
    sync(!(opts && opts.fromAddress));
    try { w.frame.focus({ preventScroll: true }); } catch (e) { /* the frame may not take the focus yet */ }
  }

  // Opens a page: the window its app has already comes to the front (as it is, or on that page with navigate), else a new one.
  function open(url, opts) {
    opts = opts || {};
    closeMenus();
    closeFolders();
    var app = opts.app || url;
    var w = opts.fresh ? null : windowOf(app);
    if (w) {
      if (opts.navigate && (pathOf(w) || w.url) !== url) { w.url = url; w.frame.setAttribute('src', url); }
      if (opts.source) { w.source = opts.source; }
      show(w, opts);
      return w;
    }
    w = create(app, url, opts);
    sync(!opts.fromAddress);
    return w;
  }

  // Puts a window away: the app stays as it is, its icon in the dock brings it back.
  function minimize(w) {
    if (w.min || w.leaving) { return; }
    var from = originOf(w);
    w.min = true;
    sync(false);
    animateWin(w, from ? 'is-closing' : 'is-leaving', from, function () { if (w.min) { w.el.hidden = true; } });
    var back = w.entry || pinned(w.app) || w.source;
    if (back && body.contains(back) && !active()) { try { back.focus({ preventScroll: true }); } catch (e) { /* no focus */ } }
  }
  function showDesktop() { wins.slice().forEach(minimize); }

  function close(w) {
    if (w.leaving) { return; }
    var wasMin = w.min;
    w.leaving = true;
    var gone = function () {
      w.el.remove();
      wins.splice(wins.indexOf(w), 1);
      sync(false);
    };
    removeEntry(w);
    sync(false);
    if (wasMin) { gone(); } else { animateWin(w, 'is-leaving', null, gone); }
    var next = active();
    if (next) { try { next.frame.focus({ preventScroll: true }); } catch (e) { /* no focus */ } }
  }
  function closeAll() { wins.slice().forEach(close); }

  function goBack(w) { try { w.frame.contentWindow.history.back(); } catch (e) { /* nothing to go back to */ } }
  function reloadWindow(w) { try { w.frame.contentWindow.location.reload(); } catch (e) { w.frame.src = w.frame.src; } }
  function windowInTab(w) { window.open(w.tab || pathOf(w) || w.url, '_blank', 'noopener'); }
  function setMode(w, mode) {
    if (w.min) { show(w); }
    w.mode = w.mode === mode ? 'normal' : mode;
    if (w.mode === 'normal') { clampRect(w.rect); }
    place(w, true);
    remember(w);
    persist();
  }

  layer.addEventListener('click', function (e) {
    var b = e.target.closest('button');
    var el = e.target.closest('.os-window');
    var w = el ? byEl(el) : null;
    if (!b || !w) { return; }
    if (b.hasAttribute('data-win-close')) { close(w); }
    else if (b.hasAttribute('data-win-min')) { minimize(w); }
    else if (b.hasAttribute('data-win-max')) { setMode(w, 'max'); }
    else if (b.hasAttribute('data-win-back')) { goBack(w); }
    else if (b.hasAttribute('data-win-reload')) { reloadWindow(w); }
    else if (b.hasAttribute('data-win-tab')) { windowInTab(w); }
  });
  layer.addEventListener('dblclick', function (e) {
    var bar = e.target.closest('.os-window__bar');
    if (!bar || e.target.closest('button') || phone.matches) { return; }
    var w = byEl(bar.closest('.os-window'));
    if (w) { setMode(w, 'max'); }
  });

  // ------------------------------------------------------------------ moving and sizing a window

  function setSnap(mode) {
    if (!mv || mv.snap === mode) { return; }
    mv.snap = mode;
    if (!snapEl) { return; }
    var r = mode ? modeRect(mode) : null;
    if (!r) { snapEl.hidden = true; return; }
    snapEl.style.left = r.x + 'px';
    snapEl.style.top = r.y + 'px';
    snapEl.style.width = r.w + 'px';
    snapEl.style.height = r.h + 'px';
    snapEl.style.zIndex = String(mv.w.z - 1);
    snapEl.hidden = false;
  }
  function beginMove(e) {
    var w = mv.w;
    mv.started = true;
    if (w.mode !== 'normal') {
      // Dragged out of the full screen or a half: its own size again, under the pointer. Sized there: from what is seen.
      var seen = modeRect(w.mode);
      if (mv.kind === 'move') {
        w.rect.x = Math.round(e.clientX - w.rect.w * ((e.clientX - seen.x) / seen.w));
        w.rect.y = seen.y;
      } else {
        w.rect = seen;
      }
      w.mode = 'normal';
      place(w);
    }
    mv.r0 = { x: w.rect.x, y: w.rect.y, w: w.rect.w, h: w.rect.h };
    mv.x0 = e.clientX;
    mv.y0 = e.clientY;
    body.classList.add('os-moving');
    body.setAttribute('data-os-moving', mv.kind);
    w.el.classList.remove('is-settling');
    closeMenus();
  }
  function endMove() {
    var m = mv;
    mv = null;
    if (!m || !m.started) { return; }
    var w = m.w;
    if (m.snap) { w.mode = m.snap; } else { clampRect(w.rect); }
    if (snapEl) { snapEl.hidden = true; }
    place(w, true);
    body.classList.remove('os-moving');
    body.removeAttribute('data-os-moving');
    remember(w);
    persist();
  }

  layer.addEventListener('pointerdown', function (e) {
    var el = e.target.closest('.os-window');
    var w = el ? byEl(el) : null;
    if (!w || w.leaving) { return; }
    if (w !== active()) { show(w); }
    if (mv || phone.matches || (e.pointerType === 'mouse' && e.button !== 0)) { return; }
    var grip = e.target.closest('[data-win-resize]');
    var bar = e.target.closest('.os-window__bar');
    if (!grip && (!bar || e.target.closest('button'))) { return; }
    mv = { w: w, id: e.pointerId, kind: grip ? grip.getAttribute('data-win-resize') : 'move', x0: e.clientX, y0: e.clientY, started: false, snap: null };
    if (grip) { e.preventDefault(); beginMove(e); }
  });
  document.addEventListener('pointermove', function (e) {
    if (!mv || e.pointerId !== mv.id) { return; }
    if (!mv.started) {
      if (Math.abs(e.clientX - mv.x0) + Math.abs(e.clientY - mv.y0) < 5) { return; }
      beginMove(e);
    }
    e.preventDefault();
    var dx = e.clientX - mv.x0;
    var dy = e.clientY - mv.y0;
    var r = mv.w.rect;
    var r0 = mv.r0;
    var a = area();
    var k = mv.kind;
    if (k === 'move') {
      r.x = r0.x + dx;
      r.y = Math.max(a.top, Math.min(r0.y + dy, window.innerHeight - 48));
      setSnap(e.clientY <= a.top + 3 ? 'max' : e.clientX <= 6 ? 'left' : e.clientX >= window.innerWidth - 7 ? 'right' : null);
    } else {
      if (k.indexOf('e') !== -1) { r.w = Math.max(MINW, r0.w + dx); }
      if (k.indexOf('s') !== -1) { r.h = Math.max(MINH, r0.h + dy); }
      if (k.indexOf('w') !== -1) { var nw = Math.max(MINW, r0.w - dx); r.x = r0.x + r0.w - nw; r.w = nw; }
      if (k.indexOf('n') !== -1) { var top = Math.min(Math.max(a.top, r0.y + dy), r0.y + r0.h - MINH); r.h = r0.y + r0.h - top; r.y = top; }
    }
    place(mv.w);
  }, { passive: false });
  document.addEventListener('pointerup', function (e) { if (mv && e.pointerId === mv.id) { endMove(); } });
  document.addEventListener('pointercancel', function (e) { if (mv && e.pointerId === mv.id) { endMove(); } });

  // Another size of the browser: every window keeps its bar on the screen, and a full one stays full.
  window.addEventListener('resize', function () {
    wins.forEach(function (w) { clampRect(w.rect); place(w); });
  });

  // A click into a page of another address (an installed app) tells nothing; the frame that took the focus does.
  window.addEventListener('blur', function () {
    setTimeout(function () {
      var f = document.activeElement;
      if (!f || f.tagName !== 'IFRAME') { return; }
      var el = f.closest('.os-window');
      var w = el ? byEl(el) : null;
      closeMenus();
      if (w && w !== active() && !w.min && !w.leaving) { show(w); }
    }, 0);
  });

  // The dock's icon of a window: to the front, or away when it is in front.
  document.addEventListener('click', function (e) {
    var entry = e.target.closest ? e.target.closest('.os-dock__win') : null;
    var w = entry ? byId(entry.getAttribute('data-win-id')) : null;
    if (!w) { return; }
    if (w === active()) { minimize(w); } else { show(w); }
  });

  // A page in a window asks to go home (a link to /): its window is put away; a page that landed on the home screen closes its window.
  window.addEventListener('message', function (e) {
    if (e.origin !== location.origin || !e.data || e.data.type !== 'mv-home') { return; }
    var w = wins.filter(function (o) { return o.frame.contentWindow === e.source; })[0];
    if (!w) { return; }
    if (e.data.close) { close(w); } else { minimize(w); }
  });

  // The browser's Back and Forward, and an address with a window in it (#/Store).
  function fromAddress() {
    var h = location.hash;
    if (h.length > 2 && h.charAt(1) === '/') {
      var path;
      try { path = decodeURIComponent(h.slice(1)); } catch (e) { return; }
      // a path of this MatOS only, never another address
      if (path.charAt(1) === '/' || path.charAt(1) === '\\') { return; }
      var w = wins.filter(function (o) { return !o.leaving && (pathOf(o) === path || o.url === path || o.start === path); })[0];
      if (!w) { open(path, { fromAddress: true, title: '' }); } else if (w !== active()) { show(w, { fromAddress: true }); }
    } else if (active()) {
      showDesktop();
    }
  }
  window.addEventListener('popstate', fromAddress);

  // The windows this tab had before a reload.
  function restoreWindows() {
    var kept = [];
    try { kept = JSON.parse(sessionStorage.getItem(OPEN) || '[]'); } catch (e) { kept = []; }
    if (!Array.isArray(kept)) { return; }
    restoring = true;
    kept.filter(function (k) { return k && typeof k.app === 'string' && typeof k.url === 'string' && k.url.charAt(0) === '/' && k.url.charAt(1) !== '/' && k.url.charAt(1) !== '\\'; })
      .sort(function (a, b) { return (a.z || 0) - (b.z || 0); })
      .slice(0, 12)
      .forEach(function (k) {
        create(k.app, k.url, { start: typeof k.start === 'string' ? k.start : k.url, title: typeof k.title === 'string' ? k.title : '', rect: validRect(k.rect) || defaultRect(), mode: validMode(k.mode), min: !!k.min });
      });
    restoring = false;
    sync(false);
  }

  // ------------------------------------------------------------------ the home screen refreshes itself, and the windows stay

  // What changes by itself (the state of the apps, the values of the widgets) is fetched and put in place of the things; a reload would take the
  // windows with it. Not while the person edits or drags, and never under a menu or a folder that is open (later(false): try again soon).
  // The home screen says how often while something runs (data-autorefresh); a page that loads in a window (an install has begun, an app was
  // removed) asks for it once.
  var refreshTimer = null;
  function refreshIn(ms) {
    clearTimeout(refreshTimer);
    refreshTimer = setTimeout(function () {
      refreshTimer = null;
      refresh(function (done) {
        var every = pagesEl ? parseInt(pagesEl.getAttribute('data-autorefresh'), 10) : 0;
        if (!done) { refreshIn(3000); } else if (every > 0) { refreshIn(every); }
      });
    }, ms);
  }
  function refresh(later) {
    var busy = function () { return editing || !!drag || !!appMenu || $$('.os-folder').some(function (f) { return !f.hidden; }); };
    if (!pagesEl) { later(true); return; }
    if (busy() || document.hidden) { later(false); return; }
    fetch(location.pathname + location.search, { credentials: 'same-origin', headers: { 'Accept': 'text/html' } })
      .then(function (r) {
        // Signed out meanwhile (the answer is the sign-in page): the tab goes there.
        if (r.redirected) { location.reload(); return null; }
        return r.ok ? r.text() : '';
      })
      .then(function (text) {
        if (text === null) { return; }
        if (busy()) { later(false); return; }
        var doc = new DOMParser().parseFromString(text, 'text/html');
        var fresh = doc.querySelector('[data-os-pages]');
        if (!fresh) { later(true); return; }
        var now = currentPage();
        pagesEl.innerHTML = fresh.innerHTML;
        if (fresh.hasAttribute('data-os-default')) { pagesEl.setAttribute('data-os-default', ''); } else { pagesEl.removeAttribute('data-os-default'); }
        keepOrder();
        if (fresh.hasAttribute('data-autorefresh')) { pagesEl.setAttribute('data-autorefresh', fresh.getAttribute('data-autorefresh')); } else { pagesEl.removeAttribute('data-autorefresh'); }
        var freshHidden = doc.querySelector('[data-os-hidden]');
        if (hiddenBox && freshHidden) { hiddenBox.innerHTML = freshHidden.innerHTML; }
        $$('.os-folder').forEach(function (f) {
          var same = f.id ? doc.getElementById(f.id) : null;
          if (same) { f.innerHTML = same.innerHTML; }
        });
        var busyOld = $('.os-status__busy');
        var busyNow = doc.querySelector('.os-status__busy');
        if (busyOld) { busyOld.remove(); }
        var end = $('.os-status__end');
        if (busyNow && end) { end.insertBefore(document.importNode(busyNow, true), end.firstChild); }
        var search = $('[data-os-search]');
        if (search && search.value) { search.dispatchEvent(new Event('input')); } else { flow(); }
        goToPage(Math.min(now, pages().length - 1), false);
        updateDots();
        markRunning(active());
        tick();
        later(true);
      }).catch(function () { later(true); });
  }
  window.MatOSOs = { refresh: refresh };

  // ------------------------------------------------------------------ the pages of the home screen

  var pagesEl = $('[data-os-pages]');
  var dotsEl = $('[data-os-dots]');
  var hiddenBox = $('[data-os-hidden]');
  var editbar = $('[data-os-editbar]');
  var editing = false;
  var drag = null;
  var thing = '.os-page > [data-home-key]';

  function pages() { return pagesEl ? $$('.os-page', pagesEl) : []; }
  function things(root) { return $$(':scope > [data-home-key]', root); }
  function keyOf(el) { return el.getAttribute('data-home-key'); }
  function nameOf(el) { return el.getAttribute('data-home-name') || el.getAttribute('data-title') || ''; }

  // From the start of one page to the start of the next (the gap between them included).
  function stride() {
    var p = pages();
    if (p.length > 1) { return p[1].getBoundingClientRect().left - p[0].getBoundingClientRect().left; }
    return pagesEl.clientWidth;
  }
  function currentPage() {
    var s = stride();
    var n = pages().length;
    return s > 0 ? Math.max(0, Math.min(n - 1, Math.round(pagesEl.scrollLeft / s))) : 0;
  }
  function goToPage(i, smooth) {
    if (!pagesEl) { return; }
    pagesEl.scrollTo({ left: Math.max(0, i) * stride(), behavior: smooth && !reduced ? 'smooth' : 'auto' });
  }

  function labelPages() {
    var p = pages();
    p.forEach(function (page, i) { page.setAttribute('aria-label', 'Page ' + (i + 1) + ' of ' + p.length); });
  }
  function markEmpty() {
    pages().forEach(function (page) { page.classList.toggle('is-empty', things(page).length === 0); });
  }

  function renderDots() {
    if (!dotsEl) { return; }
    var n = pages().length;
    var now = currentPage();
    dotsEl.innerHTML = '';
    dotsEl.hidden = n < 2 || (pagesEl && pagesEl.classList.contains('is-searching'));
    for (var i = 0; i < n; i++) {
      var dot = document.createElement('button');
      dot.type = 'button';
      dot.className = 'os-dot';
      dot.setAttribute('role', 'tab');
      dot.setAttribute('aria-selected', i === now ? 'true' : 'false');
      dot.setAttribute('aria-label', 'Page ' + (i + 1) + ' of ' + n);
      dot.setAttribute('data-page-index', String(i));
      dotsEl.appendChild(dot);
    }
  }
  function updateDots() {
    var now = currentPage();
    $$('.os-dot', dotsEl).forEach(function (d, i) { d.setAttribute('aria-selected', i === now ? 'true' : 'false'); });
    try { sessionStorage.setItem(PAGE, String(now)); } catch (e) { /* private mode */ }
  }

  function createPage() {
    var page = document.createElement('div');
    page.className = 'os-grid os-page is-empty';
    page.setAttribute('data-page', '');
    page.setAttribute('role', 'group');
    pagesEl.appendChild(page);
    labelPages();
    renderDots();
    return page;
  }
  // ------------------------------------------------------------------ a page holds what fits on the screen

  // The home screen never scrolls. The person's pages are as long as they like; the screen shows of each what fits, and the rest continues on a page of
  // its own right after it (data-continues), as a phone does. The places of the grid are taken in order (an earlier thing never moves for a later one),
  // so the first thing that reaches below the page and all after it continue. What is kept is the person's pages (layout()), so the same arrangement
  // fits a phone and a large screen. Moving, sizing, hiding a thing and a new size of the window let the pages flow again.
  //
  // The home screen as it comes (data-os-default: the person never arranged it) keeps the order of the server, and a screen that has room left at its end
  // takes the later things that fit there before the next page begins (so a phone shows the apps on the first page and no empty band above the dots).
  // An arrangement of the person's own is never reordered.
  function asItComes() { return !!pagesEl && pagesEl.hasAttribute('data-os-default'); }
  var order = new WeakMap();
  function keepOrder() {
    var n = 0;
    pages().forEach(function (page) { things(page).forEach(function (t) { order.set(t, n++); }); });
  }
  // The person changed something: from now on the home screen is theirs, as it is on the screen.
  function mine() { if (pagesEl) { pagesEl.removeAttribute('data-os-default'); } }

  // A widget that follows icons begins a row of its own at the left, when nothing reaches into that place; else at least on a pair of columns
  // (os-at-N), so that widgets line up as on a phone and do not sit half a column off. The grid itself places everything in order.
  var atClasses = ['os-at-1', 'os-at-3', 'os-at-5', 'os-at-7'];
  function align(page) {
    var list = things(page);
    list.forEach(function (t) { t.classList.remove.apply(t.classList, atClasses); });
    var g = null;
    for (var i = 1; i < list.length; i++) {
      var el = list[i];
      var prev = list[i - 1];
      if (!el.classList.contains('os-widget') || prev.classList.contains('os-widget')) { continue; }
      if (!g) {
        var cs = getComputedStyle(page);
        var cols = cs.gridTemplateColumns.split(' ').length;
        var gapX = parseFloat(cs.columnGap) || 0;
        var box = page.getBoundingClientRect();
        g = { cols: cols, box: box, step: (box.width - gapX * (cols - 1)) / cols + gapX, rowStep: (parseFloat(cs.gridAutoRows) || 0) + (parseFloat(cs.rowGap) || 0) };
      }
      var pr = prev.getBoundingClientRect();
      var x = g.box.left + 4;
      var y = pr.top + g.rowStep + 4;
      var taken = list.slice(0, i).some(function (o) { var r = o.getBoundingClientRect(); return r.left <= x && r.right >= x && r.top <= y && r.bottom >= y; });
      var at = 1;
      if (taken) {
        var span = Math.max(1, Math.round(el.getBoundingClientRect().width / g.step));
        var next = Math.round((pr.left - g.box.left) / g.step) + 2;
        at = next % 2 === 0 ? next + 1 : next;
        if (at + span - 1 > g.cols) { at = 1; }
      }
      if (at <= 7) { el.classList.add('os-at-' + at); }
    }
  }

  // The later things that fit in what is left of a full screen move up before the first that does not (the home screen as it comes only).
  function fillUp(page, stop, bottom) {
    var list = things(page);
    for (var j = list.indexOf(stop) + 1; j < list.length; j++) {
      var el = list[j];
      var back = el.nextSibling;
      page.insertBefore(el, stop);
      align(page);
      if (el.getBoundingClientRect().bottom > bottom) {
        page.insertBefore(el, back);
        align(page);
      }
    }
  }

  function flow() {
    if (!pagesEl || pagesEl.classList.contains('is-searching')) { return; }
    var owner = null;
    pages().forEach(function (page) {
      if (page.hasAttribute('data-continues') && owner) {
        things(page).forEach(function (t) { owner.appendChild(t); });
        page.remove();
      } else {
        owner = page;
      }
    });
    if (asItComes()) {
      pages().forEach(function (page) {
        things(page).sort(function (a, b) { return (order.has(a) ? order.get(a) : 1e6) - (order.has(b) ? order.get(b) : 1e6); })
          .forEach(function (t) { page.appendChild(t); });
      });
    }
    for (var page = pages()[0]; page; page = page.nextElementSibling) {
      align(page);
      var list = things(page);
      var bottom = page.getBoundingClientRect().bottom + 1;
      var cut = -1;
      for (var i = 0; i < list.length; i++) {
        if (list[i].getBoundingClientRect().bottom > bottom) { cut = i; break; }
      }
      if (cut > 0 && asItComes()) {
        var stop = list[cut];
        fillUp(page, stop, bottom);
        list = things(page);
        cut = list.indexOf(stop);
      }
      // A thing taller than the screen stays alone on its page.
      if (cut === 0) { cut = list.length > 1 ? 1 : -1; }
      if (cut > 0) {
        var next = document.createElement('div');
        next.className = 'os-grid os-page';
        next.setAttribute('data-page', '');
        next.setAttribute('data-continues', '');
        next.setAttribute('role', 'group');
        pagesEl.insertBefore(next, page.nextElementSibling);
        list.slice(cut).forEach(function (t) { next.appendChild(t); });
      }
    }
    labelPages();
    markEmpty();
    renderDots();
  }

  // Empty pages go (while editing they stay, so that something can be put there); one page always stays.
  function pruneEmptyPages() {
    var now = currentPage();
    pages().forEach(function (page) { if (things(page).length === 0 && pages().length > 1) { page.remove(); } });
    labelPages();
    renderDots();
    goToPage(Math.min(now, pages().length - 1), false);
  }

  if (pagesEl) {
    var ticking = false;
    pagesEl.addEventListener('scroll', function () {
      if (ticking) { return; }
      ticking = true;
      requestAnimationFrame(function () { ticking = false; updateDots(); });
    }, { passive: true });
    if (dotsEl) {
      dotsEl.addEventListener('click', function (e) {
        var dot = e.target.closest('.os-dot');
        if (dot) { goToPage(parseInt(dot.getAttribute('data-page-index'), 10), true); }
      });
    }
    // Native dragging of a link or a picture would take the place of moving a thing.
    pagesEl.addEventListener('dragstart', function (e) { e.preventDefault(); });
    keepOrder();
    flow();
    // Another size of the window holds more or less: the pages flow again, and the thing that was first on the screen stays in sight.
    var resizing = null;
    window.addEventListener('resize', function () {
      if (resizing === null) { var shown = pages()[currentPage()]; resizing = { first: shown ? things(shown)[0] : null }; }
      clearTimeout(resizing.timer);
      resizing.timer = setTimeout(function () {
        var first = resizing.first;
        resizing = null;
        if (drag) { return; }
        flow();
        if (first && first.parentNode) { goToPage(pages().indexOf(first.parentNode), false); }
        updateDots();
      }, 150);
    });
    // A reload (the home screen refreshes itself) comes back to the page the person was on.
    try {
      var kept = parseInt(sessionStorage.getItem(PAGE) || '0', 10);
      if (kept > 0 && kept < pages().length) { goToPage(kept, false); }
    } catch (e) { /* private mode */ }
    updateDots();
  }

  // ------------------------------------------------------------------ keeping the arrangement (the account has it)

  var saveTimer = null;
  function layout() {
    var sizes = {};
    $$('[data-home-key][data-size]').forEach(function (el) {
      if (el.closest('.os-ghost')) { return; }
      var size = el.getAttribute('data-size');
      if (size && size !== el.getAttribute('data-size-default')) { sizes[keyOf(el)] = size; }
    });
    return {
      // the person's pages: what continued on the next screen belongs to the page before it
      pages: pages().reduce(function (all, p) {
        var keys = things(p).map(keyOf);
        if (p.hasAttribute('data-continues') && all.length) { all[all.length - 1] = all[all.length - 1].concat(keys); } else { all.push(keys); }
        return all;
      }, []).filter(function (keys) { return keys.length > 0; }),
      hidden: hiddenBox ? things(hiddenBox).map(keyOf) : [],
      sizes: sizes,
    };
  }
  function token() {
    var t = $('input[name="__RequestVerificationToken"]');
    return t ? t.value : '';
  }
  function failed() {
    if (window.MatUi && window.MatUi.toast) { window.MatUi.toast('The home screen could not be saved. Try again in a moment.', { tone: 'danger' }); }
  }
  var inflight = Promise.resolve();
  function send(keepalive) {
    saveTimer = null;
    inflight = fetch('/api/home', {
      method: 'PUT',
      credentials: 'same-origin',
      keepalive: !!keepalive,
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
      body: JSON.stringify(layout()),
    }).then(function (r) { if (!r.ok) { failed(); } }, function () { if (!keepalive) { failed(); } });
  }
  function save() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(function () { send(false); }, 400);
  }
  window.addEventListener('pagehide', function () { if (saveTimer) { clearTimeout(saveTimer); send(true); } });

  function resetLayout() {
    var ask = window.MatUi && window.MatUi.confirm
      ? window.MatUi.confirm('The pages, the places of the apps and widgets, their sizes and what you took off the screen are forgotten, and the home screen is as it comes.', { title: 'Reset the home screen?', ok: 'Reset', danger: true })
      : Promise.resolve(window.confirm('Reset the home screen?'));
    ask.then(function (yes) {
      if (!yes) { return; }
      clearTimeout(saveTimer);
      saveTimer = null;
      // After a save that is still on its way, so that it cannot bring the old arrangement back.
      inflight.then(function () { return fetch('/api/home', { method: 'DELETE', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() } }); })
        .then(function (r) {
          if (!r.ok) { failed(); return; }
          try { sessionStorage.removeItem(PAGE); } catch (e) { /* private mode */ }
          location.reload();
        }, failed);
    });
  }

  // ------------------------------------------------------------------ moving things: animated, to a place, to a page, off the screen

  // FLIP: where everything was, then let each glide from there to its new place.
  function snapshot() {
    var at = [];
    pages().forEach(function (page) { things(page).forEach(function (el) { at.push({ el: el, r: el.getBoundingClientRect() }); }); });
    return at;
  }
  function glide(at) {
    if (reduced) { return; }
    at.forEach(function (a) {
      if (drag && a.el === drag.el) { return; }
      var r = a.el.getBoundingClientRect();
      var dx = a.r.left - r.left;
      var dy = a.r.top - r.top;
      // Only what moved on the page that is seen; a thing that changed pages jumps.
      if ((dx || dy) && Math.abs(dx) < window.innerWidth && a.el.animate) {
        a.el.animate([{ transform: 'translate(' + dx + 'px, ' + dy + 'px)' }, { transform: 'none' }], { duration: 220, easing: 'cubic-bezier(.2, .9, .25, 1)' });
      }
    });
  }
  function moveTo(el, page, before) {
    if (before === el || (el.parentNode === page && el.nextSibling === before)) { return; }
    mine();
    var at = snapshot();
    page.insertBefore(el, before || null);
    flow();
    glide(at);
  }
  function moveToPage(el, index) {
    var page = index >= pages().length ? createPage() : pages()[index];
    moveTo(el, page, null);
    goToPage(pages().indexOf(el.parentNode), true);
    if (!editing) { setTimeout(pruneEmptyPages, 450); }
    save();
  }
  function moveBy(el, step) {
    var list = things(el.parentNode);
    var i = list.indexOf(el) + step;
    if (i < 0 || i >= list.length) { return; }
    moveTo(el, el.parentNode, step < 0 ? list[i] : list[i].nextSibling);
    save();
    try { el.focus(); } catch (e) { /* not focusable */ }
  }

  var sizeClass = { small: 'os-w2', medium: 'os-w2x2', wide: 'os-w4x2' };
  function setSize(el, size) {
    mine();
    var at = snapshot();
    Object.keys(sizeClass).forEach(function (s) { el.classList.remove(sizeClass[s]); });
    el.classList.add(sizeClass[size] || 'os-w2');
    el.setAttribute('data-size', size);
    flow();
    glide(at);
    save();
  }

  function hide(el) {
    if (!hiddenBox) { return; }
    mine();
    var at = snapshot();
    $$(':scope > .os-remove', el).forEach(function (b) { b.remove(); });
    hiddenBox.appendChild(el);
    flow();
    glide(at);
    updateEditbar();
    save();
  }
  function restore(el) {
    var page = pages()[currentPage()] || pages()[0];
    if (!page) { return; }
    mine();
    var at = snapshot();
    page.appendChild(el);
    if (editing) { addRemove(el); }
    flow();
    glide(at);
    if (!reduced && el.animate) { el.animate([{ transform: 'scale(.6)', opacity: 0 }, { transform: 'none', opacity: 1 }], { duration: 260, easing: 'cubic-bezier(.2, .9, .25, 1)' }); }
    markEmpty();
    updateEditbar();
    save();
  }

  // ------------------------------------------------------------------ editing the home screen (wiggle, minus, Show again, Done)

  function addRemove(el) {
    if ($(':scope > .os-remove', el)) { return; }
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'os-remove';
    b.setAttribute('aria-label', 'Remove ' + nameOf(el) + ' from the home screen');
    b.title = 'Remove from the home screen';
    b.innerHTML = icon('minus');
    el.appendChild(b);
  }
  function updateEditbar() {
    var show = editbar && $('[data-os-showhidden]', editbar);
    if (show) { show.hidden = !hiddenBox || things(hiddenBox).length === 0; }
  }
  function hold() { body.classList.toggle('os-hold', editing || !!(drag && drag.started)); }
  function setEditing(on) {
    if (!pagesEl || editing === on) { return; }
    editing = on;
    body.classList.toggle('os-editing', on);
    if (editbar) { editbar.hidden = !on; }
    closeMenus();
    if (on) {
      pages().forEach(function (page) { things(page).forEach(addRemove); });
      updateEditbar();
      var done = editbar && $('[data-os-done]', editbar);
      if (done) { done.focus(); }
    } else {
      $$('.os-remove').forEach(function (b) { b.remove(); });
      pruneEmptyPages();
      save();
    }
    hold();
  }
  if (editbar) {
    editbar.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b) { return; }
      // The bar's own click: it must not reach the document, which closes a menu the click did not land in (the one "Show again" just opened).
      e.stopPropagation();
      if (b.hasAttribute('data-os-done')) { setEditing(false); }
      if (b.hasAttribute('data-os-addpage')) { createPage(); goToPage(pages().length - 1, true); }
      if (b.hasAttribute('data-os-showhidden')) { var r = b.getBoundingClientRect(); openHiddenMenu(r.left, r.bottom + 6); }
    });
  }

  // ------------------------------------------------------------------ dragging a thing (the mouse at once; a finger while editing)

  function startDrag(e) {
    var el = drag.el;
    var r = el.getBoundingClientRect();
    drag.started = true;
    drag.dx = e.clientX - r.left;
    drag.dy = e.clientY - r.top;
    drag.origin = { parent: el.parentNode, next: el.nextSibling };
    var ghost = el.cloneNode(true);
    ghost.classList.add('os-ghost');
    ghost.removeAttribute('data-home-key');
    ghost.setAttribute('aria-hidden', 'true');
    $$('.os-remove, [id]', ghost).forEach(function (n) { if (n.classList.contains('os-remove')) { n.remove(); } else { n.removeAttribute('id'); } });
    ghost.style.width = r.width + 'px';
    ghost.style.height = r.height + 'px';
    ghost.style.left = r.left + 'px';
    ghost.style.top = r.top + 'px';
    body.appendChild(ghost);
    drag.ghost = ghost;
    el.classList.add('is-placeholder');
    body.classList.add('os-dragging');
    cancelPress();
    closeMenus();
    hold();
  }

  function clearTurn() {
    if (drag && drag.turn) { clearTimeout(drag.turn.timer); drag.turn = null; }
    $$('.os-dot.is-target').forEach(function (d) { d.classList.remove('is-target'); });
  }
  // Waiting at the edge of the screen (or over a dot) turns the page; the thing comes along to its end. Past the last page a new one begins,
  // unless the last one holds nothing but the thing itself.
  function turnTo(index, why) {
    if (drag.turn && drag.turn.why === why) { return; }
    clearTurn();
    drag.turn = {
      why: why,
      timer: setTimeout(function () {
        if (!drag) { return; }
        var p = pages();
        if (index < 0) { drag.turn = null; return; }
        if (index >= p.length) {
          var last = p[p.length - 1];
          if (things(last).filter(function (t) { return t !== drag.el; }).length === 0) { drag.turn = null; return; }
          createPage();
        }
        moveTo(drag.el, pages()[index], null);
        // A full page let it continue on the next one: the page that has it.
        goToPage(pages().indexOf(drag.el.parentNode), true);
        drag.turn = null;
      }, why.indexOf('dot') === 0 ? 420 : 650),
    };
  }

  function moveDrag(x, y) {
    drag.ghost.style.left = (x - drag.dx) + 'px';
    drag.ghost.style.top = (y - drag.dy) + 'px';
    var under = document.elementFromPoint(x, y);
    var dot = under && under.closest ? under.closest('.os-dot') : null;
    if (dot) {
      dot.classList.add('is-target');
      turnTo(parseInt(dot.getAttribute('data-page-index'), 10), 'dot' + dot.getAttribute('data-page-index'));
      return;
    }
    // the edges of the strip as far as they are on the screen (it reaches out beyond the screen for the shadows)
    var box = pagesEl.getBoundingClientRect();
    var left = Math.max(box.left, 0) + 40;
    var right = Math.min(box.right, window.innerWidth) - 40;
    var now = currentPage();
    if (x < left) { turnTo(now - 1, 'left' + now); } else if (x > right) { turnTo(now + 1, 'right' + now); } else { clearTurn(); }

    var page = under && under.closest ? under.closest('.os-page') : null;
    if (!page || !pagesEl.contains(page)) { return; }
    var target = under.closest(thing);
    if (target === drag.el) { return; }
    if (!target) {
      // The empty part of a page: the thing goes to its end, when the pointer is past the last one.
      var list = things(page);
      var last = list[list.length - 1];
      // A full page has no room at its end (the thing would continue on the next page): then nothing happens until the pointer is over a thing.
      if (last !== drag.el && drag.full !== page) {
        var lr = last ? last.getBoundingClientRect() : null;
        if (!lr || y > lr.bottom || (y >= lr.top && x > lr.right)) {
          moveTo(drag.el, page, null);
          drag.last = null;
          drag.full = drag.el.parentNode === page ? null : page;
        }
      }
      return;
    }
    drag.full = null;
    var tr = target.getBoundingClientRect();
    var after = x > tr.left + tr.width / 2;
    if (drag.last && drag.last.target === target && drag.last.after === after) { return; }
    drag.last = { target: target, after: after };
    moveTo(drag.el, page, after ? target.nextSibling : target);
  }

  function endDrag(cancel) {
    var d = drag;
    drag = null;
    if (!d) { return; }
    if (d.turn) { clearTimeout(d.turn.timer); }
    $$('.os-dot.is-target').forEach(function (n) { n.classList.remove('is-target'); });
    if (!d.started) { hold(); return; }
    if (cancel && d.origin.parent.isConnected) { d.origin.parent.insertBefore(d.el, d.origin.next && d.origin.next.parentNode === d.origin.parent ? d.origin.next : null); }
    var r = d.el.getBoundingClientRect();
    var ghost = d.ghost;
    var finish = function () {
      ghost.remove();
      d.el.classList.remove('is-placeholder');
    };
    if (!reduced && ghost.animate) {
      ghost.animate([{ left: ghost.style.left, top: ghost.style.top }, { left: r.left + 'px', top: r.top + 'px' }], { duration: 170, easing: 'cubic-bezier(.2, .9, .25, 1)' }).onfinish = finish;
    } else {
      finish();
    }
    body.classList.remove('os-dragging');
    // The click that ends a drag opens nothing.
    suppressClick = true;
    markEmpty();
    if (!editing) { pruneEmptyPages(); } else { renderDots(); }
    hold();
    if (!cancel) { save(); }
  }

  document.addEventListener('pointerdown', function (e) {
    if (drag || mv || (e.pointerType === 'mouse' && e.button !== 0)) { return; }
    var el = e.target.closest ? e.target.closest(thing) : null;
    if (!el || !pagesEl || pagesEl.classList.contains('is-searching') || e.target.closest('.os-remove')) { return; }
    // A finger drags only while editing; else it swipes the pages and scrolls.
    if (e.pointerType !== 'mouse' && !editing) { return; }
    drag = { el: el, id: e.pointerId, x0: e.clientX, y0: e.clientY, started: false };
  });
  document.addEventListener('pointermove', function (e) {
    if (!drag || e.pointerId !== drag.id) { return; }
    if (!drag.started) {
      if (Math.abs(e.clientX - drag.x0) + Math.abs(e.clientY - drag.y0) < 6) { return; }
      startDrag(e);
    }
    e.preventDefault();
    moveDrag(e.clientX, e.clientY);
  }, { passive: false });
  document.addEventListener('pointerup', function (e) { if (drag && e.pointerId === drag.id) { endDrag(false); } });
  document.addEventListener('pointercancel', function (e) { if (drag && e.pointerId === drag.id) { endDrag(false); } });

  // ------------------------------------------------------------------ opening things

  // An app opens in the window of the home screen unless the person chose a new tab for it in its menu (an app that cannot be framed shows the way to a tab there).
  function prefersWindow(el) {
    var key = el.getAttribute('data-app-key');
    try { return !key || localStorage.getItem(PREFS + key) !== 'tab'; } catch (e) { return true; }
  }

  // First of all clicks: the one that ends a drag or a long press opens nothing; while editing a tap does not open a thing (its minus takes it off),
  // and a tap beside the things ends editing.
  var suppressClick = false;
  // A new touch or press starts afresh (a long press may end without a click).
  window.addEventListener('pointerdown', function () { suppressClick = false; }, true);
  window.addEventListener('click', function (e) {
    if (suppressClick && !(e.target.closest && e.target.closest('.os-menu'))) { suppressClick = false; e.preventDefault(); e.stopPropagation(); return; }
    if (!editing) { return; }
    var t = e.target;
    var minus = t.closest ? t.closest('.os-remove') : null;
    if (minus) { e.preventDefault(); e.stopPropagation(); hide(minus.closest('[data-home-key]')); return; }
    if (t.closest && t.closest(thing)) { e.preventDefault(); e.stopPropagation(); return; }
    if (t.closest && t.closest('.os-editbar, .os-menu, .os-dots, .os-status, .os-bottom, .os-window, .os-folder, .mat-dialog, dialog')) { return; }
    if (t.closest && t.closest('.os-desktop')) { setEditing(false); }
  }, true);

  document.addEventListener('click', function (e) {
    var el = e.target.closest ? e.target.closest('[data-window]') : null;
    if (!el || e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) { return; }
    if (el.getAttribute('target') === '_blank' && !prefersWindow(el)) { return; }
    e.preventDefault();
    var url = el.getAttribute('data-window');
    // The dock is the taskbar too: a click on the app whose window is in front puts that window away.
    var has = el.classList.contains('os-dock__app') ? windowOf(url) : null;
    if (has && has === active()) { minimize(has); return; }
    // From the person's menu the window grows out of the person's button.
    open(url, { source: el.closest('.os-menu') ? $('[data-os-menu]') : el, title: el.getAttribute('data-title') });
  });

  // The logo in the status bar shows the desktop: every window is put away.
  var brand = $('.os-status__brand');
  if (brand) { brand.addEventListener('click', function (e) { if (active()) { e.preventDefault(); showDesktop(); } }); }

  // ------------------------------------------------------------------ menus: the person's, and the one of everything else (a right click, a long press)

  var appMenu = null;
  function closeMenus() {
    $$('.os-menu').forEach(function (m) { if (m !== appMenu) { m.hidden = true; } });
    $$('[data-os-menu]').forEach(function (b) { b.setAttribute('aria-expanded', 'false'); });
    if (appMenu) { appMenu.remove(); appMenu = null; }
  }
  document.addEventListener('click', function (e) {
    var toggle = e.target.closest ? e.target.closest('[data-os-menu]') : null;
    if (toggle) {
      var menu = document.getElementById(toggle.getAttribute('data-os-menu'));
      var show = menu.hidden;
      closeMenus();
      menu.hidden = !show;
      toggle.setAttribute('aria-expanded', show ? 'true' : 'false');
      if (show) { var first = $('.os-menu__item', menu); if (first) { first.focus(); } }
      return;
    }
    if (!(e.target.closest && e.target.closest('.os-menu'))) { closeMenus(); }
  });

  function icon(name) {
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
  }
  function item(iconName, text, attrs) {
    var b = document.createElement(attrs.href ? 'a' : 'button');
    b.className = 'os-menu__item' + (attrs.danger ? ' os-menu__item--danger' : '');
    b.setAttribute('role', attrs.radio ? 'menuitemradio' : attrs.check != null ? 'menuitemcheckbox' : 'menuitem');
    if (!attrs.href) { b.type = 'button'; }
    Object.keys(attrs).forEach(function (k) { if (k !== 'check' && k !== 'run' && k !== 'radio' && k !== 'danger' && attrs[k] != null) { b.setAttribute(k, attrs[k]); } });
    if (attrs.check != null) { b.setAttribute('aria-checked', attrs.check ? 'true' : 'false'); }
    b.innerHTML = (iconName ? icon(iconName) : '<span class="os-menu__blank" aria-hidden="true"></span>') + '<span></span>';
    b.lastChild.textContent = text;
    if (attrs.run) { b.addEventListener('click', function (e) { e.preventDefault(); closeMenus(); attrs.run(); }); }
    return b;
  }
  function sep(menu) {
    var last = menu.lastElementChild;
    if (!last || last.classList.contains('os-menu__sep') || last.classList.contains('os-menu__head')) { return; }
    var s = document.createElement('div');
    s.className = 'os-menu__sep';
    s.setAttribute('role', 'separator');
    menu.appendChild(s);
  }
  function heading(menu, text) {
    var h = document.createElement('div');
    h.className = 'os-menu__label';
    h.setAttribute('aria-hidden', 'true');
    h.textContent = text;
    menu.appendChild(h);
  }
  function newMenu(name, pic, sub) {
    closeMenus();
    var menu = document.createElement('div');
    menu.className = 'os-menu';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', name || 'Menu');
    if (name) {
      var head = document.createElement('div');
      head.className = 'os-menu__head';
      if (pic) { head.appendChild(pic.cloneNode(true)); }
      var label = document.createElement('div');
      label.innerHTML = '<strong></strong><small></small>';
      label.firstChild.textContent = name;
      label.lastChild.textContent = sub || '';
      head.appendChild(label);
      menu.appendChild(head);
    }
    return menu;
  }
  function showMenu(menu, x, y) {
    var last = menu.lastElementChild;
    if (last && last.classList.contains('os-menu__sep')) { last.remove(); }
    body.appendChild(menu);
    appMenu = menu;
    var w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.max(8, Math.min(x, window.innerWidth - w - 8)) + 'px';
    menu.style.top = Math.max(8, Math.min(y, window.innerHeight - h - 8)) + 'px';
    var first = $('.os-menu__item', menu);
    if (first) { first.focus(); }
  }

  // The entries of an app: open it here or in a tab, always in a tab, its quick actions, its details and settings in MatOS.
  function appEntries(menu, el) {
    var name = el.getAttribute('data-title') || '';
    var tab = el.getAttribute('data-tab') || '';
    var key = el.getAttribute('data-app-key');
    var target = el.getAttribute('data-window');
    var has = windowOf(target);
    menu.appendChild(item('monitor', has ? 'Show its window' : 'Open', { run: function () { open(target, { source: el, title: name }); } }));
    if (has) { menu.appendChild(item('plus', 'New window', { run: function () { open(target, { source: el, title: name, fresh: true }); } })); }
    menu.appendChild(item('external', 'Open in a new tab', { run: function () { window.open(tab || target, '_blank', 'noopener'); } }));
    if (key && tab) {
      var inTab = !prefersWindow(el);
      menu.appendChild(item('check', 'Always open in a new tab', {
        check: inTab,
        run: function () { try { if (inTab) { localStorage.removeItem(PREFS + key); } else { localStorage.setItem(PREFS + key, 'tab'); } } catch (e) { /* private mode */ } }
      }));
    }
    // The quick actions of an app (contract 2, contributes.quickActions): an address of the app, opened like the app itself (a tab, or the window when it prefers it).
    var quick = [];
    try { quick = JSON.parse(el.getAttribute('data-quick') || '[]'); } catch (err) { quick = []; }
    quick = Array.isArray(quick) ? quick.filter(function (q) { return q && typeof q.label === 'string' && typeof q.window === 'string' && q.window.charAt(0) === '/' && q.window.charAt(1) !== '/'; }) : [];
    if (quick.length) {
      sep(menu);
      quick.forEach(function (q) {
        var qicon = /^[a-z0-9-]+$/.test(q.icon || '') ? q.icon : 'arrow-right';
        var qtab = typeof q.tab === 'string' && /^https?:\/\//.test(q.tab) ? q.tab : null;
        menu.appendChild(item(qicon, q.label, {
          run: function () {
            if (qtab && !prefersWindow(el)) { window.open(qtab, '_blank', 'noopener'); } else { open(q.window, { app: target, navigate: true, source: el, title: name }); }
          }
        }));
      });
    }
    var details = el.getAttribute('data-details');
    var manage = el.getAttribute('data-manage');
    if (details || manage) {
      sep(menu);
      if (details) { menu.appendChild(item('info', 'Details', { run: function () { open(details, { source: el, title: name }); } })); }
      if (manage) { menu.appendChild(item('sliders', 'Manage', { run: function () { open(manage, { source: el, title: name }); } })); }
    }
  }

  // A link inside a widget (an engine, a row of a list) or the widget itself: open it here or in a tab.
  function linkEntries(menu, link, widget) {
    var target = link.getAttribute('data-window');
    var title = link.getAttribute('data-title') || '';
    var row = $('.os-wlist__title, .os-engine__name', link);
    var what = link === widget ? 'Open' : 'Open ' + (row ? row.textContent : title);
    menu.appendChild(item('monitor', what, { run: function () { open(target, { source: widget, title: title }); } }));
    menu.appendChild(item('external', 'Open in a new tab', { run: function () { window.open(link.getAttribute('data-tab') || target, '_blank', 'noopener'); } }));
  }

  function editEntry(menu) {
    sep(menu);
    menu.appendChild(item(editing ? 'check' : 'edit', editing ? 'Done' : 'Edit the home screen', { run: function () { setEditing(!editing); } }));
  }

  // What every thing on a page has: move it (one place, to another page), its size (a widget), take it off the screen, edit the home screen.
  function thingEntries(menu, el) {
    var p = pages();
    var here = p.indexOf(el.parentNode);
    var list = things(el.parentNode);
    var at = list.indexOf(el);
    var moves = [];
    if (at > 0) { moves.push(item('chevron-left', 'One place back', { run: function () { moveBy(el, -1); } })); }
    if (at < list.length - 1) { moves.push(item('chevron-right', 'One place on', { run: function () { moveBy(el, 1); } })); }
    p.forEach(function (page, i) {
      if (i !== here) { moves.push(item('arrow-right', 'To page ' + (i + 1), { run: function () { moveToPage(el, i); } })); }
    });
    if (!(list.length === 1 && here === p.length - 1)) {
      moves.push(item('plus', 'To a new page', { run: function () { moveToPage(el, pages().length); } }));
    }
    if (moves.length) {
      sep(menu);
      heading(menu, 'Move');
      moves.forEach(function (m) { menu.appendChild(m); });
    }
    var sizes = (el.getAttribute('data-sizes') || '').split(' ').filter(Boolean);
    if (sizes.length > 1) {
      sep(menu);
      heading(menu, 'Size');
      var labels = { small: 'Small', medium: 'Medium', wide: 'Wide' };
      sizes.forEach(function (s) {
        menu.appendChild(item(null, labels[s] || s, { radio: true, check: el.getAttribute('data-size') === s, run: function () { setSize(el, s); } }));
      });
    }
    sep(menu);
    menu.appendChild(item('eye-off', 'Remove from the home screen', { run: function () { hide(el); } }));
    editEntry(menu);
  }

  function hiddenEntries(menu) {
    var hidden = hiddenBox ? things(hiddenBox) : [];
    if (!hidden.length) { return; }
    sep(menu);
    heading(menu, 'Show again');
    hidden.forEach(function (el) { menu.appendChild(item('plus', nameOf(el), { run: function () { restore(el); } })); });
  }
  function openHiddenMenu(x, y) {
    var menu = newMenu();
    menu.setAttribute('aria-label', 'Show again');
    hiddenEntries(menu);
    showMenu(menu, x, y);
  }

  // The empty screen, the status bar: the home screen itself.
  function openScreenMenu(x, y) {
    var menu = newMenu();
    menu.setAttribute('aria-label', 'Home screen');
    menu.appendChild(item(editing ? 'check' : 'edit', editing ? 'Done' : 'Edit the home screen', { run: function () { setEditing(!editing); } }));
    if (pagesEl) { menu.appendChild(item('plus', 'Add a page', { run: function () { setEditing(true); createPage(); goToPage(pages().length - 1, true); } })); }
    hiddenEntries(menu);
    sep(menu);
    menu.appendChild(item('image', 'Change the wallpaper', { run: function () { open('/Account/Profile?tab=home', { title: 'Wallpaper' }); } }));
    if ($('.os-dock__app[data-window="/Settings"]')) {
      menu.appendChild(item('store', 'Install an app', { run: function () { open('/Store', { title: 'Store' }); } }));
      menu.appendChild(item('settings', 'Settings', { run: function () { open('/Settings', { title: 'Settings' }); } }));
    }
    if (wins.length) {
      sep(menu);
      if (active()) { menu.appendChild(item('home', 'Show the desktop', { run: showDesktop })); }
      menu.appendChild(item('x', wins.length === 1 ? 'Close the window' : 'Close all ' + wins.length + ' windows', { run: closeAll }));
    }
    sep(menu);
    menu.appendChild(item('refresh', 'Reload', { run: function () { location.reload(); } }));
    if (pagesEl) { menu.appendChild(item('restore', 'Reset the home screen', { danger: true, run: resetLayout })); }
    showMenu(menu, x, y);
  }

  // A window (its bar, its icon in the dock): what its buttons do, and where it goes.
  function openWindowMenu(w, x, y) {
    var menu = newMenu(w.title || 'Window', w.iconEl.firstElementChild, 'Window');
    if (w.min || w !== active()) { menu.appendChild(item('monitor', 'Bring to the front', { run: function () { show(w); } })); sep(menu); }
    if (!w.backBtn.disabled) { menu.appendChild(item('chevron-left', 'Back', { run: function () { goBack(w); } })); }
    menu.appendChild(item('refresh', 'Reload', { run: function () { reloadWindow(w); } }));
    menu.appendChild(item('external', 'Open in a new tab', { run: function () { windowInTab(w); } }));
    if (!phone.matches) {
      sep(menu);
      heading(menu, 'Place');
      menu.appendChild(item(null, 'Fill the screen', { check: w.mode === 'max', run: function () { setMode(w, 'max'); } }));
      menu.appendChild(item(null, 'Left half', { check: w.mode === 'left', run: function () { setMode(w, 'left'); } }));
      menu.appendChild(item(null, 'Right half', { check: w.mode === 'right', run: function () { setMode(w, 'right'); } }));
    }
    sep(menu);
    if (!w.min) { menu.appendChild(item('minus', 'Put away (the app stays open)', { run: function () { minimize(w); } })); }
    menu.appendChild(item('x', 'Close', { run: function () { close(w); } }));
    if (wins.length > 1) { menu.appendChild(item('x', 'Close all ' + wins.length + ' windows', { danger: true, run: closeAll })); }
    showMenu(menu, x, y);
  }

  var appSelector = '.os-app[data-window], .os-dock__app[data-window]';
  function openMenuFor(target, x, y) {
    var bar = target.closest('.os-window__bar');
    var entry = target.closest('.os-dock__win');
    var ofWindow = bar ? byEl(bar.closest('.os-window')) : entry ? byId(entry.getAttribute('data-win-id')) : null;
    if (ofWindow) { openWindowMenu(ofWindow, x, y); return; }
    var el = target.closest(thing) || target.closest(appSelector) || target.closest('.os-folderbtn');
    if (!el) { openScreenMenu(x, y); return; }
    var isApp = el.matches(appSelector);
    var pic = el.querySelector('.os-app__icon > .mv-appicon, .os-app__icon > .os-ico, :scope > .os-ico');
    var name = isApp ? (el.getAttribute('data-title') || '') : nameOf(el);
    var sub = isApp ? (el.getAttribute('title') ? el.getAttribute('title').split(' · ').slice(1).join(' · ') : 'MatOS') : el.classList.contains('os-folderbtn') ? 'Folder' : 'Widget';
    var menu = newMenu(name, pic, sub);
    if (isApp) {
      appEntries(menu, el);
    } else if (el.classList.contains('os-folderbtn')) {
      menu.appendChild(item('folder-open', 'Open', { run: function () { el.click(); } }));
    } else {
      var link = target.closest('[data-window]');
      if (link && el.contains(link)) { linkEntries(menu, link, el); }
    }
    if (el.matches(thing)) { thingEntries(menu, el); } else if (!el.closest('.os-folder')) { editEntry(menu); }
    showMenu(menu, x, y);
  }

  document.addEventListener('contextmenu', function (e) {
    // Shift with the right click: the browser's own menu; a field keeps it (to paste); the window's page has its own.
    if (e.shiftKey) { return; }
    var t = e.target;
    if (!t.closest || t.closest('input, textarea, select, [contenteditable="true"], .os-window__frame, dialog')) { return; }
    e.preventDefault();
    if (t.closest('.os-menu') || drag) { return; }
    var x = e.clientX, y = e.clientY;
    // The key for the menu (or Shift+F10) has no pointer: the menu opens below what has the focus.
    if (!x && !y) { var r = t.getBoundingClientRect(); x = r.left; y = r.bottom + 6; }
    if (editing && t.closest(thing) && e.pointerType === 'touch') { return; }
    openMenuFor(t, x, y);
  });

  // A long press on a touch screen: the menu (while editing, a press on a thing is the start of dragging it), and the tap that ends it opens nothing.
  var press = null;
  document.addEventListener('pointerdown', function (e) {
    if (e.pointerType === 'mouse') { return; }
    var t = e.target;
    if (!t.closest || t.closest('input, textarea, select, .os-menu, .os-window__frame, .os-editbar, dialog')) { return; }
    if (editing && t.closest(thing)) { return; }
    if (!t.closest('.os-desktop, .os-bottom, .os-window__bar, .os-folder, .os-status')) { return; }
    var x = e.clientX, y = e.clientY;
    press = {
      x: x, y: y, timer: setTimeout(function () {
        press = null;
        suppressClick = true;
        var el = t.closest(thing) || t.closest(appSelector);
        var r = el ? el.getBoundingClientRect() : null;
        openMenuFor(t, r ? r.left : x, r ? r.bottom + 8 : y);
      }, 480)
    };
  });
  function cancelPress() { if (press) { clearTimeout(press.timer); press = null; } }
  document.addEventListener('pointerup', cancelPress);
  document.addEventListener('pointercancel', cancelPress);
  document.addEventListener('pointermove', function (e) { if (press && Math.abs(e.clientX - press.x) + Math.abs(e.clientY - press.y) > 12) { cancelPress(); } });

  // ------------------------------------------------------------------ the keyboard: menus, pages, editing

  document.addEventListener('keydown', function (e) {
    var t = e.target;
    var inMenu = t.closest && t.closest('.os-menu');
    if (inMenu && /^(ArrowDown|ArrowUp|Home|End)$/.test(e.key)) {
      var entries = $$('.os-menu__item', inMenu).filter(function (b) { return b.offsetParent !== null; });
      var i = entries.indexOf(t);
      var next = e.key === 'Home' ? 0 : e.key === 'End' ? entries.length - 1 : e.key === 'ArrowDown' ? (i + 1) % entries.length : (i - 1 + entries.length) % entries.length;
      if (entries[next]) { e.preventDefault(); entries[next].focus(); }
      return;
    }
    if (e.key === 'Escape') {
      if (drag) { endDrag(true); return; }
      var hadMenu = !!appMenu || $$('.os-menu').some(function (m) { return !m.hidden; });
      closeMenus();
      if (!hadMenu && editing) { setEditing(false); }
      return;
    }
    if (!pagesEl || (t && t.closest && t.closest('.os-window')) || e.ctrlKey || e.metaKey || e.altKey || (t && (t.isContentEditable || /^(input|textarea|select)$/i.test(t.tagName)))) { return; }
    // While editing, Delete takes the thing that has the focus off the screen.
    if (editing && (e.key === 'Delete' || e.key === 'Backspace') && t.closest && t.closest(thing) === t) { e.preventDefault(); var nextOne = t.nextElementSibling; hide(t); if (nextOne && nextOne.focus) { nextOne.focus(); } return; }
    if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && pages().length > 1 && !inMenu) {
      e.preventDefault();
      goToPage(currentPage() + (e.key === 'ArrowRight' ? 1 : -1), true);
    }
  });

  // ------------------------------------------------------------------ folders (System: the engines), like the folders of a phone

  function closeFolders() {
    $$('.os-folder').forEach(function (f) {
      if (!f.hidden) {
        f.hidden = true;
        var btn = $('[data-os-folder="' + f.id + '"]');
        if (btn) { btn.setAttribute('aria-expanded', 'false'); }
      }
    });
  }
  document.addEventListener('click', function (e) {
    var btn = e.target.closest ? e.target.closest('[data-os-folder]') : null;
    if (btn) {
      var folder = document.getElementById(btn.getAttribute('data-os-folder'));
      if (!folder) { return; }
      closeMenus();
      folder.hidden = false;
      btn.setAttribute('aria-expanded', 'true');
      var first = $('.os-app', folder);
      if (first) { first.focus(); }
      return;
    }
    // a click beside the panel closes the folder
    var openFolder = e.target.closest ? e.target.closest('.os-folder') : null;
    if (openFolder && !e.target.closest('.os-folder__panel')) { closeFolders(); }
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') { closeFolders(); } });

  // ------------------------------------------------------------------ the search: the apps of every page in one grid

  var find = $('[data-os-search]');
  if (find) {
    var filter = function () {
      var q = find.value.trim().toLowerCase();
      if (q !== '' && editing) { setEditing(false); }
      if (pagesEl) { var was = pagesEl.classList.contains('is-searching'); pagesEl.classList.toggle('is-searching', q !== ''); if (was && q === '') { flow(); } }
      if (dotsEl) { dotsEl.hidden = q !== '' || pages().length < 2; }
      $$('.os-grid .os-app[data-name]').forEach(function (a) { a.hidden = q !== '' && a.getAttribute('data-name').indexOf(q) === -1; });
      $$('.os-grid [data-widget]').forEach(function (w) { w.hidden = q !== ''; });
    };
    find.addEventListener('input', filter);
    find.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { find.value = ''; filter(); find.blur(); }
      if (e.key === 'Enter') {
        var first = $$('.os-grid .os-app[data-name]').filter(function (a) { return !a.hidden; })[0];
        if (first) { e.preventDefault(); first.click(); }
      }
    });
    document.addEventListener('keydown', function (e) {
      if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) { return; }
      var t = e.target;
      if (t && (t.isContentEditable || /^(input|textarea|select)$/i.test(t.tagName))) { return; }
      e.preventDefault();
      find.focus();
    });
  }

  // ------------------------------------------------------------------ the clocks and the greeting

  var formats = {
    time: { hour: '2-digit', minute: '2-digit' },
    date: { day: 'numeric', month: 'long', year: 'numeric' },
    'short-date': { weekday: 'short', day: 'numeric', month: 'short' },
    weekday: { weekday: 'long' },
  };
  function tick() {
    var now = new Date();
    $$('[data-os-clock]').forEach(function (el) {
      var f = formats[el.getAttribute('data-os-clock')];
      if (!f) { return; }
      el.textContent = f.hour ? now.toLocaleTimeString([], f) : now.toLocaleDateString([], f);
    });
    // The greeting is looked up each time: a refresh of the home screen draws the widget anew.
    var h = now.getHours();
    var part = h < 5 ? 'Good night' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
    $$('[data-os-greeting]').forEach(function (greet) { greet.textContent = part + ', ' + greet.getAttribute('data-os-greeting'); });
  }
  tick();
  setInterval(tick, 10000);

  // The windows of before a reload, then an address with a window in it (last: everything above is ready).
  restoreWindows();
  fromAddress();
  if (pagesEl && parseInt(pagesEl.getAttribute('data-autorefresh'), 10) > 0) { refreshIn(parseInt(pagesEl.getAttribute('data-autorefresh'), 10)); }
})();
