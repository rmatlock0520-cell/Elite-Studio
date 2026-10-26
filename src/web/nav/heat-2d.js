// STUDIO NAV, 3D MAP: the flat pictures that go with it.
//
// Two drawings on plain 2D canvases, for heatmap.html (load it after ../studio.js; it needs nothing else):
//   Heat2D.strip(canvas, block, view, onPickLayer)
//       The side strip, always on show: the galaxy seen from the side (from the south, so east is on the right
//       as on the flat map), with the block's layers stacked as bands. Each band has its fixed name, its height
//       range and EDAstro's count of the systems named after the block's sectors in it, the galaxy's middle line runs across, and a dot
//       shows the commander. It is also the layer picker: a click on a band calls onPickLayer(iy), and with the
//       strip focused the up and down arrow keys do the same (the page marks the canvas data-keeps-arrows="ArrowUp
//       ArrowDown", so only those two stay with it: left, right and Page Up, Page Down still move the highlight).
//   Heat2D.slice(canvas, scene, view, onPick)
//       The chosen layer seen from above, north (towards the core) up and east right, exactly like the flat map:
//       its sectors, the counted cubes and the cubes of a sector looked inside, coloured from the same ramp as
//       the 3D map, nebulae as circles, star dots, the commander and the heading. It stands in for the 3D map
//       when there is no 3D (no WebGL2, a software renderer, too slow, the in-game overlay, ?no3d): the wheel
//       zooms towards the pointer, a drag slides it, a double click goes back to the whole block. Drawn small
//       (view.compact, or a canvas under 200 px) it is the locator inset: no names, no zoom, clicks still pick.
//       When a sector is looked inside (or cubes were counted) in the shown layer, a column of heights on its
//       right shows one height of cubes at a time, because from above the cubes of a sector stand eight deep.
//       A click calls onPick({kind, ...}): see pickAt() below for what each kind carries.
// Calling either again with the same canvas updates it; both answer the same small handle each time
// ({redraw, dispose}, and for the slice also reset, centre, pick, onHover, level).
// Give each canvas a CSS size that does not come from the canvas itself (position:absolute with inset:0, or a
// fixed width and height, and min-width:0 in a flex row): a canvas's own size follows its pixel count, which is
// set here from its size, and on a high-DPI screen it would otherwise keep growing.
//
// What they read:
//   block  the answer of GET /api/nav/heatmap/block: layers [{iy,name,yFrom,yTo,known,here}], sectors (for the
//          footprint), nebulae, player {x,y,z,heading}. Layers 29 to 34 are always shown by their fixed names; a
//          known of null (no sector list on this PC yet) reads "no count yet", not "nothing known".
//   scene  the object heatmap.html gives HeatGL.setScene, in galaxy light years (scene.origin is not needed):
//          sectors [{ix,iy,iz,known,value,name,layerShown,outlineOnly}], area {corner,cubeLy,n,cubes:[{i,j,k,value,label,tip}],
//          blank} with i east, j up, k north (tip: the cube's tooltip in the page's words; blank: the tooltip of a cube not
//          in the list), counted [{x0,y0,z0,size,value,label}], stars [{x,y,z,state}], nebulae
//          [{name,x,y,z,r,spheres:[[x,y,z,r]...],permit}], player {x,y,z,heading}, marker {x,y,z,cube:{x0,y0,z0,size}}.
//          value is already mapped to 0..1 by the page (HeatColour.map); -1 = nothing known, -2 = too few to judge.
//   view   layer (the iy on show), ramp (the 1024 bytes of HeatColour.ramp, so 2D and 3D share their colours),
//          opacity (0.35), look ('glass' | 'glow' | 'solid'), unexploredStandsOut, showEmpty (true),
//          range ([lowest, highest] value to show, 0..1; null = all), compact, chosen (the page's highlight: a box
//          { x0, y0, z0, size, label }, outlined thick over everything, dashed when not at the heights on show, with
//          its label beside it in the big view; null = none). Missing ones take those defaults.
// Heading is in degrees clockwise from north, as everywhere in Studio Nav.
// The side and top locators follow EDAstro's SectorView (CMDR Orvidius), which shows a sector from above and
// from the side next to each other.
var Heat2D = (() => {
'use strict';

// ============================================================ the grid
// The same numbers as heat-grid.js and Galaxy.cs (selftest() compares them). A sector is a 1,280 ly box; the
// corner of sector (ix, iy, iz) is ORIGIN + 1280 * index. East is +x, up is +y, north (towards the core) is +z.
const SECTOR = 1280, ORIGIN = [-49985, -40985, -24105];
const MIDDLE = -21;   // the galaxy's middle, about 4 ly above the seam between layers 31 and 32 (y = -25)
// Fixed names: a layer keeps its name wherever the commander is.
const LAYER_NAMES = { 29: 'Deep below', 30: 'Far below', 31: 'Just below the middle', 32: 'Just above the middle', 33: 'Far above', 34: 'Deep above' };
const layerOf = y => Math.floor((y - ORIGIN[1]) / SECTOR);
const layerFloor = iy => ORIGIN[1] + SECTOR * iy;
// No recorded sector lies outside layers 29 to 34, but a far flight can: those are counted from the middle, in the
// same words as Galaxy.LayerName.
const layerName = iy => LAYER_NAMES[iy] || (iy < 29 ? (32 - iy) + ' layers below the middle' : (iy - 31) + ' layers above the middle');
const west = ix => ORIGIN[0] + SECTOR * ix;
const south = iz => ORIGIN[2] + SECTOR * iz;

// ============================================================ words, numbers, colours
const whole = n => Math.round(n).toLocaleString('en-US');
// A height as players read it on the galaxy map: "+1,255", "−25" (a real minus sign, easier to see than a hyphen).
const height = y => (y > 0.5 ? '+' : y < -0.5 ? '−' : '') + whole(Math.abs(y));
const span = (from, to) => height(from) + ' to ' + height(to) + ' ly';
const dot = ' · ';
const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
const finite = n => typeof n === 'number' && isFinite(n);
// A nebula's spheres as [x, y, z, r]: its own list when it has one (ICZ is 16 spheres spread over some 175 ly), else
// the one sphere round its middle; none for a nebula with no size. It is drawn as all of them.
function spheresOf(n) {
  const list = n && Array.isArray(n.spheres) ? n.spheres.filter(s => Array.isArray(s) && s.length === 4 && s.every(finite) && s[3] > 0) : [];
  if (list.length) return list;
  return n && finite(n.x) && finite(n.y) && finite(n.z) && n.r > 0 ? [[n.x, n.y, n.z, n.r]] : [];
}
// The way a heading points on a map seen from above, as a step across and down the canvas: 0° is north (up the
// canvas), 90° east (right), as on the flat map.
const ahead = degrees => [Math.sin(degrees * Math.PI / 180), -Math.cos(degrees * Math.PI / 180)];

// The page's own colours, from studio.css, so the drawings match the rest of Studio.
function palette(el) {
  const s = getComputedStyle(el);
  const v = (name, fallback) => s.getPropertyValue(name).trim() || fallback;
  return {
    accent: v('--accent', '#ff7100'), soft: v('--accent-soft', 'rgba(255,113,0,.14)'), text: v('--text', '#e2e8f0'),
    muted: v('--muted', '#8d99aa'), faint: v('--faint', '#5b6677'), line: v('--line', '#1f2935'), line2: v('--line2', '#2a3645'),
    bg: v('--bg', '#06080c'), bg2: v('--bg2', '#0a0e14'), good: v('--good', '#3ddc84'),
    font: v('--font', '"Segoe UI","Studio Text",system-ui,sans-serif'), hud: v('--hud', '"Bahnschrift","Studio Heading","Segoe UI","Studio Text",system-ui,sans-serif'),
  };
}
const HALO = 'rgba(6,8,12,.85)';          // a dark edge round text and dots, so they read on bright colours too
const GREY = [104, 114, 128];             // "too few to judge": off the ramp on purpose
const NEBULA = '#7fd4ff';                 // nebulae have their own outline colour, never a heat colour
const STAR = { full: '#f1f5f9', part: '#ffb020', never: '#9aa7b8' };
// Used only when the page gave no ramp: an Inferno-like dark-to-bright run, so the slice still reads.
const FALLBACK_STOPS = [[0, 0, 4], [87, 16, 110], [188, 55, 84], [249, 142, 9], [252, 255, 164]];

// The ramp colour of a value 0..1, from the same 256 colours the 3D map uploads.
function heat(ramp, value) {
  const t = clamp(value, 0, 1);
  if (ramp && ramp.length >= 1024) {
    const at = Math.round(t * 255) * 4;
    return [ramp[at], ramp[at + 1], ramp[at + 2]];
  }
  const s = FALLBACK_STOPS, f = t * (s.length - 1), a = Math.min(s.length - 2, Math.floor(f)), u = f - a;
  return [0, 1, 2].map(n => Math.round(s[a][n] + (s[a + 1][n] - s[a][n]) * u));
}
const rgba = (rgb, alpha) => 'rgba(' + rgb[0] + ',' + rgb[1] + ',' + rgb[2] + ',' + alpha + ')';
// Dark text on a bright fill, light text on a dark one.
const bright = rgb => (0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]) / 255 > 0.6;

// ============================================================ canvas plumbing
// Every canvas this file draws on, with what it last drew. One watcher for all of them: it redraws a canvas
// when its size changes, which also happens when a hidden page comes back on show (hidden, it has no size).
const live = new Map();
let watcher = null;

function plumb(canvas, kind, wire) {
  let st = live.get(canvas);
  if (st && st.kind === kind) return st;
  if (st) st.handle.dispose();
  st = { canvas, kind, queued: false, dirty: false, off: [] };
  live.set(canvas, st);
  if (!watcher) {
    watcher = new ResizeObserver(entries => { for (const e of entries) { const s = live.get(e.target); if (s) later(s); } });
    // A change of display scaling or browser zoom changes the pixel ratio without changing the layout size. Most
    // of those also send 'resize'; moving the window to a screen with other scaling may not, but a media query on
    // the present ratio hears it (and is set again for the new ratio).
    window.addEventListener('resize', () => live.forEach(later));
    const ratioWatch = () => {
      try {
        matchMedia('(resolution: ' + (window.devicePixelRatio || 1) + 'dppx)')
          .addEventListener('change', () => { live.forEach(later); ratioWatch(); }, { once: true });
      } catch (e) { /* an old engine without it: 'resize' still covers most cases */ }
    };
    ratioWatch();
    // Nothing is drawn while the page is hidden (the shell keeps hidden pages alive); catch up when it shows.
    document.addEventListener('visibilitychange', () => { if (!document.hidden) live.forEach(s => { if (s.dirty) later(s); }); });
  }
  watcher.observe(canvas);
  const on = (type, fn, options) => { canvas.addEventListener(type, fn, options); st.off.push(() => canvas.removeEventListener(type, fn, options)); };
  wire(st, on);
  return st;
}

function forget(st) {
  if (live.get(st.canvas) !== st) return;
  live.delete(st.canvas);
  if (watcher) watcher.unobserve(st.canvas);
  st.off.forEach(off => off());
  st.off = [];
}

// Redraw at the next frame, once, however many reasons there are (hover, resize...).
function later(st) {
  if (st.queued) return;
  st.queued = true;
  requestAnimationFrame(() => { st.queued = false; st.draw(); });
}

// Sizes the canvas to its box and the screen's pixels, and answers a context that draws in CSS pixels; null when
// there is nothing to draw in. The box is measured twice on purpose: in the in-game overlay the whole page is
// zoomed, and then the box on screen (getBoundingClientRect) is smaller than its layout size (clientWidth).
function prepare(st) {
  const canvas = st.canvas, w = canvas.clientWidth, h = canvas.clientHeight;
  if (!w || !h || !canvas.isConnected || document.hidden) { st.dirty = true; return null; }
  st.dirty = false;
  const box = canvas.getBoundingClientRect();
  const sharp = Math.min(window.devicePixelRatio || 1, 2) * ((box.width / w) || 1);   // 2x is sharp enough for lines and text
  const bw = Math.max(1, Math.round(w * sharp)), bh = Math.max(1, Math.round(h * sharp));
  if (canvas.width !== bw) canvas.width = bw;
  if (canvas.height !== bh) canvas.height = bh;
  const c = canvas.getContext('2d');
  c.setTransform(bw / w, 0, 0, bh / h, 0, 0);
  c.clearRect(0, 0, w, h);
  c.lineJoin = 'round';
  return { c, w, h };
}

// Where a mouse event is on the canvas, in the CSS pixels the drawing uses (see prepare about the zoom).
function local(canvas, e) {
  const box = canvas.getBoundingClientRect();
  return [(e.clientX - box.left) * canvas.clientWidth / (box.width || 1), (e.clientY - box.top) * canvas.clientHeight / (box.height || 1)];
}

// Text with a dark edge, so it reads on any fill.
function say(c, text, x, y, colour, align) {
  if (!text) return;
  c.textAlign = align || 'left';
  c.lineWidth = 3;
  c.strokeStyle = HALO;
  c.strokeText(text, x, y);
  c.fillStyle = colour;
  c.fillText(text, x, y);
}

// The text cut short with "…" so it fits the room, or '' when not even a letter does.
function fit(c, text, room) {
  if (room <= 8) return '';
  if (c.measureText(text).width <= room) return text;
  let lo = 0, hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (c.measureText(text.slice(0, mid) + '…').width <= room) lo = mid; else hi = mid - 1;
  }
  return lo ? text.slice(0, lo).trimEnd() + '…' : '';
}

