// More wallpapers for MatOS, all drawn (nothing downloaded): scenes at eye level, the sky and two abstract ones.
// They take up the motifs of the first MatOS (mountains at dusk, the beach, the dunes, the night city, the forest, the galaxy, the liquid marble),
// drawn again with the means of views.js: layered silhouettes with haze between them, and SVG noise for water, sand, fog, gas and paint.
// dawn: ridges in the morning light; coast: the sea at sunset; dunes: a desert at noon; city: a skyline at night over the river;
// forest: pines in the fog; aurora: northern lights over a frozen lake; nebula: a cloud of gas and stars; marble: poured paint.
// node scenes.js <out folder> (render.js calls it)
const fs = require('fs');
const path = require('path');
const OUT = process.argv[2];
const W = 2400, H = 1500;

function rng(seed) { let s = seed; return () => { s = (s * 16807) % 2147483647; return s / 2147483647; }; }
const f = n => Math.round(n * 10) / 10;
const full = `x="0" y="0" width="${W}" height="${H}" filterUnits="userSpaceOnUse"`;
const svg = (defs, body) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">
<defs>${defs}</defs>
${body}
</svg>`;

// A smooth line of random heights (1D value noise in octaves): the height at every `step` pixels from x = -40 to W + 40.
function line(seed, { step = 8, scale = 600, octaves = 5, rough = 0.5 } = {}) {
  const r = rng(seed);
  const lattice = Array.from({ length: 4096 }, () => r() * 2 - 1);
  const at = x => { const i = Math.floor(x), t = x - i, s = (1 - Math.cos(t * Math.PI)) / 2; return lattice[i & 4095] * (1 - s) + lattice[(i + 1) & 4095] * s; };
  const out = [];
  for (let x = -40; x <= W + 40; x += step) {
    let v = 0, a = 1, fr = 1 / scale, sum = 0;
    for (let o = 0; o < octaves; o++) { v += at(x * fr + o * 17.3) * a; sum += a; a *= rough; fr *= 2; }
    out.push([x, v / sum]);
  }
  return out;
}
// A ridge: the line at a base height with an amplitude, closed down to the bottom of the picture.
function ridge(points, base, amp, bottom = H + 10) {
  const pts = points.map(([x, v]) => [x, base - v * amp]);
  return { pts, d: `M -40 ${bottom} ` + pts.map(([x, y]) => `L ${x} ${f(y)}`).join(' ') + ` L ${W + 40} ${bottom} Z`, y: x => pts[Math.max(0, Math.min(pts.length - 1, Math.round((x + 40) / (pts[1][0] - pts[0][0]))))][1] };
}
function stars(n, seed, maxY, minY = 0) {
  const r = rng(seed);
  let out = '';
  for (let i = 0; i < n; i++) {
    const big = r() < 0.06;
    out += `<circle cx="${f(r() * W)}" cy="${f(minY + r() * (maxY - minY))}" r="${f(big ? 1.5 + r() * 1.2 : 0.5 + r() * 0.8)}" fill="${['#fff', '#dce4ff', '#fff1e0'][Math.floor(r() * 3)]}" opacity="${f(0.25 + r() * 0.75)}"/>`;
  }
  return out;
}
// A pine: a spike with three tiers of branches, standing on (x, y).
function pine(x, y, h) {
  const w = h * 0.36, p = (dx, dy) => `${f(x + dx * w)} ${f(y - dy * h)}`;
  return `M ${p(0, 1)} L ${p(-0.28, 0.74)} L ${p(-0.14, 0.74)} L ${p(-0.48, 0.42)} L ${p(-0.24, 0.42)} L ${p(-0.62, 0.08)} L ${p(-0.05, 0.08)} L ${p(-0.05, -0.02)} L ${p(0.05, -0.02)} L ${p(0.05, 0.08)} L ${p(0.62, 0.08)} L ${p(0.24, 0.42)} L ${p(0.48, 0.42)} L ${p(0.14, 0.74)} L ${p(0.28, 0.74)} Z`;
}
// Trees along a ridge: one every `gap` pixels (a little random), heights between hMin and hMax.
function treeline(rd, seed, gap, hMin, hMax, sink = 6) {
  const r = rng(seed);
  let d = '';
  for (let x = -30; x < W + 30; x += gap * (0.55 + r() * 0.9)) {
    if (r() < 0.12) continue;
    d += pine(x, rd.y(x) + sink, hMin + r() * (hMax - hMin)) + ' ';
  }
  return d;
}
// Mist: a band of white that a stretched noise thins out. Use it as filter on a rect filled with a gradient.
const mist = (id, seed, freq = '0.0016 0.009', slope = 2.2) => `<filter id="${id}" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="${freq}" numOctaves="4" seed="${seed}" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  ${slope} 0 0 0 ${f(0.4 - slope * 0.5)}" result="m"/>
    <feComposite in="SourceGraphic" in2="m" operator="in"/></filter>`;
const band = (id, color, peak, y1, y2) => `<linearGradient id="${id}" x1="0" y1="${y1}" x2="0" y2="${y2}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${color}" stop-opacity="0"/><stop offset=".6" stop-color="${color}" stop-opacity="${peak}"/><stop offset="1" stop-color="${color}" stop-opacity="0"/></linearGradient>`;
const fade = (id, color, from, to, y1, y2) => `<linearGradient id="${id}" x1="0" y1="${y1}" x2="0" y2="${y2}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${color}" stop-opacity="${from}"/><stop offset="1" stop-color="${color}" stop-opacity="${to}"/></linearGradient>`;
// Fine grain over the whole picture, so the large gradients do not band in the JPEG.
const grainDef = `<filter id="grain" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="1" seed="3"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.5  0 0 0 0 0.5  0 0 0 0 0.5  0 0 0 0.09 0"/></filter>`;
const grain = `<rect width="${W}" height="${H}" filter="url(#grain)"/>`;

