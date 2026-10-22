// STUDIO FLIGHT: what the trainer (index.html) and the stick setup page (setup.html) share.
//   Flight.curve(x, c)            a stick position through one of Studio's curves (the sums of StickCurve in Flight\Gremlin.cs)
//   Flight.stream(onFrame)         the app's readings of the sticks and keys, about sixty a second, while this page is shown
//   Flight.reader(info)            turns those readings into the ship's commands, with the player's bindings and curves
//   Flight.drawInputs(...)         the throttle hand and the stick hand drawn on a canvas, as the in-game flight overlay draws them
//   Flight.stickLost(frame)        the line to show when Studio cannot find the real stick behind pitch, roll or yaw
//   Flight.tuneBox(section, ...)   the +/- curve buttons beside a game, for the Flight Assist off or on curves
//   Flight.fit(canvas)             a canvas sized to its box for the screen's pixels
//   Flight.keyText / buttonText / axisText / deviceText    a binding in words
//   Flight.selftest()              checks the pictures, that line and the curve buttons' rules, [{ name, ok, got, want }]
// Loaded after ../studio.js.
var Flight = (() => {
'use strict';

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

// ---- curves
// The same sum as StickCurve.At: the dead zone either side, the travel used (satX), the most given (satY) and a softer middle (bend).
function curve(x, c) {
  if (!c || !isFinite(x)) return 0;
  const dead = (x < 0 ? c.deadLow : c.deadHigh) / 100, travel = Math.max(c.satX / 100, dead + 0.01);
  const t = clamp((Math.abs(x) - dead) / (travel - dead), 0, 1), k = c.bend / 100;
  return Math.sign(x) * c.satY / 100 * ((1 - k) * t + k * t * t * t);
}

// ---- the stream
// The app reads the sticks only while a page asks: every second and a half while this one is shown. (In the shell a studio
// whose tab is not in front keeps its frame, only hidden, and the browser still calls such a page "visible".)
function stream(onFrame) {
  const shown = () => {
    if (document.visibilityState !== 'visible') return false;
    try { return !(window.frameElement && window.frameElement.hidden); } catch (e) { return true; }
  };
  const ask = () => { if (shown()) Studio.api('POST', '/api/flight/stream', {}).catch(() => {}); };
  ask();
  const timer = setInterval(ask, 1500);
  document.addEventListener('visibilitychange', ask);
  const stop = Studio.onEvent('flight-input', onFrame);
  return () => { clearInterval(timer); document.removeEventListener('visibilitychange', ask); stop(); };
}

// ---- commands
// Builds a reader that turns one reading into what the ship is told, each from -1 to 1:
//   pitch (nose up), roll (right wing down), yaw (nose right), lateral (right), vertical (up), ahead (forward thrust),
//   plus the throttle as set (kept between readings, from its axis or the speed buttons), boost (pressed just now),
//   pressed (the flight actions held), and for pitch, roll and yaw: ship (the stick as the ship gets it, before the game's
//   invert: the real stick through the FA Off curves, or what the game gets), real (the stick before any curve) and game.
// info is GET /api/flight; useStick: the real stick with Studio's FA Off curves (else what the game gets, through vJoy).
// read.reset() puts the throttle back to zero, for a new attempt.
function reader() {
  let throttle = 0, before = new Set(), lastTime = 0;
  read.reset = () => { throttle = 0; };
  return read;
  function read(frame, info, useStick) {
    const out = { pitch: 0, roll: 0, yaw: 0, lateral: 0, vertical: 0, ahead: 0, throttle: 0, boost: false, pressed: new Set(), ship: {}, real: {}, game: {}, source: {} };
    if (!frame) return out;
    const pressed = new Set(frame.pressed || []);
    out.pressed = pressed;
    const seconds = lastTime ? clamp((frame.time - lastTime) / 1000, 0, 0.2) : 0;
    lastTime = frame.time;
    const off = info && info.settings.faOff;
    const c = name => (frame.controls || {})[name] || {};
    const keys = (plus, minus) => (pressed.has(plus) ? 1 : 0) - (pressed.has(minus) ? 1 : 0);

    // Turning: the game's invert decides which way is which (Elite's pitch axis is "nose down" for a stick pulled back unless inverted).
    const turn = (name, plusKey, minusKey, nativeSign) => {
      const v = c(name);
      out.real[name] = v.real;
      out.game[name] = v.game;
      let value = null;
      if (useStick && v.real != null && off) { value = curve(v.real, off[name]); out.source[name] = 'stick'; }
      else if (v.game != null) { value = v.game; out.source[name] = 'game'; }
      out.ship[name] = value;
      const sign = nativeSign * (v.invert ? -1 : 1);
      const fromKeys = keys(plusKey, minusKey);
      if (fromKeys) out.source[name] = 'keys';
      return clamp((value == null ? 0 : sign * value) + fromKeys, -1, 1);
    };
    out.pitch = turn('pitch', 'pitchUp', 'pitchDown', -1);
    out.roll = turn('roll', 'rollRight', 'rollLeft', 1);
    out.yaw = turn('yaw', 'yawRight', 'yawLeft', 1);

    const lat = c('lateral'), vert = c('vertical'), ahead = c('ahead'), thr = c('throttle');
    out.lateral = clamp((lat.game != null ? (lat.invert ? -1 : 1) * lat.game : 0) + keys('right', 'left'), -1, 1);
    out.vertical = clamp((vert.game != null ? (vert.invert ? 1 : -1) * vert.game : 0) + keys('up', 'down'), -1, 1);
    // Pushed away is ahead unless the game inverts it; with Elite's "forward only" range the axis runs from stop to full ahead.
    const forwardOnly = /forward/i.test((info && info.binds && info.binds.values && info.binds.values.ThrottleRange) || '');
    if (thr.game != null) { const t = thr.invert ? thr.game : -thr.game; throttle = forwardOnly ? (t + 1) / 2 : t; }
    else {
      for (const [button, to] of PRESETS) if (pressed.has(button) && !before.has(button)) throttle = to;
      if (pressed.has('throttleUp')) throttle = Math.min(1, throttle + seconds * 0.6);
      if (pressed.has('throttleDown')) throttle = Math.max(-1, throttle - seconds * 0.6);
    }
    out.throttle = throttle;
    out.ahead = clamp(throttle + (ahead.game != null ? (ahead.invert ? 1 : -1) * ahead.game : 0) + keys('forward', 'backward'), -1, 1);
    out.boost = pressed.has('boost') && !before.has('boost');
    before = pressed;
    return out;
  }
}
const PRESETS = [['speedMinus100', -1], ['speedMinus75', -0.75], ['speedMinus50', -0.5], ['speedMinus25', -0.25], ['speedZero', 0],
  ['speed25', 0.25], ['speed50', 0.5], ['speed75', 0.75], ['speed100', 1]];

// ---- the look's colours
// What the look the player picked sets on <html> (studio.js puts its colours there as a page opens and when the look
// changes), else the colour given. Velvet Ice sets none there, so in it the pictures stay exactly as they were drawn
// before the looks came; the other looks' grounds, text shades and accent shades reach the canvases this way.
const look = (name, fallback) => document.documentElement.style.getPropertyValue(name).trim() || fallback;

// ---- the hands, drawn
// The same picture as the in-game flight overlay (Flight\FlightOverlay.cs), on a canvas: left the throttle hand, right
// the stick hand. cmd is what the reader gave; a red dot is what the ship gets, a white ring the real stick when that
// differs. Fits itself into w x h (CSS pixels) of the context it is given.
function drawInputs(g, w, h, cmd, faOff, accent) {
  const s = Math.min(w / 600, h / 168);
  g.save();
  g.translate((w - 600 * s) / 2, (h - 168 * s) / 2);
  g.scale(s, s);
  const red = '#ff4a3d', lit = '#ffb020', dim = look('--muted', '#8a98ad'), ink = look('--accent-ink', '#0a0e14');
  const frame = (x, y, fw, fh, name, vertical, cross) => {
    g.fillStyle = 'rgba(0,0,0,.25)';
    g.strokeStyle = accent;
    g.lineWidth = 1.5;
    rounded(g, x, y, fw, fh, 7);
    g.fill();
    g.stroke();
    g.strokeStyle = 'rgba(255,255,255,.33)';
    g.lineWidth = 1;
    g.beginPath();
    if (cross || !vertical) { g.moveTo(x + 6, y + fh / 2); g.lineTo(x + fw - 6, y + fh / 2); }
    if (cross || vertical) { g.moveTo(x + fw / 2, y + 6); g.lineTo(x + fw / 2, y + fh - 6); }
    g.stroke();
    g.fillStyle = dim;
    g.font = '10px "Segoe UI",sans-serif';
    if (vertical) { g.save(); g.translate(x + fw + 3, y + 2); g.rotate(Math.PI / 2); g.fillText(name, 0, 0); g.restore(); }
    else g.fillText(name, x, y + fh + 11);
  };
  const dot = (x, y, ring) => {
    g.beginPath();
    g.arc(x, y, ring ? 7 : 6, 0, 7);
    if (ring) { g.strokeStyle = '#e8eef7'; g.lineWidth = 1.5; g.stroke(); }
    else { g.fillStyle = red; g.fill(); }
  };
  const pill = (x, y, text, on, colour) => {
    g.fillStyle = on ? colour : 'rgba(0,0,0,.25)';
    g.strokeStyle = accent;
    g.lineWidth = 1.5;
    rounded(g, x, y, 64, 22, 11);
    g.fill();
    g.stroke();
    g.fillStyle = on ? (colour === lit ? '#0a0e14' : ink) : dim;   // on the accent, the look's own text-on-accent colour
    g.font = '600 11px "Segoe UI",sans-serif';
    g.textAlign = 'center';
    g.fillText(text, x + 32, y + 15);
    g.textAlign = 'left';
  };
  const at = (v, start, size) => start + 6 + (size - 12) / 2 * (1 + clamp(isFinite(v) ? v : 0, -1, 1));
  const pressed = cmd ? cmd.pressed : new Set();

  g.fillStyle = look('--accent-hi', accent);   // the headings' shade, which reads better than the accent on a dark ground
  g.font = '600 13px "Segoe UI",sans-serif';
  g.fillText('FLIGHT INPUTS', 4, 14);
  g.fillStyle = faOff ? lit : dim;
  g.fillText(faOff ? 'FLIGHT ASSIST OFF' : 'flight assist on', 200, 14);

  // throttle hand
  frame(8, 30, 22, 118, 'THROTTLE', true);
  g.fillStyle = pressed.has('forward') ? lit : 'transparent';
  g.fillRect(8, 24, 22, 4);
  g.fillStyle = pressed.has('backward') ? lit : 'transparent';
  g.fillRect(8, 150, 22, 4);
  dot(19, at(-(cmd ? cmd.throttle : 0), 30, 118));
  pill(46, 30, 'BOOST', pressed.has('boost'), accent);
  pill(46, 60, faOff ? 'FA OFF' : 'FA', faOff, lit);
  frame(128, 30, 118, 118, 'LATERAL · VERTICAL', false, true);
  dot(at(cmd ? cmd.lateral : 0, 128, 118), at(cmd ? -cmd.vertical : 0, 30, 118));

  // stick hand
  pill(306, 30, 'CHAFF', pressed.has('chaff'), accent);
  pill(376, 30, 'ECM', pressed.has('ecm'), accent);
  pill(306, 60, 'HEAT', pressed.has('heat'), accent);
  pill(376, 60, 'SHIELD', pressed.has('shield'), accent);
  frame(306, 128, 150, 22, 'YAW', false);
  frame(470, 30, 118, 118, 'PITCH · ROLL', false, true);
  if (cmd) {
    // The stick as the ship gets it (cmd.ship: through the FA Off curves when the reader read the real stick, else as the game
    // gets it); the ring: the real stick, before any curve.
    const shipGets = name => cmd.ship[name] != null ? cmd.ship[name] : cmd[name];
    const yaw = shipGets('yaw'), yawReal = cmd.real.yaw;
    if (yawReal != null && Math.abs(yawReal - yaw) > 0.02) dot(at(yawReal, 306, 150), 139, true);
    dot(at(yaw, 306, 150), 139);
    // A stick pulled back is drawn low; with no stick, "nose up" from the keys is drawn as a stick pulled back.
    const roll = shipGets('roll'), pitch = shipGets('pitch');
    if (cmd.real.roll != null && cmd.real.pitch != null && Math.abs(cmd.real.roll - roll) + Math.abs(cmd.real.pitch - pitch) > 0.02)
      dot(at(cmd.real.roll, 470, 118), at(cmd.real.pitch, 30, 118), true);
    dot(at(roll, 470, 118), at(pitch, 30, 118));
  } else { dot(381, 139); dot(529, 89); }
  g.restore();
}

function rounded(g, x, y, w, h, r) {
  g.beginPath();
  g.moveTo(x + r, y);
  g.arcTo(x + w, y, x + w, y + h, r);
  g.arcTo(x + w, y + h, x, y + h, r);
  g.arcTo(x, y + h, x, y, r);
  g.arcTo(x, y, x + w, y, r);
  g.closePath();
}

// ---- bindings in words
const SPECIAL = { LeftShift: 'Left Shift', RightShift: 'Right Shift', LeftControl: 'Left Ctrl', RightControl: 'Right Ctrl', LeftAlt: 'Left Alt',
  RightAlt: 'Right Alt', CapsLock: 'Caps Lock', UpArrow: '↑', DownArrow: '↓', LeftArrow: '←', RightArrow: '→', PageUp: 'Page Up',
  PageDown: 'Page Down', BackSlash: '\\', Slash: '/', SemiColon: ';', Apostrophe: "'", Grave: '`', LeftBracket: '[', RightBracket: ']',
  Equals: '=', Minus: '-', Comma: ',', Period: '.' };
function keyText(key) {
  const name = String(key || '').replace(/^Key_/, '');
  if (SPECIAL[name]) return SPECIAL[name];
  return name.replace(/^Numpad_/, 'Numpad ');
}
// "LogitechExtreme3DPro" → "Logitech Extreme 3D Pro"
const deviceText = device => device === 'vJoy' ? 'vJoy' : String(device || '').replace(/([a-z])([A-Z0-9])/g, '$1 $2').replace(/([A-Z0-9])([A-Z][a-z])/g, '$1 $2');
function pressText(k) {
  if (k.device === 'Keyboard') return keyText(k.key) + ' key';
  if (k.device === 'Mouse') return k.key.replace(/^Mouse_/, 'Mouse ');
  const pov = /^Joy_POV(\d)(Up|Down|Left|Right)$/.exec(k.key);
  return (pov ? 'Hat ' + pov[2].toLowerCase() : k.key.replace(/^Joy_/, 'Button ')) + ' · ' + deviceText(k.device);
}
const buttonText = b => b.hold.map(pressText).concat(pressText(b.press)).join(' + ');
const axisText = a => deviceText(a.device) + ' ' + a.axis + ' axis' + (a.inverted ? ', inverted' : '') + (a.deadzone > 0.005 ? ', dead zone ' + Math.round(a.deadzone * 100) + '%' : '');

// ---- a real stick Studio cannot find
// The app says when it cannot read the real stick behind pitch, roll or yaw although the game gets a reading (the stick Gremlin
// names is not plugged in, or Elite reads vJoy and nothing tells Studio which stick feeds it): a page reading the real stick
// then flies that control as the game gets it, so the FA Off curves change nothing there. The one plain line to show, or null.
function stickLost(frame) {
  const lost = ['pitch', 'roll', 'yaw'].filter(n => frame && frame.controls && frame.controls[n] && frame.controls[n].realUnknown);
  if (!lost.length) return null;
  const one = lost.length === 1, names = one ? lost[0] : lost.slice(0, -1).join(', ') + ' and ' + lost[lost.length - 1];
  return `Studio cannot find your real stick behind ${names}, so ${one ? 'it is' : 'they are'} flown as the game gets ${one ? 'it' : 'them'}`
    + ` and your Flight Assist off curves change nothing there. On Stick setup, click Detect beside ${one ? 'it' : 'each'} and move that control.`;
}

// ---- a canvas fitted to its box: sized for the screen's pixels, drawn in CSS pixels; answers { g, w, h }
function fit(canvas) {
  const r = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
  const w = Math.max(1, Math.round(r.width * dpr)), hgt = Math.max(1, Math.round(r.height * dpr));
  if (canvas.width !== w || canvas.height !== hgt) { canvas.width = w; canvas.height = hgt; }
  const g = canvas.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { g, w: r.width, h: r.height };
}

// ---- the curve buttons: +/- beside a game
// The owner's ask ("manual + or - curve settings next to the test"): each part of one axis's curve in one curve set, felt at
// once (the page reads the stick through info().settings[set], which a press changes in place), saved a moment after the last
// press, "Put back" for the curves as they were before the first change on this page, and a change not sent yet sent when
// the page is left. The ranges are Stick setup's sliders', so a value set here shows there as it is.
//   const tune = Flight.tuneBox(section, { call, info: () => info, set: () => 'faOff', note: () => '…', changed: putBack => … });
//   section  an empty <section class="tune-box">: the box makes what is in it and brings its own look
//   call     the page's Studio.follow call for /api/flight (a POST answers with the picture the page shows)
//   info     the page's newest GET /api/flight answer; set: the curve set the box edits now, 'faOff' or 'faOn'
//   note     the line under the box; changed(putBack): after each press, and after "Put back" (putBack true)
//   tune.hold(answer)     first thing with each new answer: a change not sent yet stays the player's
//   tune.draw()           the numbers, "Put back" and the line again;  tune.show(axis): that axis's curve
//   tune.graph(frame)     the live graph, once an animation frame, with the newest reading
//   tune.change(axis, fields)  a one-click change (a tip's button), sent at once; "Put back" undoes it too (a promise)
//   tune.save()           sends a change not sent yet, now (a promise);  tune.nudge(key, ±1), tune.putBack(): the buttons
// With no section it is the box's rules alone, for selftest().
const TUNE = [
  { key: 'deadLow', label: 'Dead zone, − side', step: 1, min: 0, max: 25, unit: '%' },
  { key: 'deadHigh', label: 'Dead zone, + side', step: 1, min: 0, max: 25, unit: '%' },
  { key: 'satX', label: 'Travel used', step: 1, min: 50, max: 100, unit: '%' },
  { key: 'satY', label: 'Most it gives', step: 2, min: 20, max: 100, unit: '%' },
  { key: 'bend', label: 'Bend (softer middle)', step: 5, min: 0, max: 100, unit: '' },
];
const TUNE_LOOK = `.tune-box{border:1px solid var(--line);border-radius:10px;background:var(--panel);padding:10px 12px;overflow:auto;min-height:0}
.tune-box h2{margin:0 0 8px}
.tune-box .seg{display:grid;width:100%;margin-bottom:6px}
.tune-box .seg button{padding:0 8px;font-size:13px}
.tune-graph{position:relative;height:96px;border:1px solid var(--line);border-radius:8px;background:var(--panel);margin:2px 0 8px}
.tune-graph canvas{position:absolute;inset:0;width:100%;height:100%}
.tune{display:grid;gap:4px}
.tune-row{display:grid;grid-template-columns:minmax(0,1fr) 30px 52px 30px;align-items:center;gap:6px;font-size:12.5px}
.tune-row .btn.small{height:26px;padding:0;font-family:var(--hud);font-size:17px;line-height:1;letter-spacing:0}
.tune-row output{text-align:center;font-family:var(--mono);color:var(--accent)}
:root[style*="--accent-hi:"] .tune-row output{color:var(--accent-hi)}
.tune-foot{display:flex;align-items:center;gap:10px;margin-top:8px}`;
function tuneBox(section, { call, info, set = () => 'faOff', note = () => '', changed = () => {} }) {
  let axis = 'pitch', pending = null, timer = 0;   // pending: { key, curves } changed by hand and not sent yet
  const before = {};   // per curve set: { curves, own } as they were before the first change on this page, and whether they were the player's
  const clone = v => JSON.parse(JSON.stringify(v));
  const setName = () => set() === 'faOn' ? 'Flight Assist on' : 'Flight Assist off';
  const el = section && build();

  function build() {
    const { h } = Studio;
    if (!document.getElementById('tune-look')) document.head.append(h('style', { id: 'tune-look' }, TUNE_LOOK));
    // The rows are made once and only their numbers change after: a row made afresh under the pointer (each save brings the
    // curves back) would lose a click pressed on the old one. The buttons are live once the curves are known.
    const rows = TUNE.map(t => ({ t, less: h('button', { class: 'btn small', title: 'Less', disabled: true, onclick: () => nudge(t.key, -1) }, '−'),
      more: h('button', { class: 'btn small', title: 'More', disabled: true, onclick: () => nudge(t.key, 1) }, '+'), shown: h('output') }));
    const parts = {
      rows,
      which: h('span', { class: 'small muted' }, setName()),
      axes: h('div', { class: 'seg', id: 'tune-axis' }, ['pitch', 'roll', 'yaw'].map(a => h('button', { 'data-axis': a, onclick: () => show(a) }, a[0].toUpperCase() + a.slice(1)))),
      canvas: h('canvas', { id: 'tune-graph' }),
      back: h('button', { class: 'btn small', id: 'tune-back', disabled: true, title: 'The curves as they were before your first change on this page', onclick: putBack }, 'Put back'),
      saved: h('span', { class: 'small muted', id: 'tune-saved' }),
      note: h('p', { class: 'small muted', id: 'tune-note' }),
    };
    section.replaceChildren(h('h2', null, 'Curves ', parts.which), parts.axes,
      h('div', { class: 'tune-graph', title: 'Your stick across, what the ship gets up; the shaded band is the dead zone, the dot your stick now' }, parts.canvas),
      h('div', { class: 'tune', id: 'tune' }, rows.map(({ t, less, more, shown }) => h('div', { class: 'tune-row' }, h('span', null, t.label), less, shown, more))),
      h('div', { class: 'tune-foot' }, parts.back, parts.saved), parts.note);
    // Leaving the page (another tab, closing Studio) within the moment before a change is sent: it is sent now.
    window.addEventListener('pagehide', () => {
      if (!pending) return;
      clearTimeout(timer);
      const token = document.querySelector('meta[name="studio-token"]').content;
      fetch('/api/flight/settings', { method: 'POST', keepalive: true, headers: { 'X-Studio-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify({ [pending.key]: pending.curves }) }).catch(() => {});
      pending = null;
    });
    return parts;
  }

  function draw() {
    const now = info();
    if (!el || !now) return;
    const key = set(), c = now.settings[key][axis];
    for (const b of el.axes.children) b.classList.toggle('active', b.dataset.axis === axis);
    el.which.textContent = setName();
    for (const { t, less, more, shown } of el.rows) {
      shown.textContent = c[t.key] + t.unit;
      less.disabled = c[t.key] <= t.min;
      more.disabled = c[t.key] >= t.max;
    }
    el.back.disabled = !before[key];
    el.note.textContent = note();
  }
  function show(name) { axis = name; draw(); }
  // The first change on this page keeps the curves as they were, for "Put back".
  function keep() {
    const now = info(), key = set();
    if (!before[key]) before[key] = { curves: clone(now.settings[key]), own: !!(now.saved && now.saved[key]) };
  }
  function nudge(name, dir) {
    const now = info();
    if (!now) return;
    const key = set(), all = now.settings[key], t = TUNE.find(x => x.key === name);
    keep();
    const c = { ...all[axis] };
    // From a value outside the range (set before the ranges matched Stick setup's), the first press brings it inside.
    c[name] = Math.round(clamp(c[name] + dir * t.step, t.min, t.max) * 10) / 10;
    if (pending && pending.key !== key) save();   // a change waiting in the other curve set goes first
    now.settings[key] = { ...all, [axis]: c };
    pending = { key, curves: now.settings[key] };
    draw();
    changed(false);
    clearTimeout(timer);
    timer = setTimeout(save, 400);
  }
  async function save() {
    clearTimeout(timer);
    const sending = pending;
    pending = null;
    if (!sending) return;
    try {
      await call('POST', '/api/flight/settings', { [sending.key]: sending.curves });
      if (!el) return;
      el.saved.textContent = 'Saved.';
      setTimeout(() => { el.saved.textContent = ''; }, 1500);
    } catch (e) { Studio.toast(e.message, true); }
  }
  async function change(name, fields) {
    if (pending) await save();   // a +/- change still waiting goes first, so this one builds on it
    keep();
    const key = set(), all = info().settings[key];
    await call('POST', '/api/flight/settings', { [key]: { ...all, [name]: { ...all[name], ...fields } } });
    show(name);
  }
  function putBack() {
    const key = set(), was = before[key], now = info();
    if (!was) return;
    if (pending && pending.key === key) { clearTimeout(timer); pending = null; }
    delete before[key];
    now.settings[key] = was.curves;
    draw();
    changed(true);
    // Curves that were Studio's starting point go back to being that (null), so they follow again: the FA off ones the FA on
    // curves, the FA on ones Gremlin's.
    call('POST', '/api/flight/settings', { [key]: was.own ? was.curves : null }).catch(e => Studio.toast(e.message, true));
  }
  // The shown axis's curve: the stick across (−1 to 1), what the ship gets up; the dead zone shaded, the straight line
  // dashed for comparison, and a dot where the stick is now.
  function graph(frame) {
    const { g, w, h: hgt } = fit(el.canvas), now = info();
    if (w < 40 || hgt < 30 || !now) return;
    const c = now.settings[set()][axis], css = getComputedStyle(document.documentElement);
    const L = 6, R = 6, T = 6, B = 6, pw = w - L - R, ph = hgt - T - B;
    const X = v => L + (v + 1) / 2 * pw, Y = v => T + (1 - (v + 1) / 2) * ph;
    g.clearRect(0, 0, w, hgt);
    g.fillStyle = css.getPropertyValue('--accent-soft').trim() || 'rgba(255,113,0,.12)';
    g.fillRect(X(-c.deadLow / 100), T, X(c.deadHigh / 100) - X(-c.deadLow / 100), ph);
    g.strokeStyle = 'rgba(255,255,255,.12)';
    g.lineWidth = 1;
    g.beginPath(); g.moveTo(L, Y(0)); g.lineTo(L + pw, Y(0)); g.moveTo(X(0), T); g.lineTo(X(0), T + ph); g.stroke();
    g.setLineDash([3, 4]); g.strokeStyle = 'rgba(255,255,255,.25)';
    g.beginPath(); g.moveTo(X(-1), Y(-1)); g.lineTo(X(1), Y(1)); g.stroke(); g.setLineDash([]);
    g.strokeStyle = css.getPropertyValue('--accent').trim() || '#ff7100'; g.lineWidth = 2;
    g.beginPath();
    for (let i = 0; i <= 120; i++) { const x = -1 + i / 60, y = curve(x, c); if (i === 0) g.moveTo(X(x), Y(y)); else g.lineTo(X(x), Y(y)); }
    g.stroke();
    const real = frame && frame.controls && frame.controls[axis] ? frame.controls[axis].real : null;
    if (real != null && isFinite(real)) {
      g.fillStyle = look('--text', '#e2e8f0');
      g.beginPath(); g.arc(X(clamp(real, -1, 1)), Y(curve(real, c)), 4, 0, 7); g.fill();
    }
  }
  const hold = answer => { if (pending) answer.settings[pending.key] = clone(pending.curves); };
  return { hold, draw, show, graph, change, save, nudge, putBack };
}

// ---- the self-test (Flight.selftest(), from a page's console or a script): the hands picture puts the red dot where the
// ship's stick is (the trainer with the FA Off curves: the real stick through them; Stick setup: what the game gets), the
// trainer says when it cannot find the real stick, and the curve buttons change and send the curve set they were told, with
// "Put back". Answers [{ name, ok, got, want }], as the Nav studio's self-tests do.
function selftest() {
  const checks = [];
  const check = (name, got, want) => {
    let value;
    try { value = got(); } catch (e) { value = 'failed: ' + e.message; }
    checks.push({ name, ok: JSON.stringify(value) === JSON.stringify(want), got: value, want });
  };
  const round = v => Math.round(v * 100) / 100;
  const at = (v, start, size) => round(start + 6 + (size - 12) / 2 * (1 + v));   // where the picture puts a value
  // A canvas that keeps the circles drawn, filled (dots) and outlined (rings), at the picture's own 600 x 168.
  const drawn = cmd => {
    const dots = [], rings = [];
    let path = [];
    const g = new Proxy({}, {
      get: (_, k) => k === 'beginPath' ? () => { path = []; } : k === 'arc' ? (x, y) => path.push([round(x), round(y)])
        : k === 'fill' ? () => dots.push(...path) : k === 'stroke' ? () => rings.push(...path) : () => {},
      set: () => true });
    drawInputs(g, 600, 168, cmd, true, '#ff7100');
    return { dots, rings };
  };
  const straight = { deadLow: 0, deadHigh: 0, satX: 100, satY: 60, bend: 0 };   // gives 0.6 of the stick: 0.5 → 0.3
  const info = { settings: { faOff: { pitch: straight, roll: straight, yaw: straight } }, binds: { values: {} } };
  const frame = { time: 1, pressed: [], controls: { pitch: { game: 0.2, real: 0.5, invert: true }, roll: { game: -0.1, real: -0.25, invert: false }, yaw: { game: 0.3, real: 0.75, invert: false } } };
  const stickDot = pic => pic.dots.find(([x]) => x >= 470);
  const yawDot = pic => pic.dots.find(([x, y]) => y === 139 && x >= 306 && x <= 456);

  const trainer = () => drawn(reader()(frame, info, true));      // "With my FA Off curves"
  const setup = () => drawn(reader()(frame, info, false));       // Stick setup, and the trainer's "As the game gets it"
  check('trainer, with my FA Off curves: the stick dot is the real stick through the curve', () => stickDot(trainer()), [at(-0.15, 470, 118), at(0.3, 30, 118)]);
  check('trainer, with my FA Off curves: the twist dot is the real twist through the curve', () => yawDot(trainer()), [at(0.45, 306, 150), 139]);
  check('trainer: the white ring is the real stick', () => trainer().rings.some(([x, y]) => x === at(-0.25, 470, 118) && y === at(0.5, 30, 118)), true);
  check('Stick setup: the stick dot is what the game gets', () => stickDot(setup()), [at(-0.1, 470, 118), at(0.2, 30, 118)]);
  check('Stick setup: the twist dot is what the game gets', () => yawDot(setup()), [at(0.3, 306, 150), 139]);
  // The app says when it cannot find the real stick behind a control (realUnknown): then it is read as the game gets it.
  const lost = { time: 2, pressed: [], controls: { pitch: { game: 0.2, real: null, invert: true, realUnknown: true }, roll: { game: 0, real: 0, invert: false }, yaw: { game: 0.1, real: null, invert: false, realUnknown: true } } };
  check('a control whose real stick is not found is flown as the game gets it, not curved again', () => { const cmd = reader()(lost, info, true); return [cmd.source.pitch, cmd.pitch]; }, ['game', 0.2]);
  check('the trainer names the controls whose real stick is not found, and where to fix it', () => {
    const line = stickLost(lost);
    return [/pitch and yaw/.test(line), /Detect/.test(line), /Stick setup/.test(line)];
  }, [true, true, true]);
  check('one control: the line names it alone', () => /behind roll[, ]/.test(stickLost({ controls: { roll: { game: 0.1, real: null, realUnknown: true } } })), true);
  check('nothing to say when every real stick is found', () => stickLost(frame), null);

  // The curve buttons' rules, with no page: a stand-in for the page's call keeps what would be sent (and never answers).
  const curves = () => { const c = { deadLow: 2, deadHigh: 2, satX: 100, satY: 100, bend: 20 }; return { pitch: { ...c }, roll: { ...c }, yaw: { ...c } }; };
  const curvesWith = (axis, fields) => { const all = curves(); return { ...all, [axis]: { ...all[axis], ...fields } }; };
  const page = (own, key = 'faOff') => {
    const sent = [], data = { settings: { faOff: curves(), faOn: curves() }, saved: { faOff: own, faOn: own } };
    let set = key;
    const box = tuneBox(null, { call: (method, path, body) => { sent.push([method, path, JSON.parse(JSON.stringify(body))]); return new Promise(() => {}); }, info: () => data, set: () => set });
    return { sent, data, box, use: k => { set = k; } };
  };
  check('+ on pitch "softer middle" waits a moment, then sends the FA off curves with it 5 more', () => {
    const p = page(false);
    p.box.show('pitch');
    p.box.nudge('bend', 1);
    const waited = p.sent.length;
    p.box.save();
    return [waited, p.sent];
  }, [0, [['POST', '/api/flight/settings', { faOff: curvesWith('pitch', { bend: 25 }) }]]]);
  check('a press is felt at once: the curves the page reads change in place', () => { const p = page(false); p.box.nudge('bend', 1); return p.data.settings.faOff.pitch.bend; }, 25);
  check('"Put back" of Studio\'s starting curves sends null (they follow again) and the page reads them as they were', () => {
    const p = page(false);
    p.box.nudge('bend', 1);
    p.box.putBack();
    return [p.sent, p.data.settings.faOff.pitch.bend];
  }, [[['POST', '/api/flight/settings', { faOff: null }]], 20]);
  check('"Put back" of curves the player saved sends them back as they were', () => {
    const p = page(true);
    p.box.nudge('bend', 1);
    p.box.save();
    p.box.putBack();
    return p.sent.map(s => s[2]);
  }, [{ faOff: curvesWith('pitch', { bend: 25 }) }, { faOff: curves() }]);
  check('told to edit the FA on curves, it sends those and leaves the FA off ones alone', () => {
    const p = page(false, 'faOn');
    p.box.nudge('bend', 1);
    p.box.save();
    return [p.sent.map(s => Object.keys(s[2])), p.data.settings.faOn.pitch.bend, p.data.settings.faOff.pitch.bend];
  }, [[['faOn']], 25, 20]);
  check('a change not sent yet stays the player\'s when the app\'s next answer comes', () => {
    const p = page(false), answer = { settings: { faOff: curves(), faOn: curves() } };
    p.box.nudge('bend', 1);
    p.box.hold(answer);
    return answer.settings.faOff.pitch.bend;
  }, 25);
  check('a change waiting in one curve set is sent before one in the other', () => {
    const p = page(false);
    p.box.nudge('bend', 1);
    p.use('faOn');
    p.box.nudge('bend', 1);
    p.box.save();
    return p.sent.map(s => Object.keys(s[2])[0]);
  }, ['faOff', 'faOn']);
  check('a one-click change (a tip\'s) is sent at once and can be put back too', () => {
    const p = page(false);
    p.box.change('roll', { satY: 80 });
    p.box.putBack();
    return p.sent.map(s => s[2]);
  }, [{ faOff: curvesWith('roll', { satY: 80 }) }, { faOff: null }]);
  check('a press stays inside Stick setup\'s ranges', () => {
    const p = page(false);
    p.data.settings.faOff.pitch.bend = 100;
    p.box.nudge('bend', 1);
    p.box.nudge('deadLow', -1);
    p.box.nudge('deadLow', -1);
    p.box.nudge('deadLow', -1);
    return [p.data.settings.faOff.pitch.bend, p.data.settings.faOff.pitch.deadLow];
  }, [100, 0]);
  return checks;
}

return { curve, stream, reader, drawInputs, stickLost, tuneBox, fit, keyText, buttonText, axisText, deviceText, clamp, look, selftest };
})();