// The commander: a white dot with a dark edge, or a ring when they are not in what is drawn.
function you(c, x, y, filled) {
  c.beginPath();
  c.arc(x, y, 4.5, 0, 2 * Math.PI);
  c.lineWidth = 3;
  c.strokeStyle = HALO;
  c.stroke();
  c.lineWidth = 1.5;
  c.strokeStyle = '#fff';
  c.stroke();
  if (filled) { c.fillStyle = '#fff'; c.fill(); }
}

// A small padlock: the nebula needs a permit.
function padlock(c, x, y, colour) {
  c.save();
  c.strokeStyle = colour;
  c.fillStyle = colour;
  c.lineWidth = 1.4;
  c.beginPath();
  c.arc(x + 4, y - 6, 2.6, Math.PI, 0);
  c.stroke();
  c.fillRect(x, y - 6, 8, 6);
  c.restore();
}

// ============================================================ the side strip
// The bands from the highest layer down. Layers between two the block has, but missing from it, still get a
// band, so heights stay in proportion; they simply have nothing known, and cannot be picked (the page has
// nothing to show there). Layers 29 to 34 always carry their fixed names, whatever the block calls them.
function stripBands(block) {
  const given = (block && Array.isArray(block.layers) ? block.layers : []).filter(l => l && Number.isInteger(l.iy));
  if (!given.length) return [];
  const by = new Map(given.map(l => [l.iy, l]));
  const iys = given.map(l => l.iy), bands = [];
  for (let iy = Math.max(...iys); iy >= Math.min(...iys); iy--) {
    const l = by.get(iy) || {};
    bands.push({
      iy, name: LAYER_NAMES[iy] || l.name || layerName(iy),
      yFrom: finite(l.yFrom) ? l.yFrom : layerFloor(iy), yTo: finite(l.yTo) ? l.yTo : layerFloor(iy) + SECTOR,
      // null: no count to give (there is no sector list on this PC yet), which is not the same as none known.
      known: finite(l.known) ? l.known : by.has(iy) ? null : 0, here: !!l.here, missing: !by.has(iy),
    });
  }
  return bands;
}

// A band's count in words. It is EDAstro's count of the systems named after the layer's sectors (catalogue stars and
// the systems inside a nebula are not in it), so it is never called "known"; the long form, for the tooltip, names EDAstro.
const knownWords = (b, long) => b.known == null ? 'no count yet'
  : !b.known ? (long ? 'no systems named after its sectors (EDAstro)' : 'none named after its sectors')
  : whole(b.known) + (long ? ' systems named after its sectors (EDAstro)' : ' named after its sectors');

// The west and east edges of the block, in light years: from its sectors, else the three columns round its centre.
function footprintX(block) {
  const sectors = block && Array.isArray(block.sectors) ? block.sectors.filter(s => Number.isInteger(s.ix)) : [];
  if (sectors.length) return [west(Math.min(...sectors.map(s => s.ix))), west(Math.max(...sectors.map(s => s.ix)) + 1)];
  const ix = block && Array.isArray(block.centre) ? block.centre[0] : null;
  if (Number.isInteger(ix)) return [west(ix - 1), west(ix + 2)];
  const p = block && block.player;
  return p && finite(p.x) ? [p.x - 1.5 * SECTOR, p.x + 1.5 * SECTOR] : [-1.5 * SECTOR, 1.5 * SECTOR];
}

// How the strip lies on the canvas: heights run up the canvas, west to east across it. The two directions have
// their own scales (the strip is a diagram, narrower than the block is wide).
function stripLayout(block, w, h) {
  const bands = stripBands(block);
  if (!bands.length) return null;
  const top = 6, bottom = h - 18, left = 0, right = w;
  const yTop = bands[0].yTo, yBottom = bands[bands.length - 1].yFrom;
  const [xWest, xEast] = footprintX(block);
  const py = y => top + (yTop - y) / (yTop - yBottom) * (bottom - top);
  const px = x => left + (x - xWest) / (xEast - xWest) * (right - left);
  for (const b of bands) { b.top = py(b.yTo); b.bottom = py(b.yFrom); }
  return { bands, top, bottom, left, right, yTop, yBottom, xWest, xEast, py, px, sx: (right - left) / (xEast - xWest), sy: (bottom - top) / (yTop - yBottom) };
}

