// STUDIO NAV, 3D MAP: the colours.
//
// Every cube and sector on the 3D map is coloured from one strip of 256 colours, cold to hot (the "ramp"). The
// strip is made here from a few colours (a preset, or the player's own) and goes to the graphics card once;
// changing colours never rebuilds the shapes. The same strip draws the legend, so the legend can never disagree
// with the map. The page turns each number into a place on the strip with map() and hands the scene only that
// place: 0 is cold, 1 is hot, and two values lie off the strip:
//   -1  nothing known there: no fill, only the outline ("Show empty cubes")
//   -2  grey: too few systems to judge a share, or a colouring that needs a look inside first
//
// Colours are blended in OKLab (Björn Ottosson's colour space, 2020), where a step looks the same size anywhere
// along the strip and the middle of two colours does not turn muddy, as it does when red, green and blue are
// simply averaged. Counts go by their size (a log scale), because a cube can hold one system or ten thousand
// and a sector a million: on a straight scale everything but the busiest would look the same cold colour.
//
// The page loads it with <script src="heat-colour.js"></script> and uses HeatColour.*:
//   const strip = HeatColour.ramp(HeatColour.presets.heat.stops, reverse)    256 x 1 colours, 4 bytes each
//   const scale = HeatColour.scaleFor('known', 160)                          how numbers go onto the strip
//   cube.value = HeatColour.map(cube.known, scale)                           0..1, or -1 when none is known
//   HeatColour.legend(canvas, strip, scale)                                  the strip with its numbers
// ("var" on purpose, as in studio.js: it makes HeatColour reachable as window.HeatColour.)
var HeatColour = (() => {
'use strict';

const EMPTY = -1, GREY = -2;
// The grey of "too few to judge": plainly no colour of any preset, and light enough to see on the dark page.
// heat-2d.js paints the same grey (104, 114, 128), so the legend's square matches the slice.
const GREY_COLOUR = '#687280';
// The page's own background (studio.css --bg), which the coldest colour of a preset must stand out from.
const BACKGROUND = '#06080c';

// ---------------------------------------------------------------------------------------------------------------
// The presets, cold to hot, each colour an even step apart.
const preset = (name, stops, about) => Object.freeze({ name, stops: Object.freeze(stops), about });
const presets = Object.freeze({
  // After matplotlib's Inferno (by Nathaniel Smith and Stéfan van der Walt, CC0): it reads in the right order
  // in shades of grey and for colour-blind eyes too. Its darkest fifth is left out, being nearly the page's own
  // black: the coldest cube would vanish.
  heat: preset('Heat', ['#420a68', '#6a176e', '#932667', '#bc3754', '#dd513a', '#f37819', '#fca50a', '#f6d746', '#fcffa4'],
    'Dark purple for few, through red and orange, to pale yellow for many.'),
  ice: preset('Ice to Fire', ['#2448c8', '#22c4e0', '#f5e04a', '#e0322a'],
    'Blue for few, through cyan and yellow, to red for many.'),
  // The ten colours of the ED3D galaxy map's heat map (Guillaume Perez, MIT licence), cool blue-green to red.
  spectral: preset('Spectral', ['#3f98b4', '#6bc4a5', '#a5dba3', '#d9ef98', '#f7fdb1', '#fef5b0', '#fdd580', '#fda95e', '#f57345', '#de4949'],
    'Blue-green for few, through pale yellow, to red for many.'),
  // Studio's own orange (studio.css --accent), as on the flat map, rising out of the dark.
  elite: preset('Elite orange', ['#4a250c', '#a44a08', '#ff7100'],
    'Dark brown for few, to Studio orange for many.'),
  // The player's own: two to five colours from colour pickers. These are only where the pickers start.
  custom: Object.freeze({ name: 'Custom', stops: Object.freeze(['#2a3a6e', '#ff7100', '#fff0d6']), min: 2, max: 5,
    about: 'Your own colours, two to five, cold to hot.' }),
});

// ---------------------------------------------------------------------------------------------------------------
// Colour arithmetic.

const clamp = (v, lo, hi) => v < lo ? lo : v > hi ? hi : v;

// "#ff7100" (or "ff7100", "#f70") to [255, 113, 0]; null for anything else.
function parse(text) {
  let s = String(text == null ? '' : text).trim().replace(/^#/, '');
  if (/^[0-9a-f]{3}$/i.test(s)) s = s.replace(/./g, ch => ch + ch);
  if (!/^[0-9a-f]{6}$/i.test(s)) return null;
  return [0, 2, 4].map(at => parseInt(s.slice(at, at + 2), 16));
}
// [255, 113, 0] to "#ff7100".
const hex = rgb => '#' + rgb.map(v => clamp(Math.round(v), 0, 255).toString(16).padStart(2, '0')).join('');

// Screen colours are not proportional to light; OKLab is worked out from the light itself, so undo that first.
const toLight = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
const fromLight = v => 255 * (v <= 0.0031308 ? 12.92 * v : 1.055 * v ** (1 / 2.4) - 0.055);

// [r, g, b] (0..255) to [L, a, b] in OKLab: L is how light (0 black, 1 white), a and b the hue and how strong it is.
function oklab(rgb) {
  const [r, g, b] = rgb.map(toLight);
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  return [0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
          1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
          0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s];
}
// And back, to [r, g, b] (0..255). A blend can fall a little outside what a screen shows; it is cut to the nearest.
function fromOklab([L, A, B]) {
  const l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3;
  const m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3;
  const s = (L - 0.0894841775 * A - 1.2914855480 * B) ** 3;
  return [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
          -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
          -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s].map(v => clamp(fromLight(clamp(v, 0, 1)), 0, 255));
}

// The colours of a ramp, as [r, g, b] lists, from whatever the page has: a list of "#rrggbb" (or [r, g, b]), a
// preset ({ stops }) or a preset's key ("heat") or name ("Ice to Fire"). Colours that cannot be read are left
// out; with none left, Heat.
function colours(stops) {
  if (typeof stops === 'string') {
    const key = stops.trim().toLowerCase();
    const found = presets[key] || Object.values(presets).find(p => p.name.toLowerCase() === key);
    stops = found ? found.stops : [stops];
  }
  if (stops && !Array.isArray(stops) && stops.stops) stops = stops.stops;
  const list = (Array.isArray(stops) ? stops : []).map(c => Array.isArray(c) ? (c.length >= 3 && c.slice(0, 3).every(Number.isFinite) ? c.slice(0, 3) : null) : parse(c))
    .filter(Boolean);
  return list.length ? list : presets.heat.stops.map(parse);
}

// The colour a fraction t (0..1) of the way along the stops, blended in OKLab: [r, g, b], 0..255, not rounded.
function blendLab(labs, t) {
  if (labs.length === 1) return fromOklab(labs[0]);
  const at = clamp(t, 0, 1) * (labs.length - 1), k = Math.min(labs.length - 2, Math.floor(at)), f = at - k;
  return fromOklab(labs[k].map((v, n) => v + (labs[k + 1][n] - v) * f));
}
const blend = (stops, t) => blendLab(colours(stops).map(oklab), t).map(Math.round);

// The strip: 256 colours from cold (first) to hot (last), 4 bytes each (red, green, blue, and 255 for opaque),
// 1,024 bytes in all, ready to be a 256 x 1 picture for the graphics card or the legend. reverse: hot and cold
// change places. stops: as colours() above; { stops, reverse } works too.
function ramp(stops, reverse) {
  if (stops && !Array.isArray(stops) && typeof stops === 'object' && reverse == null) reverse = stops.reverse;
  const labs = colours(stops).map(oklab);
  const bytes = new Uint8Array(1024);
  for (let i = 0; i < 256; i++) {
    const rgb = blendLab(labs, reverse ? 1 - i / 255 : i / 255);
    bytes[i * 4] = Math.round(rgb[0]);
    bytes[i * 4 + 1] = Math.round(rgb[1]);
    bytes[i * 4 + 2] = Math.round(rgb[2]);
    bytes[i * 4 + 3] = 255;
  }
  return bytes;
}
// A ramp from whatever the caller passed: the bytes themselves, or anything ramp() takes.
const bytesOf = r => r instanceof Uint8Array && r.length >= 1024 ? r : ramp(r);

// The colour for a place on the strip as CSS ("rgb(252 165 10)"), for drawing in 2D: null for -1 (no fill), the grey
// for -2. alpha (0..1) adds see-through.
function css(rampBytes, t, alpha) {
  if (t === EMPTY || t == null || Number.isNaN(t)) return null;
  const a = alpha == null || alpha >= 1 ? '' : ' / ' + Math.round(clamp(alpha, 0, 1) * 1000) / 1000;
  if (t === GREY || t < 0) {
    const [r, g, b] = parse(GREY_COLOUR);
    return 'rgb(' + r + ' ' + g + ' ' + b + a + ')';
  }
  const bytes = bytesOf(rampBytes), i = Math.round(clamp(t, 0, 1) * 255) * 4;
  return 'rgb(' + bytes[i] + ' ' + bytes[i + 1] + ' ' + bytes[i + 2] + a + ')';
}

// ---------------------------------------------------------------------------------------------------------------
// Scales: how a number becomes a place on the strip.
//   { kind: 'log', min, max }   counts of systems. min and below is the coldest colour, max and above the hottest.
//   { kind: 'linear', min, max, percent, fewest }   shares, as fractions 0..1 (shown as 0 % to 100 %). A share
//                               taken over fewer than `fewest` systems is grey: one recorded system out of one is
//                               not "100 % recorded" in any sense that helps.
// Each level keeps one fixed scale wherever the view goes, so a colour means the same number everywhere.

// The count that is hottest for a cube: 10,000 for a 160 ly cube, the most a single count can say (Spansh stops
// counting there), and in proportion to the cube's volume for other widths (1,250 at 80 ly, 80,000 at 320 ly).
// Never below 4, so the smallest cubes (10 ly ones inside a nebula hold one or two known systems) keep a few steps.
const byVolume = (at160, cubeLy) => Math.max(4, at160 * ((cubeLy || 160) / 160) ** 3);
const logScale = (min, max, what) => Object.freeze({ kind: 'log', min, max, what });
const shareScale = what => Object.freeze({ kind: 'linear', min: 0, max: 1, percent: true, fewest: 3, what });

const scales = Object.freeze({
  // A whole sector (1,280 ly): EDAstro's count of the systems named after it, 10 to 1,000,000.
  sector: logScale(10, 1e6, 'known systems in a sector'),
  // A 160 ly cube: 1 to 10,000 known systems. Also the scale of counted cubes.
  cube: logScale(1, 1e4, 'known systems in a cube'),
  // A share of a cube's systems, such as how many are fully recorded.
  share: shareScale('share of the systems in a cube'),
});

// The scale for a colouring ("Colour by") and a cube width:
//   'known'        known systems (log; 1,280 ly gives the sector scale)
//   'recorded'     the share fully recorded (0..1)
//   'notRecorded'  systems not fully recorded plus the ones that must exist but no database has (log, as known)
//   'firsts'       your first discoveries (log, 1 to 100 in a 160 ly cube, by volume for other widths)
function scaleFor(metric, cubeLy) {
  const width = cubeLy || 160;
  const key = String(metric || 'known').replace(/[^a-z]/gi, '').toLowerCase();
  if (key === 'recorded' || key === 'share' || key === 'ratio' || key === 'percent') return scales.share;
  if (key === 'sector' || width >= 1280) return scales.sector;
  if (key === 'firsts' || key === 'yourfirsts' || key === 'firstdiscoveries') return logScale(1, byVolume(100, width), 'your first discoveries in a cube');
  if (key === 'notrecorded' || key === 'missing') return logScale(1, byVolume(1e4, width), 'systems in a cube not recorded');
  return width === 160 ? scales.cube : logScale(1, byVolume(1e4, width), 'known systems in a cube');
}

// A scale from whatever the caller has: a scale object ("type" and "from"/"to" are read too), a name that
// scaleFor() knows ("sector", "known", "recorded"..., or "cube80" for a width), or [min, max] (a share scale when
// min is 0, else counts). Anything else: the 160 ly cube scale.
function scale(s, cubeLy) {
  if (typeof s === 'string') {
    const width = s.match(/(\d+)/);
    return scaleFor(s.replace(/\d+/g, ''), width ? +width[1] : cubeLy);
  }
  if (Array.isArray(s) && s.length >= 2) return s[0] > 0 ? logScale(+s[0], +s[1]) : shareScale();
  if (s && typeof s === 'object') {
    const kind = String(s.kind || s.type || 'log').toLowerCase().startsWith('lin') ? 'linear' : 'log';
    const min = +(s.min != null ? s.min : s.from), max = +(s.max != null ? s.max : s.to);
    if (kind === 'linear') {
      const lo = Number.isFinite(min) ? min : 0, hi = Number.isFinite(max) && max !== lo ? max : lo + 1;
      return { kind, min: lo, max: hi, percent: s.percent != null ? !!s.percent : lo === 0 && hi === 1, fewest: s.fewest != null ? +s.fewest : 3, what: s.what };
    }
    const lo = min > 0 ? min : 1, hi = max > lo ? max : lo * 10;
    return { kind, min: lo, max: hi, what: s.what };
  }
  return scaleFor('known', cubeLy);
}

// A number's place on the strip, 0 (cold) to 1 (hot), on the given scale:
//   counts: 0 (or none) gives -1, nothing known; 1 and up go by their size, the ends held at 0 and 1.
//   shares: a fraction 0..1; of = how many systems the share was taken over: none gives -1, fewer than the
//           scale's `fewest` (3) gives -2, too few to judge. Without `of`, only the share itself is looked at.
function map(value, s, of) {
  const sc = s && s.kind && Number.isFinite(s.min) && Number.isFinite(s.max) ? s : scale(s);
  if (sc.kind === 'linear') {
    if (of != null && !(of > 0)) return EMPTY;
    if (of != null && of < sc.fewest) return GREY;
    if (value == null || !Number.isFinite(+value)) return EMPTY;
    return clamp((value - sc.min) / (sc.max - sc.min), 0, 1);
  }
  if (!(value > 0)) return EMPTY;
  return clamp(Math.log(value / sc.min) / Math.log(sc.max / sc.min), 0, 1);
}

// The other way: the number at a place on the strip. For the "hide fewer than / hide more than" sliders, which
// move along the strip but show (and filter by) real numbers.
function unmap(t, s) {
  const sc = scale(s), at = clamp(+t || 0, 0, 1);
  return sc.kind === 'linear' ? sc.min + (sc.max - sc.min) * at : sc.min * (sc.max / sc.min) ** at;
}

// A number short enough for under the strip: 7, 156, 1k, 1.25k, 10k, 1M.
function short(n) {
  const three = v => String(+v.toPrecision(3));
  return n >= 1e6 ? three(n / 1e6) + 'M' : n >= 1000 ? three(n / 1000) + 'k' : String(Math.round(n));
}

// The numbers to mark on the strip: [{ at (0..1), value, label, end }]. Counts at every power of ten, with both
// ends; the top one with "+", since everything above it is the same hottest colour. Shares at 0, 25, 50, 75 and 100 %.
function ticks(s) {
  const sc = scale(s), out = [];
  const add = (value, end) => out.push({ at: map(value, sc), value, end, label: sc.kind === 'linear' && sc.percent ? Math.round(value * 100) + '%' : short(value) });
  if (sc.kind === 'linear') {
    for (let n = 0; n <= 4; n++) add(sc.min + (sc.max - sc.min) * n / 4, n === 0 || n === 4);
    if (!sc.percent) out.forEach(tick => { tick.label = short(tick.value); });
    return out;
  }
  const first = Math.ceil(Math.log10(sc.min) - 1e-9), last = Math.floor(Math.log10(sc.max) + 1e-9);
  if (Math.abs(10 ** first - sc.min) > 1e-9 * sc.min) add(sc.min, true);
  for (let p = first; p <= last; p++) add(10 ** p, p === first && Math.abs(10 ** p - sc.min) <= 1e-9 * sc.min || p === last && Math.abs(10 ** p - sc.max) <= 1e-9 * sc.max);
  if (Math.abs(10 ** last - sc.max) > 1e-9 * sc.max) add(sc.max, true);
  out[out.length - 1].label += '+';
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// The legend: the strip, drawn from the same bytes as the map, with its numbers underneath.
// canvas: sized by the page's CSS (about 260 x 34 is right; it draws as sharp as the screen allows).
// rampBytes: from ramp() (or anything ramp() takes). scale: as map() takes it.
// opts: { empty: true } adds an outlined square "None known"; { few: true } a grey one "Too few to judge"
// (these go on a second line, when the canvas is tall enough: about 50 px).
// Answers where the strip is, { left, right, top, bottom, valueAt(x) } in CSS pixels (valueAt turns a mouse x into
// the number there, for a tooltip), or null when the canvas has no size because the page is not on show:
// call it again when it is.
function legend(canvas, rampBytes, s, opts) {
  opts = opts || {};
  const sc = scale(s), bytes = bytesOf(rampBytes);
  // Not in the page: its own size. In the page but hidden: nothing to draw in.
  const width = canvas.isConnected ? canvas.clientWidth : canvas.width, height = canvas.isConnected ? canvas.clientHeight : canvas.height;
  if (!width || !height) return null;
  const sharp = canvas.isConnected ? window.devicePixelRatio || 1 : 1;
  canvas.width = Math.round(width * sharp);
  canvas.height = Math.round(height * sharp);
  const c = canvas.getContext('2d');
  c.setTransform(sharp, 0, 0, sharp, 0, 0);
  c.clearRect(0, 0, width, height);
  const look = canvas.isConnected ? getComputedStyle(canvas) : null;
  const token = (name, fallback) => (look && look.getPropertyValue(name).trim()) || fallback;
  const ink = token('--muted', '#8d99aa'), edge = token('--line2', '#2a3645'), text = token('--text', '#e2e8f0');
  c.font = '11px "Segoe UI",system-ui,sans-serif';

  // Room at both ends for half of the end numbers, which sit centred under the strip's ends.
  const marks = ticks(sc);
  const widthOf = label => c.measureText(label).width;
  const left = Math.ceil(Math.max(1, widthOf(marks[0].label) / 2)), right = width - Math.ceil(Math.max(1, widthOf(marks[marks.length - 1].label) / 2));
  // Under the strip: 6 px to the numbers and 13 for them; a second line takes 17 more.
  const rows = (opts.empty || opts.few) && height >= 43 ? 2 : 1;
  const top = 1, barHeight = clamp(height - (rows === 2 ? 37 : 20), 6, 14), bottom = top + barHeight;
  if (right - left < 20) return null;

  // The strip itself: the 256 colours as one smooth band, as wide as there is room for.
  const band = c.createLinearGradient(left, 0, right, 0);
  for (let i = 0; i < 256; i++) band.addColorStop(i / 255, 'rgb(' + bytes[i * 4] + ',' + bytes[i * 4 + 1] + ',' + bytes[i * 4 + 2] + ')');
  c.fillStyle = band;
  c.fillRect(left, top, right - left, barHeight);
  c.lineWidth = 1;
  c.strokeStyle = edge;
  c.strokeRect(left - 0.5, top - 0.5, right - left + 1, barHeight + 1);

  // The numbers: the ends first, then the others where they do not crowd a number already placed.
  c.textBaseline = 'top';
  c.textAlign = 'center';
  const placed = [];
  for (const tick of [...marks.filter(m => m.end), ...marks.filter(m => !m.end)]) {
    const x = left + tick.at * (right - left), half = widthOf(tick.label) / 2;
    if (placed.some(([a, b]) => x - half - 6 < b && x + half + 6 > a)) continue;
    placed.push([x - half, x + half]);
    c.strokeStyle = ink;
    c.beginPath();
    c.moveTo(Math.round(x) + 0.5, bottom);
    c.lineTo(Math.round(x) + 0.5, bottom + 4);
    c.stroke();
    c.fillStyle = ink;
    c.fillText(tick.label, x, bottom + 6);
  }

  // The two states off the strip.
  if (rows === 2) {
    let x = left;
    const y = bottom + 24;
    c.textAlign = 'left';
    c.textBaseline = 'middle';
    const item = (label, paint) => {
      paint(x, y);
      c.fillStyle = ink;
      c.fillText(label, x + 16, y + 5);
      x += 16 + widthOf(label) + 18;
    };
    if (opts.empty) item('None known', (px, py) => { c.strokeStyle = text; c.strokeRect(px + 0.5, py + 0.5, 10, 10); });
    if (opts.few) item('Too few to judge', (px, py) => { c.fillStyle = GREY_COLOUR; c.fillRect(px, py, 11, 11); });
  }
  return { left, right, top, bottom, valueAt: x => unmap((x - left) / (right - left), sc) };
}

// ---------------------------------------------------------------------------------------------------------------
// The check, for heatmap.html?selftest: answers [{ name, ok, got, want }], the same shape as HeatGrid.selftest().
function selftest() {
  const results = [];
  const near = (got, want, by) => typeof got === 'number' && Math.abs(got - want) <= by;
  const check = (name, test, want, ok) => {
    let got;
    try { got = test(); } catch (e) { got = 'error: ' + e.message; }
    results.push({ name, ok: ok ? ok(got) : JSON.stringify(got) === JSON.stringify(want), got, want });
  };
  const L = colour => oklab(parse(colour))[0];

  check('A ramp is 256 colours of 4 bytes', () => { const r = ramp('heat'); return [r.length, r[3], r[1023]]; }, [1024, 255, 255]);
  check('Black to white runs from black to white', () => { const r = ramp(['#000000', '#ffffff']); return [...r.subarray(0, 3), ...r.subarray(1020, 1023)]; }, [0, 0, 0, 255, 255, 255]);
  check('Reverse swaps the ends', () => { const r = ramp(['#000000', '#ffffff'], true); return [r[0], r[1020]]; }, [255, 0]);
  check('Every preset starts and ends on its own colours', () => Object.values(presets).every(p => {
    const r = ramp(p.stops), first = parse(p.stops[0]), last = parse(p.stops[p.stops.length - 1]);
    return [0, 1, 2].every(n => Math.abs(r[n] - first[n]) <= 1 && Math.abs(r[1020 + n] - last[n]) <= 1);
  }), true);
  check('OKLab there and back keeps #ff7100', () => hex(fromOklab(oklab(parse('#ff7100')))), '#ff7100');
  check('OKLab of white is 1, 0, 0', () => oklab([255, 255, 255]).map(v => Math.round(v * 1000) / 1000), [1, 0, 0]);
  check('The middle of blue and yellow in OKLab is not muddy grey', () => { const [l, a, b] = oklab(blend(['#0000ff', '#ffff00'], 0.5)); return Math.hypot(a, b); }, '> 0.05', got => got > 0.05);
  check('Heat and Elite orange grow lighter all the way (more reads as brighter, in grey too)', () => ['heat', 'elite'].every(key => {
    const r = ramp(key);
    let last = -1;
    for (let i = 0; i < 256; i += 5) { const l = oklab([r[i * 4], r[i * 4 + 1], r[i * 4 + 2]])[0]; if (l < last - 1e-3) return false; last = l; }
    return true;
  }), true);
  check('The coldest colour of every preset stands out from the page background', () => Object.values(presets).map(p => Math.round((L(p.stops[0]) - L(BACKGROUND)) * 100) / 100),
    'all at least 0.15 lighter', got => Array.isArray(got) && got.every(d => d >= 0.15));
  check('Unreadable colours are skipped; none left gives Heat', () => [colours(['#zzz', '#fff']).length, colours([]).length], [1, presets.heat.stops.length]);

  const cube = scaleFor('known', 160), sector = scaleFor('known', 1280);
  check('160 ly cubes: 1 is cold, 100 halfway, 10,000 hot', () => [map(1, cube), map(100, cube), map(10000, cube), map(50000, cube)], [0, 0.5, 1, 1]);
  check('Sectors: 10 is cold, 1,000 two fifths of the way, 1,000,000 hot', () => [map(10, sector), map(1000, sector), map(1e6, sector)], [0, 0.4, 1]);
  check('Nothing known is -1 (no fill)', () => [map(0, cube), map(null, cube), map(undefined, sector)], [-1, -1, -1]);
  check('80 and 320 ly cubes go by volume', () => [scaleFor('known', 80).max, scaleFor('known', 320).max], [1250, 80000]);
  check('Shares go straight: 0, 50 and 100 %', () => [map(0, scales.share, 10), map(0.5, scales.share, 10), map(1, scales.share, 10)], [0, 0.5, 1]);
  check('A share of fewer than 3 systems is grey, of none is empty', () => [map(1, 'recorded', 2), map(0, 'recorded', 0)], [-2, -1]);
  check('Scales from names and lists', () => [scale('cube80').max, scale('sector').min, scale([0, 1]).kind, scale({ type: 'log', from: 1, to: 1000 }).max], [1250, 10, 'linear', 1000]);
  check('Back from the strip to the number', () => [unmap(0.5, cube), unmap(map(7933, cube), cube)].map(Math.round), [100, 7933]);
  check('Legend numbers for 160 ly cubes', () => ticks(cube).map(t => t.label), ['1', '10', '100', '1k', '10k+']);
  check('Legend numbers for sectors', () => ticks(sector).map(t => t.label), ['10', '100', '1k', '10k', '100k', '1M+']);
  check('Legend numbers for 80 ly cubes end at their own top', () => ticks(scaleFor('known', 80)).map(t => t.label), ['1', '10', '100', '1k', '1.25k+']);
  check('Legend numbers for shares', () => ticks('recorded').map(t => t.label), ['0%', '25%', '50%', '75%', '100%']);
  check('CSS colours: none for empty, grey for too few', () => [css(ramp('heat'), -1), css(ramp('heat'), -2), css(ramp(['#000000', '#ffffff']), 1)],
    [null, 'rgb(104 114 128)', 'rgb(255 255 255)']);
  return results;
}

return { EMPTY, GREY, GREY_COLOUR, presets, ramp, blend, css, parse, hex, oklab, fromOklab,
  scales, scaleFor, scale, map, unmap, ticks, legend, selftest };
})();
