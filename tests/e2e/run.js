// End-to-end checks of the pages of MatOS in a real browser (the installed Edge, or Chrome) against a running stack.
//   ./scripts/redeploy.ps1 && cd tests/e2e && npm install && node run.js [http://localhost:8030]
// The first start (Welcome, the account, the engines, the way to the apps) needs a MatOS without any account: a throwaway stack (./scripts/redeploy.ps1 -Fresh,
// in Production, or a second MatOS with its own volume), then  node run.js --first-start [url]. It creates the account anna@example.com and stops on the
// screen of the engines (its Next starts real installs): MATOS_FIRST_START_INSTALL=1 goes on, and the engines are installed.
// It signs in with the account of the development stack (admin@example.com / Start12345, set MATOS_USER and MATOS_PASSWORD for another one) and
// changes nothing that it does not remove again: the one schedule it creates is deleted at the end. Exit code 1 when a check fails.
// After a first start the person signs in through MatIdentity (MatOS forgot the local password, docs/ARCHITECTURE.md "The first start"): MATOS_SSO=1 signs in there.
// A test install reached at its free address through a local edge (CLAUDE.md, "Testing the engines"): MATOS_BROWSER_ARGS passes arguments to the browser
// (--host-resolver-rules="MAP *.<name>.my.mattiverse.de:443 127.0.0.1:18443"), MATOS_INSECURE=1 accepts a certificate of a test CA.
const { chromium } = require('playwright-core');

const FIRST_START = process.argv.includes('--first-start');
const BASE = (process.argv.slice(2).find(a => !a.startsWith('--')) || process.env.BASE || 'http://localhost:8030').replace(/\/$/, '');
const USER = process.env.MATOS_USER || 'admin@example.com';
const PASSWORD = process.env.MATOS_PASSWORD || 'Start12345';
const SSO = process.env.MATOS_SSO === '1';
// each argument starts with -- (a value may hold spaces, as the rules of the resolver do)
const BROWSER_ARGS = (process.env.MATOS_BROWSER_ARGS || '').trim().split(/\s+(?=--)/).filter(Boolean);
const results = [];

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

