// More wallpapers for MatOS, all drawn (nothing downloaded): views from above.
// earth: the Earth from orbit; jungle: a jungle with a river; lagoon: an island with a reef; mountains: a range with snow.
// Natural surfaces come from SVG noise (feTurbulence), lit by feDiffuseLighting where they need relief.
// node views.js <out folder> (render.js calls it)
const fs = require('fs');
const path = require('path');
const OUT = process.argv[2];
const W = 2400, H = 1500;

function rng(seed) { let s = seed; return () => { s = (s * 16807) % 2147483647; return s / 2147483647; }; }
const f = n => Math.round(n * 10) / 10;

function stars(n, seed, maxY) {
  const r = rng(seed);
  let out = '';
  for (let i = 0; i < n; i++) {
    const big = r() < 0.08;
    out += `<circle cx="${f(r() * W)}" cy="${f(r() * maxY)}" r="${f(big ? 1.4 + r() : 0.5 + r() * 0.8)}" fill="${['#fff', '#dce4ff', '#fff1e0'][Math.floor(r() * 3)]}" opacity="${f(0.3 + r() * 0.7)}"/>`;
  }
  return out;
}

// A colour ramp for a grey value: five stops per channel.
const ramp = (r, g, b) => `<feComponentTransfer>
  <feFuncR type="table" tableValues="${r}"/><feFuncG type="table" tableValues="${g}"/><feFuncB type="table" tableValues="${b}"/><feFuncA type="identity"/></feComponentTransfer>`;
// Stretch the red channel of a noise (values gather around 0.5) to 0..1 and copy it to all channels, alpha = 1.
const spread = (k, o) => `<feColorMatrix type="matrix" values="${k} 0 0 0 ${o}  ${k} 0 0 0 ${o}  ${k} 0 0 0 ${o}  0 0 0 0 1"/>`;

const full = `x="0" y="0" width="${W}" height="${H}" filterUnits="userSpaceOnUse"`;

// Clouds: white where the noise is high, and their shadows shifted and dark.
function cloudFilters(freq, seed, cover) {
  return `<filter id="clouds" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="${freq}" numOctaves="5" seed="${seed}"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  ${cover[0]} 0 0 0 ${cover[1]}"/></filter>
  <filter id="cloudshadow" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="${freq}" numOctaves="5" seed="${seed}"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0  0 0 0 0 0.02  0 0 0 0 0.05  ${cover[0]} 0 0 0 ${cover[1]}"/>
    <feGaussianBlur stdDeviation="10"/><feOffset dx="60" dy="80"/>
    <feComponentTransfer><feFuncA type="linear" slope="0.45"/></feComponentTransfer></filter>`;
}
const clouds = () => `<rect width="${W}" height="${H}" filter="url(#cloudshadow)"/><rect width="${W}" height="${H}" filter="url(#clouds)"/>`;

