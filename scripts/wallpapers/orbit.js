// Draws the "Orbit" wallpaper of MatOS (a planet with rings), a dark and a light variant, as SVG.
// node orbit.js <out folder> (render.js calls it)
// The small things (the moon, the galaxy, the bright star, the streak) sit at the far left and right, outside the middle 1120 px where the widgets
// are: half hidden behind a widget, a small round thing reads as a broken part of the screen, not as a picture. Only the planet is large enough to sit behind them.
const fs = require('fs');
const path = require('path');
const OUT = process.argv[2];

function rng(seed) {
  let s = seed;
  return () => { s = (s * 16807) % 2147483647; return s / 2147483647; };
}
const f = n => Math.round(n * 10) / 10;

const W = 2400, H = 1500;           // 16:10, cut to the screen with "slice"
const PX = 1680, PY = 1080, PR = 470; // the planet
const TILT = -16;

function stars(p, seed) {
  const r = rng(seed);
  let out = '';
  // a faint band of the Milky Way: more stars along a diagonal
  const band = (x) => 260 + x * 0.28;
  for (let i = 0; i < p.tiny; i++) {
    const x = r() * W;
    const inBand = r() < 0.45;
    const y = inBand ? band(x) + (r() - 0.5) * 380 : r() * H;
    const c = p.colors[Math.floor(r() * p.colors.length)];
    out += `<circle cx="${f(x)}" cy="${f(y)}" r="${f(0.5 + r() * 0.8)}" fill="${c}" opacity="${f(p.op * (0.35 + r() * 0.6))}"/>`;
  }
  for (let i = 0; i < p.medium; i++) {
    const c = p.colors[Math.floor(r() * p.colors.length)];
    out += `<circle cx="${f(r() * W)}" cy="${f(r() * H * 0.85)}" r="${f(1.3 + r() * 1.1)}" fill="${c}" opacity="${f(p.op * (0.6 + r() * 0.4))}"/>`;
  }
  return out;
}

function sparkle(x, y, s, color, op) {
  // a bright star: a soft glow and four thin rays
  return `<g transform="translate(${x} ${y})" opacity="${op}">
    <circle r="${s * 3.2}" fill="url(#glow)"/>
    <path d="M0 ${-s * 5}Q${s * 0.35} ${-s * 0.35} ${s * 5} 0Q${s * 0.35} ${s * 0.35} 0 ${s * 5}Q${-s * 0.35} ${s * 0.35} ${-s * 5} 0Q${-s * 0.35} ${-s * 0.35} 0 ${-s * 5}Z" fill="${color}"/>
    <circle r="${s * 0.7}" fill="#fff"/></g>`;
}

function ringArcs(rings, front) {
  // the half in front of the planet is the lower one (sweep 0), the half behind it the upper one
  return rings.map(([rx, w, color, op]) => {
    const ry = rx * 0.19;
    return `<path d="M ${-rx} 0 A ${rx} ${ry} 0 0 ${front ? 0 : 1} ${rx} 0" fill="none" stroke="${color}" stroke-width="${w}" opacity="${op}"/>`;
  }).join('');
}

function planetBands(t, seed) {
  const r = rng(seed);
  let out = '';
  let y = -PR;
  while (y < PR) {
    const h = 18 + r() * 60;
    const c = t.bands[Math.floor(r() * t.bands.length)];
    out += `<rect x="${-PR - 40}" y="${f(y)}" width="${PR * 2 + 80}" height="${f(h)}" fill="${c}" opacity="${f(0.12 + r() * 0.3)}"/>`;
    y += h + r() * 14;
  }
  // two wavy bands
  for (const [yy, amp, c, op] of [[-120, 14, t.bands[0], 0.35], [90, 10, t.bands[2], 0.3]]) {
    let d = `M ${-PR - 40} ${yy}`;
    for (let x = -PR - 40; x <= PR + 40; x += 60) { d += ` Q ${x + 30} ${yy + (r() > 0.5 ? amp : -amp)} ${x + 60} ${yy}`; }
    d += ` L ${PR + 40} ${yy + 34} L ${-PR - 40} ${yy + 34} Z`;
    out += `<path d="${d}" fill="${c}" opacity="${op}"/>`;
  }
  return out;
}

