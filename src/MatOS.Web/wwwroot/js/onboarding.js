/* MatOS: the first start and the tour of the home screen (docs/ARCHITECTURE.md "The first start"). Plain JavaScript, no build step.
   Switched on by data-* attributes:
     <form data-ob-flow data-ob-offset="2" data-ob-start="0">   steps in one form (data-ob-step), one shown at a time; data-ob-next / data-ob-prev move,
                                                                the dots (data-ob-dots) follow; a step is checked before Next; Enter goes on; the summary
                                                                (data-ob-summary) is written from the choices
     <div data-ob-progress="/api/setup/progress">              the progress screen: polled, only what changed is exchanged; "Try again" (data-ob-retry),
                                                                "Show details" (data-ob-details), the way home when it is done (data-ob-countdown)
     <div data-ob-tour="/api/home/tour">                        the tour of the home screen: one mark at a time (data-ob-tour-step, data-target)
   The toolkit's script does the rest (validation on submit, data-show-if, data-copy). */
(function () {
  'use strict';

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
  var calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function token() {
    var t = $('input[name="__RequestVerificationToken"]');
    return t ? t.value : '';
  }

  // ------------------------------------------------------------------ a field's message, the way the toolkit writes it (FORM-03)

  function messageFor(input) {
    var v = input.validity;
    if (v.valueMissing) { return input.getAttribute('data-msg-required') || 'Please fill in this field.'; }
    if (v.typeMismatch) { return input.getAttribute('data-msg-type') || 'Please enter a valid value.'; }
    if (v.tooShort || v.tooLong || v.rangeUnderflow || v.rangeOverflow) { return input.getAttribute('data-msg-range') || input.validationMessage; }
    if (v.patternMismatch) { return input.getAttribute('data-msg-pattern') || input.validationMessage; }
    return input.validationMessage;
  }

  function showError(input, text) {
    var field = input.closest('.mat-field');
    if (!field) { return; }
    field.classList.toggle('is-invalid', !!text);
    var line = $('.mat-error', field);
    if (line) { line.textContent = text || ''; }
    input.setAttribute('aria-invalid', text ? 'true' : 'false');
  }

  // ------------------------------------------------------------------ the steps in one form

  $$('[data-ob-flow]').forEach(function (flow) {
    var steps = $$('[data-ob-step]', flow);
    if (!steps.length) { return; }
    var dots = $$('[data-ob-dots] li', flow);
    var offset = parseInt(flow.getAttribute('data-ob-offset'), 10) || 0;
    var index = 0;

    function show(i, moved, back) {
      index = Math.max(0, Math.min(steps.length - 1, i));
      steps.forEach(function (s, n) {
        s.classList.toggle('is-active', n === index);
        s.classList.toggle('is-back', n === index && !!back);
      });
      dots.forEach(function (d, n) {
        var at = n - offset;
        d.classList.toggle('is-done', at < index);
        d.classList.toggle('is-active', at === index);
        if (at === index) { d.setAttribute('aria-current', 'step'); } else { d.removeAttribute('aria-current'); }
      });
      var summary = $('[data-ob-summary]', steps[index]);
      if (summary) { summary.textContent = summarize(); }
      if (moved) {
        window.scrollTo({ top: 0, behavior: calm ? 'auto' : 'smooth' });
        // the heading of the new step says where the person is (a screen reader reads it)
        var title = $('h1, h2', steps[index]);
        if (title) { title.setAttribute('tabindex', '-1'); title.focus({ preventScroll: true }); }
      }
    }

    // Before Next: the fields of this step that are shown must be right (the toolkit checks all of them again when the form is sent).
    function stepIsValid(step) {
      var bad = $$('input, select, textarea', step).filter(function (i) { return !i.disabled && i.type !== 'hidden' && !i.closest('[hidden]') && !i.checkValidity(); });
      $$('input, select, textarea', step).forEach(function (i) { if (bad.indexOf(i) < 0 && i.closest('.mat-field.is-invalid') && !i.hasAttribute('data-name-check')) { showError(i, ''); } });
      bad.forEach(function (i) { showError(i, messageFor(i)); });
      // a name of a free address that was answered as taken or reserved: not on with it. When the address service could not be asked, the name is asked
      // again now (js/matos.js) and the person may go on: the server asks once more at "Set up MatOS" and keeps them here with the reason.
      var name = $('[data-name-check]', step);
      var nameField = name && !name.disabled && !name.closest('[hidden]') ? name.closest('.mat-field') : null;
      if (nameField && nameField.classList.contains('is-invalid') && bad.indexOf(name) < 0) {
        if (name.getAttribute('data-name-state') === 'unreachable') { name.dispatchEvent(new CustomEvent('name-recheck')); }
        else { bad.push(name); }
      }
      if (bad.length) { bad[0].focus(); }
      return bad.length === 0;
    }

    flow.addEventListener('click', function (e) {
      var next = e.target.closest('[data-ob-next]');
      var prev = e.target.closest('[data-ob-prev]');
      if (next) {
        e.preventDefault();
        if (next.hasAttribute('data-ob-skip')) {
          $$('input[name="Apps"]', steps[index]).forEach(function (b) { b.checked = false; });
        } else if (!stepIsValid(steps[index])) {
          return;
        }
        show(index + 1, true);
      } else if (prev) {
        e.preventDefault();
        show(index - 1, true, true);
      }
    });

    // Enter in a field goes on to the next step; only the last step sends the form.
    flow.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter' || index >= steps.length - 1 || !e.target.matches('input:not([type=checkbox]):not([type=radio]), select')) { return; }
      var next = $('[data-ob-next]:not([data-ob-skip])', steps[index]);
      if (next) { e.preventDefault(); next.click(); }
    });

    // Sent with a problem in another step (the toolkit stops it): that step is shown first, so the toolkit can put the focus on the field. On the window in the
    // capture phase: the toolkit listens on the document in that phase, and the window comes before it.
    window.addEventListener('submit', function (e) {
      if (e.target !== flow) { return; }
      var bad = $$('input, select, textarea', flow).filter(function (i) { return !i.disabled && i.type !== 'hidden' && !i.closest('[hidden]') && !i.checkValidity(); })[0];
      if (bad) {
        var at = steps.findIndex(function (s) { return s.contains(bad); });
        if (at >= 0 && at !== index) { show(at, false); }
        var details = bad.closest('details');
        if (details) { details.open = true; }
      }
    }, true);

    // The password and its repetition: the same, said at the repetition; "Show the password" shows both.
    $$('[data-ob-same-as]', flow).forEach(function (repeat) {
      var first = document.getElementById(repeat.getAttribute('data-ob-same-as'));
      if (!first) { return; }
      var check = function () { repeat.setCustomValidity(repeat.value && repeat.value !== first.value ? 'The passwords do not match.' : ''); };
      repeat.addEventListener('input', check);
      first.addEventListener('input', check);
    });
    $$('[data-ob-show-passwords]', flow).forEach(function (box) {
      box.addEventListener('change', function () {
        $$('[data-ob-password]', flow).forEach(function (i) { i.type = box.checked ? 'text' : 'password'; });
      });
    });

    // The sentence above "Set up MatOS": what will happen, from the choices (SetupPlan.Summary says the same on the server).
    function summarize() {
      var reach = $('input[name="Reach"]:checked', flow);
      var engines = $$('[data-ob-engine] input[type=checkbox]', flow).filter(function (b) { return b.checked; }).length || 1;
      var apps = $$('input[name="Apps"]', flow).filter(function (b) { return b.checked; }).map(function (b) {
        var card = b.closest('[data-ob-app]');
        return card ? card.getAttribute('data-ob-app') : b.value;
      });
      var text = engines >= 5 ? 'MatOS sets up its five engines' : engines === 1 ? 'MatOS sets up its app manager' : 'MatOS sets up ' + engines + ' of its engines';
      if (apps.length) { text += ', then ' + (apps.length === 1 ? apps[0] : apps.slice(0, -1).join(', ') + ' and ' + apps[apps.length - 1]); }
      var value = reach ? reach.value : '';
      var close = apps.length ? ',' : ''; // "then MatDo, and makes ...": a comma closes the list of the apps
      var name = $('[name="Name"]', flow), domain = $('[name="BaseDomain"]', flow);
      if (value === 'free' && name && name.value.trim()) {
        // the address people open: MatOS is a host below the free address (ProxyPublisher.SelfHost)
        text += close + ' and makes everything reachable from anywhere: you open MatOS at ' + (flow.getAttribute('data-ob-self') || 'matos') + '.' +
          name.value.trim().toLowerCase() + '.' + (flow.getAttribute('data-ob-zone') || 'my.mattiverse.de');
      }
      else if (value === 'domain' && domain && domain.value.trim()) { text += close + ' and puts your apps under ' + domain.value.trim().toLowerCase(); }
      else if (value === 'home') { text += ' for use here at home'; }
      return text + '.';
    }

    // A label of the Next button of the apps: how many are picked.
    var appsNext = $('[data-ob-apps-next]', flow);
    if (appsNext) {
      flow.addEventListener('change', function (e) {
        if (!e.target.matches('input[name="Apps"]')) { return; }
        var n = $$('input[name="Apps"]', flow).filter(function (b) { return b.checked; }).length;
        appsNext.textContent = n === 0 ? 'Next' : n === 1 ? 'Next with 1 app' : 'Next with ' + n + ' apps';
      });
    }

    // Sent (the toolkit found nothing to correct): the button waits, so a second click does not start it twice.
    flow.addEventListener('submit', function (e) {
      if (e.defaultPrevented) { return; }
      var go = e.submitter || $('button[type=submit]', steps[index]);
      if (go) { setTimeout(function () { go.disabled = true; go.setAttribute('aria-busy', 'true'); }, 0); }
    });

    flow.classList.add('is-ready');
    show(parseInt(flow.getAttribute('data-ob-start'), 10) || 0, false);
  });

  // ------------------------------------------------------------------ the progress screen

  var progress = $('[data-ob-progress]');
  if (progress) {
    var url = progress.getAttribute('data-ob-progress');
    var bar = $('[data-ob-bar]', progress);
    var percent = $('[data-ob-percent]', progress);
    var estimate = $('[data-ob-estimate]', progress);
    var headline = $('[data-ob-headline]', progress);
    var countdown = $('[data-ob-countdown]', progress);
    var announce = $('[data-ob-announce]', progress);
    var offline = $('[data-ob-offline]', progress);
    var signedOut = $('[data-ob-signedout]', progress);
    var phase = progress.getAttribute('data-ob-phase');
    var stay = false;
    var timer = null;
    var misses = 0;
    var words = { waiting: 'Waiting', working: 'Working on it', done: 'Done' };

    function task(key) { return $$('[data-ob-task]', progress).filter(function (li) { return li.getAttribute('data-ob-task') === key; })[0]; }

    // A screen reader hears what changed (a line that is done or failed, the end), once, in the polite live region.
    function say(text) { if (announce && text) { announce.textContent = text; } }

    function setPhase(next) {
      if (next === phase) { return; }
      var before = phase;
      phase = next;
      progress.setAttribute('data-ob-phase', next);
      $$('[data-ob-when]', progress).forEach(function (el) { el.hidden = el.getAttribute('data-ob-when').split(' ').indexOf(next) < 0; });
      if (next !== 'working') {
        var lead = $('[data-ob-when="' + next + '"].ob-lead', progress);
        say((headline ? headline.textContent + '. ' : '') + (lead ? lead.textContent.trim() : ''));
      }
      if (next === 'ready' && before === 'working') { startCountdown(); }
    }

    // When all is done the home screen comes by itself after a while, long enough to read how to sign in from now on and to copy the address. It is said
    // once, and the focus goes to "Stay here", so a keyboard or a screen reader can stop it.
    function startCountdown() {
      if (!countdown || stay) { return; }
      var left = 20;
      var seconds = $('[data-ob-seconds]', countdown);
      countdown.hidden = false;
      var stayButton = $('[data-ob-stay]', countdown);
      if (stayButton) { stayButton.focus({ preventScroll: true }); }
      setTimeout(function () { say('Your home screen opens in ' + left + ' seconds. Press Stay here to keep this page.'); }, 1200);
      var tick = function () {
        if (stay) { return; }
        if (left <= 0) { window.location.assign('/'); return; }
        if (seconds) { seconds.textContent = String(left); }
        left -= 1;
        setTimeout(tick, 1000);
      };
      tick();
    }

    // MatOS did not answer: after a few rounds in a row the card says so (it keeps working); an answer that is the sign-in page means the session ended.
    function missed(signedOutNow) {
      misses += 1;
      if (signedOutNow && signedOut) { signedOut.hidden = false; if (offline) { offline.hidden = true; } return; }
      if (misses >= 3 && offline) { offline.hidden = false; }
    }

    function answered() {
      misses = 0;
      if (offline) { offline.hidden = true; }
      if (signedOut) { signedOut.hidden = true; }
    }

    function apply(d) {
      if (bar) { bar.value = d.percent; bar.textContent = d.percent + ' %'; }
      if (percent) { percent.textContent = String(d.percent); }
      if (estimate) { estimate.textContent = d.estimate || ''; }
      if (headline && headline.textContent !== d.headline) { headline.textContent = d.headline; document.title = d.headline + ' · MatOS'; }
      d.steps.forEach(function (s) {
        var li = task(s.key);
        if (!li) { return; }
        var job = String(s.jobId);
        if (li.getAttribute('data-job') !== job) {
          // "Try again" made a new job: its log is another one
          li.setAttribute('data-job', job);
          var log = $('[data-ob-log]', li);
          if (log) { log.textContent = ''; }
        }
        var text = $('[data-ob-state-text]', li);
        var said = s.state === 'failed' ? (s.error || 'This did not work.') : words[s.state] || s.state;
        if (li.getAttribute('data-state') !== s.state) {
          li.setAttribute('data-state', s.state);
          if (s.state === 'done' || s.state === 'failed') {
            var label = $('.ob-task__label', li);
            say((label ? label.textContent + ': ' : '') + said + (s.state === 'failed' ? ' Its line has Try again.' : ''));
          }
        }
        if (text && text.textContent !== said) { text.textContent = said; }
        var open = $('[data-ob-log]:not([hidden])', li);
        if (open) { loadLog(li); }
      });
      setPhase(!d.finished ? 'working' : d.anyFailed ? 'partial' : 'ready');
    }

    function poll() {
      clearTimeout(timer);
      fetch(url, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) {
          // the sign-in page instead of the answer (redirected, or HTML): the session ended
          var json = (r.headers.get('Content-Type') || '').indexOf('json') >= 0;
          if (r.status === 401 || r.status === 403 || r.redirected || (r.ok && !json)) { var e = new Error('signed out'); e.signedOut = true; throw e; }
          if (!r.ok) { throw new Error('HTTP ' + r.status); }
          return r.json();
        })
        .then(function (d) { answered(); apply(d); timer = setTimeout(poll, d.finished ? 15000 : 2000); })
        .catch(function (e) { missed(!!(e && e.signedOut)); timer = setTimeout(poll, 5000); });
    }

    function loadLog(li) {
      var log = $('[data-ob-log]', li);
      fetch('/api/jobs/' + li.getAttribute('data-job') + '/log?offset=0', { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (d) {
          if (!d) { log.textContent = 'The details cannot be read right now.'; return; }
          var text = (d.text || '').trim() + (d.error ? '\n\n' + d.error : '');
          if (log.textContent !== text) { log.textContent = text || 'Nothing written yet.'; log.scrollTop = log.scrollHeight; }
        })
        .catch(function () { /* the next round asks again */ });
    }

    progress.addEventListener('click', function (e) {
      var details = e.target.closest('[data-ob-details]');
      if (details) {
        var li = details.closest('[data-ob-task]');
        var log = $('[data-ob-log]', li);
        log.hidden = !log.hidden;
        details.setAttribute('aria-expanded', log.hidden ? 'false' : 'true');
        $('span', details).textContent = log.hidden ? 'Show details' : 'Hide details';
        if (!log.hidden) { loadLog(li); }
        return;
      }
      if (e.target.closest('[data-ob-stay]')) {
        stay = true;
        if (countdown) { countdown.hidden = true; }
      }
    });

    // "Try again" without leaving the page; without the script the form goes to the server and the page comes again.
    // "Try all again" the same way: every failed line waits again, in the order of the plan.
    progress.addEventListener('submit', function (e) {
      var form = e.target.closest('[data-ob-retry], [data-ob-retry-all]');
      if (!form) { return; }
      e.preventDefault();
      var lines = form.hasAttribute('data-ob-retry-all')
        ? $$('[data-ob-task][data-state="failed"]', progress)
        : [form.closest('[data-ob-task]')].filter(Boolean);
      var button = $('button', form);
      if (button) { button.disabled = true; }
      fetch(form.action, { method: 'POST', credentials: 'same-origin', body: new FormData(form), headers: { 'X-Requested-With': 'fetch' } })
        .then(function () {
          lines.forEach(function (li) { li.setAttribute('data-state', 'waiting'); $('[data-ob-state-text]', li).textContent = words.waiting; });
          stay = false;
          poll();
        })
        .catch(function () { /* the state shows what happened */ })
        .then(function () { if (button) { button.disabled = false; } });
    });

    if (phase === 'working') { poll(); } else { timer = setTimeout(poll, 15000); }
  }

  // ------------------------------------------------------------------ the tour of the home screen

  var tour = $('[data-ob-tour]');
  if (tour) {
    var ring = $('[data-ob-tour-ring]', tour);
    var card = $('[data-ob-tour-card]', tour);
    var count = $('[data-ob-tour-count]', tour);
    var nextBtn = $('[data-ob-tour-next]', tour);
    var marks = [];
    var at = 0;
    var returnFocus = null;

    var visible = function (el) {
      var r = el.getBoundingClientRect();
      return r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < window.innerHeight && !el.closest('[hidden]');
    };
    var targetOf = function (li) { return $$(li.getAttribute('data-target')).filter(visible)[0] || null; };

    var place = function () {
      var li = marks[at];
      var target = li && targetOf(li);
      if (!target) { return; }
      var r = target.getBoundingClientRect();
      var pad = 8;
      ring.style.left = (r.left - pad) + 'px';
      ring.style.top = (r.top - pad) + 'px';
      ring.style.width = (r.width + pad * 2) + 'px';
      ring.style.height = (r.height + pad * 2) + 'px';

      // the card beside the mark: above it in the lower half of the screen (the dock), else below; never off the screen
      var w = card.offsetWidth, h = card.offsetHeight, gap = 18, margin = 16;
      var top = r.top + r.height / 2 > window.innerHeight / 2 ? r.top - pad - gap - h : r.bottom + pad + gap;
      var left = r.left + r.width / 2 - w / 2;
      card.style.left = Math.max(margin, Math.min(window.innerWidth - w - margin, left)) + 'px';
      card.style.top = Math.max(margin, Math.min(window.innerHeight - h - margin, top)) + 'px';
    };

    var showMark = function (i) {
      at = i;
      marks.forEach(function (li, n) { li.hidden = n !== i; });
      count.textContent = (i + 1) + ' of ' + marks.length;
      nextBtn.textContent = i === marks.length - 1 ? 'Done' : 'Next';
      place();
    };

    var finish = function () {
      tour.hidden = true;
      document.removeEventListener('keydown', onKey, true);
      window.removeEventListener('resize', place);
      fetch(tour.getAttribute('data-ob-tour'), { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() } })
        .catch(function () { /* shown once more next time: no harm */ });
      if (returnFocus && returnFocus.focus) { returnFocus.focus(); }
    };

    var onKey = function (e) {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(); }
      else if (e.key === 'ArrowRight' && !e.target.matches('input, textarea')) { e.preventDefault(); nextBtn.click(); }
    };

    tour.addEventListener('click', function (e) {
      if (e.target.closest('[data-ob-tour-skip]')) { finish(); return; }
      if (e.target.closest('[data-ob-tour-next]')) {
        if (at >= marks.length - 1) { finish(); } else { showMark(at + 1); }
      }
    });

    // After the home screen has laid itself out (js/os.js places the things on their pages).
    setTimeout(function () {
      marks = $$('[data-ob-tour-step]', tour).filter(function (li) { return !!targetOf(li); });
      if (!marks.length) { return; }
      returnFocus = document.activeElement;
      tour.hidden = false;
      showMark(0);
      nextBtn.focus();
      document.addEventListener('keydown', onKey, true);
      window.addEventListener('resize', place);
    }, calm ? 150 : 600);
  }
})();