// The layer on show: the view's, else the commander's, else the block's centre layer (one the block has).
function shownIn(bands, block, view) {
  const real = bands.filter(b => !b.missing), has = iy => real.some(b => b.iy === iy);
  if (view && Number.isInteger(view.layer) && has(view.layer)) return view.layer;
  const here = real.find(b => b.here);
  if (here) return here.iy;
  const p = block && block.player;
  if (p && finite(p.y) && has(layerOf(p.y))) return layerOf(p.y);
  const centre = block && Array.isArray(block.centre) ? block.centre[1] : null;
  return has(centre) ? centre : real[real.length >> 1].iy;
}

function drawStrip(st) {
  const ready = prepare(st);
  if (!ready) return;
  const { c, w, h } = ready, block = st.block, view = st.view || {}, k = palette(st.canvas);
  const lay = st.layout = stripLayout(block, w, h);
  c.fillStyle = k.bg2;
  c.fillRect(0, 0, w, h);
  c.textBaseline = 'top';
  if (!lay) {
    c.font = '12px ' + k.font;
    c.fillStyle = k.muted;
    c.textAlign = 'center';
    c.fillText(fit(c, 'No layers to show yet.', w - 16), w / 2, h / 2 - 6);
    st.canvas.setAttribute('aria-label', 'Layers: none to show yet.');
    return;
  }
  const shown = st.shown = shownIn(lay.bands, block, view);
  const player = block && block.player && finite(block.player.y) ? block.player : null;
  const playerLayer = player ? layerOf(player.y) : null;

  // The bands: the shown one in the accent colour, the one under the mouse a little lighter.
  for (const b of lay.bands) {
    if (b.iy === shown) { c.fillStyle = k.soft; c.fillRect(lay.left, b.top, lay.right - lay.left, b.bottom - b.top); }
    else if (b.iy === st.hover) { c.fillStyle = 'rgba(255,255,255,.04)'; c.fillRect(lay.left, b.top, lay.right - lay.left, b.bottom - b.top); }
  }
  // The block's sector columns, faintly, so the strip reads as the block seen from the side.
  c.strokeStyle = k.line;
  c.lineWidth = 1;
  c.setLineDash([2, 4]);
  for (let x = lay.xWest + SECTOR; x < lay.xEast - 1; x += SECTOR) {
    const at = Math.round(lay.px(x)) + 0.5;
    c.beginPath(); c.moveTo(at, lay.top); c.lineTo(at, lay.bottom); c.stroke();
  }
  c.setLineDash([]);
  // Nebulae, side-on, faint and without names: enough to see which layer one sits in.
  const nebulae = block && Array.isArray(block.nebulae) ? block.nebulae : [];
  c.save();
  c.beginPath(); c.rect(lay.left, lay.top, lay.right - lay.left, lay.bottom - lay.top); c.clip();
  c.strokeStyle = NEBULA;
  c.globalAlpha = 0.35;
  c.setLineDash([3, 3]);
  for (const n of nebulae) {
    for (const [x, y, , r] of spheresOf(n)) {
      c.beginPath();
      c.ellipse(lay.px(x), lay.py(y), Math.max(1.5, r * lay.sx), Math.max(1.5, r * lay.sy), 0, 0, 2 * Math.PI);
      c.stroke();
    }
  }
  c.restore();
  // Lines between the bands; the shown band gets an accent frame.
  c.strokeStyle = k.line2;
  for (const b of lay.bands.slice(1)) { const at = Math.round(b.top) + 0.5; c.beginPath(); c.moveTo(lay.left, at); c.lineTo(lay.right, at); c.stroke(); }
  const on = lay.bands.find(b => b.iy === shown);
  c.strokeStyle = k.accent;
  c.lineWidth = 1.5;
  c.strokeRect(lay.left + 0.75, on.top + 0.75, lay.right - lay.left - 1.5, on.bottom - on.top - 1.5);

  // The band the galaxy's middle line runs along the foot of. Its name ("galaxy's middle") sits just above the
  // line, so that band keeps room for it under its own words; a band too short for both goes without the name.
  const middleBand = MIDDLE < lay.yTop && MIDDLE > lay.yBottom ? lay.bands.find(b => MIDDLE >= b.yFrom && MIDDLE < b.yTo) : null;
  const middleNamed = !!middleBand && middleBand.bottom - middleBand.top >= 36;

  // The words in each band: name, then height range and known systems, on as many lines as there is room for.
  for (const b of lay.bands) {
    const room = w - 16, tall = b.bottom - b.top - (b === middleBand && middleNamed ? 14 : 0), here = b.here || b.iy === playerLayer;
    const range = span(b.yFrom, b.yTo), known = knownWords(b);
    let y = b.top + 5;
    if (tall < 17) continue;
    // "You are here" goes beside the name when both fit on one line, else on a line of its own below.
    c.font = '600 13px ' + k.hud;
    const nameWidth = c.measureText(b.name).width;
    c.font = '600 10.5px ' + k.hud;
    const tagBeside = here && nameWidth + c.measureText('YOU ARE HERE').width + 14 <= room;
    if (tagBeside) say(c, 'YOU ARE HERE', w - 8, y + 2, '#fff', 'right');
    c.font = '600 13px ' + k.hud;
    say(c, fit(c, b.name, room), 8, y, b.iy === shown ? k.accent : b.missing ? k.muted : k.text);
    y += 17;
    c.font = '11.5px ' + k.font;
    const lines = (tall >= 66 ? [range, known] : tall >= 36 ? [range + dot + known] : []).map(text => [text, k.muted]);
    if (here && !tagBeside) lines.push(['You are in this layer', '#fff']);
    for (const [text, colour] of lines) {
      if (y + 13 > b.top + tall - 2) break;
      say(c, fit(c, text, room), 8, y, colour);
      y += 15;
    }
  }

  // The commander: a dot at their height and their west-east place in the block. Off the strip's heights, an
  // arrow at the top or bottom edge; outside the block's columns, a ring at its west or east edge. Placed first
  // (drawn last), so the middle line's words can keep out of the way of the "you" beside the dot.
  let me = null;
  if (player) {
    const x = clamp(lay.px(finite(player.x) ? player.x : (lay.xWest + lay.xEast) / 2), lay.left + 6, lay.right - 6);
    const yRaw = lay.py(player.y), y = clamp(yRaw, lay.top + 6, lay.bottom - 6), right = x < w - 40;
    c.font = '600 11px ' + k.hud;
    const word = c.measureText('you').width;
    me = { x, y, yRaw, right, inside: !finite(player.x) || (player.x >= lay.xWest && player.x <= lay.xEast),
      from: right ? x - 6 : x - 8 - word, to: right ? x + 8 + word : x + 6 };
  }

  // The galaxy's middle line. The seam between layers 31 and 32 (y = -25) is only 4 ly below it, so at this
  // scale the two are one line: the band edge, drawn over with the dashed middle line.
  if (middleBand) {
    const at = Math.round(lay.py(MIDDLE)) + 0.5;
    c.strokeStyle = 'rgba(226,232,240,.75)';
    c.lineWidth = 1.2;
    c.setLineDash([6, 4]);
    c.beginPath(); c.moveTo(lay.left, at); c.lineTo(lay.right, at); c.stroke();
    c.setLineDash([]);
    if (middleNamed) {
      c.font = '10.5px ' + k.font;
      c.textBaseline = 'bottom';
      // Its name at the west end, else at the east end, wherever the commander's "you" is not.
      const text = "galaxy's middle", width = c.measureText(text).width;
      const near = me && Math.abs(me.y - at) < 16, clear = (from, to) => !near || to < me.from || from > me.to;
      if (clear(6, 8 + width)) say(c, text, 8, at - 2, k.muted, 'left');
      else if (clear(w - 8 - width, w - 6)) say(c, text, w - 6, at - 2, k.muted, 'right');
      c.textBaseline = 'top';
    }
  }

  if (me) {
    if (me.yRaw !== me.y) {
      const up = me.yRaw < me.y;
      c.beginPath();
      c.moveTo(me.x, up ? lay.top + 1 : lay.bottom - 1);
      c.lineTo(me.x - 6, up ? lay.top + 10 : lay.bottom - 10);
      c.lineTo(me.x + 6, up ? lay.top + 10 : lay.bottom - 10);
      c.closePath();
      c.fillStyle = '#fff';
      c.fill();
    } else you(c, me.x, me.y, me.inside);
    c.font = '600 11px ' + k.hud;
    c.textBaseline = 'middle';
    say(c, 'you', me.right ? me.x + 8 : me.x - 8, me.y, '#fff', me.right ? 'left' : 'right');
    c.textBaseline = 'top';
  }

  // Which way the strip is seen.
  c.font = '10.5px ' + k.font;
  c.textBaseline = 'bottom';
  say(c, 'west', 6, h - 3, k.faint, 'left');
  say(c, 'east', w - 6, h - 3, k.faint, 'right');
  say(c, fit(c, 'seen from the side', w - 2 * (c.measureText('west').width + 16)), w / 2, h - 3, k.faint, 'center');

  const shownBand = lay.bands.find(b => b.iy === shown);
  st.canvas.setAttribute('aria-label', 'Layers seen from the side. Shown: ' + shownBand.name + ', ' + span(shownBand.yFrom, shownBand.yTo)
    + '.' + (middleBand ? " The dashed line is the galaxy's middle." : '')
    + (st.onPickLayer ? ' The up and down arrow keys show the layer above or below.' : ''));
}

