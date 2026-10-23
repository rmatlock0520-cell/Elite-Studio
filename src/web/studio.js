// ELITE STUDIO: the small layer every desktop page shares.
//
// A page in a studio's folder loads it with <script src="../studio.js"></script> ahead of its own script,
// and needs, in its <head>,
//   <link rel="icon" href="../studio.svg">
//   <link rel="stylesheet" href="../studio.css">
//   <meta name="studio-token" content="__STUDIO_TOKEN__">
// (The three paths start with "..", not with "/": the file opened from disk then still finds them, which
// is what lets it say "Start Elite Studio" instead of showing a bare, dead page.)
// The app swaps that text for the session token each time it serves a page; every call to the app
// must carry the token. Pages talk to the app only through Studio.api() and Studio.onEvent(), never
// through anything that exists only inside the app's own window, so they work the same in a browser.
// The rest is what every page would otherwise write for itself: finding and building elements (Studio.$,
// Studio.h, Studio.options, Studio.stationIcon for the picture of a station's kind), names that open like links
// (Studio.link, with Studio.inspect and Studio.stationSheet for what a click opens), numbers and times in words (Studio.whole, Studio.count, Studio.credits,
// Studio.tons, Studio.ly, Studio.ago, and the dots between the parts of a line: Studio.dot, Studio.wideDot),
// the message at the bottom (Studio.toast), the dialog (Studio.ask, and Studio.nameLength for a name it asks
// for), the clipboard (Studio.copy, Studio.copyName), the whole-page message (Studio.cover), a From box that
// follows the game (Studio.fromBox), keeping one of the app's pictures on the page (Studio.follow) and going
// to another studio's page with something for it to do (Studio.open, or Studio.go for a link that does).
// Every page also wears the player's look (the colour set, the player's own colours over it, spacing and effects the shell
// keeps as "studio.appearance.look"); the shell uses Studio.palettes, Studio.parts and Studio.applyLook for its panels.
// The red "can't reach Elite Studio" bar and the "Start Elite Studio" cover appear by themselves; a page
// has no markup or code for them.
// A page's script starts by taking what it uses and stopping if there is no app:
//   const { $, h, toast } = Studio;
//   if (!Studio.served) return;
// and then shows what the app says and keeps it up to date:
//   Studio.follow('nav', '/api/nav/route', show);
// ("var" on purpose: it makes Studio reachable as window.Studio, which a framed page uses to borrow the shell's live stream.)
var Studio = (() => {
'use strict';

const meta = document.querySelector('meta[name="studio-token"]');
const token = meta ? meta.content : '';
// False when the file was opened from disk: nothing swapped the placeholder, so there is no app to talk to.
// (Only the start of the placeholder is spelled out, so the app's swap can never change this line.)
// Such a page gets the "Start Elite Studio" cover from here; all it does itself is stop: if (!Studio.served) return;
const served = token !== '' && token.indexOf('__STUDIO_') !== 0;
// The shell's own Studio when this page is shown in one of the shell's frames, else null.
let shell = null;
try { if (window.parent !== window && window.parent.Studio) shell = window.parent.Studio; } catch (e) {}   // a foreign parent throws: not our shell
// True when the page is shown in one of the in-game overlay's boxes (the app opens it there with "?overlay"). The
// class on <html> lets the style sheets leave out what only the app window needs, such as the sub-tabs.
const boxed = new URLSearchParams(location.search).has('overlay');
if (boxed) {
  document.documentElement.classList.add('overlay');
  // A box is narrower than the 1400 pixels the pages are made for, and how many pixels it has depends on the display's
  // scaling. So the page draws itself smaller by just as much as it takes to be laid out boxWidth wide, whatever the
  // box measures: the same picture in every box, only bigger or smaller. (innerWidth does not change with this zoom.)
  const boxWidth = 1380;
  const fit = () => { document.documentElement.style.zoom = Math.min(1, window.innerWidth / boxWidth); };
  fit();
  window.addEventListener('resize', fit);
}

// ---- the player's look (appearance profile)
// The shell keeps the look the player chose in localStorage as "studio.appearance.look": a colour set, the player's own
// colours for any of its parts ("colours", see parts below), the spacing and three effects. Every page puts it on its <html> as it opens, and again the moment the shell changes it (the
// storage event reaches every other page of the app: the studios, the in-game boxes, a page in a browser tab).
// Only "studio.appearance.*" keys are read or written for this; every other key belongs to someone else.
// Studio UI does not load this file: it keeps its own colours, which follow the player's HUD.
// The colour sets: null = the one in studio.css (Velvet Ice). "set" gives the accent, its lighter text shade, the
// colour of text on it, the second colour (ice: the big actions), text on that, the live colour and the label colour.
// "ground", when given, also replaces the dark tones: page, panels, lines, field edges and the four text shades.
// Optional extras: "act" = the big actions' words and their top/bottom shades (rest, then hover), "link" = names that
// open and the shade once opened, "glow2" = the second glow behind the page. Left out, they follow the ice and accent colours.
// Space Dust, Firefly Grove and Earthen Flight are taken from the owner's HUD themes of those names.
const palettes = {
  velvet:  { name: 'Velvet Ice', set: null, sw: ['#9466ff', '#45b8ff', '#7cf0ff'] },
  dust:    { name: 'Space Dust', set: ['#8b3cf0', '#bf94ff', '#fff4e0', '#d07a30', '#1a0b00', '#fbef6a', '#d8955a'],
             ground: ['#07030f', '#0d0619', '#140a24', '#1d0f33', '#29164a', '#2a1645', '#3d2266', '#6e5a94', '#f6eedc', '#ad9dc6', '#6e5a94', '#fbe38a'] },
  firefly: { name: 'Firefly Grove', set: ['#3d8bff', '#8ab8ff', '#020c1f', '#ffe14a', '#2a2000', '#e4ff3d', '#b99cf0'],
             ground: ['#03060c', '#060c18', '#0a1426', '#0f1d36', '#16284a', '#172a4a', '#24406c', '#56709a', '#e8f0ff', '#93a6c6', '#56709a', '#ffeaa0'],
             act: ['#ffe14a', 'color-mix(in srgb,#ffe14a 16%,#0f1d36)', 'color-mix(in srgb,#ffe14a 6%,#0a1426)', 'color-mix(in srgb,#ffe14a 24%,#0f1d36)', 'color-mix(in srgb,#ffe14a 10%,#0a1426)'],
             glow2: '#b99cf0' },
  earthen: { name: 'Earthen Flight', set: ['#c1b59a', '#e2d6b8', '#1d1a12', '#ad7690', '#1b0a12', '#eac099', '#b9aa8b'],
             ground: ['#0b0a08', '#12100c', '#1a1712', '#241f18', '#302a20', '#2e281f', '#453c2e', '#7d705a', '#f3ead6', '#aba18b', '#776d5a', '#fff6d6'],
             link: ['#d9a7c0', '#b08ea0'] },
  frost:   { name: 'Frost', set: ['#45b8ff', '#8bd4ff', '#021226', '#9466ff', '#0a0520', '#7cf0ff', '#7fa6d8'] },
  orchid:  { name: 'Orchid Nebula', set: ['#c77dff', '#dcb0ff', '#16061f', '#7cf0ff', '#03222a', '#7cf0ff', '#a98ad8'] },
  classic: { name: 'Classic Orange', set: ['#ff7100', '#ffa45c', '#140900', '#3fa9ff', '#021226', '#7cd8ff', '#c7864d'] },
};
for (const p of Object.values(palettes)) if (p.set) p.sw = [p.set[0], p.set[3], p.set[5]];
const rgba = (hex, a) => { const n = parseInt(hex.slice(1), 16); return `rgba(${n >> 16 & 255},${n >> 8 & 255},${n & 255},${a})`; };
// Two colours mixed, t of the way from the first to the second, as "#rrggbb" (worked out here, not by color-mix, because
// the canvas pages read these colours back to draw with).
const mix = (a, b, t) => {
  const x = parseInt(a.slice(1), 16), y = parseInt(b.slice(1), 16);
  return '#' + [16, 8, 0].map(s => Math.round((x >> s & 255) * (1 - t) + (y >> s & 255) * t).toString(16).padStart(2, '0')).join('');
};
// The words on a colour: a deep shade of it, as the colour sets have, or white on a colour too dark for that.
const ink = c => { const n = parseInt(c.slice(1), 16); return (n >> 16 & 255) * .3 + (n >> 8 & 255) * .59 + (n & 255) * .11 < 40 ? '#ffffff' : mix(c, '#000000', .88); };
// The player's own colours: a look's "colours" gives any of these parts a colour of its own ("#rrggbb"), put on over the
// colour set, in this order (so "Tab and button words" of the player's own wins over the lighter shade "Frames and tabs"
// brings). "v" is the part's colour in studio.css; "vars" gives it and the shades that go with it (null: let
// studio.css work it out, so the big actions follow a new ice colour). The shell's Display panel lists them in this order.
const parts = {
  accent:   { name: 'Frames and tabs', about: 'Borders, the chosen tab and row, sliders', v: '--accent',
              vars: c => ({ '--accent': c, '--accent-hi': mix(c, '#ffffff', .3), '--accent-soft': rgba(c, .16), '--accent-line': rgba(c, .5), '--accent-ink': ink(c) }) },
  accentHi: { name: 'Tab and button words', about: 'The words on tabs, buttons and menus', v: '--accent-hi', vars: c => ({ '--accent-hi': c }) },
  ice:      { name: 'Big buttons', about: 'Copy name, Profile, Help and the other main actions', v: '--ice',
              vars: c => ({ '--ice': c, '--ice-soft': rgba(c, .16), '--ice-line': rgba(c, .55), '--ice-ink': ink(c),
                '--act-ink': null, '--act-top': null, '--act-bot': null, '--act-top-h': null, '--act-bot-h': null }) },
  cyan:     { name: 'Live lights', about: 'What is live or picked, and the big buttons\' words', v: '--cyan', vars: c => ({ '--cyan': c }) },
  frost:    { name: 'Headings', about: 'Card titles and the brand', v: '--frost', vars: c => ({ '--frost': c }) },
  label:    { name: 'Labels', about: 'The names over boxes and in the bars', v: '--label', vars: c => ({ '--label': c }) },
  text:     { name: 'Text', about: 'Everything you read', v: '--text', vars: c => ({ '--text': c }) },
  muted:    { name: 'Dim text', about: 'Hints and small print', v: '--muted', vars: c => ({ '--muted': c, '--faint': mix(c, '#000000', .4) }) },
  link:     { name: 'Names you can open', about: 'Systems, stations and links', v: '--link', vars: c => ({ '--link': c, '--link-seen': mix(c, '#808080', .35) }) },
  bg:       { name: 'Page background', about: 'Behind every page', v: '--bg', vars: c => ({ '--bg': c, '--bg2': mix(c, '#ffffff', .03) }) },
  panel:    { name: 'Cards and panels', about: 'Cards, boxes and the panels that open', v: '--panel',
              vars: c => ({ '--panel': c, '--panel2': mix(c, '#ffffff', .05), '--panel3': mix(c, '#ffffff', .1) }) },
  line:     { name: 'Lines', about: 'Lines between rows, and the edges of boxes', v: '--line',
              vars: c => ({ '--line': c, '--line2': mix(c, '#ffffff', .1), '--edge': mix(c, '#ffffff', .3) }) },
};
const isColour = c => typeof c === 'string' && /^#[0-9a-f]{6}$/i.test(c);
function readLook() { try { const v = JSON.parse(localStorage.getItem('studio.appearance.look') || 'null'); return v && typeof v === 'object' ? v : null; } catch (e) { return null; } }
function applyLook() {
  const look = readLook(), root = document.documentElement, s = root.style;
  const pal = look && Object.prototype.hasOwnProperty.call(palettes, look.palette) ? palettes[look.palette] : null;
  const p = pal && pal.set, g = pal && pal.ground;
  const names = ['--accent', '--accent-hi', '--accent-ink', '--ice', '--ice-ink', '--cyan', '--label'];
  const grounds = ['--bg', '--bg2', '--panel', '--panel2', '--panel3', '--line', '--line2', '--edge', '--text', '--muted', '--faint', '--frost'];
  const acts = ['--act-ink', '--act-top', '--act-bot', '--act-top-h', '--act-bot-h'];
  for (const n of [...names, ...grounds, ...acts, '--accent-soft', '--accent-line', '--ice-soft', '--ice-line', '--link', '--link-seen', '--glow2']) s.removeProperty(n);
  if (p) {
    names.forEach((n, i) => s.setProperty(n, p[i]));
    s.setProperty('--accent-soft', rgba(p[0], .16)); s.setProperty('--accent-line', rgba(p[0], .5));
    s.setProperty('--ice-soft', rgba(p[3], .16)); s.setProperty('--ice-line', rgba(p[3], .55));
  }
  if (g) grounds.forEach((n, i) => s.setProperty(n, g[i]));
  if (pal && pal.act) acts.forEach((n, i) => s.setProperty(n, pal.act[i]));
  if (pal && pal.link) { s.setProperty('--link', pal.link[0]); s.setProperty('--link-seen', pal.link[1]); }
  if (pal && pal.glow2) s.setProperty('--glow2', pal.glow2);
  const own = look && look.colours && typeof look.colours === 'object' ? look.colours : {};
  for (const [k, part] of Object.entries(parts))
    if (isColour(own[k])) for (const [n, v] of Object.entries(part.vars(own[k]))) { if (v == null) s.removeProperty(n); else s.setProperty(n, v); }
  root.classList.toggle('dense', !!look && look.density === 'compact');
  root.classList.toggle('no-stars', !!look && look.stars === false);
  root.classList.toggle('no-glow', !!look && look.glow === false);
  root.classList.toggle('no-motion', !!look && look.motion === false);
}
applyLook();
window.addEventListener('storage', e => { if (e.key === 'studio.appearance.look') applyLook(); });

// ---- building the page
// The first element that matches a CSS selector, or null: $('#save'), $('.modal input').
const $ = selector => document.querySelector(selector);

// Makes an element: h('button', { class: 'btn', title: 'Why', onclick: save }, 'Save').
// attrs (or null): "class" is the class list; a name that starts with "on" and holds a function listens
// for that event; value, checked, selected, disabled and hidden are set as the element's live state;
// everything else becomes an attribute (true = present, without a value). null and false are left out.
// kids: text, numbers, elements, and lists of them however deep. null and false are left out as well,
// so "condition && h(...)" works in the middle of a list.
// Text always goes in as text, never as HTML: a name from the game or from an outside service cannot
// smuggle markup into the page. That is why there is no way to pass HTML here.
function h(tag, attrs, ...kids) {
  const n = document.createElement(tag);
  if (attrs) for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'class') n.className = v;
    else if (k.startsWith('on') && typeof v === 'function') n.addEventListener(k.slice(2), v);
    else if (k === 'value' || k === 'checked' || k === 'selected' || k === 'disabled' || k === 'hidden') n[k] = v;
    else n.setAttribute(k, v === true ? '' : v);
  }
  for (const k of kids.flat(Infinity)) if (k != null && k !== false) n.append(k.nodeType ? k : String(k));
  return n;
}

