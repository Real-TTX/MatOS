// Browser checks of MatOS Services (src/MatOS.Services): every page of the admin area and the public pages, light, dark and on a phone, in a real
// browser (the installed Edge, or Chrome).
//   cd tests/e2e && npm install && node services.js            starts a local `dotnet run` of the service itself (Development, a data folder of its
//                                                              own in the temp folder, free ports; the edge and the DNS on ports of the system's choice)
//                                                              and stops it at the end
//   node services.js http://127.0.0.1:5080                     against a service that runs already (in Development: the admin area answers on the
//                                                              machine itself, and the test account admin@example.com / Start12345 exists)
// It signs in with the test account of every Mat app (MATOS_USER / MATOS_PASSWORD for another one), registers two names (one through the API for the
// page of an installation, one on the DynDNS page in the browser, with its proof of work) and gives both back. Exit code 1 when a check fails.
const { chromium } = require('playwright-core');
const { spawn, execSync } = require('child_process');
const fs = require('fs');
const net = require('net');
const os = require('os');
const path = require('path');

const GIVEN = process.argv.slice(2).find(a => !a.startsWith('--'));
const USER = process.env.MATOS_USER || 'admin@example.com';
const PASSWORD = process.env.MATOS_PASSWORD || 'Start12345';
const ROOT = path.resolve(__dirname, '..', '..');
const results = [];
const errors = [];

async function check(name, fn) {
  try {
    await fn();
    results.push({ name, ok: true });
    console.log('  ok    ' + name);
  } catch (e) {
    results.push({ name, ok: false, why: e.message });
    console.log('  FAIL  ' + name + '\n        ' + e.message.split('\n')[0]);
  }
}

function expectTrue(value, what) {
  if (!value) { throw new Error(what); }
}

function expectEqual(actual, expected, what) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => { const { port } = server.address(); server.close(() => resolve(port)); });
  });
}