function bandAt(st, y) {
  const lay = st.layout;
  return lay ? lay.bands.find(b => y >= b.top && y < b.bottom) || null : null;
}

function strip(canvas, block, view, onPickLayer) {
  const st = plumb(canvas, 'strip', (st, on) => {
    st.draw = () => drawStrip(st);
    st.handle = { redraw: () => drawStrip(st), dispose: () => forget(st) };
    on('click', e => {
      const b = bandAt(st, local(canvas, e)[1]);
      if (b && !b.missing && st.onPickLayer) st.onPickLayer(b.iy);
    });
    on('mousemove', e => {
      const b = bandAt(st, local(canvas, e)[1]), iy = b && !b.missing ? b.iy : null;
      canvas.style.cursor = iy != null && st.onPickLayer ? 'pointer' : '';
      const title = b ? b.name + ': ' + span(b.yFrom, b.yTo) + dot + knownWords(b, true)
        + (st.onPickLayer && iy != null && iy !== st.shown ? '. Click to show this layer.' : '') : '';
      if (canvas.title !== title) canvas.title = title;
      if (iy !== st.hover) { st.hover = iy; later(st); }
    });
    on('mouseleave', () => { if (st.hover != null) { st.hover = null; later(st); } });
    // Up and down step through the layers when the strip has the keyboard (over any the block does not have).
    on('keydown', e => {
      const lay = st.layout;
      if (!lay || !st.onPickLayer || (e.key !== 'ArrowUp' && e.key !== 'ArrowDown')) return;
      e.preventDefault();
      const real = lay.bands.filter(b => !b.missing), at = real.findIndex(b => b.iy === st.shown);
      const next = real[at + (e.key === 'ArrowUp' ? -1 : 1)];
      if (next) st.onPickLayer(next.iy);
    });
    if (!canvas.hasAttribute('tabindex')) canvas.tabIndex = 0;
  });
  st.block = block;
  st.view = view;
  st.onPickLayer = typeof onPickLayer === 'function' ? onPickLayer : null;
  drawStrip(st);
  return st.handle;
}

// ============================================================ the slice from above
// The block's footprint from above, in light years: from the scene's sectors, else round the commander, else the
// opened area. Null when there is nothing to frame.
function footprint(scene) {
  const sectors = scene && Array.isArray(scene.sectors) ? scene.sectors.filter(s => Number.isInteger(s.ix) && Number.isInteger(s.iz)) : [];
  if (sectors.length) {
    const ix = sectors.map(s => s.ix), iz = sectors.map(s => s.iz);
    return { x0: west(Math.min(...ix)), x1: west(Math.max(...ix) + 1), z0: south(Math.min(...iz)), z1: south(Math.max(...iz) + 1) };
  }
  const p = scene && scene.player;
  if (p && finite(p.x) && finite(p.z)) {
    const ix = Math.floor((p.x - ORIGIN[0]) / SECTOR), iz = Math.floor((p.z - ORIGIN[2]) / SECTOR);
    return { x0: west(ix - 1), x1: west(ix + 2), z0: south(iz - 1), z1: south(iz + 2) };
  }
  const a = scene && scene.area;
  if (a && Array.isArray(a.corner) && a.cubeLy > 0 && a.n > 0) {
    const size = a.cubeLy * a.n;
    return { x0: a.corner[0], x1: a.corner[0] + size, z0: a.corner[2], z1: a.corner[2] + size };
  }
  return null;
}

// The layer on show from above: the view's, else the one the scene marks as shown, else the commander's.
function shownLayer(scene, view) {
  if (view && Number.isInteger(view.layer)) return view.layer;
  const sectors = scene && Array.isArray(scene.sectors) ? scene.sectors : [];
  const marked = sectors.find(s => s.layerShown);
  if (marked) return marked.iy;
  if (scene && scene.player && finite(scene.player.y)) return layerOf(scene.player.y);
  return sectors.length ? sectors[0].iy : 32;
}

// The heights the column on the right offers inside the shown layer: the levels of the opened area's cubes, else
// 160 ly steps when cubes were counted in this layer (counted cubes sit on the 160 ly grid of their sector).
function heightsIn(scene, y0, y1) {
  const a = scene.area;
  if (a && Array.isArray(a.corner) && a.cubeLy > 0 && a.n > 0) {
    const list = [];
    for (let j = 0; j < a.n; j++) {
      const from = a.corner[1] + j * a.cubeLy, to = from + a.cubeLy;
      if (to > y0 && from < y1) list.push({ j, y0: from, y1: to });
    }
    if (list.length) return { area: true, list };
  }
  const counted = (scene.counted || []).filter(q => q.y0 + q.size > y0 && q.y0 < y1);
  if (counted.length) {
    const list = [];
    for (let j = 0; j < SECTOR / 160; j++) list.push({ j, y0: y0 + j * 160, y1: y0 + (j + 1) * 160 });
    return { area: false, list };
  }
  return null;
}

// Lays the slice out: the map area, the column of heights, and the scale that fits the footprint, zoomed and slid.
function sliceLayout(st, w, h) {
  const scene = st.scene || {}, view = st.view || {};
  const foot = footprint(scene);
  if (!foot) return null;
  const compact = !!view.compact || Math.min(w, h) < 200;
  const iy = shownLayer(scene, view), y0 = layerFloor(iy), y1 = y0 + SECTOR;
  let heights = compact ? null : heightsIn(scene, y0, y1);
  const pad = compact ? 4 : 10, caption = compact ? 0 : 20;
  let column = heights && w >= 360 ? 58 : 0;
  if (column && (h - 2 * pad - 18) / (heights.list.length + 1) < 12) column = 0;   // too short to hold a button per height
  if (!column) heights = null;

  // A different block (another footprint) or other heights: back to the whole block and all heights.
  const footKey = [foot.x0, foot.x1, foot.z0, foot.z1].join();
  if (footKey !== st.footKey) { st.footKey = footKey; st.zoom = 1; st.pan = [0, 0]; }
  const heightKey = heights ? heights.area + ':' + heights.list.map(l => l.y0).join() : '';
  if (heightKey !== st.heightKey) { st.heightKey = heightKey; st.level = null; }
  if (compact) { st.zoom = 1; st.pan = [0, 0]; }

  const area = { left: pad, top: pad, width: w - 2 * pad - column, height: h - 2 * pad - caption };
  if (area.width < 20 || area.height < 20) return null;
  const base = Math.min(area.width / (foot.x1 - foot.x0), area.height / (foot.z1 - foot.z0));
  if (st.want) {   // centre() asked for a place: frame `span` light years round it
    const want = st.want;
    st.want = null;
    st.zoom = clamp(Math.min(area.width, area.height) / (want.span * base), 1, 48);
    st.pan = [want.x - (foot.x0 + foot.x1) / 2, want.z - (foot.z0 + foot.z1) / 2];
  }
  // Sliding stops with the middle of the view at the block's edge, so the block can never be lost off screen.
  const halfX = (foot.x1 - foot.x0) / 2, halfZ = (foot.z1 - foot.z0) / 2;
  st.pan = [clamp(st.pan[0], -halfX, halfX), clamp(st.pan[1], -halfZ, halfZ)];
  const scale = base * st.zoom;
  const cx = (foot.x0 + foot.x1) / 2 + st.pan[0], cz = (foot.z0 + foot.z1) / 2 + st.pan[1];
  const mx = area.left + area.width / 2, my = area.top + area.height / 2;
  return {
    foot, compact, iy, y0, y1, heights, column, area, base, scale, cx, cz, mx, my,
    // North (larger z) up and east (larger x) right, exactly as on the flat map.
    px: x => mx + (x - cx) * scale,
    py: z => my - (z - cz) * scale,
    wx: x => cx + (x - mx) / scale,
    wz: y => cz - (y - my) / scale,
  };
}

// The heights on show: the one picked in the column, else the whole layer.
const range = (st, lay) => st.level || { y0: lay.y0, y1: lay.y1 };

