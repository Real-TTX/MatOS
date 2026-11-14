/* Mattiverse: a page inside a window of the home screen. Loaded in the <head> of every page, before it is drawn.
   The home screen (Pages/Shared/_Launcher.cshtml, js/os.js) opens a page in a window of its own: an iframe of the same Mattiverse.
   In there a page drops the menu and the header of the shell (the window has a title, Back and Close); see .mv-in-window in mattiverse.css.
   - A page that belongs to the whole tab (sign-in, a shared link, an error: <html data-top-level>) leaves the window and takes the tab.
   - The home screen itself never draws inside the window: when a page lands on it (a redirect to /), the window closes instead.
   - A link to the home screen inside the window puts the window away (the page stays as it is). */
(function () {
  'use strict';

  var inWindow = false;
  try { inWindow = window.top !== window.self && window.top.location.origin === window.location.origin; } catch (e) { inWindow = false; }
  if (!inWindow) { return; }

  var html = document.documentElement;
  var home = function (close) { window.top.postMessage({ type: 'mv-home', close: !!close }, window.location.origin); };

  if (html.hasAttribute('data-os')) { home(true); return; }
  if (html.hasAttribute('data-top-level')) { window.top.location.href = window.location.href; return; }

  html.classList.add('mv-in-window');

  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a || e.defaultPrevented || a.target === '_blank' || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) { return; }
    var url = new URL(a.getAttribute('href'), window.location.href);
    if (url.origin === window.location.origin && url.pathname === '/' && !url.search) {
      e.preventDefault();
      home();
    }
  }, true);
})();