function svg(t) {
  const rings = t.rings;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">
<defs>
  <linearGradient id="sky" x1="0" y1="0" x2="0.35" y2="1">${t.sky.map(([o, c]) => `<stop offset="${o}" stop-color="${c}"/>`).join('')}</linearGradient>
  <radialGradient id="glow"><stop offset="0" stop-color="${t.glow}" stop-opacity=".55"/><stop offset="1" stop-color="${t.glow}" stop-opacity="0"/></radialGradient>
  <radialGradient id="halo" cx=".5" cy=".5" r=".5"><stop offset=".55" stop-color="${t.halo}" stop-opacity="${t.haloOp}"/><stop offset="1" stop-color="${t.halo}" stop-opacity="0"/></radialGradient>
  <radialGradient id="body" cx=".3" cy=".26" r=".9">${t.body.map(([o, c]) => `<stop offset="${o}" stop-color="${c}"/>`).join('')}</radialGradient>
  <radialGradient id="night" cx=".78" cy=".86" r=".95"><stop offset="0" stop-color="${t.night}" stop-opacity="${t.nightOp}"/><stop offset=".55" stop-color="${t.night}" stop-opacity="${t.nightOp * 0.55}"/><stop offset="1" stop-color="${t.night}" stop-opacity="0"/></radialGradient>
  <radialGradient id="moon" cx=".35" cy=".3" r=".8"><stop offset="0" stop-color="${t.moon[0]}"/><stop offset="1" stop-color="${t.moon[1]}"/></radialGradient>
  <radialGradient id="galaxy"><stop offset="0" stop-color="${t.galaxy}" stop-opacity=".9"/><stop offset=".25" stop-color="${t.galaxy}" stop-opacity=".35"/><stop offset="1" stop-color="${t.galaxy}" stop-opacity="0"/></radialGradient>
  <linearGradient id="streak" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${t.streak}" stop-opacity="0"/><stop offset="1" stop-color="${t.streak}" stop-opacity=".9"/></linearGradient>
  <filter id="blur-xl" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="90"/></filter>
  <filter id="blur-m" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="14"/></filter>
  <filter id="blur-s"><feGaussianBlur stdDeviation="3"/></filter>
  <clipPath id="disc"><circle r="${PR}"/></clipPath>
  <filter id="streaks" x="${-PR}" y="${-PR}" width="${PR * 2}" height="${PR * 2}" filterUnits="userSpaceOnUse" color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0022 0.03" numOctaves="5" seed="3"/>
    <feColorMatrix type="matrix" values="0 0 0 0 ${t.streakLight[0]}  0 0 0 0 ${t.streakLight[1]}  0 0 0 0 ${t.streakLight[2]}  3 0 0 0 -1.45"/></filter>
  <filter id="streaks2" x="${-PR}" y="${-PR}" width="${PR * 2}" height="${PR * 2}" filterUnits="userSpaceOnUse" color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0016 0.022" numOctaves="4" seed="17"/>
    <feColorMatrix type="matrix" values="0 0 0 0 ${t.streakDark[0]}  0 0 0 0 ${t.streakDark[1]}  0 0 0 0 ${t.streakDark[2]}  3 0 0 0 -1.5"/></filter>
  <filter id="gas" x="0" y="0" width="${W}" height="${H}" filterUnits="userSpaceOnUse" color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0016" numOctaves="6" seed="9"/>
    <feColorMatrix type="matrix" values="0 0 0 0 ${t.gas[0]}  0 0 0 0 ${t.gas[1]}  0 0 0 0 ${t.gas[2]}  2.6 0 0 0 -1.2"/></filter>
</defs>
<rect width="${W}" height="${H}" fill="url(#sky)"/>
<g filter="url(#blur-xl)">${t.nebula.map(([x, y, rx, ry, c, op]) => `<ellipse cx="${x}" cy="${y}" rx="${rx}" ry="${ry}" fill="${c}" opacity="${op}"/>`).join('')}</g>
<ellipse cx="1200" cy="600" rx="1500" ry="150" transform="rotate(16 1200 600)" fill="${t.milky}" opacity="${t.milkyOp}" filter="url(#blur-xl)"/>
<rect width="${W}" height="${H}" filter="url(#gas)" opacity="${t.gasOp}"/>
${stars(t.stars, 11)}
<g transform="translate(130 330) rotate(-28)"><ellipse rx="90" ry="26" fill="url(#galaxy)" filter="url(#blur-s)"/><ellipse rx="40" ry="12" fill="url(#galaxy)"/></g>
${t.sparkles.map(([x, y, s, op]) => sparkle(x, y, s, t.sparkle, op)).join('')}
<line x1="2150" y1="240" x2="2380" y2="158" stroke="url(#streak)" stroke-width="2.5" stroke-linecap="round"/>
<circle cx="${PX}" cy="${PY}" r="${PR * 1.45}" fill="url(#halo)"/>
<g transform="translate(${PX} ${PY}) rotate(${TILT})">
  <g filter="url(#blur-s)">${ringArcs(rings, false)}</g>
  <g clip-path="url(#disc)">
    <circle r="${PR}" fill="url(#body)"/>
    <g filter="url(#blur-m)">${planetBands(t, 5)}</g>
    <rect x="${-PR}" y="${-PR}" width="${PR * 2}" height="${PR * 2}" filter="url(#streaks2)" opacity=".55"/>
    <rect x="${-PR}" y="${-PR}" width="${PR * 2}" height="${PR * 2}" filter="url(#streaks)" opacity=".6"/>
    <ellipse cx="-150" cy="40" rx="62" ry="26" fill="${t.storm}" opacity=".55" filter="url(#blur-s)"/>
    <ellipse cx="-150" cy="40" rx="30" ry="11" fill="${t.storm}" opacity=".5"/>
    <path d="M ${-PR * 1.6} 64 A ${PR * 1.6} ${PR * 1.6 * 0.19} 0 0 0 ${PR * 1.6} 64" fill="none" stroke="${t.night}" stroke-width="34" opacity="${t.ringShadow}" filter="url(#blur-m)"/>
    <circle r="${PR}" fill="url(#night)"/>
  </g>
  <circle r="${PR - 3}" fill="none" stroke="${t.rim}" stroke-width="7" opacity="${t.rimOp}" filter="url(#blur-s)" stroke-dasharray="${f(PR * Math.PI * 0.95)} ${f(PR * Math.PI * 1.05)}" transform="rotate(150)"/>
  <g filter="url(#blur-s)">${ringArcs(rings, true)}</g>
  <circle cx="${PR * 1.78}" cy="-26" r="16" fill="url(#moon)"/>
</g>
<g transform="translate(150 1080)">
  <circle r="58" fill="url(#halo)" opacity=".6"/>
  <circle r="44" fill="url(#moon)"/>
  <circle cx="-12" cy="-10" r="8" fill="${t.crater}" opacity=".35"/><circle cx="14" cy="8" r="5" fill="${t.crater}" opacity=".3"/><circle cx="-4" cy="18" r="4" fill="${t.crater}" opacity=".3"/><circle cx="18" cy="-16" r="3" fill="${t.crater}" opacity=".3"/>
  <circle r="44" fill="url(#night)"/>
</g>
</svg>`;
}

const dark = {
  sky: [[0, '#05040f'], [0.4, '#0f0c2c'], [0.75, '#22184e'], [1, '#3a2266']],
  nebula: [[380, 380, 560, 320, '#5a3fd2', 0.32], [900, 900, 520, 300, '#b0479e', 0.16], [2050, 380, 520, 360, '#2b6cd6', 0.2], [1700, 1100, 760, 520, '#6d5cff', 0.22]],
  milky: '#c9c2ff', milkyOp: 0.07,
  stars: { tiny: 520, medium: 70, op: 1, colors: ['#ffffff', '#d7deff', '#ffe9d8', '#e4dcff'] },
  sparkles: [[120, 210, 3.2, 0.95], [1460, 170, 2.6, 0.85], [2210, 610, 2.8, 0.8], [760, 1180, 2.2, 0.7], [1180, 820, 1.8, 0.6], [2000, 120, 2, 0.7]],
  sparkle: '#e9e5ff', glow: '#a99dff', streak: '#ffffff', galaxy: '#d9d2ff',
  halo: '#7c6cff', haloOp: 0.3,
  body: [[0, '#cfc6ff'], [0.35, '#8c7af0'], [0.7, '#4c39c2'], [1, '#1d1460']],
  bands: ['#a99dff', '#3b2aa6', '#e2dcff', '#6150d8'],
  storm: '#f0b6ff', night: '#06051a', nightOp: 0.9, ringShadow: 0.55,
  rim: '#c8bdff', rimOp: 0.6,
  rings: [[600, 10, '#8e82ff', 0.35], [640, 26, '#cfc8ff', 0.5], [672, 8, '#b2a8ff', 0.3], [712, 30, '#a99dff', 0.38], [752, 6, '#d6d0ff', 0.3], [790, 12, '#8e82ff', 0.2], [835, 3, '#cfc8ff', 0.2]],
  moon: ['#e9e6ff', '#7d73c4'], crater: '#4a3f99',
  streakLight: [0.86, 0.83, 1], streakDark: [0.12, 0.08, 0.36], gas: [0.48, 0.36, 1], gasOp: 0.35,
};
const light = {
  sky: [[0, '#eeeaff'], [0.45, '#f3eefe'], [0.8, '#fbeaf1'], [1, '#ffe7da']],
  nebula: [[380, 380, 560, 320, '#c5baff', 0.55], [900, 950, 520, 300, '#ffc7df', 0.4], [2050, 360, 520, 360, '#bcd9ff', 0.45], [1700, 1100, 760, 520, '#d8d0ff', 0.5]],
  milky: '#ffffff', milkyOp: 0.35,
  stars: { tiny: 160, medium: 26, op: 0.55, colors: ['#9e92e8', '#b7acf2', '#d8a8d8'] },
  sparkles: [[120, 210, 3.2, 0.7], [1460, 170, 2.6, 0.6], [2210, 610, 2.8, 0.55], [1180, 820, 1.8, 0.45]],
  sparkle: '#ffffff', glow: '#ffffff', streak: '#ffffff', galaxy: '#a99cf0',
  halo: '#b8acff', haloOp: 0.35,
  body: [[0, '#f1edff'], [0.35, '#b6a9ff'], [0.7, '#7a67e4'], [1, '#4a38bd']],
  bands: ['#ffffff', '#8a7ae8', '#ede8ff', '#a596f3'],
  storm: '#ffd1f0', night: '#4d3db8', nightOp: 0.5, ringShadow: 0.3,
  rim: '#ffffff', rimOp: 0.45,
  rings: [[600, 10, '#8f80e8', 0.35], [640, 26, '#a79bf0', 0.45], [672, 8, '#8f80e8', 0.3], [712, 30, '#b5aaf4', 0.45], [752, 6, '#8f80e8', 0.3], [790, 12, '#a79bf0', 0.25], [835, 3, '#8f80e8', 0.25]],
  moon: ['#ffffff', '#c9c0f5'], crater: '#a497e6',
  streakLight: [1, 1, 1], streakDark: [0.36, 0.28, 0.78], gas: [0.94, 0.78, 1], gasOp: 0.55,
};

fs.writeFileSync(path.join(OUT, 'orbit-dark.svg'), svg(dark));
fs.writeFileSync(path.join(OUT, 'orbit-light.svg'), svg(light));
for (const n of ['orbit-dark.svg', 'orbit-light.svg']) { console.log(n, fs.statSync(path.join(OUT, n)).size, 'bytes'); }
