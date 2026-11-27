namespace MatOS.Services.Pages;

/// <summary>
/// The one script of the DynDNS page (<c>/dyndns/app.js</c>, the only script the page's policy allows): the live check of a name, the
/// proof of work before the registration, and the copy buttons. The page works without it except for the proof of work.
/// </summary>
internal static class DynDnsScript
{
    public const string Source = """
        (() => {
          'use strict';
          const form = document.getElementById('register');
          const input = document.getElementById('name');
          const status = document.getElementById('name-status');
          let timer = 0;
          let asked = '';

          function show(text, kind) {
            status.textContent = text;
            status.className = 'status' + (kind ? ' ' + kind : '');
          }

          async function check() {
            const name = input.value.trim().toLowerCase();
            asked = name;
            if (!name) { show('3 to 32 characters: letters, digits and hyphens.', ''); return; }
            try {
              const response = await fetch('/api/v1/names/' + encodeURIComponent(name), { headers: { Accept: 'application/json' } });
              if (asked !== name) return;
              if (response.status === 429) { show('Not so fast. Wait a moment, then type again.', 'no'); return; }
              const answer = await response.json();
              if (answer.available) show(answer.address + ' is free.', 'yes');
              else if (answer.reason === 'taken') show(answer.address + ' is taken. Try another name.', 'no');
              else if (answer.reason === 'reserved') show(answer.name + ' is reserved. Try another name.', 'no');
              else if (answer.reason === 'blocked') show(answer.address + ' is not available. Try another name.', 'no');
              else show('3 to 32 characters: letters, digits and hyphens, not at the start or the end.', 'no');
            } catch {
              show('', '');
            }
          }

          if (form && input && status) {
            input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(check, 300); });
            if (input.value) check();

            form.addEventListener('submit', async event => {
              const bits = Number(form.dataset.bits || 0);
              const nonce = form.elements.namedItem('nonce');
              if (!bits || nonce.value) return;
              event.preventDefault();
              const button = form.querySelector('button[type=submit]');
              button.disabled = true;
              button.textContent = 'One moment…';
              nonce.value = await solve(form.elements.namedItem('token').value, bits);
              form.submit();
            });
          }

          // The proof of work: a number whose SHA-256 together with the token starts with enough zero bits.
          async function solve(token, bits) {
            const encoder = new TextEncoder();
            for (let n = 0; ; n++) {
              const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(token + ':' + n)));
              if (zeros(hash) >= bits) return String(n);
            }
          }

          function zeros(hash) {
            let count = 0;
            for (const b of hash) {
              if (b === 0) { count += 8; continue; }
              return count + Math.clz32(b) - 24;
            }
            return count;
          }

          for (const button of document.querySelectorAll('[data-copy]')) {
            button.hidden = false;
            button.addEventListener('click', async () => {
              const source = document.getElementById(button.dataset.copy);
              try {
                await navigator.clipboard.writeText(source.textContent);
                button.textContent = 'Copied';
              } catch {
                const range = document.createRange();
                range.selectNodeContents(source);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                button.textContent = 'Selected: press Ctrl+C';
              }
              setTimeout(() => { button.textContent = 'Copy'; }, 2500);
            });
          }
        })();
        """;
}
