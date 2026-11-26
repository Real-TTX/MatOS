// The wallpapers of the home screen: draws them (orbit.js, views.js write SVG) and renders each to a JPEG for the screen and a small one for the picker.
// Nothing is downloaded; every picture is drawn here. Run it again after changing a generator:
//   cd tests/e2e && npm install && cd ../.. && node scripts/wallpapers/render.js
// It uses Playwright with the installed Edge (like the browser checks). The JPEGs land in src/MatOS.Web/wwwroot/img/wallpapers.
const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { chromium } = require(path.join(__dirname, '..', '..', 'tests', 'e2e', 'node_modules', 'playwright-core'));

const OUT = path.join(__dirname, '..', '..', 'src', 'MatOS.Web', 'wwwroot', 'img', 'wallpapers');
const NAMES = ['orbit-dark', 'orbit-light', 'earth', 'jungle', 'lagoon', 'mountains'];
const SIZES = [['', 2560, 1600, 82], ['-thumb', 480, 300, 80]];

(async () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mv-wallpapers-'));
  execFileSync(process.execPath, [path.join(__dirname, 'orbit.js'), tmp], { stdio: 'inherit' });
  execFileSync(process.execPath, [path.join(__dirname, 'views.js'), tmp], { stdio: 'inherit' });
  fs.mkdirSync(OUT, { recursive: true });

  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  for (const name of NAMES) {
    const svg = fs.readFileSync(path.join(tmp, name + '.svg'), 'utf8');
    for (const [suffix, w, h, quality] of SIZES) {
      const page = await browser.newPage({ viewport: { width: w, height: h } });
      // The SVG inline at the size of the picture, so its noise filters are computed at that size.
      await page.setContent(`<html><body style="margin:0;overflow:hidden">${svg.replace('<svg ', `<svg width="${w}" height="${h}" `)}</body></html>`);
      await page.waitForTimeout(500);
      const file = path.join(OUT, `${name}${suffix}.jpg`);
      await page.screenshot({ path: file, type: 'jpeg', quality });
      console.log(path.basename(file), Math.round(fs.statSync(file).size / 1024) + ' KB');
      await page.close();
    }
  }
  await browser.close();
  fs.rmSync(tmp, { recursive: true, force: true });
})().catch(e => { console.error(e); process.exit(1); });