// The small picture of a station's kind, to go before its name: h('td', null, stationIcon(row.type), row.station).
// The kind is taken as the game's journal spells it ("CraterOutpost") or as Spansh words it ("Planetary
// Outpost"); the mouse resting on the picture shows it in words. A kind with no picture gives null, which h() leaves out.
const stationKinds = [   // [what the kind's name holds, the picture, the kind in words when the name is the journal's]
  [/construction/, null], [/carrier/, 'carrier', 'Fleet carrier'], [/settlement/, 'settlement', 'Settlement'], [/surfacestation/, 'settlement', 'Surface settlement'],
  [/craterport|planetaryport/, 'port', 'Planetary port'], [/crateroutpost|planetaryoutpost/, 'port', 'Planetary outpost'],
  [/coriolis/, 'coriolis', 'Coriolis starport'], [/dodec/, 'coriolis', 'Dodec starport'], [/orbis/, 'orbis', 'Orbis starport'],
  [/ocellus|bernal/, 'ocellus', 'Ocellus starport'], [/asteroid/, 'asteroid', 'Asteroid base'], [/megaship/, 'megaship', 'Mega ship'], [/outpost/, 'outpost', 'Outpost'],
];
function stationIcon(type) {
  const name = String(type || '').toLowerCase().replace(/[^a-z]/g, '');
  const kind = stationKinds.find(([holds]) => holds.test(name));
  if (!kind || !kind[1]) return null;
  return h('span', { class: 'st-icon st-' + kind[1], role: 'img', title: /\s/.test(type) ? type : kind[2], 'aria-label': kind[2] });
}