// ---------- The Earth from orbit ----------
function earth() {
  const cx = 1250, cy = 2900, R = 2050;          // the top of the Earth is at y = 850
  const sunX = 420, sunY = cy - Math.sqrt(R * R - (sunX - cx) ** 2) - 6;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">
<defs>
  <linearGradient id="space" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#01020a"/><stop offset=".55" stop-color="#040a1f"/><stop offset="1" stop-color="#0a1a3a"/></linearGradient>
  <radialGradient id="ocean" cx="${cx}" cy="${cy}" r="${R}" gradientUnits="userSpaceOnUse"><stop offset=".8" stop-color="#0c4f8a"/><stop offset=".95" stop-color="#0a3c70"/><stop offset="1" stop-color="#0d2f5c"/></radialGradient>
  <radialGradient id="limb" cx="${cx}" cy="${cy}" r="${R}" gradientUnits="userSpaceOnUse"><stop offset=".88" stop-color="#000814" stop-opacity="0"/><stop offset=".985" stop-color="#7cc3ff" stop-opacity=".18"/><stop offset="1" stop-color="#bfe4ff" stop-opacity=".55"/></radialGradient>
  <radialGradient id="air" cx="${cx}" cy="${cy}" r="${R + 170}" gradientUnits="userSpaceOnUse"><stop offset="${f(R / (R + 170) * 1000) / 1000}" stop-color="#4aa8ff" stop-opacity=".7"/><stop offset="${f((R + 30) / (R + 170) * 1000) / 1000}" stop-color="#2b78e8" stop-opacity=".35"/><stop offset="1" stop-color="#1b3f9a" stop-opacity="0"/></radialGradient>
  <radialGradient id="sun"><stop offset="0" stop-color="#fff"/><stop offset=".12" stop-color="#fffbe8"/><stop offset=".35" stop-color="#ffd27a" stop-opacity=".45"/><stop offset="1" stop-color="#ff9a3c" stop-opacity="0"/></radialGradient>
  <radialGradient id="night" cx="${cx + 1700}" cy="${cy - 1300}" r="${R * 1.1}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#000510" stop-opacity=".85"/><stop offset=".55" stop-color="#000510" stop-opacity=".3"/><stop offset="1" stop-color="#000510" stop-opacity="0"/></radialGradient>
  <clipPath id="globe"><circle cx="${cx}" cy="${cy}" r="${R}"/></clipPath>
  <filter id="land" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0014" numOctaves="7" seed="12" result="n"/>
    <feTurbulence type="fractalNoise" baseFrequency="0.004" numOctaves="4" seed="3" result="biome"/>
    <feColorMatrix in="biome" type="matrix" values="3 0 0 0 -1  3 0 0 0 -1  3 0 0 0 -1  0 0 0 0 1"/>
    ${ramp('0.08 0.12 0.20 0.45 0.72', '0.27 0.36 0.42 0.50 0.60', '0.12 0.14 0.15 0.24 0.40')}
    <feOffset result="color"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  1 0 0 0 0" result="h"/>
    <feDiffuseLighting in="h" surfaceScale="22" diffuseConstant="1.1" lighting-color="#fff" result="light"><feDistantLight azimuth="225" elevation="40"/></feDiffuseLighting>
    <feComposite in="color" in2="light" operator="arithmetic" k1="1.1" k2="0.15" result="lit"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  32 0 0 0 -16.9" result="mask"/>
    <feComposite in="lit" in2="mask" operator="in"/></filter>
  <filter id="shallow" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0014" numOctaves="7" seed="12"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.16  0 0 0 0 0.62  0 0 0 0 0.74  7 0 0 0 -3.55"/>
    <feGaussianBlur stdDeviation="6"/><feComponentTransfer><feFuncA type="linear" slope="0.55"/></feComponentTransfer></filter>
  ${cloudFilters('0.0024', 31, [5.4, -2.8])}
  <filter id="soft"><feGaussianBlur stdDeviation="8"/></filter>
  <filter id="flare" x="-50%" y="-500%" width="200%" height="1100%"><feGaussianBlur stdDeviation="4"/></filter>
</defs>
<rect width="${W}" height="${H}" fill="url(#space)"/>
${stars(420, 17, 980)}
<circle cx="${cx}" cy="${cy}" r="${R + 170}" fill="url(#air)"/>
<g clip-path="url(#globe)">
  <rect width="${W}" height="${H}" fill="url(#ocean)"/>
  <rect width="${W}" height="${H}" filter="url(#shallow)"/>
  <rect width="${W}" height="${H}" filter="url(#land)"/>
  ${clouds()}
  <rect width="${W}" height="${H}" fill="url(#night)"/>
  <rect width="${W}" height="${H}" fill="url(#limb)"/>
</g>
<circle cx="${cx}" cy="${cy}" r="${R + 4}" fill="none" stroke="#9fd6ff" stroke-width="6" opacity=".55" filter="url(#soft)"/>
<circle cx="${sunX}" cy="${sunY}" r="260" fill="url(#sun)"/>
<ellipse cx="${sunX}" cy="${sunY}" rx="700" ry="5" fill="#fff6dc" opacity=".55" filter="url(#flare)"/>
<circle cx="${sunX}" cy="${sunY}" r="16" fill="#fff"/>
<g opacity=".9" transform="translate(1720 380) rotate(-12)"><rect x="-26" y="-3" width="20" height="6" fill="#4a6fa8"/><rect x="6" y="-3" width="20" height="6" fill="#4a6fa8"/><rect x="-6" y="-5" width="12" height="10" rx="2" fill="#d8dde6"/></g>
</svg>`;
}

// ---------- A jungle with a river ----------
function jungle() {
  const river = 'M -100 980 C 220 900 330 700 560 720 C 820 742 760 1020 1040 1060 C 1320 1100 1380 760 1640 700 C 1900 640 2000 860 2200 820 C 2330 794 2400 700 2520 660';
  const creek = 'M 1040 1060 C 1000 1240 1150 1330 1120 1560';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">
<defs>
  <filter id="canopy" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0035" numOctaves="4" seed="7" result="big"/>
    <feColorMatrix in="big" type="matrix" values="3.2 0 0 0 -1.1  3.2 0 0 0 -1.1  3.2 0 0 0 -1.1  0 0 0 0 1"/>
    ${ramp('0.03 0.06 0.10 0.18 0.30', '0.16 0.25 0.33 0.42 0.47', '0.06 0.08 0.09 0.12 0.14')}
    <feOffset result="tone"/>
    <feTurbulence type="fractalNoise" baseFrequency="0.05" numOctaves="3" seed="2" result="crowns"/>
    <feColorMatrix in="crowns" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  1 0 0 0 0" result="h"/>
    <feDiffuseLighting in="h" surfaceScale="9" diffuseConstant="1.15" lighting-color="#fff" result="light"><feDistantLight azimuth="235" elevation="38"/></feDiffuseLighting>
    <feComposite in="tone" in2="light" operator="arithmetic" k1="1.25" k2="0.1"/></filter>
  <filter id="banks" ${full}><feTurbulence type="fractalNoise" baseFrequency="0.012" numOctaves="2" seed="5" result="t"/>
    <feDisplacementMap in="SourceGraphic" in2="t" scale="36" xChannelSelector="R" yChannelSelector="G"/><feGaussianBlur stdDeviation="2"/></filter>
  <filter id="ripples" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.004 0.06" numOctaves="2" seed="9"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  2.2 0 0 0 -1.2"/></filter>
  <filter id="shade"><feGaussianBlur stdDeviation="18"/></filter>
  <clipPath id="water"><path d="${river}" fill="none" stroke="#000" stroke-width="88"/></clipPath>
  ${cloudFilters('0.0022', 44, [4.6, -2.6])}
  <linearGradient id="haze" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#cfe8ff" stop-opacity=".18"/><stop offset=".4" stop-color="#cfe8ff" stop-opacity="0"/></linearGradient>
</defs>
<rect width="${W}" height="${H}" fill="#173a1c"/>
<rect width="${W}" height="${H}" filter="url(#canopy)"/>
<g filter="url(#shade)" opacity=".55"><path d="${river}" fill="none" stroke="#04140a" stroke-width="190"/><path d="${creek}" fill="none" stroke="#04140a" stroke-width="80"/></g>
<g filter="url(#banks)">
  <path d="${river}" fill="none" stroke="#7a6a45" stroke-width="122"/>
  <path d="${creek}" fill="none" stroke="#7a6a45" stroke-width="46"/>
  <path d="${river}" fill="none" stroke="#3f6f5f" stroke-width="96"/>
  <path d="${creek}" fill="none" stroke="#3f6f5f" stroke-width="30"/>
  <path d="${river}" fill="none" stroke="#2f6a64" stroke-width="70"/>
  <path d="${creek}" fill="none" stroke="#33706a" stroke-width="18"/>
  <path d="M 560 720 C 640 728 700 770 720 830" fill="none" stroke="#d6c38f" stroke-width="16" stroke-linecap="round" opacity=".85"/>
  <path d="M 1640 700 C 1560 718 1500 760 1470 820" fill="none" stroke="#d6c38f" stroke-width="12" stroke-linecap="round" opacity=".8"/>
</g>
<g clip-path="url(#water)" opacity=".35"><rect width="${W}" height="${H}" filter="url(#ripples)"/></g>
<g transform="translate(1310 958) rotate(-38)"><path d="M -60 -7 L -8 0 L -60 7" fill="none" stroke="#e9f4ef" stroke-width="2" opacity=".6"/><rect x="-8" y="-3" width="18" height="6" rx="3" fill="#f2efe6"/></g>
${clouds()}
<rect width="${W}" height="${H}" fill="url(#haze)"/>
</svg>`;
}

