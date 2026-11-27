/* MatOS: the first start and the tour of the home screen (docs/ARCHITECTURE.md "The first start"). Plain JavaScript, no build step.
   Switched on by data-* attributes:
     <form data-ob-flow data-ob-offset="3" data-ob-start="0">   steps in one form (data-ob-step), one shown at a time; data-ob-next / data-ob-prev move,
                                                                the dots (data-ob-dots) follow (dot offset+n, or the step's data-ob-dot); a step is
                                                                checked before Next; Enter goes on; data-ob-phase1 says how far the first engines are
     <ol data-ob-test="/api/setup/test">                        the test of the choices: one request at a time, when its step is shown and after a change
                                                                (data-ob-test-row per row, data-ob-test-run, data-ob-test-say)
     <div data-ob-records="/api/setup/ip">                      the records of an own domain by hand: the domain and the addresses written in
     <div data-ob-progress="/api/setup/progress">              the progress screen: polled, only what changed is exchanged; "Try again" (data-ob-retry),
                                                                "Show details" (data-ob-details)
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
      // the dot of the step: its own (several cards of one screen share a dot), else the one after the offset
      var own = parseInt(steps[index].getAttribute('data-ob-dot'), 10);
      var current = isNaN(own) ? offset + index : own;
      dots.forEach(function (d, n) {
        d.classList.toggle('is-done', n < current);
        d.classList.toggle('is-active', n === current);
        if (n === current) { d.setAttribute('aria-current', 'step'); } else { d.removeAttribute('aria-current'); }
      });
      // the step says that it is shown (the card of the test starts its test then)
      steps[index].dispatchEvent(new CustomEvent('ob-step-shown', { bubbles: true }));
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
      $$('input, select, textarea', step).forEach(function (i) { if (bad.indexOf(i) < 0 && i.closest('.mat-field.is-invalid')) { showError(i, ''); } });
      bad.forEach(function (i) { showError(i, messageFor(i)); });
      // (whether a free name is taken is the test's row, on its own card: the server reserves it there)
      if (bad.length) { bad[0].focus(); }
      return bad.length === 0;
    }

    flow.addEventListener('click', function (e) {
      var next = e.target.closest('[data-ob-next]');
      var prev = e.target.closest('[data-ob-prev]');
      if (next) {
        e.preventDefault();
        if (!next.hasAttribute('data-ob-skip') && !stepIsValid(steps[index])) { return; }
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

    // Sent (the toolkit found nothing to correct): the button waits, so a second click does not start it twice.
    flow.addEventListener('submit', function (e) {
      if (e.defaultPrevented) { return; }
      var go = e.submitter || $('button[type=submit]', steps[index]);
      if (go) { setTimeout(function () { go.disabled = true; go.setAttribute('aria-busy', 'true'); }, 0); }
    });

    flow.classList.add('is-ready');
    show(parseInt(flow.getAttribute('data-ob-start'), 10) || 0, false);
  });

  // ------------------------------------------------------------------ the first engines while the way to the apps is chosen (phase 1)

  // One line under the dots: how far MatDock and MatSMTP are, asked every 5 seconds until they are over. A failed one links to the progress screen and its
  // "Try again". When MatOS does not answer on localhost (Docker Desktop may stop answering it once MatOS joined its network), the line offers 127.0.0.1.
  var phase1 = $('[data-ob-phase1]');
  if (phase1) {
    var phaseWords = { waiting: 'waiting', working: 'installing', done: 'ready', failed: 'did not work' };
    var link = function (href, text) { var a = document.createElement('a'); a.href = href; a.textContent = text; return a; };
    var pollPhase1 = function () {
      fetch(phase1.getAttribute('data-ob-phase1'), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) { if (!r.ok || (r.headers.get('Content-Type') || '').indexOf('json') < 0) { throw new Error('HTTP ' + r.status); } return r.json(); })
        .then(function (d) {
          var lines = d.steps.map(function (s) { return s.label + ': ' + (phaseWords[s.state] || s.state); });
          var failed = d.steps.some(function (s) { return s.state === 'failed'; });
          phase1.textContent = lines.join(' · ') + (lines.length ? (failed ? '. ' : '.') : '');
          if (failed) { phase1.appendChild(link('/Setup/Progress', 'Try again')); }
          phase1.hidden = !lines.length;
          if (!d.finished) { setTimeout(pollPhase1, 5000); }
        })
        .catch(function () {
          if (window.location.hostname === 'localhost') {
            phase1.textContent = 'MatOS does not answer as localhost right now. ';
            phase1.appendChild(link(window.location.protocol + '//127.0.0.1' + (window.location.port ? ':' + window.location.port : '') + window.location.pathname, 'Open it as 127.0.0.1'));
            phase1.hidden = false;
          }
          setTimeout(pollPhase1, 5000);
        });
    };
    pollPhase1();
  }

  // ------------------------------------------------------------------ the test of the choices

  // The card of the test (SetupService.TestAsync): one request at a time (the button waits meanwhile), when its card is shown, after a change of an answer
  // (800 ms after the last one, while the card is shown) and on "Run the test again". Each row gets its state and its words; the polite line says the result,
  // and names the required rows that failed. "Set up the rest" stays enabled: the server tests again and keeps the person here with the reason.
  var test = $('[data-ob-test]');
  if (test) {
    var testForm = test.closest('form') || document;
    var testStep = test.closest('[data-ob-step]');
    var testSay = $('[data-ob-test-say]', testForm);
    var runButton = $('[data-ob-test-run]', testForm);
    var testBusy = false, testAgain = false, testTimer = null;

    var answer = function (name) {
      var field = $('[name="' + name + '"]', testForm);
      if (!field) { return ''; }
      if (field.type === 'radio') { var on = $('input[name="' + name + '"]:checked', testForm); return on ? on.value : ''; }
      return field.value.trim();
    };
    var testShown = function () { return !testStep || testStep.classList.contains('is-active'); };

    var applyTest = function (d) {
      var byKey = {};
      d.rows.forEach(function (r) { byKey[r.key] = r; });
      $$('[data-ob-test-row]', test).forEach(function (li) {
        var row = byKey[li.getAttribute('data-ob-test-row')];
        li.hidden = !row;
        if (!row) { return; }
        li.setAttribute('data-state', row.state);
        $('[data-ob-test-label]', li).textContent = row.label;
        $('[data-ob-test-text]', li).textContent = row.text;
      });
      var summary = $('[data-ob-test-summary]', testForm);
      if (summary && d.summary) { summary.textContent = d.summary; }
      var failed = d.rows.filter(function (r) { return !r.advisory && r.state === 'failed'; });
      var warned = d.rows.filter(function (r) { return r.state === 'warn'; });
      if (!testSay) { return; }
      testSay.textContent = failed.length
        ? 'Not ready yet: ' + failed.map(function (r) { return r.label; }).join('; ') + '. Correct it, then run the test again; "Set up the rest" tests again too.'
        : warned.length
          ? 'Ready. ' + (warned.length === 1 ? 'One row is' : warned.length + ' rows are') + ' worth a look, but nothing keeps you here.'
          : 'Ready: everything that is needed is there.';
    };

    var runTest = function () {
      clearTimeout(testTimer);
      if (testBusy) { testAgain = true; return; }
      testBusy = true;
      testAgain = false;
      if (runButton) { runButton.disabled = true; runButton.setAttribute('aria-busy', 'true'); }
      $$('[data-ob-test-row]', test).forEach(function (li) { if (!li.hidden) { li.setAttribute('data-state', 'working'); } });
      var body = {
        reach: answer('Reach'), name: answer('Name'), domain: answer('BaseDomain'), provider: answer('DnsProvider'),
        ipMode: answer('DnsIp'), fixedIp: answer('DnsFixedIp'), cname: answer('DnsCname'),
      };
      fetch(test.getAttribute('data-ob-test'), {
        method: 'POST', credentials: 'same-origin', body: JSON.stringify(body),
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json', 'RequestVerificationToken': token() },
      })
        .then(function (r) {
          // past the choices (another tab confirmed the way): on to where the first start stands
          if (r.status === 409) { return r.json().then(function (d) { window.location.assign(d.redirect || '/Setup/Progress'); return null; }); }
          if (!r.ok || (r.headers.get('Content-Type') || '').indexOf('json') < 0) { throw new Error('HTTP ' + r.status); }
          return r.json();
        })
        .then(function (d) { if (d) { applyTest(d); } })
        .catch(function () {
          $$('[data-ob-test-row][data-state="working"]', test).forEach(function (li) { li.setAttribute('data-state', 'waiting'); });
          if (testSay) { testSay.textContent = 'The test could not run right now. Run it again in a moment; "Set up the rest" tests again too.'; }
        })
        .then(function () {
          testBusy = false;
          if (runButton) { runButton.disabled = false; runButton.removeAttribute('aria-busy'); }
          if (testAgain) { runTest(); }
        });
    };

    var later = function (e) {
      if (!testShown() || (e.target.closest && e.target.closest('[data-ob-test]'))) { return; }
      clearTimeout(testTimer);
      testTimer = setTimeout(runTest, 800);
    };
    testForm.addEventListener('input', later);
    testForm.addEventListener('change', later);
    if (testStep) { testStep.addEventListener('ob-step-shown', runTest); }
    if (runButton) { runButton.addEventListener('click', runTest); }
    if (testStep && testStep.classList.contains('is-active')) { runTest(); }
  }

  // ------------------------------------------------------------------ the records of an own domain by hand

  // The rows of the records card follow the answers: the domain in every name, A (and AAAA once an IPv6 is known) or CNAME when a host name is given, and the
  // public addresses MatOS Services saw (asked once, when an own domain is chosen; they also fill an empty fixed IP).
  var records = $('[data-ob-records]');
  if (records) {
    var recordsForm = records.closest('form') || document;
    var seen = null, asked = false;
    var domainChosen = function () { var r = $('input[name="Reach"]:checked', recordsForm); return !!r && r.value === 'domain'; };
    var picked = function (name) { var r = $('input[name="' + name + '"]:checked', recordsForm); return r ? r.value : ''; };
    var fillRecords = function () {
      // The card shows for records by hand, and for a provider with a fixed IP (nothing writes the records then either).
      var provider = picked('DnsProvider');
      var manual = provider === records.getAttribute('data-ob-records-manual');
      var fixedIp = !manual && (records.getAttribute('data-ob-records-providers') || '').split(',').indexOf(provider) !== -1 &&
        picked('DnsIp') === records.getAttribute('data-ob-records-fixed');
      records.hidden = !(manual || fixedIp);
      var field = $('[name="BaseDomain"]', recordsForm);
      var domain = (field ? field.value.trim().toLowerCase().replace(/^\.+|\.+$/g, '') : '') || 'example.com';
      var cname = $('[name="DnsCname"]', recordsForm);
      var host = manual && cname ? cname.value.trim().toLowerCase().replace(/\.+$/, '') : '';
      var fixedField = $('[data-ob-fixed-ip]', recordsForm);
      var fixed = fixedIp && fixedField ? fixedField.value.trim() : '';
      $$('[data-ob-record-name]', records).forEach(function (c) { c.textContent = c.getAttribute('data-ob-record-name') + '.' + domain; });
      $$('tr[data-ob-record]', records).forEach(function (tr) {
        var type = tr.getAttribute('data-ob-record');
        tr.hidden = host ? type !== 'cname' : type === 'cname' || (type === 'aaaa' && !(seen && seen.ipv6));
      });
      $$('[data-ob-record-value]', records).forEach(function (c) {
        var type = c.getAttribute('data-ob-record-value');
        if (type === 'cname' && host) { c.textContent = host; }
        else if (type === 'a') {
          if (c.obDefault === undefined) { c.obDefault = c.textContent; }
          c.textContent = fixed || (seen && seen.ipv4) || c.obDefault;
        }
        else if (type === 'aaaa' && seen && seen.ipv6) { c.textContent = seen.ipv6; }
      });
    };
    var askAddresses = function () {
      if (asked || !domainChosen()) { return; }
      asked = true;
      fetch(records.getAttribute('data-ob-records'), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) {
          if (r.status === 409) { return r.json().then(function (d) { window.location.assign(d.redirect || '/Setup/Progress'); return null; }); }
          return r.ok ? r.json() : null;
        })
        .then(function (d) {
          if (!d) { return; }
          seen = d;
          fillRecords();
          var fixed = $('[data-ob-fixed-ip]', recordsForm);
          if (fixed && !fixed.value.trim() && d.ipv4) { fixed.value = d.ipv4; }
        })
        .catch(function () { asked = false; });
    };
    recordsForm.addEventListener('input', fillRecords);
    recordsForm.addEventListener('change', function () { fillRecords(); askAddresses(); });
    fillRecords();
    askAddresses();
  }

  // ------------------------------------------------------------------ the progress screen

  var progress = $('[data-ob-progress]');
  if (progress) {
    var url = progress.getAttribute('data-ob-progress');
    var bar = $('[data-ob-bar]', progress);
    var percent = $('[data-ob-percent]', progress);
    var estimate = $('[data-ob-estimate]', progress);
    var headline = $('[data-ob-headline]', progress);
    var announce = $('[data-ob-announce]', progress);
    var offline = $('[data-ob-offline]', progress);
    var signedOut = $('[data-ob-signedout]', progress);
    var phase = progress.getAttribute('data-ob-phase');
    var timer = null;
    var misses = 0;
    var words = { waiting: 'Waiting', working: 'Working on it', done: 'Done' };

    function task(key) { return $$('[data-ob-task]', progress).filter(function (li) { return li.getAttribute('data-ob-task') === key; })[0]; }

    // A screen reader hears what changed (a line that is done or failed, the end), once, in the polite live region.
    function say(text) { if (announce && text) { announce.textContent = text; } }

    function setPhase(next) {
      if (next === phase) { return; }
      phase = next;
      progress.setAttribute('data-ob-phase', next);
      $$('[data-ob-when]', progress).forEach(function (el) { el.hidden = el.getAttribute('data-ob-when').split(' ').indexOf(next) < 0; });
      if (next !== 'working') {
        var lead = $('[data-ob-when="' + next + '"].ob-lead', progress);
        say((headline ? headline.textContent + '. ' : '') + (lead ? lead.textContent.trim() : ''));
        // the status of the reachability (data-reach, js/matos.js) was drawn before its check ran: ask for it now
        document.dispatchEvent(new CustomEvent('mv:reach-refresh'));
      }
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
        var said = s.state === 'failed' ? (s.error || 'This did not work.')
          : s.state === 'waiting' && s.waitsFor ? 'Waits for ' + s.waitsFor
          : words[s.state] || s.state;
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