function drawSlice(st) {
  const ready = prepare(st);
  if (!ready) return;
  const { c, w, h } = ready, scene = st.scene || {}, view = st.view || {}, k = palette(st.canvas);
  const lay = st.layout = sliceLayout(st, w, h);
  c.fillStyle = k.bg2;
  c.fillRect(0, 0, w, h);
  st.drawn = { stars: [], nebulae: [], columns: new Map(), counted: [] };
  if (!lay) {
    c.font = '12px ' + k.font;
    c.fillStyle = k.muted;
    c.textAlign = 'center';
    c.textBaseline = 'middle';
    c.fillText(fit(c, 'Nothing to show yet.', w - 16), w / 2, h / 2);
    return;
  }
  const { px, py, scale, compact } = lay, on = range(st, lay);
  const ramp = view.ramp, showEmpty = view.showEmpty !== false, solid = view.look === 'solid';
  const filter = Array.isArray(view.range) ? view.range : null;
  const opacity = finite(view.opacity) ? view.opacity : 0.35;
  // From above, one fill per square: the 3D map's see-through opacity would wash it out, so it is raised (0.35 → 0.8).
  const alphaOf = v => {
    let a = solid ? 1 : clamp(0.3 + 1.4 * opacity, 0.25, 0.95);
    if (view.unexploredStandsOut && v >= 0) a *= 0.45 + 0.55 * (1 - v);   // cold squares more solid
    return a;
  };
  const hidden = v => v >= 0 && filter && ((finite(filter[0]) && v < filter[0]) || (finite(filter[1]) && v > filter[1]));
  // Fills a square by its value and answers the colour used, or null when it has no fill: nothing known, or
  // hidden by the range filter, leaves only a faint outline (nothing known only with "Show empty cubes" on).
  const fill = (x, y, size, v) => {
    if (!finite(v) || v === -1 || hidden(v)) {
      if (showEmpty || hidden(v)) { c.strokeStyle = 'rgba(141,153,170,.35)'; c.lineWidth = 1; c.strokeRect(x + 0.5, y + 0.5, size - 1, size - 1); }
      return null;
    }
    const rgb = v < 0 ? GREY : heat(ramp, v);
    c.fillStyle = rgba(rgb, alphaOf(v));
    c.fillRect(x, y, size + 0.5, size + 0.5);   // a hair wider: no seams between squares
    return rgb;
  };
  c.save();
  c.beginPath(); c.rect(lay.area.left, lay.area.top, lay.area.width, lay.area.height); c.clip();
  if (view.look === 'glow') c.globalCompositeOperation = 'lighter';

  // The sectors of the shown layer. The one looked inside is only an outline: its cubes are drawn instead.
  const sectors = (scene.sectors || []).filter(s => s.iy === lay.iy);
  for (const s of sectors) if (!s.outlineOnly) fill(px(west(s.ix)), py(south(s.iz + 1)), SECTOR * scale, s.value);
  c.globalCompositeOperation = 'source-over';
  c.lineWidth = 1;
  c.strokeStyle = k.line2;
  for (const s of sectors) {
    const x = px(west(s.ix)), y = py(south(s.iz + 1)), size = SECTOR * scale;
    c.strokeRect(Math.round(x) + 0.5, Math.round(y) + 0.5, Math.round(size) - 1, Math.round(size) - 1);
  }

  // The numbers on the cubes, gathered here and written after the star dots so the dots never hide them.
  const numbers = [];

  // The cubes of the opened area, one square per column: the cube at the picked height, or, for the whole layer,
  // the strongest of the cubes stacked there (the one with the highest value).
  const a = scene.area;
  if (a && Array.isArray(a.corner) && a.cubeLy > 0 && a.n > 0 && a.corner[1] < on.y1 && a.corner[1] + a.n * a.cubeLy > on.y0) {
    const cubes = Array.isArray(a.cubes) ? a.cubes : [];
    const columns = st.drawn.columns;
    // "Too few to judge" (-2) still beats nothing known (-1): something is there.
    const strength = v => v >= 0 ? v : v === -2 ? -0.5 : -1;
    cubes.forEach((q, index) => {
      const from = a.corner[1] + q.j * a.cubeLy;
      if (!(from + a.cubeLy > on.y0 && from < on.y1)) return;
      const key = q.i * a.n + q.k, best = columns.get(key);
      const v = finite(q.value) ? q.value : -1;
      if (!best || strength(v) > strength(best.v)) columns.set(key, { q, index, v, stacked: (best ? best.stacked : 0) + 1 });
      else best.stacked++;
    });
    const size = a.cubeLy * scale;
    if (view.look === 'glow') c.globalCompositeOperation = 'lighter';
    for (const col of columns.values()) col.rgb = fill(px(a.corner[0] + col.q.i * a.cubeLy), py(a.corner[2] + (col.q.k + 1) * a.cubeLy), size, col.v);
    c.globalCompositeOperation = 'source-over';
    // The cube grid over the whole area, faintly, so it reads as a sector broken into cubes.
    if (size >= 4) {
      c.strokeStyle = 'rgba(141,153,170,.22)';
      c.lineWidth = 1;
      c.beginPath();
      for (let n = 0; n <= a.n; n++) {
        const gx = Math.round(px(a.corner[0] + n * a.cubeLy)) + 0.5, gz = Math.round(py(a.corner[2] + n * a.cubeLy)) + 0.5;
        c.moveTo(gx, py(a.corner[2])); c.lineTo(gx, py(a.corner[2] + a.n * a.cubeLy));
        c.moveTo(px(a.corner[0]), gz); c.lineTo(px(a.corner[0] + a.n * a.cubeLy), gz);
      }
      c.stroke();
    }
    if (!compact && size >= 30) {
      for (const col of columns.values()) {
        if (col.q.label == null || col.q.label === '') continue;
        numbers.push({ text: String(col.q.label), x: px(a.corner[0] + (col.q.i + 0.5) * a.cubeLy), y: py(a.corner[2] + (col.q.k + 0.5) * a.cubeLy), room: size - 4, rgb: col.rgb });
      }
    }
  }

  // Counted cubes: solid squares. Stacked ones (a counted column) draw lowest value first, so the strongest is on top.
  const counted = (scene.counted || []).map((q, index) => ({ q, index })).filter(({ q }) => finite(q.x0) && q.size > 0 && q.y0 + q.size > on.y0 && q.y0 < on.y1);
  counted.sort((p, q) => (finite(p.q.value) ? p.q.value : -1) - (finite(q.q.value) ? q.q.value : -1));
  st.drawn.counted = counted;
  for (const entry of counted) {
    const q = entry.q, x = px(q.x0), y = py(q.z0 + q.size), size = q.size * scale;
    const v = finite(q.value) ? q.value : -1;
    entry.rgb = v === -1 || hidden(v) ? null : v < 0 ? GREY : heat(ramp, v);
    if (entry.rgb) { c.fillStyle = rgba(entry.rgb, 0.92); c.fillRect(x, y, size, size); }
    c.strokeStyle = entry.rgb ? HALO : 'rgba(141,153,170,.6)';
    c.lineWidth = 1;
    c.strokeRect(x + 0.5, y + 0.5, size - 1, size - 1);
  }
  if (!compact) {
    const labelled = new Set();
    for (let n = counted.length - 1; n >= 0; n--) {   // the top one of each stack carries the label
      const { q, rgb } = counted[n], where = q.x0 + ',' + q.z0, size = q.size * scale;
      if (labelled.has(where) || size < 22 || q.label == null) continue;
      labelled.add(where);
      numbers.push({ text: String(q.label), x: px(q.x0 + q.size / 2), y: py(q.z0 + q.size / 2), room: size - 4, rgb, bold: true });
    }
  }

  // The opened sector's outline, over its cubes; and the square under the mouse.
  for (const s of sectors) {
    if (!s.outlineOnly) continue;
    c.strokeStyle = k.text;
    c.lineWidth = 1.5;
    c.strokeRect(px(west(s.ix)) + 0.75, py(south(s.iz + 1)) + 0.75, SECTOR * scale - 1.5, SECTOR * scale - 1.5);
  }
  if (st.hover && st.hover.box) {
    const b = st.hover.box;
    c.strokeStyle = '#fff';
    c.lineWidth = 1.5;
    c.strokeRect(px(b.x0) + 0.75, py(b.z1) + 0.75, (b.x1 - b.x0) * scale - 1.5, (b.z1 - b.z0) * scale - 1.5);
  }

  // Nebulae: where the sphere crosses the heights on show, a dashed circle in their own colour with the name and,
  // when it needs a permit, a padlock. One that misses those heights is only a faint dotted ring.
  // A nebula of several spheres (ICZ) gets a circle for each, and its name once, over its middle.
  (scene.nebulae || []).forEach((n, index) => {
    const spheres = spheresOf(n);
    if (!spheres.length || !finite(n.x) || !finite(n.z)) return;
    let crossed = 0;   // the widest circle where one of its spheres crosses the heights on show, in pixels
    for (const [sx, sy, sz, sr] of spheres) {
      const off = sy < on.y0 ? on.y0 - sy : sy > on.y1 ? sy - on.y1 : 0;
      const cross = off >= sr ? 0 : Math.sqrt(sr * sr - off * off);
      const x = px(sx), y = py(sz), r = (cross || sr) * scale;
      if (!cross && compact) continue;
      c.strokeStyle = NEBULA;
      c.lineWidth = cross ? 1.3 : 1;
      c.globalAlpha = cross ? 0.9 : 0.3;
      c.setLineDash(cross ? [5, 4] : [1.5, 4]);
      c.beginPath(); c.arc(x, y, Math.max(r, 2), 0, 2 * Math.PI); c.stroke();
      c.setLineDash([]);
      c.globalAlpha = 1;
      st.drawn.nebulae.push({ n, index, x, y, r: Math.max(r, 2), cross: !!cross });
      if (cross) crossed = Math.max(crossed, r);
    }
    if (!crossed || compact) return;
    const x = px(n.x), y = py(n.z), r = crossed;
    c.beginPath(); c.arc(x, y, 2, 0, 2 * Math.PI); c.fillStyle = NEBULA; c.fill();
    c.font = '600 11.5px ' + k.hud;
    c.textBaseline = 'bottom';
    const name = n.name || 'Nebula or cluster', ty = y - Math.min(r, 60) - 3 > lay.area.top + 12 ? y - Math.min(r, 60) - 3 : y - 6;
    say(c, name, x, ty, NEBULA, 'center');
    if (n.permit) padlock(c, x + c.measureText(name).width / 2 + 4, ty - 2, NEBULA);
  });

  // Star dots of the heights on show, coloured by how well each is recorded. Like the 3D map, which shows them
  // only near the camera, they wait until the view is zoomed in (a 160 ly cube 40 px wide), unless there are few:
  // a thousand dots over a whole sector would hide its cubes' colours. Never in the locator.
  const stars = [];
  if (!compact && Array.isArray(scene.stars)) scene.stars.forEach((s, index) => { if (finite(s.x) && finite(s.z) && s.y >= on.y0 && s.y < on.y1) stars.push(index); });
  st.starsWaiting = stars.length > 400 && scale < 0.25;
  if (stars.length && !st.starsWaiting) {
    const r = clamp(scale * 4, 1.2, 3), byState = { full: [], part: [], never: [] };
    for (const index of stars) {
      const s = scene.stars[index], x = px(s.x), y = py(s.z);
      if (x < lay.area.left - 4 || x > lay.area.left + lay.area.width + 4 || y < lay.area.top - 4 || y > lay.area.top + lay.area.height + 4) continue;
      (byState[s.state] || byState.never).push(x, y);
      st.drawn.stars.push(x, y, index);
    }
    for (const state of ['never', 'part', 'full']) {
      const at = byState[state];
      if (!at.length) continue;
      c.fillStyle = HALO;
      c.beginPath();
      for (let n = 0; n < at.length; n += 2) { c.moveTo(at[n] + r + 0.75, at[n + 1]); c.arc(at[n], at[n + 1], r + 0.75, 0, 2 * Math.PI); }
      c.fill();
      c.fillStyle = STAR[state];
      c.beginPath();
      for (let n = 0; n < at.length; n += 2) { c.moveTo(at[n] + r, at[n + 1]); c.arc(at[n], at[n + 1], r, 0, 2 * Math.PI); }
      c.fill();
    }
  }

  // The selected star: its cube outlined and a diamond on it.
  const m = scene.marker;
  if (m && finite(m.x) && finite(m.z)) {
    if (m.cube && finite(m.cube.x0) && m.cube.size > 0) {
      c.strokeStyle = '#fff';
      c.lineWidth = 1.5;
      c.setLineDash([4, 3]);
      c.strokeRect(px(m.cube.x0), py(m.cube.z0 + m.cube.size), m.cube.size * scale, m.cube.size * scale);
      c.setLineDash([]);
    }
    const x = px(m.x), y = py(m.z);
    c.beginPath(); c.moveTo(x, y - 6); c.lineTo(x + 6, y); c.lineTo(x, y + 6); c.lineTo(x - 6, y); c.closePath();
    c.fillStyle = k.accent; c.fill();
    c.lineWidth = 1.5; c.strokeStyle = HALO; c.stroke();
  }

  // The numbers on the cubes. One that does not fit is left out rather than cut short: a cut "10,000+" would
  // read as "10".
  c.textBaseline = 'middle';
  for (const n of numbers) {
    c.font = (n.bold ? '600 ' : '') + '10.5px ' + k.font;
    if (c.measureText(n.text).width > n.room) continue;
    if (n.rgb && bright(n.rgb)) { c.textAlign = 'center'; c.fillStyle = '#06080c'; c.fillText(n.text, n.x, n.y); }
    else say(c, n.text, n.x, n.y, k.text, 'center');
  }

  // Sector names, last, so nothing covers them. The sector looked inside has its name just above its top edge
  // (when there is room), off its cubes.
  if (!compact && SECTOR * scale >= 70) {
    for (const s of sectors) {
      const x = px(west(s.ix)) + 6, top = py(south(s.iz + 1)), room = SECTOR * scale - 12;
      c.font = '600 12.5px ' + k.hud;
      if (s.outlineOnly && top - 18 > lay.area.top) {
        c.textBaseline = 'bottom';
        say(c, fit(c, s.name || '', room), x - 6, top - 3, k.text);
        continue;
      }
      c.textBaseline = 'top';
      say(c, fit(c, s.name || '', room), x, top + 5, k.text);
      if (SECTOR * scale >= 110 && finite(s.known) && !s.outlineOnly) {
        c.font = '11px ' + k.font;
        say(c, fit(c, whole(s.known) + ' named after it', room), x, top + 21, k.muted);
      }
    }
  }

  // The commander and the way they are heading. A ring, not a dot, when they are in another layer.
  const p = scene.player;
  if (p && finite(p.x) && finite(p.z)) {
    const x = px(p.x), y = py(p.z), inLayer = !finite(p.y) || layerOf(p.y) === lay.iy;
    if (finite(p.heading)) {
      const [across, down] = ahead(p.heading), reach = compact ? 18 : 44;
      c.beginPath();
      c.moveTo(x, y);
      c.lineTo(x + across * reach, y + down * reach);
      c.setLineDash([5, 4]);
      c.lineWidth = 3; c.strokeStyle = HALO; c.stroke();
      c.lineWidth = 1.5; c.strokeStyle = '#fff'; c.stroke();
      c.setLineDash([]);
    }
    you(c, x, y, inLayer);
    if (!compact) {
      c.font = '600 11px ' + k.hud;
      c.textBaseline = 'middle';
      say(c, inLayer ? 'you' : 'you (' + layerName(layerOf(p.y)) + ')', x + 8, y, '#fff');
    }
  }

  // The highlight (view.chosen: the page's chosen sector or cube, { x0, y0, z0, size, label }): a thick white outline
  // with a dark edge over everything, so a click or an arrow press always shows where it went. Dashed when it lies at
  // other heights than those on show (an arrow up or down a level); in the big view its name and level beside it.
  const ch = view.chosen;
  if (ch && finite(ch.x0) && finite(ch.z0) && ch.size > 0) {
    const x = px(ch.x0), y = py(ch.z0 + ch.size), size = Math.max(ch.size * scale, 4);
    const here = !finite(ch.y0) || (ch.y0 < on.y1 && ch.y0 + ch.size > on.y0);
    c.setLineDash(here ? [] : [5, 4]);
    c.lineWidth = compact ? 3 : 4.5; c.strokeStyle = HALO; c.strokeRect(x, y, size, size);
    c.lineWidth = compact ? 1.5 : 2.5; c.strokeStyle = '#fff'; c.strokeRect(x, y, size, size);
    c.setLineDash([]);
    if (!compact && ch.label) {
      c.font = '600 12px ' + k.font;
      const text = fit(c, ch.label + (here ? '' : dot + 'not at the heights shown'), lay.area.width - 12);
      const tw = c.measureText(text).width, above = y - 6 - 16 > lay.area.top;
      c.textBaseline = above ? 'bottom' : 'top';
      const tx = clamp(x, lay.area.left + 6, lay.area.left + lay.area.width - tw - 6);
      say(c, text, tx, above ? y - 6 : Math.min(y + size + 6, lay.area.top + lay.area.height - 18), '#fff');
    }
  }
  c.restore();

  if (!compact) drawCaption(c, lay, k, on, st.starsWaiting);
  if (lay.heights) drawHeights(c, st, lay, k, w);
}