// ---------------------------------------------------------------------------------------------------------------
// Names that open. Every station, star system and commander a page shows is a link, as on a web site:
//   Studio.link('station', 'Jameson Memorial', 'Shinrarta Dezhra')    Studio.link('system', 'Sol')    Studio.link('cmdr', 'Jameson')
// A click opens its sheet over the page (the inspector, below); the right mouse button brings a short menu: open
// it, copy the name, plot a route to its system. So no page needs a Copy or a Route button beside a name. The
// text can be selected like any other. A name that has been opened is drawn a shade apart, like a visited link.
const opened = new Set();
const nameKey = (kind, name) => kind + '|' + String(name).toLowerCase();
function link(kind, name, system) {
  if (!name) return null;
  const where = kind === 'system' ? name : system || '';
  return h('a', { class: 'lnk' + (opened.has(nameKey(kind, name)) ? ' seen' : ''), href: '#', draggable: 'false',
    'data-kind': kind, 'data-name': name, 'data-system': where,
    title: 'Click to open ' + name + '. Right-click to copy the name' + (where ? ' or plot a route' : '') + '.' }, name);
}

// The menu of a name, at the mouse. It goes with the next click anywhere, with Escape, and when the page scrolls.
let menu = null;
function closeMenu() { if (menu) { menu.remove(); menu = null; } }
function openMenu(a, x, y) {
  closeMenu();
  const { kind, name, system } = a.dataset;
  const item = (text, act) => h('button', { type: 'button', onclick: () => { closeMenu(); act(); } }, text);
  menu = h('div', { class: 'ctx', role: 'menu' },
    item('Open ' + name, () => inspect(kind, name, system)),
    item('Copy "' + name + '"', () => copyName(name)),
    kind === 'station' && system && item('Copy its system, "' + system + '"', () => copyName(system)),
    kind === 'station' && system && item('Open the system ' + system, () => inspect('system', system)),
    system && item('Plot a route to ' + system, () => { closeInspector(); open('nav', '?to=' + encodeURIComponent(system)); }));
  document.body.append(menu);
  // Kept inside the window: a menu opened near the right or the bottom edge opens to the other side.
  const box = menu.getBoundingClientRect(), zoom = Number(document.documentElement.style.zoom) || 1;
  menu.style.left = Math.max(4, Math.min(x / zoom, window.innerWidth / zoom - box.width / zoom - 6)) + 'px';
  menu.style.top = Math.max(4, Math.min(y / zoom, window.innerHeight / zoom - box.height / zoom - 6)) + 'px';
  menu.querySelector('button').focus();
}
// Heard before anything on the page hears it: a name in a row that does something when clicked opens, and the row does nothing.
document.addEventListener('click', e => {
  const a = e.target.closest && e.target.closest('a.lnk');
  if (menu && !menu.contains(e.target)) closeMenu();
  if (!a) return;
  e.preventDefault();
  e.stopPropagation();
  if (String(window.getSelection()).length) return;   // letters were being selected, not a name clicked
  inspect(a.dataset.kind, a.dataset.name, a.dataset.system);
}, true);
document.addEventListener('contextmenu', e => {
  const a = e.target.closest && e.target.closest('a.lnk');
  if (!a) { closeMenu(); return; }
  e.preventDefault();
  e.stopPropagation();
  openMenu(a, e.clientX, e.clientY);
}, true);
document.addEventListener('scroll', closeMenu, true);
document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  if (menu) closeMenu();
  else if (layer) back();
});