// `dotnet run` of the service in Development, in a data folder of its own; resolves with its address once /healthz answers.
async function startService() {
  const port = await freePort();
  const data = fs.mkdtempSync(path.join(os.tmpdir(), 'matos-services-e2e-'));
  const base = `http://127.0.0.1:${port}`;
  const child = spawn('dotnet', [
    'run', '--project', path.join(ROOT, 'src', 'MatOS.Services'), '--no-launch-profile', '--',
    `--urls=${base}`, `--Services:DataPath=${data}`, '--Services:ListenAddress=127.0.0.1',
    '--Services:Edge:HttpsPort=0', '--Services:Edge:HttpPort=0', '--Services:Dns:Port=0', '--Logging:LogLevel:Default=Warning',
  ], { cwd: ROOT, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '';
  child.stdout.on('data', d => { log += d; });
  child.stderr.on('data', d => { log += d; });
  const stop = async () => {
    try {
      if (process.platform === 'win32') { execSync(`taskkill /PID ${child.pid} /T /F`, { stdio: 'ignore' }); } else { child.kill('SIGTERM'); }
    } catch { /* gone already */ }
    // the database may still be open for a moment
    for (let i = 0; i < 10 && fs.existsSync(data); i++) {
      await new Promise(r => setTimeout(r, 300));
      try { fs.rmSync(data, { recursive: true, force: true }); } catch { /* try again */ }
    }
  };
  const until = Date.now() + 180000;
  while (Date.now() < until) {
    if (child.exitCode !== null) { throw new Error('the service stopped:\n' + log); }
    try {
      const r = await fetch(base + '/healthz');
      if (r.ok) { return { base, stop }; }
    } catch { /* not yet */ }
    await new Promise(r => setTimeout(r, 500));
  }
  await stop();
  throw new Error('the service did not answer in time:\n' + log);
}

const adminPages = name => [
  '/admin', '/admin?period=7d', '/admin/Installations', `/admin/Installations?q=${name}`, `/admin/Installations/Details?name=${name}`, '/admin/Tunnels',
  '/admin/Domains', '/admin/Statistics', '/admin/Statistics?period=12m', '/admin/Users', '/admin/Users/Edit', '/admin/Settings', '/admin/Account',
  '/admin/Account?tab=password',
];
const publicPages = ['/', '/dyndns', '/dyndns/rules', '/dyndns/manage'];

(async () => {
  const service = GIVEN ? { base: GIVEN.replace(/\/$/, ''), stop: async () => {} } : await startService();
  const BASE = service.base;
  console.log('MatOS Services at ' + BASE);
  const browser = await chromium.launch({ channel: 'msedge', headless: true }).catch(() => chromium.launch({ channel: 'chrome', headless: true }));

  // A name of its own for the page of an installation (given back at the end).
  const installation = 'e2e' + Date.now().toString(36).slice(-6);
  const registered = await fetch(BASE + '/api/v1/instances', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: installation }) });
  const installationKey = registered.ok ? (await registered.json()).key : null;

  async function context(options, signIn) {
    const ctx = await browser.newContext(options);
    const page = await ctx.newPage();
    page.on('pageerror', e => errors.push(page.url() + ': ' + e.message));
    page.on('console', m => { if (m.type() === 'error') { errors.push(page.url() + ': ' + m.text()); } });
    if (signIn) {
      await page.goto(BASE + '/admin/SignIn');
      await page.fill('#Input_Email', USER);
      await page.fill('#Input_Password', PASSWORD);
      await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/admin/SignIn')), page.click('button[type=submit]')]);
    }
    return { ctx, page };
  }

  async function looksRight(page, url, mode) {
    const response = await page.goto(BASE + url, { waitUntil: 'networkidle' });
    expectEqual(response.status(), 200, url + ' status');
    await page.evaluate(t => document.documentElement.setAttribute('data-theme', t), mode === 'dark' ? 'dark' : 'light');
    expectEqual(await page.locator('h1').count(), 1, url + ' h1');
    const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expectTrue(wide <= 1, `${url} scrolls sideways by ${wide}px`);
    expectEqual(await page.locator('[style]').count(), 0, url + ' elements with an inline style');
    expectEqual(await page.locator('script:not([src])').count(), 0, url + ' inline scripts');
    const csp = response.headers()['content-security-policy'] || '';
    expectTrue(csp.includes("default-src 'self'") && !csp.includes('unsafe-inline'), url + ' has the strict policy: ' + csp);
    // The colours are the toolkit's in both themes: the text is not the colour of the page.
    const [fg, bg] = await page.evaluate(() => { const s = getComputedStyle(document.body); return [s.color, s.backgroundColor]; });
    expectTrue(fg !== bg, `${url} text and background differ (${fg} on ${bg})`);
  }

  const modes = [
    ['light', { viewport: { width: 1360, height: 900 }, colorScheme: 'light' }],
    ['dark', { viewport: { width: 1360, height: 900 }, colorScheme: 'dark' }],
    ['phone', { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, colorScheme: 'light' }],
  ];

  console.log('Every page, light, dark and on a phone');
  for (const [mode, options] of modes) {
    const { ctx, page } = await context(options, true);
    const pages = adminPages(installation);
    await check(`${mode}: ${pages.length} pages of the admin area load, have one h1, no inline style and do not scroll sideways`, async () => {
      for (const url of pages) { await looksRight(page, url, mode); }
    });
    await ctx.close();

    const visitor = await context(options, false);
    await check(`${mode}: ${publicPages.length} public pages load, have one h1, no inline style and do not scroll sideways`, async () => {
      for (const url of publicPages) { await looksRight(visitor.page, url, mode); }
      expectEqual((await visitor.ctx.cookies()).length, 0, 'a public page sets no cookie');
    });
    await visitor.ctx.close();
  }

  console.log('The admin area');
  {
    const { ctx, page } = await context({ viewport: { width: 1360, height: 900 } }, true);
    await check('a stranger is sent to the sign-in', async () => {
      const stranger = await browser.newContext();
      const p = await stranger.newPage();
      await p.goto(BASE + '/admin/Installations');
      expectTrue(new URL(p.url()).pathname === '/admin/SignIn', 'went to ' + p.url());
      await stranger.close();
    });
    await check('the theme button cycles through three themes and saves each choice', async () => {
      await page.goto(BASE + '/admin');
      for (let i = 0; i < 3; i++) {
        const [response] = await Promise.all([page.waitForResponse(r => r.url().endsWith('/admin/api/theme')), page.click('[data-theme-toggle]')]);
        expectTrue(response.status() < 300, 'the choice is saved: ' + response.status());
      }
    });
    await check('the page of an installation shows its facts', async () => {
      await page.goto(BASE + `/admin/Installations/Details?name=${installation}`);
      expectTrue((await page.locator('h1').innerText()).includes(installation), 'its name is the title');
    });
    await check('the menu opens on a phone', async () => {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.goto(BASE + '/admin');
      await page.click('[data-nav-toggle]');
      await page.waitForTimeout(300);
      expectTrue(await page.locator('#mat-nav .mat-nav__item[href="/admin/Statistics"]').isVisible(), 'the menu shows its items');
      await page.setViewportSize({ width: 1360, height: 900 });
    });
    await ctx.close();
  }

  console.log('DynDNS for everybody');
  {
    const { ctx, page } = await context({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }, false);
    const name = 'e2e' + Math.random().toString(36).slice(2, 9);
    let key = '';
    await check('the live check says whether a name is free', async () => {
      await page.goto(BASE + '/dyndns');
      await page.fill('#name', name);
      await page.waitForFunction(() => /is free\./.test(document.getElementById('name-status').textContent), null, { timeout: 5000 });
      expectTrue(await page.locator('#name-status.mat-text-success').count() === 1, 'in the colour of success');
    });
    await check('the toolkit checks the field before the proof of work', async () => {
      await page.fill('#name', '');
      await page.click('#register button[type=submit]');
      expectTrue(await page.locator('#register .mat-field.is-invalid').count() === 1, 'the field says what is missing');
      expectEqual(await page.inputValue('input[name=nonce]'), '', 'no proof of work was made');
    });
    await check('Get my name does the proof of work, shows the key once and the settings, with copy buttons', async () => {
      await page.fill('#name', name);
      await Promise.all([page.waitForURL(u => u.pathname === '/dyndns/register', { timeout: 60000 }), page.click('#register button[type=submit]')]);
      expectEqual(await page.locator('h1').innerText(), `${name}.my.mattiverse.de is yours`, 'the title');
      key = (await page.locator('#key').innerText()).trim();
      expectTrue(/^mos_/.test(key), 'the key: ' + key);
      expectEqual(await page.locator('[data-copy]:visible').count(), 5, 'the copy buttons show where the script runs');
      const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expectTrue(wide <= 1, `the key and the settings wrap on a phone (${wide}px sideways)`);
    });
    await check('Manage: sign in with the key, then give the name back', async () => {
      await page.goto(BASE + '/dyndns/manage');
      await page.fill('#name', name);
      await page.fill('#key', key);
      await Promise.all([page.waitForURL(u => u.pathname === '/dyndns/manage'), page.click('button[type=submit]')]);
      expectEqual(await page.locator('h1').innerText(), `${name}.my.mattiverse.de`, 'signed in');
      await page.click('.mat-accordion summary');
      expectTrue(await page.locator('#s-ddclient').isVisible(), 'the settings open');
      await page.check('input[name=confirm]');
      await Promise.all([page.waitForURL(u => u.pathname === '/dyndns/manage/release'), page.click('form[action="/dyndns/manage/release"] button[type=submit]')]);
      expectTrue((await page.locator('h1').innerText()).endsWith('is free again'), 'given back');
    });
    await check('the theme button of a public page keeps the choice in this browser', async () => {
      await page.goto(BASE + '/dyndns');
      await page.click('[data-theme-toggle]');
      const theme = await page.evaluate(() => [document.documentElement.getAttribute('data-theme'), localStorage.getItem('mat-theme')]);
      expectTrue(theme[0] === 'light' && theme[1] === 'light', 'light, kept: ' + theme.join(', '));
    });
    await ctx.close();
  }

  await check('no script errors and no refusals of the content security policy', async () => {
    expectEqual(errors, [], 'errors');
  });

  if (installationKey) {
    await fetch(BASE + '/api/v1/instance', { method: 'DELETE', headers: { Authorization: 'Bearer ' + installationKey } });
  }
  await browser.close();
  await service.stop();

  const failed = results.filter(r => !r.ok);
  console.log(`\n${results.length - failed.length} of ${results.length} checks passed`);
  process.exit(failed.length ? 1 : 0);
})().catch(e => {
  console.error(e);
  process.exit(1);
});
