/* MatOS: a page inside a window of the home screen. Loaded in the <head> of every page, before it is drawn.
   The home screen (Pages/Shared/_Launcher.cshtml, js/os.js) opens a page in a window of its own: an iframe of the same MatOS.
   In there a page drops the menu and the header of the shell (the window has a title, Back and Close); see .mv-in-window in matos.css.
   - A page that belongs to the whole tab (sign-in, a shared link, an error: <html data-top-level>) leaves the window and takes the tab.
   - The home screen itself never draws inside the window: when a page lands on it (a redirect to /), the window closes instead.
   - A link to the home screen inside the window puts the window away (the page stays as it is).
   - A link with data-window asks the home screen for a window of its own (the message mv-open), beside this one.
   - Tabs that do not fit show the current one and fade out where more of them wait. */
(function () {
  'use strict';

  var inWindow = false;
  try { inWindow = window.top !== window.self && window.top.location.origin === window.location.origin; } catch (e) { inWindow = false; }
  if (!inWindow) { return; }

  var html = document.documentElement;
  // A path of this MatOS only, never another address (//host, /\host, and /<tab>/host: the address parser drops tabs and line breaks, so a control
  // character or a backslash is refused outright): resolved against this page and kept only on this origin, the resolved path. Null for anything else.
  function ownPath(path) {
    if (typeof path !== 'string' || path.charAt(0) !== '/' || /[\u0000-\u001F\u007F\\]/.test(path)) { return null; }
    var u;
    try { u = new URL(path, window.location.href); } catch (err) { return null; }
    return u.origin === window.location.origin ? u.pathname + u.search + u.hash : null;
  }

  var home = function (close) { window.top.postMessage({ type: 'mv-home', close: !!close }, window.location.origin); };

  if (html.hasAttribute('data-os')) { home(true); return; }
  if (html.hasAttribute('data-top-level')) { window.top.location.href = window.location.href; return; }

  html.classList.add('mv-in-window');

  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a || e.defaultPrevented || a.target === '_blank' || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) { return; }
    // A link that opens a window of its own (data-window, the list "Getting started"): the home screen opens it beside this one. A path of this MatOS only.
    var own = a.getAttribute('data-window');
    if (own !== null) {
      own = ownPath(own);
      if (own) {
        e.preventDefault();
        window.top.postMessage({ type: 'mv-open', url: own, title: a.getAttribute('data-title') || '' }, location.origin);
      }
      return;
    }
    var url = new URL(a.getAttribute('href'), window.location.href);
    if (url.origin === window.location.origin && url.pathname === '/' && !url.search) {
      e.preventDefault();
      home();
    }
  }, true);

  // Tabs wider than the window scroll sideways: the current one is brought into view (only the tabs scroll, not the page), and the side where more tabs
  // wait is marked (mv-more-start, mv-more-end), which fades out in matos.css. A window that is resized or a tray that is scrolled marks again.
  function mark(tabs) {
    var more = tabs.scrollWidth - tabs.clientWidth > 1;
    tabs.classList.toggle('mv-more-start', more && tabs.scrollLeft > 1);
    tabs.classList.toggle('mv-more-end', more && tabs.scrollLeft < tabs.scrollWidth - tabs.clientWidth - 1);
  }

  function tabs() {
    Array.prototype.forEach.call(document.querySelectorAll('.mat-tabs'), function (list) {
      var current = list.querySelector('.mat-tab.is-active, .mat-tab[aria-selected="true"]');
      if (current && list.scrollWidth > list.clientWidth) {
        var box = list.getBoundingClientRect();
        var tab = current.getBoundingClientRect();
        if (tab.right > box.right) { list.scrollLeft += tab.right - box.right + 24; }
        else if (tab.left < box.left) { list.scrollLeft -= box.left - tab.left + 24; }
      }
      mark(list);
      list.addEventListener('scroll', function () { mark(list); }, { passive: true });
    });
    window.addEventListener('resize', function () {
      Array.prototype.forEach.call(document.querySelectorAll('.mat-tabs'), mark);
    });
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', tabs); } else { tabs(); }
})();