// The inspector: the sheet of the name opened, over the page, which stays as it was underneath. A name inside a sheet
// opens the next sheet; Back goes to the one before, and from the first one back to the page.
const trail = [];   // what is open, the newest last: { kind, name, system }
let layer = null;
function inspect(kind, name, system) {
  trail.push({ kind, name, system: system || '' });
  showSheet();
}
function back() {
  trail.pop();
  if (trail.length) showSheet();
  else closeInspector();
}
function closeInspector() {
  trail.length = 0;
  if (layer) { layer.remove(); layer = null; }
}
async function showSheet() {
  const at = trail[trail.length - 1], before = trail[trail.length - 2];
  opened.add(nameKey(at.kind, at.name));
  for (const a of document.querySelectorAll('a.lnk')) if (a.dataset.kind === at.kind && a.dataset.name.toLowerCase() === at.name.toLowerCase()) a.classList.add('seen');
  const body = h('div', { class: 'sheet' }, h('p', { class: 'muted' }, at.kind === 'cmdr' ? '' : 'Asking Spansh about ' + at.name + '…'));
  const fresh = h('div', { class: 'inspector', role: 'dialog', 'aria-label': at.name },
    h('div', { class: 'bar' },
      h('button', { type: 'button', class: 'btn', onclick: back }, before ? '◄ Back to ' + before.name : '◄ Back'),
      h('span', { class: 'what' }, { station: 'Station', system: 'Star system', cmdr: 'Commander' }[at.kind] || ''),
      h('button', { type: 'button', class: 'btn', title: 'Closes every sheet and goes back to the page', onclick: closeInspector }, '✕ Close')),
    h('div', { class: 'scroll' }, body));
  if (layer) layer.replaceWith(fresh); else document.body.append(fresh);
  layer = fresh;
  fresh.querySelector('button').focus();
  const fill = nodes => body.replaceChildren(...[nodes].flat(Infinity).filter(Boolean));
  if (at.kind === 'cmdr') { fill(commanderSheet(at.name)); return; }
  try {
    const found = await api('GET', '/api/market/inspect/' + at.kind + '?name=' + encodeURIComponent(at.name) + (at.kind === 'station' && at.system ? '&system=' + encodeURIComponent(at.system) : ''));
    if (trail[trail.length - 1] !== at) return;   // another name was opened, or Back pressed, while waiting
    fill(at.kind === 'station' ? stationSheet(found, found.station, found.system, undefined, showSheet) : systemSheet(found));
  } catch (problem) {
    if (trail[trail.length - 1] !== at) return;
    fill([h('h2', null, at.name), h('p', { class: 'card bad' }, problem.message)]);
  }
}

// What a sheet is made of: label and value pairs in a list, and "not known" in place of nothing.
const unknown = text => h('span', { class: 'unknown' }, text || 'not known');
const facts = rows => h('dl', null, rows.filter(Boolean).map(([label, value]) => [h('dt', null, label), h('dd', null, value || unknown())]));
const seenAgo = time => time ? ago(time) : unknown('not reported');
const sheetHint = () => h('p', { class: 'muted small hint' }, 'Click a name to open it. Right-click a name to copy it or to plot a route to it.');

// The services a station can have, in the three rows a player thinks of them in: its docks and yards, its offices,
// its concourse. Each with the letters Spansh's name for it holds ("exact" where a shorter name is part of another's).
// A service Spansh names that is none of these gets a tag of its own at the end; what every station has is left out.
const serviceRows = [
  [['Commodity market', ['market'], true], ['Outfitting', ['outfitting']], ['Rearm', ['restock', 'rearm']], ['Refuel', ['refuel']], ['Repair', ['repair']], ['Shipyard', ['shipyard']]],
  [['Black market', ['blackmarket']], ['Contacts', ['contacts']], ['Fleet carrier administration', ['carrieradministration', 'carriermanagement']],
    ['Fleet carrier services', ['carrierservices', 'carrierfuel']], ['Fleet carrier vendor', ['carriervendor']], ['Interstellar factors', ['interstellarfactors', 'facilitator']],
    ['Material trader', ['materialtrader']], ['Power contact', ['powerplay', 'powercontact']], ['Redemption office', ['redemption']], ['Search and rescue', ['searchandrescue', 'searchrescue']],
    ['System colonisation', ['colonisation']], ['Construction services', ['construction']], ['Refinery contact', ['refinery']], ['Technology broker', ['technologybroker', 'techbroker']],
    ['Universal Cartographics', ['universalcartographics', 'exploration']], ['Vendors', ['shop', 'vendors'], true], ['Workshop', ['workshop', 'engineer']]],
  [['Bartender', ['bartender']], ['Concourse', ['socialspace', 'concourse']], ['Crew lounge', ['crewlounge']], ['Frontline Solutions', ['frontlinesolutions']],
    ['Missions', ['missions'], true], ['Pioneer Supplies', ['pioneersupplies']], ['Tuning', ['tuning']], ['Vista Genomics', ['vistagenomics']], ['Apex Interstellar', ['apexinterstellar']]],
];
const everywhere = ['dock', 'autodock', 'flightcontroller', 'stationoperations', 'stationmenu', 'missionsgenerated', 'livery'];
const lettersOf = text => text.toLowerCase().replace(/\(.*\)/, '').replace(/[^a-z]/g, '');