// ---------- Ridges in the morning light ----------
function dawn() {
  const layers = [
    { base: 760, amp: 150, color: '#c79aa8', top: '#e2b6b6', seed: 3, scale: 520 },
    { base: 850, amp: 170, color: '#9f7f9f', top: '#b88fa6', seed: 8, scale: 480 },
    { base: 950, amp: 160, color: '#76678e', top: '#8a7299', seed: 14, scale: 430 },
    { base: 1060, amp: 150, color: '#4f4d74', top: '#5f5680', seed: 22, scale: 400 },
    { base: 1190, amp: 140, color: '#30344f', top: '#3a3b5c', seed: 31, scale: 360 },
    { base: 1340, amp: 120, color: '#171b2c', top: '#1d2236', seed: 40, scale: 320 },
  ];
  let defs = `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="${H}" gradientUnits="userSpaceOnUse">
    <stop offset="0" stop-color="#1c2550"/><stop offset=".28" stop-color="#3e4482"/><stop offset=".46" stop-color="#9a6f9c"/><stop offset=".58" stop-color="#e79a8c"/><stop offset=".66" stop-color="#ffc79a"/><stop offset=".72" stop-color="#ffe2b0"/></linearGradient>
  <radialGradient id="glow" cx="1680" cy="790" r="900" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fff4d8" stop-opacity=".95"/><stop offset=".12" stop-color="#ffd9a0" stop-opacity=".6"/><stop offset=".45" stop-color="#ff9e80" stop-opacity=".18"/><stop offset="1" stop-color="#ff9e80" stop-opacity="0"/></radialGradient>
  <filter id="clouds" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0009 0.0065" numOctaves="5" seed="11"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 0.78  0 0 0 0 0.72  3.4 0 0 0 -1.9"/></filter>
  ${fade('cloudfade', '#000', 1, 0, 120, 640)}
  <mask id="cloudmask" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><rect width="${W}" height="680" fill="#fff" opacity=".9"/></mask>
  ${grainDef}`;
  let body = `<rect width="${W}" height="${H}" fill="url(#sky)"/>
${stars(90, 5, 360)}
<rect width="${W}" height="${H}" fill="url(#glow)"/>
<circle cx="1680" cy="790" r="54" fill="#fff6e0"/>
<g mask="url(#cloudmask)" opacity=".75"><rect width="${W}" height="700" filter="url(#clouds)"/></g>`;
  layers.forEach((l, i) => {
    const rd = ridge(line(l.seed, { scale: l.scale, rough: 0.52 }), l.base, l.amp);
    defs += `<linearGradient id="m${i}" x1="0" y1="${l.base - l.amp}" x2="0" y2="${l.base + 260}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${l.top}"/><stop offset="1" stop-color="${l.color}"/></linearGradient>`;
    defs += mist(`mist${i}`, 70 + i, '0.0012 0.008', 2.4);
    defs += band(`mf${i}`, '#fff4ea', i < 2 ? 0.6 : 0.45, l.base - 120, l.base + 90);
    body += `<path d="${rd.d}" fill="url(#m${i})"/>`;
    if (i >= 3) body += `<path d="${treeline(rd, 90 + i, i === 5 ? 26 : 18, i === 5 ? 60 : 26, i === 5 ? 150 : 60)}" fill="url(#m${i})"/>`;
    if (i < 5) body += `<rect y="${l.base - 120}" width="${W}" height="${210}" fill="url(#mf${i})" filter="url(#mist${i})"/>`;
  });
  const r = rng(9);
  let birds = '';
  for (let i = 0; i < 7; i++) {
    const x = 980 + r() * 360, y = 420 + r() * 170, s = 7 + r() * 7;
    birds += `<path d="M ${f(x - s)} ${f(y - s * 0.3)} Q ${f(x - s * 0.45)} ${f(y - s * 0.55)} ${f(x)} ${f(y)} Q ${f(x + s * 0.45)} ${f(y - s * 0.55)} ${f(x + s)} ${f(y - s * 0.3)}"/>`;
  }
  body += `<g fill="none" stroke="#2a2440" stroke-width="2.4" stroke-linecap="round" opacity=".7">${birds}</g>${grain}`;
  return svg(defs, body);
}