// ---------- An island with a reef ----------
function lagoon() {
  // the shapes are ellipses that a low noise turns into coasts
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">
<defs>
  <radialGradient id="deep" cx="1500" cy="900" r="1700" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#0b5c8c"/><stop offset=".5" stop-color="#084673"/><stop offset="1" stop-color="#05284a"/></radialGradient>
  <filter id="coast" ${full}><feTurbulence type="fractalNoise" baseFrequency="0.0045" numOctaves="4" seed="8" result="t"/>
    <feDisplacementMap in="SourceGraphic" in2="t" scale="230" xChannelSelector="R" yChannelSelector="G"/></filter>
  <filter id="coastsoft" ${full}><feTurbulence type="fractalNoise" baseFrequency="0.0045" numOctaves="4" seed="8" result="t"/>
    <feDisplacementMap in="SourceGraphic" in2="t" scale="230" xChannelSelector="R" yChannelSelector="G"/><feGaussianBlur stdDeviation="22"/></filter>
  <filter id="surf" ${full}><feTurbulence type="fractalNoise" baseFrequency="0.0045" numOctaves="4" seed="8" result="t"/>
    <feDisplacementMap in="SourceGraphic" in2="t" scale="230" xChannelSelector="R" yChannelSelector="G" result="d"/>
    <feTurbulence type="fractalNoise" baseFrequency="0.03" numOctaves="2" seed="4" result="foam"/>
    <feColorMatrix in="foam" type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  3 0 0 0 -1.2" result="fa"/><feComposite in="d" in2="fa" operator="in"/><feGaussianBlur stdDeviation="1.2"/></filter>
  <filter id="waves" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0015 0.012" numOctaves="3" seed="6"/>
    <feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  2 0 0 0 -1.12"/></filter>
  <filter id="bottom" ${full} color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.012" numOctaves="4" seed="13"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.05  0 0 0 0 0.35  0 0 0 0 0.38  2.6 0 0 0 -1.25"/><feComposite in2="SourceGraphic" operator="in"/></filter>
  <filter id="canopy" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.006" numOctaves="3" seed="2" result="big"/>
    <feColorMatrix in="big" type="matrix" values="3 0 0 0 -1  3 0 0 0 -1  3 0 0 0 -1  0 0 0 0 1"/>
    ${ramp('0.08 0.12 0.18 0.30 0.42', '0.30 0.40 0.48 0.56 0.60', '0.10 0.12 0.14 0.18 0.22')}
    <feOffset result="tone"/>
    <feTurbulence type="fractalNoise" baseFrequency="0.06" numOctaves="2" seed="5" result="crowns"/>
    <feColorMatrix in="crowns" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  1 0 0 0 0" result="h"/>
    <feDiffuseLighting in="h" surfaceScale="7" diffuseConstant="1.1" lighting-color="#fff" result="light"><feDistantLight azimuth="235" elevation="40"/></feDiffuseLighting>
    <feComposite in="tone" in2="light" operator="arithmetic" k1="1.2" k2="0.12" result="lit"/>
    <feComposite in="lit" in2="SourceGraphic" operator="in"/></filter>
  ${cloudFilters('0.0026', 52, [3.4, -1.9])}
</defs>
<rect width="${W}" height="${H}" fill="url(#deep)"/>
<rect width="${W}" height="${H}" filter="url(#waves)" opacity=".18"/>
<g filter="url(#coastsoft)"><ellipse cx="1560" cy="930" rx="820" ry="470" fill="#1592b0"/></g>
<g filter="url(#coast)">
  <ellipse cx="1560" cy="930" rx="700" ry="390" fill="#27b9c2"/>
  <ellipse cx="1560" cy="930" rx="620" ry="330" fill="#45d3cb"/>
  <ellipse cx="1600" cy="950" rx="470" ry="240" fill="#86e6d6"/>
</g>
<g filter="url(#coast)" opacity=".22"><ellipse cx="1560" cy="930" rx="620" ry="330" fill="#000" filter="url(#bottom)"/></g>
<g filter="url(#surf)" opacity=".8"><ellipse cx="1560" cy="930" rx="700" ry="390" fill="none" stroke="#fff" stroke-width="14"/></g>
<g filter="url(#coast)">
  <ellipse cx="1640" cy="960" rx="360" ry="170" fill="#f3e3b9"/>
  <ellipse cx="1650" cy="955" rx="320" ry="140" fill="#e9d6a4"/>
</g>
<mask id="isle" maskUnits="userSpaceOnUse" x="0" y="0" width="${W}" height="${H}"><g filter="url(#coast)"><ellipse cx="1660" cy="950" rx="290" ry="118" fill="#fff"/></g></mask>
<rect width="${W}" height="${H}" fill="#2f6b34" filter="url(#canopy)" mask="url(#isle)"/>
<g filter="url(#coast)"><ellipse cx="520" cy="360" rx="150" ry="70" fill="#2fb3c0" opacity=".85"/><ellipse cx="520" cy="360" rx="70" ry="26" fill="#f1e1b4"/></g>
<g transform="translate(1020 760) rotate(28)"><path d="M -90 -14 L -10 0 L -90 14" fill="none" stroke="#fff" stroke-width="2.5" opacity=".75"/><path d="M -150 -26 L -10 0 L -150 26" fill="none" stroke="#fff" stroke-width="1.5" opacity=".35"/><rect x="-10" y="-5" width="26" height="10" rx="5" fill="#fff"/></g>
${clouds()}
</svg>`;
}

// ---------- A mountain range with snow ----------
function mountains() {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">
<defs>
  <filter id="relief" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0022" numOctaves="8" seed="21" result="n"/>
    <feColorMatrix in="n" type="matrix" values="2.7 0 0 0 -0.85  2.7 0 0 0 -0.85  2.7 0 0 0 -0.85  0 0 0 0 1" result="hgt"/>
    <feComponentTransfer in="hgt">
      <feFuncR type="table" tableValues="0.13 0.24 0.30 0.42 0.50 0.58 0.93 0.97"/>
      <feFuncG type="table" tableValues="0.42 0.40 0.42 0.44 0.47 0.55 0.95 0.98"/>
      <feFuncB type="table" tableValues="0.48 0.20 0.24 0.36 0.42 0.52 0.98 1.00"/></feComponentTransfer>
    <feOffset result="color"/>
    <feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  1.6 0 0 0 -0.3" result="h"/>
    <feDiffuseLighting in="h" surfaceScale="60" diffuseConstant="1.05" lighting-color="#fff" result="light"><feDistantLight azimuth="225" elevation="32"/></feDiffuseLighting>
    <feComposite in="color" in2="light" operator="arithmetic" k1="1.15" k2="0.12"/></filter>
  <filter id="lakes" ${full} color-interpolation-filters="sRGB">
    <feTurbulence type="fractalNoise" baseFrequency="0.0022" numOctaves="8" seed="21"/>
    <feColorMatrix type="matrix" values="0 0 0 0 0.16  0 0 0 0 0.40  0 0 0 0 0.50  -40 0 0 0 15.9"/><feGaussianBlur stdDeviation="0.8"/></filter>
  ${cloudFilters('0.0028', 61, [3.8, -2.15])}
  <linearGradient id="haze" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#e6f0ff" stop-opacity=".12"/><stop offset="1" stop-color="#0a1830" stop-opacity=".18"/></linearGradient>
</defs>
<rect width="${W}" height="${H}" fill="#3a5a3a"/>
<rect width="${W}" height="${H}" filter="url(#relief)"/>
<rect width="${W}" height="${H}" filter="url(#lakes)"/>
${clouds()}
<rect width="${W}" height="${H}" fill="url(#haze)"/>
</svg>`;
}

for (const [name, make] of [['earth', earth], ['jungle', jungle], ['lagoon', lagoon], ['mountains', mountains]]) {
  fs.writeFileSync(path.join(OUT, name + '.svg'), make());
  console.log(name, fs.statSync(path.join(OUT, name + '.svg')).size, 'bytes');
}