// A station with a technology broker: Spansh's kind for it is a guess from the economy (two of its "Guardian" brokers
// were human, the owner found in October 2026), so the player marks the kind they saw in game, which every broker search
// then goes by. marked() draws the sheet again, with the mark.
function brokerLine(d, marked) {
  if (!d.marketId || !(d.brokerSeen || (d.services || []).some(s => lettersOf(s) === 'technologybroker'))) return null;
  const mark = kind => api('POST', '/api/market/find/broker', { marketId: d.marketId, station: d.station, system: d.system, kind }).then(() => {
    toast(d.station + (kind ? ': marked as a ' + kind + ' broker' : ': your mark is taken away'));
    if (marked) marked();
  }, problem => toast(problem.message, true));
  return h('p', { class: 'small' }, d.brokerSeen
    ? ['You marked its technology broker ' + d.brokerSeen + '. ', h('button', { type: 'button', class: 'btn small', title: 'Takes your mark away: Studio goes by Spansh\'s guess again', onclick: () => mark('') }, 'Undo')]
    : ['Seen its technology broker in game? ', ...['Human', 'Guardian'].map(kind => [h('button', { type: 'button', class: 'btn small',
        title: 'You saw a ' + kind + ' broker here: Studio remembers it in every broker search', onclick: () => mark(kind) }, 'Saw ' + kind), ' '])]);
}

// A station's sheet: its name with what it is, two columns of what is known of it, its services as rows of tags (lit
// when it has them), and for a fleet carrier who may dock. d is what the app knows of it (StationDetails); null when
// only its name and system are known. The Find by name page shows the same sheet over a station's price list.
// marked: draws the sheet again once its broker is marked.
function stationSheet(d, station, system, prices, marked) {
  const acts = h('div', { class: 'acts' },
    prices !== false && h('button', { type: 'button', class: 'btn small', title: 'Opens Find by name with what this station sells and buys',
      onclick: () => { closeInspector(); open('market', 'find.html?find=station&name=' + encodeURIComponent(station)); } }, 'Its price list'),
    !boxed && h('a', { class: 'btn small', href: 'https://inara.cz/elite/station/?search=' + encodeURIComponent(system + ' [' + station + ']'), target: '_blank', rel: 'noopener',
      title: 'Opens this station on Inara in your browser' }, 'Open on Inara ↗'));
  if (!d) {
    return [h('h2', null, link('station', station, system)), facts([['Star system', link('system', system)]]),
      h('p', { class: 'muted small' }, 'Spansh has told Studio nothing more of this station.'), acts, sheetHint()];
  }
  const carrier = /carrier/i.test(d.type);
  const pads = [d.largePads && d.largePads + ' large', d.mediumPads && d.mediumPads + ' medium', d.smallPads && d.smallPads + ' small'].filter(Boolean).join(', ');
  const largest = d.largePads ? 'Large' : d.mediumPads ? 'Medium' : d.smallPads ? 'Small' : '';
  const has = (d.services || []).map(name => ({ name, letters: lettersOf(name), used: false }));
  const tag = ([label, keys, exact]) => {
    const found = has.filter(s => keys.some(key => exact ? s.letters === key : s.letters.includes(key)));
    for (const s of found) s.used = true;
    // The kind of a material trader or technology broker comes in brackets after Spansh's name for it.
    const kind = found.map(s => (s.name.match(/\((.*)\)/) || [])[1]).find(Boolean);
    return h('span', { class: 'tag' + (found.length ? ' has' : '') }, label + (kind ? ' (' + kind + ')' : ''));
  };
  const rows = serviceRows.map(row => h('div', { class: 'tags' }, row.map(tag)));
  const others = has.filter(s => !s.used && !everywhere.includes(s.letters));
  return [
    h('h2', null, stationIcon(d.type),
      carrier && d.carrierName ? [h('span', null, d.carrierName), h('span', { class: 'call' }, '('), link('station', station, system), h('span', { class: 'call' }, ')')] : link('station', station, system),
      h('span', { class: 'call' }, '–'), link('system', system)),
    h('div', { class: 'cols' },
      facts([
        ['Star system', link('system', system)],
        ['Power', d.power ? [d.power, d.powerState && h('span', { class: 'muted' }, ' · ' + d.powerState)] : unknown('none')],
        ['Station distance', d.starLs == null ? null : whole(d.starLs) + ' ls from the star'],
        ['From you', d.distanceLy == null ? null : d.distanceLy.toLocaleString('en-US', { maximumFractionDigits: 2 }) + ' ly'],
        ['Landing pad', largest && [largest, pads && h('span', { class: 'muted' }, '  (' + pads + ')')]],
        ['Station type', d.type],
        d.state && ['State', d.state],
      ]),
      facts([
        ['Economy', (d.economies || []).join(', ')],
        ['Population', d.population ? whole(d.population) + ' in the system' : null],
        ['Government', d.government],
        ['Allegiance', d.allegiance],
        ['Minor faction', d.faction],
        ['Station update', seenAgo(d.updated)],
        ['Market update', seenAgo(d.marketUpdated)],
        ['Shipyard update', seenAgo(d.shipyardUpdated)],
        ['Outfitting update', seenAgo(d.outfittingUpdated)],
      ])),
    h('h3', null, 'Station services'), rows,
    others.length ? h('div', { class: 'tags' }, others.map(s => h('span', { class: 'tag has' }, s.name))) : null,
    brokerLine(d, marked),
    carrier ? [h('h3', null, 'Fleet carrier'), facts([
      ['Docking access', d.access && h('span', { class: d.access === 'open to all' ? 'good' : 'warn' }, d.access)],
      ['Owner', unknown('No data service says who owns a carrier. Inara shows it where the owner has registered: press Open on Inara.')],
    ])] : null,
    acts, sheetHint()];
}