// ---------- The sea at sunset ----------
function coast() {
  const hz = 880, sx = 1520;
  const shore = 'M -40 1360 C 400 1300 800 1330 1200 1385 C 1600 1440 2000 1420 2440 1330 L 2440 1540 L -40 1540 Z';
  const foamLine = 'M -40 1360 C 400 1300 800 1330 1200 1385 C 1600 1440 2000 1420 2440 1330';
  const defs = `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="${hz}" gradientUnits="userSpaceOnUse">
    <stop offset="0" stop-color="#1d1f4a"/><stop offset=".35" stop-color="#4b3f7e"/><stop offset=".62" stop-color="#c0607a"/><stop offset=".84" stop-color="#ff9a6a"/><stop offset="1" stop-color="#ffd08a"/></linearGradient>
  <linearGradient id="sea" x1="0" y1="${hz}" x2="0" y2="${H}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#7a5a8a"/><stop offset=".12" stop-color="#3d3a6e"/><stop offset=".6" stop-color="#1c2550"/><stop offset="1" stop-color="#0e1634"/></linearGradient>
  <radialGradient id="glow" cx="${sx}" cy="${hz}" r="1100" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fff2c8" stop-opacity=".95"/><stop offset=".08" stop-color="#ffcf8a" stop-opacity=".7"/><stop offset=".35" stop-color="#ff8a6a" stop-opacity=".22"/><stop offset="1" stop-color="#ff8a6a" stop-opacity="0"/></radialGradient>
  <radialGradient id="path" cx="${sx}" cy="${hz}" r="1" gradientTransform="translate(${sx} ${hz}) scale(260 640) translate(${-sx} ${-hz})" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#ffe6a8"/><stop offset=".5" stop-color="#ffb070" stop-opacity=".7"/><stop offset="1" stop-color="#ff8a60" stop-opacity="0"/></radialGradient>
  <clipPath id="above"><rect width="${W}" height="${hz}"/></clipPath>
  <clipPath id="below"><rect y="${hz}" width="${W}" height="${H - hz}"/></clipPath>
  <filter id="clouds" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0007 0.006" numOctaves="5" seed="23" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 1  0 0 0 0 0.62  0 0 0 0 0.6  3.6 0 0 0 -2.05" result="c"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0.24  0 0 0 0 0.16  0 0 0 0 0.34  3.6 0 0 0 -2.05"/><feOffset dy="10"/><feGaussianBlur stdDeviation="3"/>
    <feComposite in="c" operator="over"/></filter>
  ${fade('cloudfade', '#fff', 0.95, 0, 0, hz - 40)}
  <mask id="cloudmask" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><rect width="${W}" height="${hz}" fill="url(#cloudfade)"/></mask>
  <filter id="waves" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0016 0.045" numOctaves="3" seed="12"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 0.88  0 0 0 0 0.8  3 0 0 0 -1.75"/></filter>
  <filter id="dark" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0012 0.03" numOctaves="3" seed="4"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.03  0 0 0 0 0.05  0 0 0 0 0.15  3 0 0 0 -1.3"/></filter>
  <filter id="glitter" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.006 0.11" numOctaves="2" seed="8" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  5 0 0 0 -2.4" result="a"/><feComposite in="SourceGraphic" in2="a" operator="in"/></filter>
  ${fade('wavefade', '#fff', 0.15, 0.7, hz, H)}
  <mask id="wavemask" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#wavefade)"/></mask>
  <linearGradient id="sand" x1="0" y1="1300" x2="0" y2="${H}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#7d5a66"/><stop offset=".25" stop-color="#5d4458"/><stop offset="1" stop-color="#2c2236"/></linearGradient>
  <filter id="foam" ${full}><feTurbulence type="fractalNoise" baseFrequency="0.01 0.05" numOctaves="2" seed="6" result="t"/>
    <feDisplacementMap in="SourceGraphic" in2="t" scale="26" xChannelSelector="R" yChannelSelector="G"/><feGaussianBlur stdDeviation="1.4"/></filter>
  <filter id="sandgrain" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.004 0.03" numOctaves="3" seed="15" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0.05  0 0 0 0 0.03  0 0 0 0 0.08  2.2 0 0 0 -1.05" result="a"/><feComposite in="a" in2="SourceGraphic" operator="in"/></filter>
  ${grainDef}`;
  const body = `<rect width="${W}" height="${hz}" fill="url(#sky)"/>
${stars(110, 41, 300)}
<g clip-path="url(#above)"><rect width="${W}" height="${H}" fill="url(#glow)"/><circle cx="${sx}" cy="${hz + 20}" r="86" fill="#fff3cf"/></g>
<g mask="url(#cloudmask)"><rect width="${W}" height="${hz}" filter="url(#clouds)" opacity=".85"/></g>
<rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#sea)"/>
<g clip-path="url(#below)"><rect width="${W}" height="${H}" fill="url(#glow)" opacity=".55"/>
  <rect y="${hz}" width="${W}" height="${H - hz}" filter="url(#dark)" opacity=".55"/>
  <g mask="url(#wavemask)"><rect y="${hz}" width="${W}" height="${H - hz}" filter="url(#waves)" opacity=".5"/></g>
  <ellipse cx="${sx}" cy="${hz}" rx="260" ry="640" fill="url(#path)" filter="url(#glitter)"/>
  <ellipse cx="${sx}" cy="${hz + 6}" rx="190" ry="7" fill="#fff0c0" opacity=".8"/></g>
<rect y="${hz - 1}" width="${W}" height="2" fill="#ffd9a0" opacity=".5"/>
<path d="${shore}" fill="url(#sand)"/>
<path d="${shore}" filter="url(#sandgrain)" opacity=".8"/>
<g filter="url(#foam)"><path d="${foamLine}" fill="none" stroke="#ffe9e0" stroke-width="7" opacity=".75"/><path d="${foamLine}" transform="translate(0 -22)" fill="none" stroke="#ffd9d0" stroke-width="3" opacity=".35"/></g>
${grain}`;
  return svg(defs, body);
}