// Under the map: a scale bar, which way is north, and what is on show.
function drawCaption(c, lay, k, on, starsWaiting) {
  const y = lay.area.top + lay.area.height + 11;
  c.textBaseline = 'middle';
  c.font = '11px ' + k.font;
  // The longest round length that makes a bar of at most 120 px.
  const lengths = [10, 20, 40, 80, 160, 320, 640, 1280, 2560];
  let ly = lengths[0];
  for (const n of lengths) if (n * lay.scale <= 120) ly = n;
  const bar = ly * lay.scale, x = lay.area.left + 2;
  c.strokeStyle = k.muted;
  c.lineWidth = 1.5;
  c.beginPath(); c.moveTo(x, y - 4); c.lineTo(x, y); c.lineTo(x + bar, y); c.lineTo(x + bar, y - 4); c.stroke();
  const scaleText = whole(ly) + ' ly' + dot + '↑ north';
  say(c, scaleText, x + bar + 6, y - 1, k.muted);
  const what = (starsWaiting ? 'Zoom in to see the stars' + dot : '') + layerName(lay.iy) + dot
    + (on.y0 === lay.y0 && on.y1 === lay.y1 ? span(lay.y0, lay.y1) : 'cubes from ' + span(on.y0, on.y1));
  const left = x + bar + 6 + c.measureText(scaleText).width + 14, room = lay.area.left + lay.area.width - left;
  say(c, fit(c, what, room), lay.area.left + lay.area.width, y - 1, k.muted, 'right');
}