// A star system's sheet: who runs it and how many live there, the factions in it, and every station it has, each a name that opens.
function systemSheet(s) {
  const kinds = [s.stations.some(b => b.market) && 'M = commodity market', s.stations.some(b => b.shipyard) && 'S = shipyard', s.stations.some(b => b.outfitting) && 'O = outfitting'].filter(Boolean).join(', ');
  return [
    h('h2', null, link('system', s.name), s.needsPermit && h('span', { class: 'call warn' }, 'needs a permit')),
    h('div', { class: 'cols' },
      facts([
        ['From you', s.distanceLy == null ? null : s.distanceLy.toLocaleString('en-US', { maximumFractionDigits: 2 }) + ' ly'],
        ['Allegiance', s.allegiance],
        ['Government', s.government],
        ['Security', s.security],
        ['Economy', s.economy],
        ['Population', s.population ? whole(s.population) : unknown('none')],
      ]),
      facts([
        ['Power', s.power ? [s.power, s.powerState && h('span', { class: 'muted' }, ' · ' + s.powerState)] : unknown('none')],
        s.powers.length > 1 && ['Powers in reach', s.powers.join(', ')],
        ['Controlling faction', s.faction && [s.faction, s.factionState && s.factionState !== 'None' && h('span', { class: 'muted' }, ' · ' + s.factionState)]],
        ['Region', s.region],
        ['Bodies', s.bodies ? whole(s.bodies) : null],
        ['Last reported', seenAgo(s.updated)],
      ])),
    s.factions.length ? [h('h3', null, 'Minor factions'), h('div', { class: 'tablebox' }, h('table', null,
      h('thead', null, h('tr', null, ['Faction', 'Influence', 'State', 'Government', 'Allegiance'].map((title, at) => h('th', { class: at === 1 ? 'r' : null }, title)))),
      h('tbody', null, s.factions.map(f => h('tr', null, h('td', null, f.name), h('td', { class: 'r' }, f.influence.toLocaleString('en-US', { maximumFractionDigits: 1 }) + '%'),
        h('td', null, f.state), h('td', null, f.government), h('td', null, f.allegiance))))))] : null,
    h('h3', null, count(s.stations.length, 'station') + (kinds ? ' (' + kinds + ')' : '')),
    s.stations.length ? h('div', { class: 'tablebox' }, h('table', null,
      h('thead', null, h('tr', null, ['Station', 'Type', 'From star (ls)', 'Pad', 'Has'].map((title, at) => h('th', { class: at === 2 ? 'r' : null }, title)))),
      h('tbody', null, s.stations.map(b => h('tr', null, h('td', null, stationIcon(b.type), link('station', b.name, s.name)), h('td', null, b.type),
        h('td', { class: 'r' }, b.starLs == null ? '' : whole(b.starLs)), h('td', null, b.pad || '?'),
        h('td', null, [b.market && 'M', b.shipyard && 'S', b.outfitting && 'O'].filter(Boolean).join(' '))))))) : h('p', { class: 'muted' }, 'No station is known here.'),
    h('div', { class: 'acts' },
      !boxed && h('a', { class: 'btn small', href: 'https://inara.cz/elite/starsystem/?search=' + encodeURIComponent(s.name), target: '_blank', rel: 'noopener', title: 'Opens this system on Inara in your browser' }, 'Open on Inara ↗')),
    sheetHint()];
}

// A commander's sheet. No data service tells one program about another player, so it holds the name and the way to Inara.
function commanderSheet(name) {
  return [h('h2', null, 'CMDR ', link('cmdr', name)),
    h('p', { class: 'muted' }, 'No data service tells Studio anything about other commanders. Inara shows what a commander has chosen to share there.'),
    h('div', { class: 'acts' }, !boxed && h('a', { class: 'btn small', href: 'https://inara.cz/elite/cmdrs/?search=' + encodeURIComponent(name), target: '_blank', rel: 'noopener' }, 'Find this commander on Inara ↗')),
    h('p', { class: 'muted small hint' }, 'Right-click the name to copy it, for a message in the game.')];
}

// Fills a drop-down list from [value, text] entries: options(select, [['near', 'Near me'], ['anywhere', 'Anywhere']]).
// A list that already holds exactly these is left alone (a page draws again with every live event, and an
// open list would close), and what was picked stays picked when it is still there.
function options(select, entries) {
  const mark = JSON.stringify(entries);
  if (select.dataset.mark === mark) return;
  select.dataset.mark = mark;
  const picked = select.value;
  select.replaceChildren(...entries.map(([value, text]) => h('option', { value }, text)));
  select.value = picked;
}

// A "From" box that follows the game: it shows the star system the player is in until they type one of
// their own, and from then on a Here button beside it gives the box back to the game. The button is made
// here and put after the box's label.
//   const from = Studio.fromBox($('#from'));
//   from.show(state.starSystem)   with every game snapshot
//   from.typed                    true while the player's own text stands
function fromBox(input) {
  let typed = false, system = '';
  const here = h('button', { type: 'button', class: 'btn', title: 'Use the system you are in, and keep following it', hidden: true,
    onclick: () => { typed = false; show(system); } }, 'Here');
  input.closest('label').after(here);
  function show(now) {
    system = now || '';
    // (Written only when different: writing a box, even with the same text, moves the cursor in it and closes its list of suggestions.)
    if (!typed && system && input.value !== system) input.value = system;
    here.hidden = !typed || !system;
  }
  input.addEventListener('input', () => { typed = true; show(system); });
  return { show, get typed() { return typed; } };
}

// ---- numbers and times in words
// The dot between two parts of one line: the narrow one inside a phrase ("Type-8 · SE-06M"), the wide one
// between whole statements ("3 stops left   ·   about 9 jumps").
const dot = ' · ', wideDot = '   ·   ';
// A number rounded to a whole one, its thousands set apart: whole(45099.4) is "45,099".
const whole = n => Math.round(n).toLocaleString('en-US');
// So many of a thing: count(1, 'jump') is "1 jump", count(1200, 'place') is "1,200 places".
const count = (n, word) => whole(n) + ' ' + word + (n === 1 ? '' : 's');
// Credits: credits(1888101) is "1,888,101 cr".
const credits = n => whole(n) + ' cr';
// Tons, with one decimal when there is one to show: tons(32) is "32 t", tons(388.61) is "388.6 t".
const tons = n => (+n.toFixed(1)).toLocaleString('en-US') + ' t';
// How far away a place is: ly(84.26) is "84.3 ly", ly(22000.4) is "22,000 ly"; a distance that is not known (null) stays null.
const ly = n => n == null ? null : (n < 1000 ? n.toFixed(1) : whole(n)) + ' ly';
// How long ago a time was (as the app sends times): "just now", "5 min ago", "3 h ago" up to two days, then "4 days ago".
function ago(time) {
  const minutes = Math.floor((Date.now() - Date.parse(time)) / 60000);
  return minutes < 1 ? 'just now' : minutes < 60 ? minutes + ' min ago' : minutes < 2880 ? Math.floor(minutes / 60) + ' h ago' : Math.floor(minutes / 1440) + ' days ago';
}

// ---- messages
// A line at the bottom of the page that goes away by itself: toast('Saved.'), or for a problem
// toast(e.message, true), which has a red edge and stays longer. A new message replaces the one showing.
let toastBox = null, toastTimer = 0;
function toast(text, bad) {
  if (!toastBox) {
    toastBox = h('div', { id: 'toast', role: 'status', 'aria-live': 'polite' });
    document.body.append(toastBox);
    void toastBox.offsetWidth;   // has the browser lay it out hidden first, so the very first message slides in like the rest
  }
  toastBox.textContent = text;
  toastBox.className = 'show' + (bad ? ' bad' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toastBox.className = ''; }, bad ? 7000 : 4500);
}