// ---------- A desert at noon ----------
function dunes() {
  const layers = [
    { base: 820, amp: 70, light: '#f3d7ae', dark: '#d9a978', seed: 6, scale: 700 },
    { base: 900, amp: 90, light: '#efc795', dark: '#c98f5a', seed: 12, scale: 620 },
    { base: 1000, amp: 150, light: '#eab97f', dark: '#b0703c', seed: 19, scale: 560 },
    { base: 1140, amp: 190, light: '#e6a96a', dark: '#9a5a2a', seed: 27, scale: 520 },
    { base: 1320, amp: 220, light: '#e19c58', dark: '#874a1e', seed: 33, scale: 480 },
  ];
  let defs = `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="900" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#4f8fc6"/><stop offset=".55" stop-color="#9cc6e0"/><stop offset="1" stop-color="#f1dcc0"/></linearGradient>
  <radialGradient id="sun" cx="620" cy="300" r="520" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fffdf2"/><stop offset=".08" stop-color="#fff6dc" stop-opacity=".9"/><stop offset=".4" stop-color="#fff2d0" stop-opacity=".25"/><stop offset="1" stop-color="#fff2d0" stop-opacity="0"/></radialGradient>
  <filter id="haze" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0008 0.01" numOctaves="4" seed="5"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  2.6 0 0 0 -1.45"/></filter>
  <filter id="ripples" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0015 0.05" numOctaves="2" seed="9" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0.35  0 0 0 0 0.17  0 0 0 0 0.05  5 0 0 0 -2.6" result="a"/><feComposite in="a" in2="SourceGraphic" operator="in"/></filter>
  <filter id="crest" ${full}><feGaussianBlur stdDeviation="1.6"/></filter>
  <filter id="soft" ${full}><feGaussianBlur stdDeviation="22"/></filter>
  ${grainDef}`;
  let body = `<rect width="${W}" height="${H}" fill="url(#sky)"/>
<rect width="${W}" height="${H}" fill="url(#sun)"/>
<rect width="${W}" height="700" filter="url(#haze)" opacity=".35"/>`;
  layers.forEach((l, i) => {
    const rd = ridge(line(l.seed, { scale: l.scale, octaves: 3, rough: 0.35 }), l.base, l.amp);
    // the side towards the sun (left) is light, the far side in shadow: a horizontal gradient that repeats with the crests is too regular,
    // so the shadow is the same ridge, moved down and right, laid over the light face.
    defs += `<linearGradient id="d${i}" x1="0" y1="${l.base - l.amp}" x2="0" y2="${l.base + 320}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${l.light}"/><stop offset="1" stop-color="${l.dark}"/></linearGradient>`;
    defs += `<linearGradient id="s${i}" x1="0" y1="${l.base - l.amp}" x2="0" y2="${l.base + 300}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${l.dark}"/><stop offset=".6" stop-color="${l.dark}" stop-opacity=".7"/><stop offset="1" stop-color="${l.dark}" stop-opacity=".3"/></linearGradient>`;
    const shadow = ridge(line(l.seed, { scale: l.scale, octaves: 3, rough: 0.35 }).map(([x, v]) => [x + 150 + i * 30, v]), l.base + 50 + i * 14, l.amp * 1.05);
    defs += `<clipPath id="c${i}"><path d="${rd.d}"/></clipPath>`;
    body += `<path d="${rd.d}" fill="url(#d${i})"/>
<g clip-path="url(#c${i})"><path d="${shadow.d}" fill="url(#s${i})" opacity=".9"/><path d="${rd.d}" filter="url(#ripples)" opacity="${0.12 + i * 0.06}"/></g>
<path d="${rd.d.replace(/ L -?\d+ \d+ Z$/, '').replace(/^M -40 \d+ /, 'M ')}" fill="none" stroke="#fff6e6" stroke-width="${2 + i}" opacity=".55" filter="url(#crest)"/>`;
  });
  return svg(defs, body + grain);
}