// The column of heights on the right: "All", then each height of cubes from the top down.
function heightButtons(lay, w) {
  const list = lay.heights.list, x = w - lay.column + 6, width = lay.column - 10, top = lay.area.top + 18;
  const tall = Math.min(26, (lay.area.top + lay.area.height - top) / (list.length + 1));
  const buttons = [{ all: true, x, y: top, width, height: tall - 3 }];
  list.slice().reverse().forEach((level, n) => buttons.push({ level, x, y: top + (n + 1) * tall, width, height: tall - 3 }));
  return buttons;
}

function drawHeights(c, st, lay, k, w) {
  c.font = '10.5px ' + k.font;
  c.textBaseline = 'middle';
  say(c, 'Height', w - lay.column / 2 - 2, lay.area.top + 7, k.muted, 'center');
  for (const b of heightButtons(lay, w)) {
    const chosen = b.all ? !st.level : st.level && st.level.y0 === b.level.y0;
    const over = st.hover && st.hover.button && (b.all ? st.hover.button.all : st.hover.button.level && st.hover.button.level.y0 === b.level.y0);
    c.fillStyle = chosen ? k.soft : over ? 'rgba(255,255,255,.06)' : 'rgba(255,255,255,.02)';
    c.fillRect(b.x, b.y, b.width, b.height);
    c.strokeStyle = chosen ? k.accent : k.line2;
    c.lineWidth = 1;
    c.strokeRect(b.x + 0.5, b.y + 0.5, b.width - 1, b.height - 1);
    c.fillStyle = chosen ? k.accent : k.text;
    c.textAlign = 'center';
    c.fillText(fit(c, b.all ? 'All' : height(b.level.y0), b.width - 4), b.x + b.width / 2, b.y + b.height / 2);
  }
}

// What is at a point of the canvas (CSS pixels), as onPick hears it:
//   {kind:'level', all:true} or {kind:'level', j, y0, y1}         a button of the column of heights
//   {kind:'star', index, star, x, y, z}                            a star dot (within 8 px)
//   {kind:'nebula', index, nebula, name, permit}                   a nebula's centre (8 px) or its circle (6 px)
//   {kind:'counted', index, cube, stack}                           a counted cube (the top one; stack = all here)
//   {kind:'cube', i, j, k, index, cube, stacked}                   a cube of the opened area (index -1, cube null: empty)
//   {kind:'sector', ix, iy, iz, index, sector}                     a sector of the shown layer
//   {kind:'none'}                                                  nothing there
// All but 'level' also carry point: [x, y, z], the galaxy place under the pointer (y = the middle of the heights on show).
function pickAt(st, x, y) {
  const lay = st.layout, scene = st.scene || {}, drawn = st.drawn;
  if (!lay || !drawn) return null;
  if (lay.heights) {
    const b = heightButtons(lay, st.canvas.clientWidth).find(b => x >= b.x && x < b.x + b.width && y >= b.y && y < b.y + b.height);
    if (b) return b.all ? { kind: 'level', all: true, button: b } : { kind: 'level', j: b.level.j, y0: b.level.y0, y1: b.level.y1, button: b };
  }
  if (x < lay.area.left || x > lay.area.left + lay.area.width || y < lay.area.top || y > lay.area.top + lay.area.height) return null;
  const on = range(st, lay), wx = lay.wx(x), wz = lay.wz(y), point = [wx, (on.y0 + on.y1) / 2, wz];

  let best = -1, near = 64;   // 8 px, squared
  for (let n = 0; n < drawn.stars.length; n += 3) {
    const d = (drawn.stars[n] - x) ** 2 + (drawn.stars[n + 1] - y) ** 2;
    if (d <= near) { near = d; best = drawn.stars[n + 2]; }
  }
  if (best >= 0) { const s = scene.stars[best]; return { kind: 'star', index: best, star: s, x: s.x, y: s.y, z: s.z, point }; }
  const nebula = n => ({ kind: 'nebula', index: n.index, nebula: n.n, name: n.n.name, permit: !!n.n.permit, point });
  const centre = drawn.nebulae.find(n => n.cross && (n.x - x) ** 2 + (n.y - y) ** 2 <= 64);
  if (centre) return nebula(centre);

  const stack = drawn.counted.filter(({ q }) => wx >= q.x0 && wx < q.x0 + q.size && wz >= q.z0 && wz < q.z0 + q.size);
  if (stack.length) {
    const top = stack[stack.length - 1];
    return { kind: 'counted', index: top.index, cube: top.q, stack: stack.map(s => s.q).reverse(), box: { x0: top.q.x0, x1: top.q.x0 + top.q.size, z0: top.q.z0, z1: top.q.z0 + top.q.size }, point };
  }
  const a = scene.area;
  if (a && Array.isArray(a.corner) && a.cubeLy > 0 && a.n > 0 && a.corner[1] < on.y1 && a.corner[1] + a.n * a.cubeLy > on.y0) {
    const i = Math.floor((wx - a.corner[0]) / a.cubeLy), k = Math.floor((wz - a.corner[2]) / a.cubeLy);
    if (i >= 0 && i < a.n && k >= 0 && k < a.n) {
      const col = drawn.columns.get(i * a.n + k);
      const j = col ? col.q.j : st.level && st.level.j != null && lay.heights && lay.heights.area ? st.level.j : clamp(Math.floor((on.y0 - a.corner[1]) / a.cubeLy), 0, a.n - 1);
      const x0 = a.corner[0] + i * a.cubeLy, z0 = a.corner[2] + k * a.cubeLy;
      return { kind: 'cube', i, j, k, index: col ? col.index : -1, cube: col ? col.q : null, stacked: col ? col.stacked : 0, box: { x0, x1: x0 + a.cubeLy, z0, z1: z0 + a.cubeLy }, point };
    }
  }
  const ring = drawn.nebulae.find(n => n.cross && Math.abs(Math.hypot(n.x - x, n.y - y) - n.r) <= 6);
  if (ring) return nebula(ring);
  const ix = Math.floor((wx - ORIGIN[0]) / SECTOR), iz = Math.floor((wz - ORIGIN[2]) / SECTOR);
  const sectors = scene.sectors || [], index = sectors.findIndex(s => s.iy === lay.iy && s.ix === ix && s.iz === iz);
  if (index >= 0) return { kind: 'sector', ix, iy: lay.iy, iz, index, sector: sectors[index], box: { x0: west(ix), x1: west(ix + 1), z0: south(iz), z1: south(iz + 1) }, point };
  return { kind: 'none', point };
}

// The pointer's tooltip, in the words the page uses.
function describe(hit, st) {
  if (!hit) return '';
  const state = { full: 'fully recorded', part: 'partly recorded', never: 'never scanned' };
  switch (hit.kind) {
    case 'level': return hit.all ? 'Show the cubes at every height of this layer (the strongest of each stack)' : 'Show only the cubes from ' + span(hit.y0, hit.y1);
    case 'star': return (hit.star.name || 'A known star') + dot + (state[hit.star.state] || 'known');
    case 'nebula': return (hit.name || 'Nebula or cluster') + ' (nebula or cluster)' + (hit.permit ? dot + 'needs a permit' : '');
    case 'counted': return hit.stack.length > 1
      ? 'Counted cubes here, top to bottom: ' + hit.stack.map(q => height(q.y0) + ' ly: ' + (q.label == null ? '?' : q.label)).join(dot)
      : 'A counted cube of ' + whole(hit.cube.size) + ' ly: ' + (hit.cube.label == null ? 'no count' : hit.cube.label);
    case 'cube': {
      // The page's own words for the cube when it gave them (tip), as in the 3D map's tooltip; else its number. A cube
      // not in the list takes the area's word for it (blank): nothing known, or in a busy sector not counted yet.
      const area = st.scene.area, q = hit.cube;
      const label = q && q.tip ? String(q.tip) : q && q.label != null && q.label !== '' ? String(q.label) : area.blank ? String(area.blank) : 'nothing known';
      return st.level || hit.stacked <= 1 ? 'A cube of ' + whole(area.cubeLy) + ' ly: ' + label
        : 'The strongest of the cubes stacked here: ' + label + '. Pick a height on the right to see each one.';
    }
    // EDAstro's figure for a sector counts the systems named after it, not every known one: it says so.
    case 'sector': return (hit.sector.name || 'A sector') + ': ' + (!finite(hit.sector.known) ? 'no count yet'
      : hit.sector.known ? whole(hit.sector.known) + ' systems named after it (EDAstro)' : 'no systems named after it (EDAstro)');
    default: return '';
  }
}

