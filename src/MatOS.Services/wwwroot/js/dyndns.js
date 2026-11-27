/* MatOS Services: the behaviour of the DynDNS pages (everything else is MatUi's matui.js, which also copies: data-copy).
   The live check of a name while it is typed (GET /api/v1/names/{name}), the proof of work before the registration (a number whose SHA-256
   together with the form's token starts with data-bits zero bits; FormGuard checks it), and the copy buttons, which show only where this runs.
   The pages work without it except for the proof of work. */
(function () {
  'use strict';

  var form = document.getElementById('register');
  var input = document.getElementById('name');
  var status = document.getElementById('name-status');
  var HINT = '3 to 32 characters: letters, digits and hyphens.';
  var timer = 0;
  var asked = '';

  function show(text, tone) {
    status.textContent = text;
    status.classList.toggle('mat-text-success', tone === 'yes');
    status.classList.toggle('mat-text-danger', tone === 'no');
  }

  function check() {
    var name = input.value.trim().toLowerCase();
    asked = name;
    if (!name) { show(HINT, ''); return; }
    fetch('/api/v1/names/' + encodeURIComponent(name), { headers: { Accept: 'application/json' } })
      .then(function (response) {
        if (asked !== name) { return null; }
        if (response.status === 429) { show('Not so fast. Wait a moment, then type again.', 'no'); return null; }
        return response.json();
      })
      .then(function (answer) {
        if (!answer || asked !== name) { return; }
        if (answer.available) { show(answer.address + ' is free.', 'yes'); }
        else if (answer.reason === 'taken') { show(answer.address + ' is taken. Try another name.', 'no'); }
        else if (answer.reason === 'reserved') { show(answer.name + ' is reserved. Try another name.', 'no'); }
        else if (answer.reason === 'blocked') { show(answer.address + ' is not available. Try another name.', 'no'); }
        else { show('3 to 32 characters: letters, digits and hyphens, not at the start or the end.', 'no'); }
      })
      .catch(function () { show(HINT, ''); });
  }

  function zeros(hash) {
    var count = 0;
    for (var i = 0; i < hash.length; i++) {
      if (hash[i] === 0) { count += 8; continue; }
      return count + Math.clz32(hash[i]) - 24;
    }
    return count;
  }

  // The proof of work: tries 0, 1, 2, ... until the hash of "token:n" starts with enough zero bits (about a second for 16 bits).
  async function solve(token, bits) {
    var encoder = new TextEncoder();
    for (var n = 0; ; n++) {
      var digest = await crypto.subtle.digest('SHA-256', encoder.encode(token + ':' + n));
      if (zeros(new Uint8Array(digest)) >= bits) { return String(n); }
    }
  }

  if (form && input && status) {
    input.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(check, 300); });
    if (input.value) { check(); }

    form.addEventListener('submit', function (event) {
      // MatUi's check of the fields runs first (capture) and stops a form with a mistake.
      var bits = Number(form.getAttribute('data-bits') || 0);
      var nonce = form.elements.namedItem('nonce');
      if (event.defaultPrevented || !bits || nonce.value) { return; }
      event.preventDefault();
      var button = form.querySelector('button[type=submit]');
      var label = button.querySelector('span') || button;
      button.disabled = true;
      label.textContent = 'One moment…';
      solve(form.elements.namedItem('token').value, bits).then(function (value) {
        nonce.value = value;
        form.submit();
      });
    });
  }

  document.querySelectorAll('[data-copy][hidden]').forEach(function (button) { button.hidden = false; });
})();