// ---------- A skyline at night over the river ----------
function city() {
  const hz = 1080;
  const r = rng(77);
  const layer = (n, hMin, hMax, wMin, wMax, color, lit, seed) => {
    const rr = rng(seed);
    let shapes = '', windows = '';
    let x = -60;
    while (x < W + 60) {
      const w = wMin + rr() * (wMax - wMin), h = hMin + rr() * (hMax - hMin), top = hz - h;
      shapes += `<rect x="${f(x)}" y="${f(top)}" width="${f(w)}" height="${f(h + 4)}"/>`;
      const kind = rr();
      if (kind < 0.18) shapes += `<rect x="${f(x + w / 2 - 2)}" y="${f(top - 40 - rr() * 70)}" width="4" height="${f(120)}"/>`;
      else if (kind < 0.3) shapes += `<path d="M ${f(x + w * 0.15)} ${f(top)} L ${f(x + w / 2)} ${f(top - w * 0.5)} L ${f(x + w * 0.85)} ${f(top)} Z"/>`;
      else if (kind < 0.42) shapes += `<rect x="${f(x + w * 0.2)}" y="${f(top - 26)}" width="${f(w * 0.6)}" height="28"/>`;
      if (lit > 0) {
        const cols = Math.max(2, Math.floor((w - 10) / 13)), rows = Math.floor((h - 20) / 17);
        const warm = rr() < 0.6;
        for (let cy = 0; cy < rows; cy++) for (let cx = 0; cx < cols; cx++) {
          if (rr() > lit) continue;
          const c = rr() < 0.85 ? (warm ? '#ffcf7a' : '#bfe4ff') : '#ffffff';
          windows += `<rect x="${f(x + 7 + cx * 13)}" y="${f(top + 14 + cy * 17)}" width="6" height="8" fill="${c}" opacity="${f(0.45 + rr() * 0.55)}"/>`;
        }
      }
      x += w + rr() * 8;
    }
    return `<g fill="${color}">${shapes}</g><g>${windows}</g>`;
  };
  const far = layer(0, 140, 380, 50, 120, '#2a2756', 0.08, 5);
  const mid = layer(0, 220, 600, 60, 140, '#1a1940', 0.18, 11);
  const near = layer(0, 160, 780, 70, 170, '#0c0d24', 0.3, 19);
  const skyline = far + mid + near;
  const defs = `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="${hz}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#03061a"/><stop offset=".45" stop-color="#0d1238"/><stop offset=".8" stop-color="#2a2160"/><stop offset="1" stop-color="#6a3a78"/></linearGradient>
  <radialGradient id="glow" cx="1200" cy="${hz}" r="1300" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#ff8fa0" stop-opacity=".45"/><stop offset=".4" stop-color="#8a4fb0" stop-opacity=".18"/><stop offset="1" stop-color="#8a4fb0" stop-opacity="0"/></radialGradient>
  <radialGradient id="moonglow"><stop offset="0" stop-color="#fff8e6" stop-opacity=".55"/><stop offset=".3" stop-color="#cfd8ff" stop-opacity=".12"/><stop offset="1" stop-color="#cfd8ff" stop-opacity="0"/></radialGradient>
  <filter id="moon" x="-20%" y="-20%" width="140%" height="140%" color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.045" numOctaves="4" seed="2" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0.62  0 0 0 0 0.62  0 0 0 0 0.66  2.4 0 0 0 -1.05" result="m"/><feComposite in="m" in2="SourceGraphic" operator="in" result="spots"/>
    <feMerge><feMergeNode in="SourceGraphic"/><feMergeNode in="spots"/></feMerge></filter>
  <linearGradient id="water" x1="0" y1="${hz}" x2="0" y2="${H}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#1b1740"/><stop offset="1" stop-color="#05071a"/></linearGradient>
  <filter id="ripple" ${full}><feTurbulence type="fractalNoise" baseFrequency="0.002 0.06" numOctaves="2" seed="7" result="t"/>
    <feDisplacementMap in="SourceGraphic" in2="t" scale="34" xChannelSelector="R" yChannelSelector="G"/><feGaussianBlur stdDeviation="2 5"/></filter>
  ${fade('reflfade', '#fff', 0.75, 0, hz, H)}
  <mask id="reflmask" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#reflfade)"/></mask>
  <filter id="mist" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0012 0.012" numOctaves="4" seed="31"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.75  0 0 0 0 0.55  0 0 0 0 0.85  2.4 0 0 0 -1.35"/></filter>
  ${grainDef}`;
  let lamps = '';
  for (let x = 20; x < W; x += 46) lamps += `<circle cx="${x}" cy="${hz - 6}" r="2.2" fill="#ffd58a" opacity="${f(0.5 + r() * 0.5)}"/>`;
  const body = `<rect width="${W}" height="${hz}" fill="url(#sky)"/>
${stars(260, 13, 700)}
<rect width="${W}" height="${hz}" fill="url(#glow)"/>
<circle cx="1880" cy="250" r="260" fill="url(#moonglow)"/>
<circle cx="1880" cy="250" r="56" fill="#fdf6e4" filter="url(#moon)"/>
<rect y="520" width="${W}" height="${hz - 520}" filter="url(#mist)" opacity=".22"/>
${skyline}
${lamps}
<rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#water)"/>
<g mask="url(#reflmask)"><g filter="url(#ripple)" transform="translate(0 ${2 * hz}) scale(1 -1)" opacity=".5">${skyline}</g></g>
<rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#glow)" opacity=".35"/>
${grain}`;
  return svg(defs, body);
}