const pages = [
  '/', '/Apps', '/Store', '/Store/Install?id=matfile', '/Backups', '/Backups?tab=schedules', '/Backups/Create', '/Backups/ScheduleEdit', '/Backups/Targets', '/Backups/TargetEdit', '/Jobs', '/Tasks',
  '/Users', '/Users/Edit', '/Settings', '/Settings/Address', '/Settings/Engines', '/Settings/Updates', '/Settings/System', '/Wizard', '/Account/Profile', '/Account/Profile?tab=home', '/Account/Profile?tab=password', '/Account/Profile?tab=security',
  '/Start',
];

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true, args: BROWSER_ARGS }).catch(() => chromium.launch({ channel: 'chrome', headless: true, args: BROWSER_ARGS }));
  if (process.env.MATOS_INSECURE === '1') {
    const plain = browser.newContext.bind(browser);
    browser.newContext = options => plain({ ...options, ignoreHTTPSErrors: true });
  }
  const errors = [];

  if (FIRST_START) {
    await firstStart(browser, errors);
    return;
  }

  async function signedIn(options) {
    const ctx = await browser.newContext(options);
    const page = await ctx.newPage();
    page.on('pageerror', e => errors.push(page.url() + ': ' + e.message));
    page.on('console', m => { if (m.type() === 'error' && !/status of 40[34]/.test(m.text())) { errors.push(page.url() + ': ' + m.text()); } });
    // MatOS's own form (?local=1): with MatIdentity connected the page would go there, and its address may not be reachable from this browser
    if (SSO) {
      // MatOS's sign-in goes on to MatIdentity, which brings the person back
      await page.goto(BASE + '/Account/Login');
      await page.locator('input[type=email], input[name*="user" i], input[name*="mail" i]').first().fill(USER);
      await page.locator('input[type=password]').first().fill(PASSWORD);
      await Promise.all([page.waitForURL(u => u.origin === new URL(BASE).origin && !u.pathname.startsWith('/Account/')), page.locator('button[type=submit]').first().click()]);
      return { ctx, page };
    }
    await page.goto(BASE + '/Account/Login?local=1');
    await page.fill('#Input_Username', USER);
    await page.fill('#Input_Password', PASSWORD);
    await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/Account/Login')), page.click('button[type=submit]')]);
    return { ctx, page };
  }

  const modes = [
    ['light', { viewport: { width: 1360, height: 900 }, colorScheme: 'light' }],
    ['dark', { viewport: { width: 1360, height: 900 }, colorScheme: 'dark' }],
    ['phone', { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, colorScheme: 'light' }],
  ];

  console.log('Every page, light, dark and on a phone');
  for (const [mode, options] of modes) {
    const { ctx, page } = await signedIn(options);
    await check(`${mode}: ${pages.length} pages load, have one h1 and do not scroll sideways`, async () => {
      for (const url of pages) {
        const response = await page.goto(BASE + url, { waitUntil: 'networkidle' });
        expectEqual(response.status(), 200, url + ' status');
        await page.evaluate(t => document.documentElement.setAttribute('data-theme', t), mode === 'dark' ? 'dark' : 'light');
        expectEqual(await page.locator('h1').count(), 1, url + ' h1');
        const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        expectTrue(wide <= 1, `${url} scrolls sideways by ${wide}px`);
        expectEqual(await page.locator('[style]').count(), 0, url + ' elements with an inline style');
      }
    });
    await ctx.close();
  }

  const { ctx, page } = await signedIn({ viewport: { width: 1360, height: 900 } });

  console.log('The shell');
  await check('the theme button cycles through three themes and saves each choice', async () => {
    await page.goto(BASE + '/');
    const seen = [await page.evaluate(() => document.documentElement.getAttribute('data-theme'))];
    for (let i = 0; i < 3; i++) {
      const [response] = await Promise.all([page.waitForResponse(r => r.url().endsWith('/api/theme')), page.click('[data-theme-toggle]')]);
      expectEqual(response.status(), 204, 'the choice is saved');
      await page.waitForTimeout(150);
      seen.push(await page.evaluate(() => document.documentElement.getAttribute('data-theme')));
    }
    expectTrue(new Set(seen.slice(0, 3)).size === 3 && seen[3] === seen[0], 'system, light and dark, then the first again: ' + seen.join(' > '));
  });
  console.log('The Task Manager');
  await check('the Task Manager: the host in five cards, the list in parts, an app opens to its containers', async () => {
    await page.goto(BASE + '/Tasks');
    expectEqual(await page.locator('.mv-tm-card').count(), 5, 'processor, memory, network, disk activity, storage');
    expectTrue(await page.locator('tbody.mv-tm-section').count() >= 1, 'at least one part (Apps, System, Something else)');
    const group = page.locator('tbody.mv-tm-group:has([data-group-toggle])').first();
    const toggle = group.locator('[data-group-toggle]');
    if ((await toggle.getAttribute('aria-expanded')) === 'true') { await toggle.click(); }
    expectTrue(!(await group.locator('tr.mv-tm-row').first().isVisible()), 'an app starts closed');
    await toggle.click();
    expectEqual(await toggle.getAttribute('aria-expanded'), 'true', 'the toggle says it is open');
    expectTrue(await group.locator('tr.mv-tm-row').first().isVisible(), 'its containers show');
    await toggle.click();
  });
  await check('the Task Manager is live: its numbers come as JSON and the charts fill', async () => {
    await page.goto(BASE + '/Tasks');
    const response = await page.waitForResponse(r => r.url().includes('handler=Live'), { timeout: 10000 });
    expectEqual(response.status(), 200, 'the numbers answer');
    const data = await response.json();
    expectEqual(data.cards.length, 5, 'five cards in the answer');
    expectTrue(Array.isArray(data.groups) && data.groups.length > 0, 'the groups in the answer');
    expectEqual(data.signature, await page.locator('#tm-list').getAttribute('data-signature'), 'the rows are those the page drew');
    if (data.measured) {
      await page.waitForFunction(() => (document.querySelector('[data-card="cpu"] [data-f="line"]').getAttribute('d') || '').startsWith('M'), null, { timeout: 15000 });
      expectTrue(await page.locator('[data-card="cpu"] svg.mv-tm-chart').isVisible(), 'the chart of the processor shows');
    }
  });
  await check('the Task Manager sorts by a column, finds a container, and acts only on what MatOS runs', async () => {
    await page.goto(BASE + '/Tasks');
    await page.click('th a.mat-table__sort[href*="sort=memory"]');
    await page.waitForURL(u => u.search.includes('sort=memory'));
    expectEqual(await page.locator('th[aria-sort="descending"]').innerText(), 'Memory', 'sorted by memory, most first');
    expectEqual(await page.locator('tbody[data-key="matos"] form').count(), 0, 'MatOS does not stop itself');
    expectEqual(await page.locator('tbody.mv-tm-group[data-section="other"] form').count(), 0, 'nothing else on the host is changed');
    expectEqual(await page.locator('tbody[data-key="app:matdock"] form').count(), 0, 'MatDock is not restarted from here');
    const name = await page.locator('tr.mv-tm-row a[href^="/Tasks/Log"]').first().innerText();
    await page.goto(BASE + '/Tasks?q=' + encodeURIComponent(name));
    expectTrue(await page.locator(`tr.mv-tm-row a:text-is("${name}"), .mv-tm-lone a:text-is("${name}")`).first().isVisible(), 'the search shows the container, its app open');
    await page.goto(BASE + '/Tasks?q=no-such-thing-anywhere');
    expectEqual(await page.locator('#tm-list .mat-empty').count(), 1, 'nothing matches says so');
  });
  await check('the log of a container opens in a window of the home screen', async () => {
    // a context of its own: the windows it opens stay out of the checks of the home screen below
    const own = await signedIn({ viewport: { width: 1360, height: 900 } });
    const page = own.page;
    await page.goto(BASE + '/');
    await page.click('.os-grid .os-app[data-window="/Tasks"]');
    await page.waitForFunction(() => { const el = document.querySelector('.os-window.is-active [data-win-title]'); return !!el && el.textContent === 'Task Manager'; });
    const frame = page.frameLocator('.os-window.is-active .os-window__frame');
    const group = frame.locator('tbody.mv-tm-group:has([data-group-toggle])').first();
    if ((await group.locator('[data-group-toggle]').getAttribute('aria-expanded')) !== 'true') { await group.locator('[data-group-toggle]').click(); }
    const link = group.locator('tr.mv-tm-row a[href^="/Tasks/Log"]').first();
    const name = await link.innerText();
    await link.click();
    await page.waitForFunction(n => { const el = document.querySelector('.os-window.is-active [data-win-title]'); return !!el && el.textContent === n; }, name);
    await page.frameLocator('.os-window.is-active .os-window__frame').locator('pre.mv-log').waitFor({ timeout: 15000 });
    expectEqual(await page.locator('.os-window').count(), 2, 'the log in a window of its own, beside the Task Manager');
    await own.ctx.close();
  });

  console.log('The home screen (MatOS)');
  const win = () => page.frameLocator('.os-window.is-active .os-window__frame');
  const winTitle = () => page.locator('.os-window.is-active [data-win-title]').innerText();
  const titled = title => page.waitForFunction(t => { const el = document.querySelector('.os-window.is-active [data-win-title]'); return !!el && el.textContent === t; }, title);
  const windows = () => page.locator('.os-window').count();
  const noWindows = () => page.waitForFunction(() => document.querySelectorAll('.os-window').length === 0);
  // a window that grows out of its icon, or glides to a place, has its size only when that is over
  const settled = () => page.waitForFunction(() => !document.querySelector('.os-window.is-opening, .os-window.is-closing, .os-window.is-appearing, .os-window.is-leaving, .os-window.is-settling'));
  const box = async selector => {
    await settled();
    const b = await page.locator(selector).boundingBox(); return [b.x, b.y, b.width, b.height].map(Math.round);
  };
  await check('the home screen is its own system: a status bar, the dock, the engines in a widget and in the folder System', async () => {
    await page.goto(BASE + '/');
    expectEqual(await page.locator('.mat-nav').count(), 0, 'no menu of the toolkit');
    expectEqual(await page.locator('.os-status').count(), 1, 'the status bar');
    expectEqual(await page.locator('.os-desktop[data-wallpaper]').count(), 1, 'the wallpaper');
    for (const href of ['/Store', '/Apps', '/Backups', '/Settings']) {
      expectEqual(await page.locator(`.os-dock a[data-window="${href}"]`).count(), 1, href + ' in the dock');
    }
    for (const href of ['/Jobs', '/Tasks', '/People']) {
      expectEqual(await page.locator(`.os-grid .os-app[data-window="${href}"]`).count(), 1, href + ' on the screen');
    }
    expectEqual(await page.locator('.os-engines .os-engine').count(), 5, 'the five engines in their widget');
    await page.click('[data-os-folder="os-folder-system"]');
    expectTrue(await page.locator('#os-folder-system').isVisible(), 'the folder opens');
    expectEqual(await page.locator('#os-folder-system .os-app[href^="/Engines?id="]').count(), 5, 'the engines are its apps');
    await page.keyboard.press('Escape');
    expectTrue(!(await page.locator('#os-folder-system').isVisible()), 'Escape closes it');
  });
  await check('a part of MatOS opens in a window: a title, the page without its menu, Back', async () => {
    await page.goto(BASE + '/');
    await page.click('.os-dock a[data-window="/Store"]');
    await titled('Store');
    expectTrue(await page.locator('.os-window.is-active').isVisible(), 'the window is open');
    expectEqual(new URL(page.url()).hash, '#/Store', 'the address names its page');
    expectEqual(await page.locator('.os-status__app').innerText(), 'Store', 'the status bar names the window in front');
    expectEqual(await win().locator('html.mv-in-window').count(), 1, 'the page knows it is in the window');
    expectTrue(!(await win().locator('.mat-nav').isVisible()), 'without the menu of the toolkit');
    expectTrue(await page.locator('.os-window.is-active [data-win-back]').isDisabled(), 'nothing to go back to yet');
    await win().locator('a[href^="/Store/Install"]').first().click();
    await page.waitForFunction(() => !document.querySelector('.os-window.is-active [data-win-back]').disabled);
    await page.click('.os-window.is-active [data-win-back]');
    await titled('Store');
  });
  await check('yellow puts the window away and keeps the app, red closes it', async () => {
    await page.click('.os-window.is-active [data-win-min]');
    await page.waitForFunction(() => document.querySelector('.os-window').hidden);
    expectEqual(new URL(page.url()).hash, '', 'the address is the home screen again');
    expectEqual(await page.locator('.os-dock a[data-window="/Store"].is-running').count(), 1, 'the store still runs: a dot under it');
    await page.click('.os-dock a[data-window="/Store"]');
    await page.waitForFunction(() => !document.querySelector('.os-window').hidden);
    expectEqual(await winTitle(), 'Store', 'it comes back as it was');
    await page.click('.os-dock a[data-window="/Store"]');
    await page.waitForFunction(() => document.querySelector('.os-window').hidden);
    await page.click('.os-dock a[data-window="/Store"]');
    await page.waitForFunction(() => !document.querySelector('.os-window').hidden);
    await page.click('.os-window.is-active .os-lights [data-win-close]');
    await noWindows();
    expectEqual(await page.locator('.is-running').count(), 0, 'closed: no dot');
  });
  await check('the places of the windows that Mattiverse kept (mattiverse.os.*) move to matos.os.* once', async () => {
    await page.goto(BASE + '/');
    await page.evaluate(() => { localStorage.setItem('mattiverse.os.geo.e2e-legacy', '{"x":1}'); localStorage.removeItem('matos.os.geo.e2e-legacy'); });
    await page.goto(BASE + '/');
    const keys = await page.evaluate(() => [localStorage.getItem('matos.os.geo.e2e-legacy'), localStorage.getItem('mattiverse.os.geo.e2e-legacy')]);
    expectEqual(keys[0], '{"x":1}', 'moved to matos.os.geo.*');
    expectEqual(keys[1], null, 'the old key is gone');
    await page.evaluate(() => localStorage.removeItem('matos.os.geo.e2e-legacy'));
  });
  await check('several windows: the bar moves one, an edge sizes it, a click brings one to the front, the edge of the screen takes a half', async () => {
    await page.goto(BASE + '/');
    await page.evaluate(() => { Object.keys(localStorage).filter(k => k.startsWith('matos.os.geo.')).forEach(k => localStorage.removeItem(k)); });
    await page.click('.os-dock a[data-window="/Store"]');
    await titled('Store');
    await page.click('.os-dock a[data-window="/Settings"]');
    await titled('Settings');
    expectEqual(await windows(), 2, 'two windows side by side');
    const store = await box('.os-window:not(.is-active)');
    const before = await box('.os-window.is-active');
    expectTrue(before[0] > store[0] && before[1] > store[1], 'the second one opens a step beside the first');

    // the bar moves it
    await page.mouse.move(before[0] + 300, before[1] + 20);
    await page.mouse.down();
    await page.mouse.move(before[0] + 400, before[1] + 80, { steps: 6 });
    await page.mouse.up();
    const moved = await box('.os-window.is-active');
    expectTrue(moved[0] > before[0] + 60 && moved[1] > before[1] + 30, 'moved: ' + before + ' > ' + moved);
    expectEqual(moved.slice(2), before.slice(2), 'its size stays');

    // its left edge sizes it
    await page.mouse.move(moved[0] - 1, moved[1] + 300);
    await page.mouse.down();
    await page.mouse.move(moved[0] + 200, moved[1] + 300, { steps: 6 });
    await page.mouse.up();
    const sized = await box('.os-window.is-active');
    expectTrue(Math.abs(sized[2] - (moved[2] - 201)) <= 3 && sized[0] > moved[0] + 190, 'narrower from the left: ' + moved + ' > ' + sized);

    // a click into the page of the other one brings it to the front
    await page.mouse.click(store[0] + 12, store[1] + 300);
    await titled('Store');
    expectEqual(new URL(page.url()).hash, '#/Store', 'the address follows the window in front');
    expectEqual(await page.locator('.os-dock a[data-window="/Store"].is-front').count(), 1, 'the dock marks it');

    // a double click on the bar fills the screen, another one gives its size back
    await page.dblclick('.os-window.is-active .os-window__bar', { position: { x: 300, y: 20 } });
    const full = await box('.os-window.is-active');
    expectEqual([full[0], full[2]], [0, 1360], 'as wide as the screen');
    expectTrue(full[1] + full[3] < (await box('.os-dock'))[1], 'above the dock');
    await page.dblclick('.os-window.is-active .os-window__bar', { position: { x: 300, y: 20 } });
    expectEqual(await box('.os-window.is-active'), store, 'its own place again');

    // let go at the left edge of the screen: the left half
    await page.mouse.move(store[0] + 300, store[1] + 20);
    await page.mouse.down();
    await page.mouse.move(200, 300, { steps: 5 });
    await page.mouse.move(2, 300, { steps: 3 });
    expectTrue(await page.locator('.os-snap').isVisible(), 'the place it will take is shown');
    await page.mouse.up();
    const half = await box('.os-window.is-active');
    expectTrue(half[0] < 12 && Math.abs(half[2] - 668) < 6, 'the left half: ' + half);
  });
  await check('the dock is the taskbar, a reload keeps the windows, the logo shows the desktop', async () => {
    // Activity has no icon in the dock: its window gets one
    await page.evaluate(() => document.querySelector('.os-grid .os-app[data-window="/Jobs"]').click());
    await titled('Activity');
    expectEqual(await page.locator('.os-dock__win').count(), 1, 'an icon in the dock for the window of Activity');
    await page.click('.os-dock__win');
    await page.waitForFunction(() => document.querySelector('.os-window.is-active [data-win-title]').textContent !== 'Activity');
    await page.click('.os-dock__win');
    await titled('Activity');

    await page.reload();
    await titled('Activity');
    expectEqual(await windows(), 3, 'the three windows are back');
    const half = await box('.os-window:has([data-win-title]:text-is("Store"))');
    expectTrue(half[0] < 12 && Math.abs(half[2] - 668) < 6, 'the store is still the left half: ' + half);

    // the home screen refreshes itself and the windows stay
    await page.evaluate(() => new Promise(done => window.MatOSOs.refresh(done)));
    expectEqual(await windows(), 3, 'a refresh of the home screen keeps the windows');
    expectTrue((await page.locator('.os-page [data-home-key]').count()) > 5, 'and its things are there');

    await page.click('.os-status__brand');
    await page.waitForFunction(() => [...document.querySelectorAll('.os-window')].every(w => w.hidden));
    expectEqual(new URL(page.url()).hash, '', 'the desktop: the address is the home screen');
    expectEqual(await page.locator('.is-running').count(), 4, 'the three apps still run (Activity on the screen and in the dock)');

    await page.locator('.os-dock__win').click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Close all 3 windows' }).click();
    await noWindows();
    expectEqual(await page.locator('.os-dock__win').count(), 0, 'its icon left the dock');
    await page.reload();
    expectEqual(await windows(), 0, 'closed windows do not come back');
  });
  await check('an address with a page opens its window, and the settings have their tabs inside it', async () => {
    await page.goto(BASE + '/#/Settings/Engines');
    await titled('Engines');
    expectTrue(await win().locator('.mv-window-only .mat-tabs').isVisible(), 'the tabs of the settings');
    await page.evaluate(() => { location.hash = '#/Jobs'; });
    await titled('Activity');
    expectEqual(new URL(page.url()).hash, '#/Jobs', 'another address: that page in a window of its own');
    expectEqual(await windows(), 2, 'beside the first one');
    await page.click('.os-window.is-active .os-lights [data-win-close]');
    await titled('Engines');
    await page.click('.os-window.is-active .os-lights [data-win-close]');
    await noWindows();
  });
  await check('a link with data-window inside a window (the list "Getting started") opens a second window', async () => {
    await page.goto(BASE + '/#/Start');
    await titled('Getting started');
    const link = win().locator('a[data-window]').first();
    await link.waitFor();
    const title = await link.getAttribute('data-title');
    await link.click();
    await titled(title);
    expectEqual(await windows(), 2, 'beside the list');
    await page.click('.os-window.is-active .os-lights [data-win-close]');
    await titled('Getting started');
    await page.click('.os-window.is-active .os-lights [data-win-close]');
    await noWindows();
  });
  await check('an engine opens its own window in MatOS', async () => {
    await page.goto(BASE + '/');
    await page.click('[data-os-folder="os-folder-system"]');
    const first = page.locator('#os-folder-system .os-app[href^="/Engines?id="]').first();
    const name = await first.getAttribute('data-title');
    await first.click();
    await titled(name);
    expectEqual(await win().locator('h1').innerText(), name, 'the page of the engine');
    expectTrue(!(await page.locator('#os-folder-system').isVisible()), 'the folder closed');
    await page.click('.os-window.is-active .os-lights [data-win-close]');
    await noWindows();
  });
  await check('the search: / focuses it, it narrows the apps and the widgets step aside', async () => {
    await page.goto(BASE + '/');
    const all = await page.locator('.os-grid .os-app:visible').count();
    await page.keyboard.press('/');
    expectEqual(await page.evaluate(() => document.activeElement && document.activeElement.id), 'home-find', 'the search has the focus');
    await page.keyboard.type('people');
    expectEqual(await page.locator('.os-grid .os-app:visible').count(), 1, 'only People is left');
    expectEqual(await page.locator('.os-grid [data-widget]:visible').count(), 0, 'the widgets step aside');
    await page.fill('#home-find', '');
    expectEqual(await page.locator('.os-grid .os-app:visible').count(), all, 'all apps again');
  });
  await check('the person in the status bar: account, wallpaper, sign-out and the version; an app has a menu', async () => {
    await page.goto(BASE + '/');
    await page.click('[data-os-menu="os-user-menu"]');
    expectTrue(await page.locator('#os-user-menu').isVisible(), 'the menu opens');
    expectEqual(await page.locator('#os-user-menu [data-window="/Account/Profile?tab=home"]').count(), 1, 'the wallpaper');
    expectTrue(/MatOS \S+/.test(await page.locator('#os-user-menu .os-menu__foot').innerText()), 'the version (SHELL-06)');
    await page.keyboard.press('Escape');
    await page.click('.os-grid .os-app[data-window="/People"]', { button: 'right' });
    expectTrue(await page.locator('.os-menu[role=menu]:not(#os-user-menu)').isVisible(), 'the menu of an app');
    expectTrue((await page.locator('.os-menu[role=menu]:not(#os-user-menu)').innerText()).includes('Open in a new tab'), 'a new tab');
    await page.keyboard.press('Escape');
  });
  await check('the person arranges the home screen (a right click everywhere, dragging, pages, sizes, off the screen) and the account keeps it', async () => {
    await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    expectEqual((await page.request.delete(BASE + '/api/home', { headers: { RequestVerificationToken: token } })).status(), 204, 'the home screen as it comes');
    // Activity and People first, so that both are on the screen whatever fits on it; the rest follows as it comes.
    expectEqual((await page.request.put(BASE + '/api/home', { headers: { RequestVerificationToken: token }, data: { pages: [['s:jobs', 's:people']] } })).status(), 204, 'a layout is taken');
    await page.reload({ waitUntil: 'networkidle' });
    const keys = i => page.locator('.os-page').nth(i).locator(':scope > [data-home-key]').evaluateAll(els => els.map(e => e.getAttribute('data-home-key')));
    const menu = () => page.locator('.os-menu[role=menu]:not(#os-user-menu)');
    const entry = text => menu().locator('.os-menu__item', { hasText: text }).first();
    const saved = () => page.waitForResponse(r => r.url().endsWith('/api/home') && r.request().method() === 'PUT');

    await page.mouse.click(1300, 800, { button: 'right' });
    const screen = await menu().innerText();
    expectTrue(/Edit the home screen/.test(screen) && /Add a page/.test(screen) && /Reset the home screen/.test(screen), 'the menu of the empty screen');
    await page.keyboard.press('Escape');

    await page.locator('[data-home-key="w:today"]').click({ button: 'right', position: { x: 30, y: 30 } });
    let put = saved();
    await entry('Wide').click();
    expectEqual((await put).status(), 204, 'a size is saved');

    // People dragged with the mouse onto the place of Activity.
    const from = await page.locator('[data-home-key="s:people"]').boundingBox();
    const to = await page.locator('[data-home-key="s:jobs"]').boundingBox();
    put = saved();
    await page.mouse.move(from.x + from.width / 2, from.y + 30);
    await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2 - 20, from.y + 30, { steps: 4 });
    await page.mouse.move(to.x + 10, to.y + 30, { steps: 16 });
    await page.mouse.up();
    await put;
    const order = await keys(0);
    expectTrue(order.indexOf('s:people') < order.indexOf('s:jobs'), 'People is before Activity: ' + order.join(' '));
    expectEqual(await page.locator('.os-window').count(), 0, 'the drag opened nothing');

    await page.locator('[data-home-key="s:jobs"]').click({ button: 'right' });
    put = saved();
    await entry('To a new page').click();
    await put;
    await page.locator('[data-home-key="w:docker"]').click({ button: 'right', position: { x: 20, y: 20 } });
    put = saved();
    await entry('Remove from the home screen').click();
    await put;

    await page.reload({ waitUntil: 'networkidle' });
    const screens = await page.locator('.os-page').count();
    expectEqual(await page.locator('.os-page:not([data-continues])').count(), 2, 'two pages of the person after a reload');
    expectEqual(await keys(screens - 1), ['s:jobs'], 'Activity alone on the last page');
    expectEqual(await page.getAttribute('[data-home-key="w:today"]', 'data-size'), 'wide', 'Today is wide');
    expectEqual(await page.locator('.os-page [data-home-key="w:docker"]').count(), 0, 'Docker is off the screen');
    expectEqual(await page.locator('.os-dots .os-dot').count(), screens, 'a dot per page');

    await page.mouse.click(1300, 800, { button: 'right' });
    await entry('Edit the home screen').click();
    expectTrue(await page.locator('.os-editbar').isVisible() && await page.locator('.os-page .os-remove').count() > 0, 'editing: the bar, a minus on each thing');
    await page.locator('[data-os-done]').click();
    expectEqual(await page.locator('.os-remove').count(), 0, 'Done ends it');

    await page.mouse.click(1300, 800, { button: 'right' });
    await entry('Reset the home screen').click();
    await Promise.all([page.waitForEvent('load'), page.locator('dialog[open] button[value="ok"]').click()]);
    // reset: the home screen as it comes (HomeLayout.OrDefault): the person's page and the page of the machine (engines, Docker, Task Manager, System)
    expectEqual(await page.locator('.os-page:not([data-continues])').count(), 2, 'reset: the two pages of the home screen as it comes');
    expectEqual(await page.locator('.os-page [data-home-key="w:docker"]').count(), 1, 'reset: Docker is back');
  });
  await check('the home screen never scrolls: what does not fit on the screen is on the next page (a large screen, a phone)', async () => {
    for (const options of [{ viewport: { width: 1360, height: 900 } }, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }]) {
      const { ctx: c, page: p } = await signedIn(options);
      await p.goto(BASE + '/', { waitUntil: 'networkidle' });
      const what = options.viewport.width + 'x' + options.viewport.height;
      expectTrue(await p.evaluate(() => document.scrollingElement.scrollHeight <= window.innerHeight + 1), what + ': the page does not scroll');
      const outside = await p.evaluate(() => Array.prototype.slice.call(document.querySelectorAll('.os-page')).reduce((n, page) => {
        const bottom = page.getBoundingClientRect().bottom + 1;
        return n + Array.prototype.slice.call(page.children).filter(t => t.hasAttribute('data-home-key') && t.getBoundingClientRect().bottom > bottom && page.children.length > 1).length;
      }, 0));
      expectEqual(outside, 0, what + ': things below the end of their page');
      const screens = await p.locator('.os-page').count();
      expectTrue(screens === 1 || await p.locator('.os-dots .os-dot').count() === screens, what + ': a dot per page');
      await c.close();
    }
  });
  await check('with Today off the screen the home screen still has its one h1', async () => {
    await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    const put = await page.request.put(BASE + '/api/home', { headers: { RequestVerificationToken: token }, data: { hidden: ['w:today'] } });
    expectEqual(put.status(), 204, 'Today off the screen');
    await page.reload({ waitUntil: 'networkidle' });
    expectEqual(await page.locator('h1').count(), 1, 'one h1');
    expectEqual((await page.request.put(BASE + '/api/home', { headers: { RequestVerificationToken: token }, data: { pages: [['<x>']] } })).status(), 400, 'a key that is no key: 400');
    await page.request.delete(BASE + '/api/home', { headers: { RequestVerificationToken: token } });
  });
  await check('the wallpaper is chosen on the account and drawn on the home screen', async () => {
    const choose = async (id) => {
      await page.goto(BASE + '/Account/Profile?tab=home');
      await page.check(`input[name=wallpaper][value=${id}]`);
      await Promise.all([page.waitForURL(/tab=home/), page.click('form[action*="handler=Wallpaper"] button[type=submit]')]);
    };
    await choose('jungle');
    await page.goto(BASE + '/');
    expectEqual(await page.getAttribute('.os-desktop', 'data-wallpaper'), 'jungle', 'the picture of the account');
    const image = await page.evaluate(() => getComputedStyle(document.querySelector('.os-desktop'), '::before').backgroundImage);
    expectTrue(image.includes('wallpapers/jungle.jpg'), 'the picture is drawn: ' + image.slice(0, 120));
    await choose('orbit');
    await page.goto(BASE + '/');
    expectEqual(await page.getAttribute('.os-desktop', 'data-wallpaper'), 'orbit', 'back to the default');
  });

  await check('an administrator has two menus, the switcher shows the one with the page', async () => {
    await page.goto(BASE + '/People');
    expectEqual(await page.locator('.mat-switcher__name').innerText(), 'Administration', 'the menu of the page');
    await page.goto(BASE + '/Store');
    expectEqual(await page.locator('.mat-switcher__name').innerText(), 'MatOS', 'the menu of the launcher');
  });
  await check('the menu marks the current page and the submenu opens on its branch', async () => {
    await page.goto(BASE + '/Settings/Engines');
    expectEqual(await page.locator('.mat-nav__item--sub.is-active').innerText(), 'Engines', 'the current child');
    expectEqual(await page.locator('.mat-nav__parent').first().getAttribute('aria-expanded'), 'true', 'the branch is open');
  });

  console.log('Lists');
  await check('the store: the search field, a filter dialog with a chip and the pager', async () => {
    await page.goto(BASE + '/Store');
    expectEqual(await page.locator('.mat-gallery .mat-card').count(), 12, 'cards on the first page');
    expectTrue(await page.locator('.mat-pager').count() === 1, 'a pager under the gallery');
    await page.click('[data-dialog-open="store-filter"]');
    expectEqual(await page.locator('dialog#store-filter[open]').count(), 1, 'the dialog opens');
    await page.selectOption('#store-f-category', 'System');
    await page.click('dialog#store-filter button[type=submit]');
    await page.waitForURL(/category=System/);
    expectEqual(await page.locator('.mat-chip').count(), 1, 'a chip for the filter');
    expectTrue(await page.locator('.mat-gallery .mat-card').count() >= 1, 'apps of the category');
  });
  await check('the users: search by field, sort in a dialog, the key / focuses the search', async () => {
    await page.goto(BASE + '/Users?q=' + USER);
    expectEqual(await page.locator('table.mat-table tbody tr').count(), 1, 'the user is found');
    expectEqual(await page.inputValue('#users-q'), USER, 'the field shows the term');
    await page.goto(BASE + '/Users');
    await page.keyboard.press('/');
    expectEqual(await page.evaluate(() => document.activeElement && document.activeElement.id), 'users-q', 'the search has the focus');
    await page.click('[data-dialog-open="users-sort"]');
    await page.check('#users-sort-by-name');
    await page.click('dialog#users-sort button[type=submit]');
    await page.waitForURL(/sort=name/);
  });
  await check('an empty search says so and offers to reset the tools', async () => {
    await page.goto(BASE + '/Users?q=nobody-has-this-name');
    expectEqual(await page.locator('.mat-empty').count(), 1, 'the empty state');
    await page.click('.mat-empty a');
    await page.waitForURL(u => !u.search);
  });

  console.log('Forms');
  await check('a required field says what to do, in the field and in the summary', async () => {
    await page.goto(BASE + '/Backups/ScheduleEdit');
    await page.fill('#Input_Name', '');
    await page.click('form.mat-form button[type=submit]:not([formnovalidate])');
    await page.waitForTimeout(200);
    expectEqual(await page.locator('.mat-field.is-invalid').count(), 1, 'one invalid field');
    expectTrue((await page.locator('.mat-field.is-invalid .mat-error').innerText()).length > 0, 'a message');
    expectEqual(await page.locator('[data-validation-summary]:not([hidden])').count(), 1, 'the summary shows');
  });
  await check('what the server finds lands in the summary too', async () => {
    await page.goto(BASE + '/Account/Profile?tab=password');
    await page.fill('#Input_CurrentPassword', 'not-the-password');
    await page.fill('#Input_NewPassword', 'another-password-1');
    await page.fill('#Input_ConfirmPassword', 'another-password-1');
    await page.click('form.mat-form button[type=submit]');
    await page.waitForLoadState('networkidle');
    expectTrue(await page.locator('[data-validation-summary]:not([hidden]) li').count() >= 1, 'a message in the summary');
    expectEqual(await page.locator('.mat-field.is-invalid').count(), 1, 'the field is marked');
  });
  await check('the tab Security explains the second step and links it only where it lives, in a new tab', async () => {
    await page.goto(BASE + '/Account/Profile?tab=security');
    expectEqual(await page.locator('.mat-tabs [aria-current="page"]').filter({ hasText: 'Security' }).count(), 1, 'the tab is active');
    expectEqual(await page.locator('[aria-label="Second step"]').count(), 1, 'the card of the second step');
    const links = page.locator('[aria-label="Second step"] a.mat-btn');
    for (let i = 0; i < await links.count(); i++) {
      const href = await links.nth(i).getAttribute('href');
      expectTrue(!!href && href !== '#', 'a button goes somewhere');
      if (/^https?:/.test(href)) {
        expectEqual(await links.nth(i).getAttribute('target'), '_blank', 'the page of the second step opens in a new tab');
        expectTrue(/\/Account\/Security(\?brand=[^&]+)?$/.test(href), 'it is the page of the second step: ' + href);
      }
    }
    expectEqual(/MatIdentity/.test(await page.locator('[aria-label="Second step"]').innerText()), false, 'the tab does not name the engine');
  });
  await check('a saved schedule is a toast once, and deleting it asks first', async () => {
    await page.goto(BASE + '/Backups/ScheduleEdit');
    await page.fill('#Input_Name', 'E2E schedule');
    await page.click('form.mat-form button[type=submit]:not([formnovalidate])');
    await page.waitForURL(/tab=schedules/);
    await page.waitForTimeout(400);
    expectTrue(await page.locator('.mat-toast').count() >= 1, 'a toast says it was saved');
    await page.goto(BASE + '/Backups?tab=schedules');
    await page.waitForTimeout(400);
    expectEqual(await page.locator('.mat-toast').count(), 0, 'not shown a second time');
    await page.locator('table.mat-table tbody tr', { hasText: 'E2E schedule' }).locator('a[href*="ScheduleEdit"]').first().click();
    await page.click('button[formaction*="Delete"], button[asp-page-handler=Delete], form.mat-form button.mat-btn--danger');
    expectEqual(await page.locator('dialog.mat-dialog[open]').count(), 1, 'a dialog asks');
    await page.locator('dialog.mat-dialog[open] .mat-btn--danger').click();
    await page.waitForURL(/tab=schedules/);
    expectEqual(await page.locator('table.mat-table tbody tr', { hasText: 'E2E schedule' }).count(), 0, 'the schedule is gone');
  });
  await check('backup targets: a third tab, a form per kind, a test, a password never shown again, deleting asks first', async () => {
    await page.goto(BASE + '/Backups/Targets');
    expectEqual(await page.locator('.mat-tabs [aria-current="page"]').filter({ hasText: 'Targets' }).count(), 1, 'the tab Targets is active');
    expectEqual(/MatDock/.test(await page.locator('main').innerText()), false, 'the page does not name the engine');
    if (await page.locator('a[href="/Backups/TargetEdit"]').count() === 0) {
      // An engine without the targets API: the page says so and offers nothing it cannot do.
      expectEqual(await page.locator('.mat-empty').count(), 1, 'the empty state says targets cannot be used now');
      return;
    }

    await page.goto(BASE + '/Backups/TargetEdit');
    const visible = name => page.locator(`[data-show-value="${name}"]`).isVisible();
    expectTrue(await visible('Sftp') && !(await visible('WebDav')) && !(await visible('Smb')), 'SFTP is the first kind, the others are hidden');
    await page.selectOption('#Input_Kind', 'WebDav');
    expectTrue(await visible('WebDav') && !(await visible('Sftp')), 'choosing WebDAV shows its fields');
    expectTrue(await page.locator('[data-show-value="Sftp"] input').first().isDisabled(), 'the hidden fields post nothing');
    await page.fill('#Input_Name', 'E2E target');
    await page.fill('#Input_Url', 'ftp://not-webdav');
    await page.click('form.mat-form button[type=submit]:not([formnovalidate]):not([formaction*="Test"])');
    await page.waitForLoadState('networkidle');
    expectTrue(/WebDAV folder/.test(await page.locator('[data-validation-summary]').innerText()), 'the server says what is wrong');

    // An SFTP target on a server that is not there: saved, the test says why it fails, the password is not rendered back.
    const sftp = (process.env.MATOS_E2E_SFTP || '127.0.0.1:9:keeper:e2e-secret-Pass1:upload/e2e').split(':');
    await page.goto(BASE + '/Backups/TargetEdit');
    await page.fill('#Input_Name', 'E2E target');
    await page.fill('#Input_Host', sftp[0]);
    await page.fill('#Input_Port', sftp[1]);
    await page.fill('#Input_Username', sftp[2]);
    await page.fill('#Input_Password', sftp[3]);
    await page.fill('#Input_Folder', sftp[4]);
    await Promise.all([page.waitForURL(/handler=Test/), page.click('button[formaction*="Test"]')]);
    expectEqual(await page.locator('[data-target-test]').count(), 1, 'the test answers');
    if (process.env.MATOS_E2E_SFTP) { expectEqual(await page.locator('[data-target-test].mat-alert--success').count(), 1, 'the connection works: ' + await page.locator('[data-target-test]').innerText()); }
    expectEqual(await page.locator('#Input_Password').inputValue(), '', 'the typed password is not sent back by the test');
    await page.fill('#Input_Password', sftp[3]);
    await Promise.all([page.waitForURL(u => u.pathname === '/Backups/Targets' || /id=\d+/.test(u.search)), page.click('form.mat-form button[type=submit]:not([formnovalidate]):not([formaction*="Test"])')]);
    if (!/TargetEdit\?id=/.test(page.url())) {
      const row = page.locator('table.mat-table tbody tr', { hasText: 'E2E target' });
      expectEqual(await row.count(), 1, 'the target is listed');
      await row.locator('a[href*="TargetEdit"]').first().click();
    }
    expectTrue(!(await page.content()).includes(sftp[3]), 'the stored password is not in the page');
    expectEqual(await page.locator('#Input_Password').getAttribute('placeholder'), 'Stored', 'the field says a password is stored');
    expectEqual(await page.locator('#Input_Kind').getAttribute('type'), 'hidden', 'the kind stays');

    await page.goto(BASE + '/Backups/ScheduleEdit');
    expectTrue(await page.locator('#Input_Target option', { hasText: 'E2E target' }).count() === 1, 'a schedule can keep its backups at the target');
    await page.goto(BASE + '/Backups/Create');
    if (await page.locator('#Destination').count() === 1) {
      expectTrue(await page.locator('#Destination option', { hasText: 'E2E target' }).count() === 1, 'a backup by hand can go to the target');
    }

    await page.goto(BASE + '/Backups/Targets');
    await page.locator('table.mat-table tbody tr', { hasText: 'E2E target' }).locator('a[href*="TargetEdit"]').first().click();
    await page.click('form.mat-form button.mat-btn--danger');
    expectEqual(await page.locator('dialog.mat-dialog[open]').count(), 1, 'a dialog asks');
    await page.locator('dialog.mat-dialog[open] .mat-btn--danger').click();
    await page.waitForURL(/\/Backups\/Targets/);
    expectEqual(await page.locator('table.mat-table tbody tr', { hasText: 'E2E target' }).count(), 0, 'the target is gone');
  });
  await check('the wizard (to change the setup later) walks through four steps, the engines under Advanced', async () => {
    await page.goto(BASE + '/Wizard');
    expectEqual(await page.locator('.mv-step').count(), 4, 'steps');
    await page.locator('.mv-step.is-active [data-next]').click();
    expectTrue(!(await page.locator('[data-wizard-advanced]').evaluate(d => d.open)), 'Advanced is closed');
    await page.locator('[data-wizard-advanced] summary').click();
    expectEqual(await page.locator('[data-wizard-advanced] [data-engine]').count(), 5, 'the five engines under Advanced');
    for (let i = 0; i < 2; i++) {
      await page.locator('.mv-step.is-active [data-next]').click();
      await page.waitForTimeout(120);
    }
    expectEqual(await page.locator('[data-wizard-summary] dt').count(), 5, 'rows of the summary (host, domain, engines, DNS provider, DNS records)');
    await page.locator('.mv-step.is-active [data-prev]').click();
    expectEqual(await page.locator('[data-step-label]').innerText(), 'Step 3 of 4', 'back goes one step');
  });
  await check('the tour of the home screen: marks one after the other, Escape ends it', async () => {
    await page.goto(BASE + '/?tour=1');
    await page.waitForSelector('[data-ob-tour]:not([hidden])', { timeout: 5000 });
    expectEqual(await page.locator('[data-ob-tour-count]').innerText(), '1 of 4', 'the first of four marks');
    const ring = await page.locator('[data-ob-tour-ring]').boundingBox();
    const dock = await page.locator('.os-dock').boundingBox();
    expectTrue(ring && dock && ring.x <= dock.x && ring.y <= dock.y && ring.width >= dock.width, 'the ring is around the dock');
    await page.click('[data-ob-tour-next]');
    expectEqual(await page.locator('[data-ob-tour-count]').innerText(), '2 of 4', 'Next goes on');
    await page.keyboard.press('Escape');
    expectTrue(await page.locator('[data-ob-tour]').isHidden(), 'Escape ends it');
    await page.goto(BASE + '/');
    expectEqual(await page.locator('[data-ob-tour]').count(), 0, 'not shown again');
  });
  await ctx.close();

  console.log('A phone');
  const phone = await signedIn({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  await check('the menu is a drawer behind the burger button and closes again', async () => {
    await phone.page.goto(BASE + '/Apps');
    await phone.page.click('[data-nav-toggle]');
    await phone.page.waitForTimeout(350);
    expectTrue(await phone.page.evaluate(() => document.querySelector('.mat-app').classList.contains('is-nav-open')), 'open');
    await phone.page.click('.mat-nav__close');
    await phone.page.waitForTimeout(350);
    expectTrue(!(await phone.page.evaluate(() => document.querySelector('.mat-app').classList.contains('is-nav-open'))), 'closed');
  });
  await check('a dialog fills the screen', async () => {
    await phone.page.goto(BASE + '/Users');
    await phone.page.click('[data-dialog-open="users-sort"]');
    await phone.page.waitForTimeout(350);
    const box = await phone.page.locator('dialog#users-sort').boundingBox();
    expectTrue(box && box.width >= 389 && box.height >= 843, 'full screen: ' + JSON.stringify(box));
  });
  await phone.ctx.close();

  // A person who signs in through the single sign-on (docs/ARCHITECTURE.md "The second step"): the tab Security links MatIdentity's page of the second step (in MatOS's
  // look, ?brand=), the tab Password its sign-in password and no form for a password MatOS does not have. A throwaway person of MatIdentity (People) is given MatOS,
  // signs in through it, and is removed again with their account of MatOS. Without MatIdentity connected there is nobody to sign in that way.
  console.log('The single sign-on');
  await singleSignOn();

  // MatOS's sign-in looks like MatOS (docs/ARCHITECTURE.md): no word of MatIdentity on the way in or out; the sign-out ends on MatOS's own page.
  console.log('The sign-in and the sign-out');
  await check('the signed-out page is MatOS\'s and names no MatIdentity', async () => {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const response = await page.goto(BASE + '/Account/SignedOut');
    expectEqual(response.status(), 200, 'status');
    expectTrue(!/matidentity/i.test(await page.locator('body').innerText()), 'MatIdentity named');
    expectTrue(await page.locator('a[href="/Account/Login"]').count() === 1, 'a way to sign in again');
    await ctx.close();
  });
  if (SSO) {
    await check('the sign-in at MatIdentity looks like MatOS (no MatIdentity in the text or the title)', async () => {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE + '/Account/Login');
      await page.waitForURL(u => u.origin !== new URL(BASE).origin);
      expectTrue(!/matidentity/i.test(await page.locator('body').innerText()), 'MatIdentity named on the page');
      expectTrue(!/matidentity/i.test(await page.title()), 'MatIdentity in the title: ' + await page.title());
      await ctx.close();
    });
    await check('the sign-out ends on MatOS\'s signed-out page without a page of MatIdentity', async () => {
      const { ctx, page } = await signedIn();
      await page.goto(BASE + '/Account/Profile');
      await Promise.all([
        page.waitForURL(u => u.origin === new URL(BASE).origin && u.pathname === '/Account/SignedOut', { timeout: 15000 }),
        page.evaluate(() => document.querySelector('form[action="/Account/Logout"]').requestSubmit()),
      ]);
      await ctx.close();
    });
  }

  await check('no script errors on any page', async () => { expectEqual(errors, [], 'errors'); });

  await browser.close();
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed} of ${results.length} checks passed`);
  process.exit(failed ? 1 : 0);

  async function singleSignOn() {
    const admin = await signedIn({ viewport: { width: 1360, height: 900 } });
    await admin.page.goto(BASE + '/Account/Profile?tab=security');
    if (/no single sign-on yet/.test(await admin.page.locator('[aria-label="Second step"]').innerText())) {
      console.log('  skip  MatOS has no single sign-on (MatIdentity is not connected)');
      await admin.ctx.close();
      return;
    }

    const name = 'e2e-sso-' + Date.now().toString(36);
    const password = 'Quiet-Harbor-4711';
    let subject = null;
    await check('a person of MatIdentity is made in People and given MatOS', async () => {
      const p = admin.page;
      await p.goto(BASE + '/People/Edit');
      await p.fill('#Input_DisplayName', 'E2E single sign-on');
      await p.fill('#Input_UserName', name);
      await p.fill('#Input_Email', name + '@example.com');
      await p.fill('#Input_Password', password);
      await p.fill('#Input_ConfirmPassword', password);
      await Promise.all([p.waitForURL(/\/People\/Edit\?id=/), p.click('form.mat-form button[type=submit]:not([formnovalidate])')]);
      subject = new URL(p.url()).searchParams.get('id');
      const give = p.locator('table.mat-table tbody tr', { hasText: 'MatOS' }).locator('a[href*="/People/Access"]').first();
      await Promise.all([p.waitForURL(/\/People\/Access/), give.click()]);
      await p.check('input[name="Given"]');
      await Promise.all([p.waitForURL(u => u.pathname !== '/People/Access'), p.click('form.mat-form button[type=submit]')]);
    });

    if (subject) {
      const sso = await browser.newContext({ viewport: { width: 1360, height: 900 } });
      const page = await sso.newPage();
      page.on('pageerror', e => errors.push(page.url() + ': ' + e.message));
      let signedInThere = false;
      await check('the person signs in through the single sign-on', async () => {
        await page.goto(BASE + '/Account/Login');
        await page.waitForURL(u => u.origin !== new URL(BASE).origin);
        await page.locator('input[type=email], input[name*="user" i], input[name*="mail" i]').first().fill(name);
        await page.locator('input[type=password]').first().fill(password);
        await Promise.all([page.waitForURL(u => u.origin === new URL(BASE).origin && !u.pathname.startsWith('/Account/'), { timeout: 15000 }), page.locator('button[type=submit]').first().click()]);
        signedInThere = true;
      });

      if (signedInThere) {
        const looks = [['light', 1360, 'light'], ['dark', 1360, 'dark'], ['phone', 390, 'light']];
        await check('the tab Security: what the second step is, and its page where it lives, light, dark and on a phone', async () => {
          for (const [mode, width, scheme] of looks) {
            await page.setViewportSize({ width, height: 844 });
            await page.emulateMedia({ colorScheme: scheme });
            await page.goto(BASE + '/Account/Profile?tab=security', { waitUntil: 'networkidle' });
            const card = page.locator('[aria-label="Second step"]');
            const link = card.locator('a.mat-btn');
            expectEqual(await link.count(), 1, mode + ': one button');
            expectTrue(/^https?:\/\/[^/]+(\/.*)?\/Account\/Security\?brand=[^&]+$/.test(await link.getAttribute('href')), mode + ': MatIdentity\'s page of the second step in MatOS\'s look: ' + await link.getAttribute('href'));
            expectEqual(await link.getAttribute('target'), '_blank', mode + ': in a new tab (a passkey cannot be made in the window)');
            expectEqual(await link.getAttribute('rel'), 'noopener', mode + ': without a way back into MatOS');
            expectTrue(!/MatIdentity/.test(await card.innerText()), mode + ': the tab does not name the engine');
            expectEqual(await page.locator('h1').count(), 1, mode + ': h1');
            const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
            expectTrue(wide <= 1, `${mode}: scrolls sideways by ${wide}px`);
            expectEqual(await page.locator('[style]').count(), 0, mode + ': elements with an inline style');
          }
          await page.setViewportSize({ width: 1360, height: 900 });
          await page.emulateMedia({ colorScheme: 'light' });
          const [tab] = await Promise.all([sso.waitForEvent('page'), page.locator('[aria-label="Second step"] a.mat-btn').click()]);
          await tab.waitForLoadState('networkidle');
          expectEqual(new URL(tab.url()).pathname, '/Account/Security', 'the page of the second step opens without a second sign-in');
          expectTrue(await tab.getByText('Authenticator app').count() >= 1, 'with the authenticator app');
          expectEqual(await tab.locator('[data-passkey-register]').count(), 1, 'and the passkeys');
          if (await tab.locator('html[data-app="brand"]').count() === 1) {
            expectTrue(!/matidentity/i.test(await tab.locator('body').innerText()) && !/matidentity/i.test(await tab.title()), 'in MatOS\'s look: no word of MatIdentity');
            expectEqual(await tab.locator('.mat-nav').count(), 0, 'without the menu of MatIdentity');
            expectEqual(await tab.locator('a.mat-btn', { hasText: 'Back to' }).count(), 1, 'a way back to MatOS');
          } else {
            console.log('        note: this MatIdentity shows its own account (its account pages take no brand yet)');
          }
          await tab.close();
        });
        await check('the tab Password: the sign-in password where it lives, and no form for a password MatOS does not have', async () => {
          await page.goto(BASE + '/Account/Profile?tab=password', { waitUntil: 'networkidle' });
          const link = page.locator('[aria-label="Sign-in password"] a.mat-btn');
          expectEqual(await link.count(), 1, 'one button');
          expectTrue(/\/Account\/Profile\?tab=password&brand=[^&]+$/.test(await link.getAttribute('href')), 'MatIdentity\'s password tab in MatOS\'s look: ' + await link.getAttribute('href'));
          expectEqual(await link.getAttribute('target'), '_blank', 'in a new tab');
          expectEqual(await page.locator('#Input_CurrentPassword').count(), 0, 'no form of MatOS\'s own password');
        });
      }
      await sso.close();
    }

    await check('the person and their account of MatOS are removed again', async () => {
      const p = admin.page;
      if (subject) {
        await p.goto(BASE + '/People/Edit?id=' + encodeURIComponent(subject));
        await p.click('button[formaction*="handler=Delete"]');
        await p.locator('dialog.mat-dialog[open] .mat-btn--danger').click();
        await p.waitForURL(u => !u.pathname.startsWith('/People/Edit'));
      }
      await p.goto(BASE + '/Users?q=' + encodeURIComponent(name));
      const row = p.locator('table.mat-table tbody tr', { hasText: name });
      if (await row.count() > 0) {
        await row.first().locator('button[aria-label^="Delete"]').click();
        await p.locator('dialog.mat-dialog[open] .mat-btn--danger').click();
        await p.waitForLoadState('networkidle');
        await p.goto(BASE + '/Users?q=' + encodeURIComponent(name));
      }
      expectEqual(await p.locator('table.mat-table tbody tr', { hasText: name }).count(), 0, 'no account of MatOS left');
    });
    await admin.ctx.close();
  }
})();

// The first start on a MatOS without any account (--first-start): Welcome, the account, the engines, the way to the apps, the one button.
async function firstStart(browser, errors) {
  console.log('The first start');
  const looks = [
    ['light', { viewport: { width: 1360, height: 900 }, colorScheme: 'light' }],
    ['dark', { viewport: { width: 1360, height: 900 }, colorScheme: 'dark' }],
    ['phone', { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }],
  ];
  for (const [mode, options] of looks) {
    const ctx = await browser.newContext(options);
    const page = await ctx.newPage();
    page.on('pageerror', e => errors.push(page.url() + ': ' + e.message));
    await check(mode + ': every request goes to Welcome, one card at a time, nothing sideways', async () => {
      await page.goto(BASE + '/Apps');
      expectEqual(new URL(page.url()).pathname, '/Setup', 'went to Welcome');
      expectEqual(await page.locator('h1').count(), 1, 'h1');
      expectEqual(await page.locator('.ob-step.is-active').count(), 1, 'one step shown');
      expectEqual(await page.locator('.ob-desktop[data-wallpaper]').count(), 1, 'the wallpaper');
      expectEqual(await page.locator('[style]').count(), 0, 'elements with an inline style');
      const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expectTrue(wide <= 1, 'scrolls sideways by ' + wide + 'px');
    });
    await ctx.close();
  }

  const ctx = await browser.newContext({ viewport: { width: 1360, height: 900 } });
  const page = await ctx.newPage();
  page.on('pageerror', e => errors.push(page.url() + ': ' + e.message));
  await check('the account: the browser checks the fields, the server the password, then the engines', async () => {
    await page.goto(BASE + '/Setup');
    await page.click('.ob-step.is-active [data-ob-next]');
    await page.click('button[type=submit]');
    expectTrue(await page.locator('.mat-field.is-invalid').count() >= 3, 'empty fields are marked');
    await page.fill('#Input_Name', 'Anna');
    await page.fill('#Input_Email', 'anna@example.com');
    await page.fill('#Input_Password', 'anna-secret-1');
    await page.fill('#Input_ConfirmPassword', 'anna-secret-1');
    await Promise.all([page.waitForLoadState('load'), page.click('button[type=submit]')]);
    expectTrue((await page.locator('[data-validation-summary]').innerText()).includes('e-mail'), 'a password with the e-mail in it is refused');
    expectEqual(await page.locator('.ob-step.is-active h2').innerText(), 'Your account', 'back at the account');
    await page.fill('#Input_Password', 'blue-river-42');
    await page.fill('#Input_ConfirmPassword', 'blue-river-42');
    await Promise.all([page.waitForURL(/\/Setup\/Engines/), page.click('button[type=submit]')]);
  });
  // A browser that came as localhost went on at 127.0.0.1 at Welcome (the cookie is made there): the rest of the first start stays on that origin.
  const origin = new URL(page.url()).origin;
  await check('the engines: three fixed rows and MatAi coming later, nothing to tick, no Back', async () => {
    expectEqual(await page.locator('.ob-engine:not(.ob-engine--later)').count(), 3, 'three fixed rows');
    expectEqual(await page.locator('.ob-engine--later[aria-disabled=true]').count(), 1, 'MatAi, coming later');
    expectEqual(await page.locator('.ob-engines input').count(), 0, 'no box to tick');
    expectEqual(await page.locator('[data-ob-prev]').count(), 0, 'no Back');
  });
  await check('the engines: the one question of the updates, the recommended answer chosen', async () => {
    expectEqual(await page.locator('input[name="Updates"]').count(), 3, 'three answers');
    expectEqual(await page.locator('input[name="Updates"]:checked').getAttribute('value'), 'apps', 'apps and engines at night');
  });
  const state = await ctx.storageState();
  for (const [mode, options] of looks) {
    const lookCtx = await browser.newContext({ ...options, storageState: state });
    const look = await lookCtx.newPage();
    look.on('pageerror', e => errors.push(look.url() + ': ' + e.message));
    await check(mode + ': the engines, nothing sideways', async () => {
      await look.goto(origin + '/Setup/Engines');
      expectEqual(new URL(look.url()).pathname, '/Setup/Engines', 'on the engines');
      expectEqual(await look.locator('h1').count(), 1, 'h1');
      expectEqual(await look.locator('[style]').count(), 0, 'elements with an inline style');
      const wide = await look.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expectTrue(wide <= 1, 'scrolls sideways by ' + wide + 'px');
    });
    await lookCtx.close();
  }
  // Next starts real installs (MatDock and MatSMTP): only with MATOS_FIRST_START_INSTALL=1 (catalog copies with subnets of their own, CLAUDE.md).
  if (process.env.MATOS_FIRST_START_INSTALL === '1') {
    let first = [];
    await check('Next on the engines queues MatDock and MatSMTP, once', async () => {
      // the token of the form as it is now: posted once more below, like a second tab that still shows the engines
      const token = await page.locator('form[data-ob-flow] input[name="__RequestVerificationToken"]').inputValue();
      await Promise.all([page.waitForURL(/\/Setup\/Choose/), page.click('.ob-step.is-active button[type=submit]')]);
      const progress = await (await page.request.get(origin + '/api/setup/progress')).json();
      first = progress.steps.map(s => s.key + '#' + s.jobId);
      expectEqual(progress.steps.map(s => s.key), ['engine:matdock', 'engine:matsmtp'], 'the first lines');
      expectEqual(progress.phase, 1, 'phase 1');
      // a second visit (the back button, a second tab) does not start a second run
      await page.goto(origin + '/Setup/Engines');
      expectEqual(new URL(page.url()).pathname, '/Setup/Choose', 'the engines lead on to the choices');
      const again = await (await page.request.get(origin + '/api/setup/progress')).json();
      expectEqual(again.steps.map(s => s.key + '#' + s.jobId), first, 'the same lines and jobs');
      // a second POST of the engines (the old form of a second tab) does not queue a second run either
      const repost = await page.request.post(origin + '/Setup/Engines', { form: { __RequestVerificationToken: token }, maxRedirects: 0 });
      expectEqual(repost.status(), 302, 'the second post is sent on');
      expectEqual(new URL(repost.headers()['location'], origin).pathname, '/Setup/Choose', 'to the choices');
      const third = await (await page.request.get(origin + '/api/setup/progress')).json();
      expectEqual(third.steps.map(s => s.key + '#' + s.jobId), first, 'still the same lines and jobs');
    });
    await check('the way to the apps: three choices, the first engines said in one line', async () => {
      expectEqual(await page.locator('.ob-choice input[name=Reach]').count(), 3, 'three choices');
      expectTrue(await page.locator('input[name=Reach][value=free]').isChecked(), 'the free address is the default');
      expectEqual(await page.locator('.ob-engines').count(), 0, 'no engines to tick');
      expectEqual(await page.locator('[data-ob-step]').count(), 3, 'three cards: the tree, the certificates, the test');
      await page.waitForFunction(() => { const p = document.querySelector('[data-ob-phase1]'); return p && !p.hidden && p.textContent.indexOf('MatDock') >= 0; }, null, { timeout: 15000 });
    });
    const chooseState = await ctx.storageState();
    for (const [mode, options] of looks) {
      const lookCtx = await browser.newContext({ ...options, storageState: chooseState });
      const look = await lookCtx.newPage();
      look.on('pageerror', e => errors.push(look.url() + ': ' + e.message));
      await check(mode + ': the three cards of the way to the apps, nothing sideways', async () => {
        await look.goto(origin + '/Setup/Choose');
        expectEqual(new URL(look.url()).pathname, '/Setup/Choose', 'on the choices');
        await look.locator('label.ob-choice', { has: look.locator('input[value=home]') }).click();
        for (let card = 0; card < 3; card++) {
          expectEqual(await look.locator('.ob-step.is-active').count(), 1, 'one card shown');
          expectEqual(await look.locator('[style]').count(), 0, 'elements with an inline style');
          const wide = await look.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
          expectTrue(wide <= 1, 'card ' + (card + 1) + ' scrolls sideways by ' + wide + 'px');
          if (card < 2) { await look.click('.ob-step.is-active [data-ob-next]'); }
        }
      });
      await lookCtx.close();
    }
    await check('an own domain by hand: the records to create name matos and the wildcard of the domain', async () => {
      const domain = 'e2e-' + Date.now().toString(36) + '.example.net';
      await page.locator('label.ob-choice', { has: page.locator('input[value=domain]') }).click();
      await page.fill('#BaseDomain', domain);
      await page.locator('label.mat-choice', { has: page.locator('input[name=DnsProvider][value=manual]') }).click();
      expectTrue(await page.locator('[data-ob-records]').isVisible(), 'the records card shows');
      const names = await page.locator('[data-ob-records] tr[data-ob-record="a"] [data-ob-record-name]').allInnerTexts();
      expectEqual(names, ['matos.' + domain, '*.' + domain], 'A records for matos and the wildcard');
      expectEqual(await page.locator('[data-ob-records] td:not([data-label])').count(), 0, 'every cell has its data-label');
      await page.fill('#DnsCname', 'home.example.org');
      expectEqual(await page.locator('[data-ob-records] tr[data-ob-record="cname"]:not([hidden])').count(), 2, 'a host name gives CNAME records');
      expectEqual(await page.locator('[data-ob-records] tr[data-ob-record="a"]:not([hidden])').count(), 0, 'and no A records');
      await page.fill('#DnsCname', '');
      await page.fill('#BaseDomain', '');
    });
    await check('at home: the certificates say plain http, the test has no failed required row', async () => {
      await page.locator('label.ob-choice', { has: page.locator('input[value=home]') }).click();
      expectTrue(await page.locator('#Name').isDisabled(), 'the name is not sent for "at home"');
      await page.click('.ob-step.is-active [data-ob-next]');
      expectEqual(await page.locator('.ob-step.is-active h2').innerText(), 'Certificates', 'on to the certificates');
      expectTrue((await page.locator('.ob-step.is-active .ob-panel:not([hidden])').innerText()).includes('plain http'), 'at home: plain http');
      await page.click('.ob-step.is-active [data-ob-next]');
      expectEqual(await page.locator('.ob-step.is-active h2').innerText(), 'A quick test', 'on to the test');
      expectEqual(await page.locator('[data-ob-dots] li.is-active').count(), 1, 'one dot');
      expectEqual(await page.locator('[data-ob-dots] li').nth(3).getAttribute('aria-current'), 'step', 'still the dot of the way to the apps');
      await page.waitForFunction(() => { const r = document.querySelector('[data-ob-test-row="ports80"]'); return r && ['done', 'failed', 'warn', 'skip'].indexOf(r.getAttribute('data-state')) >= 0; }, null, { timeout: 30000 });
      expectEqual(await page.locator('[data-ob-test-row]:not([hidden])').count(), 1, 'at home only the ports here');
      expectEqual(await page.locator('[data-ob-test-row="ports80"]').getAttribute('data-state'), 'done', 'the ports 80 and 443 are free here');
      expectEqual(await page.locator('[data-ob-test-summary]').innerText(), 'MatOS sets up its engines for use here at home.', 'the summary');
      expectTrue(await page.locator('[data-ob-go]').isEnabled(), 'Set up the rest stays enabled');
    });
    await check('Set up the rest: a line per engine in plain words, the self check last, the next screen at any time', async () => {
      await Promise.all([page.waitForURL(/\/Setup\/Progress/), page.click('[data-ob-go]')]);
      expectTrue(await page.locator('[data-ob-task]').count() >= 4, 'a line per engine at least');
      const labels = await page.locator('[data-ob-task] .ob-task__label').allInnerTexts();
      expectEqual(labels[0], 'App manager (MatDock)', 'MatDock first, in plain words');
      expectEqual(labels[1], 'Mail (MatSMTP)', 'MatSMTP second (the first phase)');
      expectTrue(!labels.includes('Address updater (Matddns)'), 'no address updater at home');
      expectEqual(await page.locator('[data-ob-task]').last().getAttribute('data-ob-task'), 'selfcheck', 'the self check is the last line');
      expectEqual(await page.locator('[data-ob-countdown]').count(), 0, 'no countdown');
      const next = page.locator('[data-ob-next-screen]');
      const nextText = await next.innerText();
      expectTrue(nextText.startsWith('Next:') || nextText.startsWith('Go to my home screen'), 'the next screen, while it works');
      const nextHref = await next.getAttribute('href');
      expectEqual((await page.request.get(new URL(nextHref, page.url()).href)).status(), 200, 'the next screen is there');
      await page.waitForFunction(() => document.querySelector('[data-ob-progress]').getAttribute('data-ob-phase') !== 'working', null, { timeout: 15 * 60 * 1000 });
      expectEqual(await page.getAttribute('[data-ob-progress]', 'data-ob-phase'), 'ready', 'all done');
      expectTrue(await page.locator('[data-ob-next-screen]').isVisible(), 'the next screen when it is done');
    });
    await check('Next: the sign-in in MatIdentity, or the mail without it', async () => {
      const withIdentity = (await page.locator('[data-ob-next-screen]').getAttribute('href')).endsWith('/Setup/Account');
      await Promise.all([page.waitForLoadState('load'), page.click('[data-ob-next-screen]')]);
      const path = new URL(page.url()).pathname;
      if (withIdentity) {
        expectEqual(path, '/Setup/Account', 'on the sign-in');
        await page.waitForFunction(() => { const p = document.querySelector('[data-ob-progress]'); return !p || p.getAttribute('data-ob-phase') !== 'working'; }, null, { timeout: 5 * 60 * 1000 });
        await page.waitForLoadState('load');
        const accountPhase = await page.locator('[data-ob-progress]').getAttribute('data-ob-phase').catch(() => null);
        if (await page.locator('[data-ob-progress][data-ob-wait]').count()) {
          // with the line "signin" the screen ends ready (or partial: then its line says why); ready names the e-mail
          expectTrue(accountPhase === 'ready' || accountPhase === 'partial', 'the sign-in line is over: ' + accountPhase);
          if (accountPhase === 'ready') {
            expectTrue(await page.locator('.ob-card > [data-ob-when="ready"]:not(.ob-done)').isVisible(), 'the ready block is shown');
            expectTrue((await page.locator('.ob-card').innerText()).includes('anna@example.com'), 'the ready block names the e-mail');
          }
        }
        expectTrue((await page.locator('.ob-note').innerText()).includes('admin'), 'the break-glass note');
        await Promise.all([page.waitForURL(/\/Setup\/Mail/), page.click('[data-ob-next-screen]')]);
      } else {
        expectEqual(path, '/Setup/Mail', 'without MatIdentity the mail follows');
      }
    });
    await check('the mail: the facts of MatSMTP, and Later leaves the assistant', async () => {
      expectEqual(new URL(page.url()).pathname, '/Setup/Mail', 'on the mail');
      expectEqual(await page.locator('dl.mat-kv[data-ob-mail] [data-ob-fact]').count(), 3, 'answers, accounts, health');
      expectEqual(await page.locator('form.mat-form[novalidate] input[name=AdminEmail][type=email]').count(), 1, 'where MatOS writes to you');
    });
    const endState = await ctx.storageState();
    for (const [mode, options] of looks) {
      const lookCtx = await browser.newContext({ ...options, storageState: endState });
      const look = await lookCtx.newPage();
      look.on('pageerror', e => errors.push(look.url() + ': ' + e.message));
      await check(mode + ': the sign-in and the mail, nothing sideways', async () => {
        const own = { '/Setup/Account': 'Your account in MatIdentity', '/Setup/Mail': 'Notifications and mail' };
        for (const screen of ['/Setup/Account', '/Setup/Mail']) {
          await look.goto(origin + screen);
          expectEqual(await look.locator('h1').count(), 1, screen + ': h1');
          // a poll of its line (asked at once when the tab is shown again) keeps the screen's own headline and title
          if (await look.locator('[data-ob-progress][data-ob-wait]').count()) {
            await Promise.all([
              look.waitForResponse(r => r.url().includes('/api/setup/progress'), { timeout: 15000 }),
              look.evaluate(() => document.dispatchEvent(new Event('visibilitychange'))),
            ]);
            await look.waitForTimeout(200);
          }
          expectEqual((await look.locator('h1').innerText()).trim(), own[screen], screen + ': its own headline');
          expectTrue((await look.title()).startsWith(own[screen]), screen + ': its own title, not ' + (await look.title()));
          expectEqual(await look.locator('[style]').count(), 0, screen + ': elements with an inline style');
          const wide = await look.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
          expectTrue(wide <= 1, screen + ' scrolls sideways by ' + wide + 'px');
        }
      });
      await lookCtx.close();
    }
    const startTitle = () => page.waitForFunction(() => { const el = document.querySelector('.os-window.is-active [data-win-title]'); return !!el && el.textContent === 'Getting started'; }, null, { timeout: 15000 });
    await check('Later leaves the assistant for the home screen, with the list "Getting started" in a window and no tour', async () => {
      await page.goto(origin + '/Setup/Mail');
      await Promise.all([page.waitForURL(u => !new URL(u).pathname.startsWith('/Setup')), page.click('button[formnovalidate]')]);
      expectEqual(new URL(page.url()).pathname, '/', 'on the home screen');
      expectEqual(new URL(page.url()).origin, origin, 'on the same origin (127.0.0.1 since Welcome)');
      await startTitle();
      expectEqual(new URL(page.url()).search, '', 'the query ?start=1 is gone');
      const list = page.frameLocator('.os-window.is-active .os-window__frame');
      await list.locator('[data-start-item="mail"]').waitFor();
      expectEqual(await list.locator('[data-start-item="mail"]').getAttribute('data-done'), 'false', 'Set up mail is not done');
      expectTrue((await list.locator('[data-start-item="mail"] .mat-item__title').innerText()).includes('Set up mail'), 'the row Set up mail');
      expectEqual(await page.locator('[data-ob-tour]').count(), 0, 'no tour on this visit');
    });
    await check('the widget Setup opens the list again (or, with a failed line, leads back to the progress)', async () => {
      await page.click('.os-window.is-active .os-lights [data-win-close]');
      await page.waitForFunction(() => document.querySelectorAll('.os-window').length === 0);
      if (await page.locator('.os-widget[data-window="/Start"]').count()) {
        await page.click('.os-widget[data-window="/Start"]');
        await startTitle();
      } else {
        // a line failed (the sign-in may end partial): the widget stays the way back to its Try again
        expectEqual(await page.locator('.os-widget[href="/Setup/Progress"]').count(), 1, 'the widget leads to the progress screen');
      }
    });
  }
  await ctx.close();

  await check('no script errors in the first start', async () => { expectEqual(errors, [], 'errors'); });
  await browser.close();
  const failed = results.filter(r => !r.ok).length;
  console.log('\n' + (results.length - failed) + ' of ' + results.length + ' checks passed');
  process.exit(failed ? 1 : 0);
}