// Puts a message in place of the whole page, for a page that cannot go on:
// cover('Opening EDSY…', 'Your build is being handed over.'). Calling it again changes the message.
function cover(title, text) {
  for (const part of document.body.children) part.hidden = true;   // also takes the page's buttons out of reach of the Tab key
  document.body.append(h('div', { class: 'cover' }, h('div', { class: 'box' }, h('h2', null, title), h('p', { class: 'muted' }, text))));
}
if (!served) document.addEventListener('DOMContentLoaded', () =>
  cover('Start Elite Studio', 'This page only works when Elite Studio opens it. Start Elite Studio instead of opening this file.'));

// A question in a dialog inside the page (nothing may pop up outside it). Gives a promise of the answer:
//   ask({ title: 'Delete “Trader”?', body: 'It goes to the Recycle Bin.', ok: 'Delete', danger: true })   true, or false
//   ask({ title: 'Save current ship', body: '…', value: 'Suggested name', max: 80, ok: 'Save' })         the text typed, or false
//   ask({ title: 'Copy this build', body: 'Press Ctrl+C…', area: text })                                 text to copy by hand; false when closed
// body is one paragraph or a list of them. "value" adds a text box that starts with that text ("max" is
// the most characters it takes); the answer is what was typed, trimmed, and a box left empty counts as
// Cancel. "danger" makes the OK button the red one. Cancel, Escape and a click beside the box answer false.
// Enter accepts from the text box; on a button it presses that button, so on Cancel it cancels.
// Enter held down is ignored, so one long press cannot also answer the next question (see hush() for
// the same press after the dialog has gone).
// Tab stays inside the dialog, and when it closes the focus goes back to where it was.
// nameLength is the "max" for a name the app saves a file under, a build's say: the longest it takes
// (Paths.LongestFileName; the app itself refuses a longer one).
const nameLength = 80;
function ask({ title, body, value, area, max, ok, danger }) {
  return new Promise(resolve => {
    const before = document.activeElement;
    const done = answer => {
      box.remove();
      document.removeEventListener('keydown', onKey);
      document.addEventListener('keydown', hush, true);
      if (before) before.focus();
      resolve(answer);
    };
    const accept = () => done(value != null ? input.value.trim() || false : true);
    const input = area != null ? h('textarea', { readonly: true, value: area })
      : value != null ? h('input', { type: 'text', maxlength: max, value }) : null;
    const okButton = area == null && h('button', { class: 'btn ' + (danger ? 'danger' : 'primary'), onclick: accept }, ok || 'OK');
    const box = h('div', { class: 'modal', role: 'dialog', 'aria-modal': 'true', onmousedown: e => { if (e.target === box) done(false); } },
      h('div', { class: 'box' }, h('h2', null, title),
        h('div', { class: 'body' }, [].concat(body).map(p => h('p', null, p)), input && h('div', { class: 'field' }, input)),
        h('div', { class: 'foot' }, h('button', { class: 'btn', onclick: () => done(false) }, area != null ? 'Close' : 'Cancel'), okButton)));
    const onKey = e => {
      if (e.key === 'Escape') done(false);
      else if (e.key === 'Enter') {
        const typing = e.target === input && value != null;
        // Enter in the text box, and Enter held down, are used up here. Left alone, the rest of the key press
        // would go to whatever has the focus once the dialog is gone, and press that button as well.
        if (typing || e.repeat) e.preventDefault();
        if (typing && !e.repeat) accept();
      } else if (e.key === 'Tab') {
        // Round and round inside the dialog, never out to the page behind it.
        const stops = [...box.querySelectorAll('input,textarea,button')];
        const at = stops.indexOf(document.activeElement);
        if (at < 0 || at === (e.shiftKey ? 0 : stops.length - 1)) {
          e.preventDefault();
          stops[e.shiftKey ? stops.length - 1 : 0].focus();
        }
      }
    };
    document.addEventListener('keydown', onKey);
    document.body.append(box);
    if (input) { input.focus(); input.select(); } else okButton.focus();
  });
}

// Uses up what is left of the key press that closed a dialog. The focus is back on the page by then,
// usually on the very button that opened the dialog, and Enter still held down would press that button
// over and over: a second "Delete?" while the first is being carried out. A key that keeps repeating was
// down before the dialog closed, so it is swallowed; the next key pressed afresh ends this.
function hush(e) {
  if (e.repeat) e.preventDefault();
  else document.removeEventListener('keydown', hush, true);
}

// Puts text on the clipboard and says whether it got there:
//   if (await Studio.copy(name)) toast('Copied: ' + name); else toast('Could not copy.', true);
async function copy(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch (e) {}
  // The browser would not let this page at the clipboard (it does that to pages that lack the focus): the older way.
  const before = document.activeElement;
  const box = h('textarea', { value: text, style: 'position:fixed;opacity:0' });
  document.body.append(box);
  box.select();
  let done = false;
  try { done = document.execCommand('copy'); } catch (e) {}
  box.remove();
  if (before) before.focus();
  return done;
}

// Copies a name the player will paste somewhere else (a star system, for the galaxy map) and says at the
// bottom of the page whether it got there.
async function copyName(name) {
  if (await copy(name)) toast('Copied: ' + name);
  else toast('Could not copy. Select the name and press Ctrl+C.', true);
}

// ---- from one studio's page to another's
// Goes to a page of a studio and hands it a request, so the player never carries a name across by hand:
//   Studio.open('nav', '?to=' + encodeURIComponent(system))                                     Routes, with that system under To
//   Studio.open('market', 'find.html?find=services&name=' + encodeURIComponent(service))        Find by name, searching at once
//   Studio.open('nav')                                                                          a studio's first page
// "studio" is the studio's folder; "page" is the page in it and what is asked of it, written as the end of
// a link. Inside the shell that studio's tab comes to the front with the page in its frame. A page opened
// on its own in a browser simply goes there. The page that is opened reads the request from its own
// address (new URLSearchParams(location.search)), so it need not know which of the two happened.
function open(studio, page) {
  if (shell) window.parent.postMessage({ studio: 'open', to: studio, page: page || '' }, location.origin);
  // In one of the overlay's boxes the other studio has a box of its own: that one is opened, and this one stays as it is,
  // with its own key still showing and hiding it. (It used to turn into the other page, with no way back.)
  else if (boxed) api('POST', '/api/overlay/open', { box: studio, page: page || '' }).catch(problem => toast(problem.message, true));
  else location.href = '../' + studio + '/' + (page || '');
}
// A link in the middle of a line that does so when clicked (its look is .go in studio.css):
//   Studio.go('Route', 'Opens Studio Nav with Sol under To, ready to plot', 'nav', '?to=Sol')
const go = (text, title, studio, page) => h('button', { class: 'textbtn go', title, onclick: () => open(studio, page) }, text);