// ---------- Pines in the fog ----------
function forest() {
  const layers = [
    { base: 860, amp: 60, color: '#a9bdb9', seed: 2, gap: 20, h: [50, 110] },
    { base: 930, amp: 70, color: '#8aa39f', seed: 7, gap: 24, h: [80, 170] },
    { base: 1020, amp: 70, color: '#678680', seed: 13, gap: 30, h: [130, 260] },
    { base: 1150, amp: 60, color: '#46655f', seed: 21, gap: 38, h: [190, 330] },
    { base: 1290, amp: 50, color: '#2a443f', seed: 29, gap: 56, h: [300, 520] },
    { base: 1440, amp: 20, color: '#132522', seed: 37, gap: 150, h: [520, 820] },
  ];
  let defs = `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="${H}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#e9efe6"/><stop offset=".5" stop-color="#cfdcd2"/><stop offset="1" stop-color="#b3c7bf"/></linearGradient>
  <radialGradient id="sun" cx="560" cy="140" r="1300" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fffbe6"/><stop offset=".15" stop-color="#fff3c8" stop-opacity=".75"/><stop offset=".6" stop-color="#ffeec0" stop-opacity=".12"/><stop offset="1" stop-color="#ffeec0" stop-opacity="0"/></radialGradient>
  <linearGradient id="ray" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fffbe8" stop-opacity=".55"/><stop offset="1" stop-color="#fffbe8" stop-opacity="0"/></linearGradient>
  <filter id="rays" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="18"/></filter>
  ${grainDef}`;
  let body = `<rect width="${W}" height="${H}" fill="url(#sky)"/><rect width="${W}" height="${H}" fill="url(#sun)"/>`;
  layers.forEach((l, i) => {
    const rd = ridge(line(l.seed, { scale: 500, octaves: 3, rough: 0.4 }), l.base, l.amp);
    defs += mist(`mist${i}`, 50 + i, '0.0011 0.006', 2);
    defs += band(`mf${i}`, '#f4f7f0', 0.85, l.base - 260, l.base + 70);
    body += `<path d="${rd.d}" fill="${l.color}"/><path d="${treeline(rd, 100 + i, l.gap, l.h[0], l.h[1], 40)}" fill="${l.color}"/>`;
    if (i < 5) body += `<rect y="${l.base - 260}" width="${W}" height="${330}" fill="url(#mf${i})" filter="url(#mist${i})"/>`;
    if (i === 3) {
      // the light falls through between the near trees
      const rr = rng(4);
      let rays = '';
      for (let k = 0; k < 7; k++) {
        const a = -0.15 + k * 0.11 + rr() * 0.05, w = 0.02 + rr() * 0.03, L = 2600;
        const p = t => `${f(560 + Math.sin(t) * L)} ${f(140 + Math.cos(t) * L)}`;
        rays += `<path d="M 560 140 L ${p(a)} L ${p(a + w)} Z" opacity="${f(0.35 + rr() * 0.4)}"/>`;
      }
      body += `<g fill="url(#ray)" filter="url(#rays)">${rays}</g>`;
    }
  });
  return svg(defs, body + grain);
}