function slice(canvas, scene, view, onPick) {
  const st = plumb(canvas, 'slice', (st, on) => {
    st.zoom = 1;
    st.pan = [0, 0];
    st.level = null;
    st.hover = null;
    st.hoverCb = null;
    st.draw = () => drawSlice(st);
    st.handle = {
      redraw: () => drawSlice(st),
      dispose: () => forget(st),
      // Back to the whole block (the "Overview" button).
      reset: () => { st.zoom = 1; st.pan = [0, 0]; st.want = null; later(st); },
      // Frames `across` light years round a galaxy place (the "Me" button, a search result).
      centre: (x, z, across) => { if (finite(x) && finite(z)) { st.want = { x, z, span: across > 0 ? across : SECTOR }; later(st); } },
      // What is under a point of the screen (clientX, clientY), without picking it.
      pick: (clientX, clientY) => { const [x, y] = local(canvas, { clientX, clientY }); return pickAt(st, x, y); },
      onHover: cb => { st.hoverCb = typeof cb === 'function' ? cb : null; },
      // The heights on show: null for the whole layer, else {j, y0, y1}.
      level: () => st.level ? { ...st.level } : null,
    };
    let press = null;   // where a press started, to tell a click from a drag
    on('pointerdown', e => {
      if (e.button !== 0) return;
      press = { x: e.clientX, y: e.clientY, pan: st.pan.slice(), dragging: false, id: e.pointerId };
    });
    on('pointermove', e => {
      if (press && !(e.buttons & 1)) press = null;   // the button went up outside the canvas: no drag left to follow
      if (press && press.id === e.pointerId && st.layout && !st.layout.compact) {
        const moved = Math.hypot(e.clientX - press.x, e.clientY - press.y);
        if (!press.dragging && moved > 4) { press.dragging = true; try { canvas.setPointerCapture(e.pointerId); } catch (x) {} }
        if (press.dragging) {
          const [x0, y0] = local(canvas, { clientX: press.x, clientY: press.y }), [x1, y1] = local(canvas, e);
          st.pan = [press.pan[0] - (x1 - x0) / st.layout.scale, press.pan[1] + (y1 - y0) / st.layout.scale];
          canvas.style.cursor = 'grabbing';
          later(st);
          return;
        }
      }
      const [x, y] = local(canvas, e), hit = pickAt(st, x, y);
      const key = hit ? hit.kind + ':' + [hit.index, hit.i, hit.k, hit.ix, hit.iz, hit.y0, hit.all].join() : '';
      canvas.style.cursor = hit && hit.kind !== 'none' ? 'pointer' : '';
      const title = describe(hit, st);
      if (canvas.title !== title) canvas.title = title;
      if (key !== st.hoverKey) {
        st.hoverKey = key;
        st.hover = hit;
        later(st);
        if (st.hoverCb) st.hoverCb(hit && hit.kind !== 'none' ? hit : null);
      }
    });
    const release = e => {
      if (!press || press.id !== e.pointerId) return;
      const was = press;
      press = null;
      canvas.style.cursor = '';
      if (was.dragging || e.type === 'pointercancel') return;
      const [x, y] = local(canvas, e), hit = pickAt(st, x, y);
      if (!hit) return;
      if (hit.kind === 'level') {   // the column of heights is the slice's own: it redraws by itself
        st.level = hit.all ? null : { j: hit.j, y0: hit.y0, y1: hit.y1 };
        later(st);
      }
      const { button, box, ...picked } = hit;
      if (st.onPick) st.onPick(picked);
    };
    on('pointerup', release);
    on('pointercancel', release);
    on('mouseleave', () => {
      if (st.hoverKey) { st.hoverKey = ''; st.hover = null; later(st); if (st.hoverCb) st.hoverCb(null); }
    });
    // The wheel zooms towards the pointer: the place under it stays under it.
    on('wheel', e => {
      const lay = st.layout;
      if (!lay || lay.compact) return;
      e.preventDefault();
      const [x, y] = local(canvas, e), wx = lay.wx(x), wz = lay.wz(y);
      st.zoom = clamp(st.zoom * Math.exp(-clamp(e.deltaY, -300, 300) * 0.0015), 1, 48);
      const scale = lay.base * st.zoom, foot = lay.foot;
      st.pan = [wx - (x - lay.mx) / scale - (foot.x0 + foot.x1) / 2, wz + (y - lay.my) / scale - (foot.z0 + foot.z1) / 2];
      if (st.zoom === 1) st.pan = [0, 0];
      later(st);
    }, { passive: false });
    on('dblclick', () => { if (st.layout && !st.layout.compact) st.handle.reset(); });
  });
  st.scene = scene;
  st.view = view;
  st.onPick = typeof onPick === 'function' ? onPick : null;
  drawSlice(st);
  return st.handle;
}

// ============================================================ the self-test (heatmap.html?selftest)
// Checks the grid numbers against heat-grid.js and the spec, and that both drawings face the right way:
// north up and east right from above (Colonia upper left of Sol, as on the flat map), the highest layer at the
// top of the strip, the middle line just above the 31/32 seam. Answers {ok, checks:[{name, ok, got, want}]}.
function selftest() {
  const checks = [];
  const check = (name, got, want) => checks.push({ name, ok: JSON.stringify(got) === JSON.stringify(want), got, want });
  check('layer of y 27', layerOf(27), 32);
  check('layer of y -25', layerOf(-25), 32);
  check('layer of y -25.03', layerOf(-25.03), 31);
  check('floor of layer 32', layerFloor(32), -25);
  check('Wredguia west and south edges', [west(38), west(39), south(18), south(19)], [-1345, -65, -1065, 215]);
  check('layer names', [29, 30, 31, 32, 33, 34].map(layerName), ['Deep below', 'Far below', 'Just below the middle', 'Just above the middle', 'Far above', 'Deep above']);
  check('far layers are counted from the middle, as in Galaxy.cs', [28, 35].map(layerName), ['4 layers below the middle', '4 layers above the middle']);
  const g = window.HeatGrid;
  if (g) {
    if (g.SECTOR != null) check('sector size matches heat-grid.js', g.SECTOR, SECTOR);
    if (Array.isArray(g.ORIGIN)) check('grid origin matches heat-grid.js', Array.from(g.ORIGIN), ORIGIN);
    if (typeof g.layerName === 'function') check('layer names match heat-grid.js', [29, 30, 31, 32, 33, 34].map(iy => g.layerName(iy)), [29, 30, 31, 32, 33, 34].map(layerName));
    if (typeof g.layerOf === 'function') check('layers match heat-grid.js', [27, -25, -25.03, 2600].map(y => g.layerOf(y)), [27, -25, -25.03, 2600].map(layerOf));
  }

  // From above: a block round Sol, drawn 600 x 400, with Colonia and Sol placed on it.
  const st = { scene: { sectors: [{ ix: 39, iy: 32, iz: 18 }], player: null }, view: { layer: 32 }, zoom: 1, pan: [0, 0] };
  const lay = sliceLayout(st, 600, 400), sol = [lay.px(0), lay.py(0)], colonia = [lay.px(-9530.5), lay.py(19808.1)];
  check('Colonia is left of Sol (east is right)', colonia[0] < sol[0], true);
  check('Colonia is above Sol (north is up)', colonia[1] < sol[1], true);
  check('a point maps back to the same place', [Math.round(lay.wx(lay.px(-167.09)) * 100) / 100, Math.round(lay.wz(lay.py(-55.38)) * 100) / 100], [-167.09, -55.38]);
  check('heading 0 points up the canvas', ahead(0).map(Math.round), [0, -1]);
  check('heading 90 points right', ahead(90).map(Math.round), [1, 0]);

  // From the side: layers 31 and 32, drawn 240 x 300.
  const side = stripLayout({ layers: [{ iy: 31, yFrom: -1305, yTo: -25 }, { iy: 32, yFrom: -25, yTo: 1255 }], centre: [38, 32, 18] }, 240, 300);
  check('the higher layer is the top band', side.bands.map(b => b.iy), [32, 31]);
  check('the seam is the line between the bands', Math.abs(side.py(-25) - side.bands[0].bottom) < 1e-6, true);
  check('the middle line is just above the seam', side.py(MIDDLE) < side.py(-25), true);
  check('the player is above the middle line', side.py(26.72) < side.py(MIDDLE), true);
  check('east is right in the strip', side.px(-65) > side.px(-1345), true);
  // A block with a layer missing between two it has: the gap keeps its height but is never the one shown.
  const gap = stripBands({ layers: [{ iy: 30, name: 'x', known: 5 }, { iy: 32, known: null }] });
  check('a missing layer still gets a band', gap.map(b => [b.iy, b.missing]), [[32, false], [31, true], [30, false]]);
  check('bands carry the fixed names', gap.map(b => b.name), ['Just above the middle', 'Just below the middle', 'Far below']);
  check('no count is not the same as none, and the count is of the systems named after the sectors', gap.map(b => knownWords(b)),
    ['no count yet', 'none named after its sectors', '5 named after its sectors']);
  check('the long form names EDAstro', knownWords({ known: 2449629 }, true), '2,449,629 systems named after its sectors (EDAstro)');
  check('a nebula of several spheres is each of them', spheresOf({ x: 0, y: 0, z: 0, r: 40, spheres: [[-60, 0, 0, 40], [60, 0, 0, 40]] }).length, 2);
  check('a nebula of one sphere is the one round its middle', spheresOf({ x: 1, y: 2, z: 3, r: 10 }), [[1, 2, 3, 10]]);
  check('a missing layer is never the shown one', shownIn(gap, {}, { layer: 31 }), 30);
  return { ok: checks.every(x => x.ok), checks };
}

return { strip, slice, selftest };
})();