// ---- the red bar
// Shown across the top of the page while the app cannot be reached. It is made here the first time it
// is needed and put at the very top; a page that wants it somewhere else has an empty
// <div class="banner" id="banner" hidden></div> there (the shell does, under its tabs).
// A page shown inside the shell gets none: the shell shows the bar for the whole window.
// (The theme editor does not load this file; it has its own copy of the bar and shows it only when opened on its own.)
let linkOk = true, restarted = false;
function setLink(ok, why) {
  if (ok && linkOk) return;
  linkOk = ok;
  if (why === 'restarted') restarted = true;
  let bar = $('#banner');
  if (!bar) {
    if (shell) return;
    bar = h('div', { class: 'banner', id: 'banner' });
    document.body.prepend(bar);
  }
  bar.hidden = ok;
  if (ok) return;
  bar.replaceChildren(why === 'restarted'
    ? 'Elite Studio was restarted. Reload this page to reconnect.'
    : "Can't reach Elite Studio. Start Elite Studio, then reload this page.",
    h('button', { class: 'btn small', onclick: () => location.reload() }, 'Reload'));
}

// ---- calls
// One call to the app, e.g. api('GET', '/api/state') or api('POST', '/api/app/dirty', { count: 2 }).
// Gives the app's JSON answer. Throws an Error whose message is fit to show to the player when the app
// cannot be reached, was restarted (its token changed, so this page must be reloaded), or said no.
async function api(method, path, body) {
  const opt = { method, headers: { 'X-Studio-Token': token } };
  if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  let res;
  try { res = await fetch(path, opt); } catch (e) { setLink(false, 'down'); throw new Error("Can't reach Elite Studio."); }
  let data = null;
  try { data = await res.json(); } catch (e) {}
  if (res.status === 401) { setLink(false, 'restarted'); throw new Error('Elite Studio was restarted. Please reload this page.'); }
  setLink(true);
  if (!res.ok || !data || data.ok === false) throw new Error((data && data.error) || ('Request failed (' + res.status + ')'));
  return data;
}

// ---- live events
// One stream for the whole window, shared by every onEvent() caller. A browser allows only six connections
// to the app at a time, counted over the shell and every studio page framed in it together, and a stream
// holds one for as long as its page lives. So a page framed in the shell borrows the shell's stream instead
// of opening its own; a page opened on its own (a browser tab on /nav/, say) opens one.
let source = null;
const listeners = [];   // [name, listener], kept so a fresh stream gets them again

function listen() {
  source = new EventSource('/api/events?t=' + encodeURIComponent(token));
  for (const [name, fn] of listeners) source.addEventListener(name, fn);
  source.onerror = () => {
    // Ask the app directly, so the red bar says why (or goes away). A dropped connection the browser
    // retries by itself; after an error answer it gives up for good, so start a fresh stream then,
    // unless the app was restarted: this page's token is no longer good and only a reload helps.
    const gaveUp = source.readyState === EventSource.CLOSED;
    api('GET', '/api/state').catch(() => {}).then(() => { if (gaveUp && !restarted) setTimeout(listen, 5000); });
  };
}

// Calls handler(data) for every event of that name the app sends, e.g. onEvent('state', show).
// The name 'open' is the stream's own: it comes with no data each time the stream connects or
// reconnects, which is the moment to re-read whatever may have changed while it was away.
// Gives back a function that stops the calls. A page need not use it: its handlers end with the page.
// For a framed page that is done here, because its handlers sit on the shell's stream and the shell
// outlives it: every click on a .subtabs link puts a new page in the same frame. Left on the stream, each
// page ever shown would stay in memory, and the shell would go on reading every event once more for it.
function onEvent(name, handler) {
  if (shell) {
    const stop = shell.onEvent(name, handler);
    window.addEventListener('pagehide', stop);
    return stop;
  }
  const entry = [name, e => handler(typeof e.data === 'string' ? JSON.parse(e.data) : undefined)];
  listeners.push(entry);
  if (source) source.addEventListener(...entry); else listen();
  return () => {
    const at = listeners.indexOf(entry);
    if (at < 0) return;   // stopped already; without this, a second call would take away somebody else's
    listeners.splice(at, 1);
    source.removeEventListener(...entry);
  };
}

// Keeps one of the app's pictures on the page: follow('nav', '/api/nav/route', show) asks for the picture
// now and again whenever the live stream (re)connects, to catch up on anything missed, and calls
// show(picture) with each answer and with every live event of that name. An answer that a live event
// overtook is older than what is shown, so it is dropped.
// It gives back the call for the page's own requests that answer with the same picture, as most POSTs do:
//   const call = Studio.follow('nav', '/api/nav/route', show);
//   call('POST', '/api/nav/clear', {})   shown under the same rule; a promise of the answer, failing as Studio.api does
function follow(name, path, show) {
  let events = 0;
  const take = before => picture => { if (events === before) show(picture); return picture; };
  onEvent(name, picture => { events++; show(picture); });
  const load = () => { api('GET', path).then(take(events), () => {}); };   // no answer: the red bar says so
  onEvent('open', load);
  load();
  return (method, to, body) => api(method, to, body).then(take(events));
}

// ---- script errors go to the app's log, so a blank or broken page can be diagnosed from the log file
let reported = 0;   // capped: one broken loop must not fill the log
function report(text) {
  if (served && reported++ < 20) api('POST', '/api/app/log', { message: 'Page ' + location.pathname + ': ' + text }).catch(() => {});
}
window.addEventListener('error', e => report((e.message || 'error') + (e.filename ? ' (line ' + e.lineno + ')' : '')));
window.addEventListener('unhandledrejection', e => report('Unhandled: ' + ((e.reason && e.reason.message) || e.reason)));

return { served, boxed, api, onEvent, follow, open, go, $, h, stationIcon, link, inspect, stationSheet, options, fromBox, whole, count, credits, tons, ly, ago, dot, wideDot, toast, ask, nameLength, copy, copyName, cover, palettes, parts, applyLook };
})();