// ---------- Northern lights over a frozen lake ----------
function aurora() {
  const hz = 1060;
  const band = (d, w, top, bottom, seed, op) => `<g opacity="${op}"><path d="${d}" fill="none" stroke="url(#${top})" stroke-width="${w}" filter="url(#curtain${seed})"/></g>`;
  const curtain = seed => `<filter id="curtain${seed}" x="-10%" y="-40%" width="120%" height="180%" color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.022 0.0012" numOctaves="3" seed="${seed}" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  3 0 0 0 -1.05" result="a"/>
    <feGaussianBlur in="SourceGraphic" stdDeviation="26" result="b"/>
    <feComposite in="b" in2="a" operator="in" result="streaks"/>
    <feGaussianBlur in="SourceGraphic" stdDeviation="60" result="halo"/>
    <feMerge><feMergeNode in="halo"/><feMergeNode in="streaks"/><feMergeNode in="streaks"/></feMerge></filter>`;
  const hills = ridge(line(61, { scale: 360, rough: 0.55 }), hz - 70, 170, hz + 2);
  const back = ridge(line(53, { scale: 520, rough: 0.5 }), hz - 150, 150, hz + 2);
  const r1 = 'M -200 640 C 300 300 700 760 1150 470 C 1550 210 1900 520 2600 260';
  const r2 = 'M -200 380 C 400 200 800 520 1300 330 C 1750 160 2100 360 2600 180';
  const defs = `<linearGradient id="sky" x1="0" y1="0" x2="0" y2="${hz}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#020617"/><stop offset=".55" stop-color="#06173a"/><stop offset="1" stop-color="#0f3550"/></linearGradient>
  <linearGradient id="g1" x1="0" y1="150" x2="0" y2="800" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#b14ce8"/><stop offset=".45" stop-color="#3be6c0"/><stop offset="1" stop-color="#2cf28a"/></linearGradient>
  <linearGradient id="g2" x1="0" y1="80" x2="0" y2="560" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#ff5fb4"/><stop offset=".5" stop-color="#6a7cff"/><stop offset="1" stop-color="#36d6d0"/></linearGradient>
  ${curtain(3)}${curtain(9)}${curtain(15)}
  <linearGradient id="snow" x1="0" y1="${hz - 320}" x2="0" y2="${hz}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#dfe9f5"/><stop offset=".35" stop-color="#7f97b5"/><stop offset="1" stop-color="#1a2944"/></linearGradient>
  <linearGradient id="snowback" x1="0" y1="${hz - 320}" x2="0" y2="${hz}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#4d6688"/><stop offset="1" stop-color="#1a2d4d"/></linearGradient>
  <filter id="relief" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.004 0.012" numOctaves="5" seed="17" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0.02  0 0 0 0 0.05  0 0 0 0 0.12  3.2 0 0 0 -1.6" result="a"/><feComposite in="a" in2="SourceGraphic" operator="in"/></filter>
  <linearGradient id="ice" x1="0" y1="${hz}" x2="0" y2="${H}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#14304d"/><stop offset="1" stop-color="#060f22"/></linearGradient>
  <filter id="cracks" ${full} color-interpolation-filters="sRGB"><feTurbulence type="turbulence" baseFrequency="0.003 0.012" numOctaves="3" seed="5"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.75  0 0 0 0 0.9  0 0 0 0 1  -9 0 0 0 0.6"/></filter>
  <filter id="refl" ${full}><feGaussianBlur stdDeviation="6 14"/></filter>
  ${fade('reflfade', '#fff', 0.65, 0, hz, H)}
  <mask id="reflmask" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#reflfade)"/></mask>
  ${grainDef}`;
  const lights = band(r1, 230, 'g1', '', 3, 0.9) + band(r2, 150, 'g2', '', 9, 0.6) + band('M -200 760 C 500 560 900 820 1500 640 C 1900 520 2200 700 2600 560', 120, 'g1', '', 15, 0.35);
  const body = `<rect width="${W}" height="${hz}" fill="url(#sky)"/>
${stars(520, 29, hz - 60)}
${lights}
<path d="${back.d}" fill="url(#snowback)"/>
<path d="${hills.d}" fill="url(#snow)"/>
<path d="${hills.d}" filter="url(#relief)" opacity=".7"/>
<rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#ice)"/>
<g mask="url(#reflmask)"><g transform="translate(0 ${2 * hz}) scale(1 -1)" filter="url(#refl)" opacity=".9">${lights}</g></g>
<rect y="${hz}" width="${W}" height="${H - hz}" filter="url(#cracks)" opacity=".12"/>
<rect y="${hz - 1}" width="${W}" height="2" fill="#9fc2e0" opacity=".25"/>
${grain}`;
  return svg(defs, body);
}

// ---------- A cloud of gas and stars ----------
function nebula() {
  const gas = (id, seed, freq, r, g, b, k, o) => `<filter id="${id}" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="${freq}" numOctaves="7" seed="${seed}" result="n"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 ${r}  0 0 0 0 ${g}  0 0 0 0 ${b}  ${k} 0 0 0 ${o}" result="c"/>
    <feComposite in="c" in2="SourceGraphic" operator="in"/></filter>`;
  const r = rng(91);
  let bright = '';
  for (let i = 0; i < 26; i++) {
    const x = r() * W, y = r() * H, s = 1.5 + r() * 2.5, L = 14 + r() * 34, c = ['#ffffff', '#cfe0ff', '#ffe6c8'][Math.floor(r() * 3)];
    bright += `<g opacity="${f(0.6 + r() * 0.4)}"><circle cx="${f(x)}" cy="${f(y)}" r="${f(s * 5)}" fill="url(#halo)"/><path d="M ${f(x - L)} ${f(y)} H ${f(x + L)} M ${f(x)} ${f(y - L)} V ${f(y + L)}" stroke="${c}" stroke-width=".9" opacity=".7"/><circle cx="${f(x)}" cy="${f(y)}" r="${f(s)}" fill="${c}"/></g>`;
  }
  const defs = `<radialGradient id="space" cx="1300" cy="700" r="1700" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#140c2e"/><stop offset=".6" stop-color="#080618"/><stop offset="1" stop-color="#02020a"/></radialGradient>
  <radialGradient id="shape" cx="1350" cy="720" r="1150" gradientUnits="userSpaceOnUse" gradientTransform="rotate(-24 1350 720) translate(1350 720) scale(1 .55) translate(-1350 -720)"><stop offset="0" stop-color="#fff"/><stop offset=".55" stop-color="#fff" stop-opacity=".6"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>
  <radialGradient id="core" cx="1420" cy="690" r="620" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fff2e0" stop-opacity=".85"/><stop offset=".25" stop-color="#ffb08a" stop-opacity=".35"/><stop offset="1" stop-color="#ff6aa0" stop-opacity="0"/></radialGradient>
  <radialGradient id="halo"><stop offset="0" stop-color="#fff" stop-opacity=".5"/><stop offset="1" stop-color="#9fb8ff" stop-opacity="0"/></radialGradient>
  ${gas('magenta', 4, '0.0011', 0.95, 0.3, 0.7, 3.6, -1.2)}
  ${gas('blue', 11, '0.0014', 0.3, 0.5, 1, 3.8, -1.4)}
  ${gas('teal', 23, '0.002', 0.3, 0.95, 0.88, 4, -1.85)}
  ${gas('gold', 37, '0.0024', 1, 0.66, 0.36, 4, -2.3)}
  ${gas('dust', 51, '0.0018', 0.01, 0.0, 0.03, 4.4, -2.25)}
  <mask id="cloud" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><rect width="${W}" height="${H}" fill="url(#shape)"/></mask>
  ${grainDef}`;
  const body = `<rect width="${W}" height="${H}" fill="url(#space)"/>
${stars(900, 7, H)}
<g mask="url(#cloud)">
  <rect width="${W}" height="${H}" fill="#fff" filter="url(#blue)" opacity=".85"/>
  <rect width="${W}" height="${H}" fill="#fff" filter="url(#magenta)" opacity=".9"/>
  <rect width="${W}" height="${H}" fill="#fff" filter="url(#teal)" opacity=".55"/>
  <rect width="${W}" height="${H}" fill="#fff" filter="url(#gold)" opacity=".5"/>
  <rect width="${W}" height="${H}" fill="url(#core)"/>
  <rect width="${W}" height="${H}" fill="#fff" filter="url(#dust)" opacity=".5"/>
</g>
${stars(240, 19, H)}
${bright}
${grain}`;
  return svg(defs, body);
}

// ---------- Poured paint ----------
function marble() {
  // The contour lines of a field: a diagonal ramp plus a slow noise, its value mapped to a palette that repeats. Where the noise bends the ramp,
  // the bands swirl. (Bending a picture of bands with feDisplacementMap instead made furry edges: the map is 8 bit, at a large scale every step jumps.)
  const palette = [[246, 238, 230], [244, 166, 198], [255, 255, 255], [127, 214, 216], [58, 155, 181], [246, 238, 230], [199, 166, 240], [251, 224, 168], [233, 139, 184], [159, 224, 208]];
  const seq = [];
  for (let k = 0; k < 3; k++) palette.forEach(c => { seq.push(c, c); });
  seq.push(palette[0]);
  const ch = i => seq.map(c => f(c[i] / 255 * 1000) / 1000).join(' ');
  const defs = `<linearGradient id="ramp" x1="0" y1="0" x2="${W}" y2="${H}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#000"/><stop offset="1" stop-color="#fff"/></linearGradient>
  <filter id="paint" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0008" numOctaves="2" seed="14" result="n"/>
    <feComposite in="SourceGraphic" in2="n" operator="arithmetic" k2=".72" k3=".55" k4="-.16" result="v"/>
    <feColorMatrix in="v" type="matrix" values="1 0 0 0 0  1 0 0 0 0  1 0 0 0 0  0 0 0 0 1" result="g"/>
    <feComponentTransfer in="g"><feFuncR type="table" tableValues="${ch(0)}"/><feFuncG type="table" tableValues="${ch(1)}"/><feFuncB type="table" tableValues="${ch(2)}"/></feComponentTransfer><feGaussianBlur stdDeviation="7"/></filter>
  <filter id="sheen" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0009" numOctaves="4" seed="14"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  2 0 0 0 -1.1"/><feGaussianBlur stdDeviation="40"/></filter>
  ${grainDef}`;
  const body = `<rect width="${W}" height="${H}" fill="url(#ramp)" filter="url(#paint)"/>
<rect width="${W}" height="${H}" filter="url(#sheen)" opacity=".25"/>
${grain}`;
  return svg(defs, body);
}

module.exports = { names: ['dawn', 'coast', 'dunes', 'city', 'forest', 'aurora', 'nebula', 'marble'] };
if (require.main === module) {
  for (const [name, make] of [['dawn', dawn], ['coast', coast], ['dunes', dunes], ['city', city], ['forest', forest], ['aurora', aurora], ['nebula', nebula], ['marble', marble]]) {
    if (process.argv[3] && !process.argv.slice(3).includes(name)) continue;
    fs.writeFileSync(path.join(OUT, name + '.svg'), make());
    console.log(name, Math.round(fs.statSync(path.join(OUT, name + '.svg')).size / 1024), 'KB');
  }
}
