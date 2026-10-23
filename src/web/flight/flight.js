// STUDIO FLIGHT: what the trainer (index.html) and the stick setup page (setup.html) share.
//   Flight.curve(x, c)            a stick position through one of Studio's curves (the sums of StickCurve in Flight\Gremlin.cs)
//   Flight.stream(onFrame)         the app's readings of the sticks and keys, about sixty a second, while this page is shown
//   Flight.reader(info)            turns those readings into the ship's commands, with the player's bindings and curves
//   Flight.drawInputs(...)         the throttle hand and the stick hand drawn on a canvas, as the in-game flight overlay draws them
//   Flight.stickLost(frame, info)  the line to show when Studio cannot find the real stick behind pitch, roll or yaw, and why
//   Flight.olderLine(gremlin)      Stick setup's line when the FA Off profile saved for Gremlin is older than the curves
//   Flight.tuneBox(section, ...)   the +/- curve buttons beside a game, for the Flight Assist off or on curves
//   Flight.recorder(o)             every reading of the sticks while a round is flown, and the record kept after it
//   Flight.showRecord(box, record) the record's two views after a round: "Your curve" and "Over the round"
//   Flight.fit(canvas)             a canvas sized to its box for the screen's pixels
//   Flight.keyText / buttonText / axisText / deviceText    a binding in words;  Flight.stickName(stick): a stick in words
//   Flight.deviceNames(binds, sticks) / idsText(names) / devicesInWords(text, binds, names)   a controller Elite knows only by
//                                  Windows' id ("231D0200"), in words, and that id for small print
//   Flight.selftest()              checks the pictures, that line and the curve buttons' rules, [{ name, ok, got, want }]
// Loaded after ../studio.js.
var Flight = (() => {
'use strict';

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

// ---- curves
// The same sum as StickCurve.At: the dead zone either side, how far the stick travels (satX), the most it gives (satY) and a
// softer middle (bend). (Pages show only those plain names; satX, satY and bend are the saved settings' names.)
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
    out.pitch = turn('pitch', 'pitchUp', 'pitchDown', NATIVE.pitch);
    out.roll = turn('roll', 'rollRight', 'rollLeft', NATIVE.roll);
    out.yaw = turn('yaw', 'yawRight', 'yawLeft', NATIVE.yaw);

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
// Which way a stick position turns the ship before the game's invert: Elite's pitch axis is "nose down" for a stick pulled back.
const NATIVE = { pitch: -1, roll: 1, yaw: 1 };

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
// A device as Elite's bindings name it, in words: "LogitechExtreme3DPro" → "Logitech Extreme 3D Pro". Elite names a controller it
// has no name for by Windows' id run together ("231D0200"): that never shows, but the name the page found for it (names, from
// deviceNames) or "Unnamed controller"; the id goes in small print (idsText). names is optional here and below.
const unnamed = device => /^[0-9a-f]{8}$/i.test(String(device || ''));
const idOf = device => (String(device).slice(0, 4) + ':' + String(device).slice(4)).toUpperCase();   // "231D0200" → "231D:0200"
function deviceText(device, names) {
  if (device === 'vJoy') return 'vJoy';
  if (unnamed(device)) return (names && names[String(device).toUpperCase()]) || 'Unnamed controller';
  return String(device || '').replace(/([a-z])([A-Z0-9])/g, '$1 $2').replace(/([A-Z0-9])([A-Z][a-z])/g, '$1 $2');
}
function pressText(k, names) {
  if (k.device === 'Keyboard') return keyText(k.key) + ' key';
  if (k.device === 'Mouse') return k.key.replace(/^Mouse_/, 'Mouse ');
  const pov = /^Joy_POV(\d)(Up|Down|Left|Right)$/.exec(k.key);
  return (pov ? 'Hat ' + pov[2].toLowerCase() : k.key.replace(/^Joy_/, 'Button ')) + ' · ' + deviceText(k.device, names);
}
const buttonText = (b, names) => b.hold.map(k => pressText(k, names)).concat(pressText(b.press, names)).join(' + ');
// A stick Studio sees, in words: its name as Windows gives it, never its hex id ("046D:C215"), which Windows puts in the name of a
// controller it has no name for ("controller 231D:0200", with " #2" behind the second of two alike). The id is for small print.
const stickName = s => String((s && s.name) || '').trim().replace(/^controller [0-9a-f]{4}:[0-9a-f]{4}/i, 'Unnamed controller') || 'Unnamed controller';
const axisText = (a, names) => deviceText(a.device, names) + ' ' + a.axis + ' axis' + (a.inverted ? ', inverted' : '') + (a.deadzone > 0.005 ? ', dead zone ' + Math.round(a.deadzone * 100) + '%' : '');
// Each controller the bindings (GET /api/flight's binds) name only by Windows' id, in words: the name Windows gives the stick with
// that id in the newest reading (sticks), else "Unnamed controller"; a name that comes twice told apart by a number. Keyed by the
// id in capitals: { '231D0200': 'VKBsim Gladiator EVO' }. A page draws its bindings again when the sticks it sees change.
function deviceNames(binds, sticks) {
  const names = {}, seen = {};
  for (const device of Object.keys((binds && binds.devices) || {}).filter(unnamed)) {
    const stick = (sticks || []).find(s => String(s.device).split('#')[0].toUpperCase() === idOf(device));
    const name = stick ? stickName(stick) : 'Unnamed controller';
    seen[name] = (seen[name] || 0) + 1;
    names[device.toUpperCase()] = seen[name] > 1 ? name + ' (' + seen[name] + ')' : name;
  }
  return names;
}
// The small print under bindings shown that way: Windows' id of each controller Elite knows only by it ('' for none).
function idsText(names) {
  const all = Object.entries(names || {});
  if (all.length === 1) return 'Elite knows ' + all[0][1] + ' only by its id in Windows, ' + idOf(all[0][0]) + '.';
  return all.length ? 'Elite knows these controllers only by their ids in Windows: ' + all.map(([d, name]) => name + ' ' + idOf(d)).join(', ') + '.' : '';
}
// The app's own words naming a device as the bindings do (the notes after saving for Gremlin), with each such name in words.
const devicesInWords = (text, binds, names) => Object.keys((binds && binds.devices) || {}).sort((a, b) => b.length - a.length)
  .reduce((said, device) => said.split(device).join(deviceText(device, names)), String(text));

// ---- a real stick Studio cannot find
// The app says when it cannot read the real stick behind pitch, roll or yaw although the game gets a reading (realUnknown). Two
// reasons, told apart from GET /api/flight's answer (info) and the sticks in the reading: the stick Joystick Gremlin reads for
// that control (or the one picked with Detect) is not among those Windows shows, so it is to be plugged in; or nothing tells
// Studio which stick feeds it (Elite reads vJoy and Gremlin's profile has no route there), or that stick is there but not the
// axis, which Detect on Stick setup fixes. A page reading the real stick then flies that control as the game gets it, so the
// FA Off curves change nothing there. The plain line to show, or null. o: { controls (pitch, roll and yaw), effect(one): what
// it means on that page }.
const listed = names => names.length === 1 ? names[0] : names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
const plain = text => String(text || '').toLowerCase().replace(/[^a-z0-9]/g, '');
const flownAsTheGame = one => `${one ? 'it is' : 'they are'} flown as the game gets ${one ? 'it' : 'them'} and your Flight Assist off curves change nothing there`;
function stickLost(frame, info, o = {}) {
  const controls = o.controls || ['pitch', 'roll', 'yaw'], effect = o.effect || flownAsTheGame;
  const lost = controls.filter(n => frame && frame.controls && frame.controls[n] && frame.controls[n].realUnknown);
  if (!lost.length) return null;
  const sticks = (frame && frame.sticks) || [], unseen = new Map(), unknown = [];
  for (const n of lost) {
    const named = stickNamed(info, n);
    const seen = named && sticks.some(s => named.device ? String(s.device).split('#')[0].toLowerCase() === named.device.split('#')[0].toLowerCase()
      : plain(s.name) && (plain(s.name).startsWith(plain(named.name)) || plain(named.name).startsWith(plain(s.name))));
    if (named && !seen) { const key = named.name || named.device; unseen.set(key, { named, controls: [...((unseen.get(key) || {}).controls || []), n] }); }
    else unknown.push(n);
  }
  const lines = [...unseen.values()].map(({ named, controls: on }) => (named.name
    ? `Studio can't see your ${named.name}, the stick Joystick Gremlin reads for ${listed(on)}`
    : `Studio can't see the stick you picked for ${listed(on)} with Detect`) + `, so ${effect(on.length === 1)}. Check it is plugged in.`);
  if (unknown.length) lines.push(`Studio cannot find your real stick behind ${listed(unknown)}, so ${effect(unknown.length === 1)}.`
    + ` On Stick setup, click Detect beside ${unknown.length === 1 ? 'it' : 'each'} and move that control.`);
  return lines.join(' ');
}
// The stick Studio was told to read for a control: the one picked with Detect ({ device }), else the one Gremlin passes on to the
// vJoy axis the game reads ({ name }, as Gremlin keeps it, on vJoy device 1 when the profile feeds several); null for none.
function stickNamed(info, control) {
  if (!info) return null;
  const picked = info.settings && info.settings.sources && info.settings.sources[control];
  if (picked) return { device: String(picked).split(' ')[0] };
  const bound = info.binds && info.binds.axes && info.binds.axes[control];
  if (!bound || String(bound.device).toLowerCase() !== 'vjoy') return null;
  const all = ((info.gremlin && info.gremlin.axes) || []).filter(a => a.mode === 'Default' && a.vjoyAxis === bound.axis);
  const through = all.find(a => a.vjoyDevice === 1) || all[0];
  return through && through.deviceName ? { name: through.deviceName } : null;
}

// ---- Stick setup's line when the FA Off profile saved for Gremlin is older than the curves: which curves changed since
// (GET /api/flight's gremlin.older: the Flight Assist off ones, the on ones, or both). Null when it is up to date.
function olderLine(g) {
  if (!g || !g.outOfDate) return null;
  const o = g.older || {}, which = o.faOff && o.faOn ? 'Flight Assist on and off curves' : o.faOn ? 'Flight Assist on curves' : o.faOff ? 'Flight Assist off curves' : 'curves';
  return 'Your saved Flight Assist off profile is older than your ' + which + ': press "Save the FA Off profile for Gremlin" again'
    + (g.hasFaOff ? ', then open it again in Joystick Gremlin.' : '.');
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
//   axes, rows  which axes and which rows it shows (all of them unless told: the dot game shows pitch and yaw, and no
//            "Most it gives", which changes nothing there); it never changes one it does not show
//   tune.hold(answer)     first thing with each new answer: a change not sent yet stays the player's
//   tune.draw()           the numbers, "Put back" and the line again;  tune.show(axis): that axis's curve
//   tune.graph(frame)     the live graph, once an animation frame, with the newest reading
//   tune.change(axis, fields)  a one-click change (a tip's button), sent at once; "Put back" undoes it too (a promise)
//   tune.save()           sends a change not sent yet, now (a promise);  tune.nudge(key, ±1), tune.putBack(): the buttons
// With no section it is the box's rules alone, for selftest().
const TUNE = [
  { key: 'deadLow', label: 'Dead zone, − side', step: 1, min: 0, max: 25, unit: '%' },
  { key: 'deadHigh', label: 'Dead zone, + side', step: 1, min: 0, max: 25, unit: '%' },
  { key: 'satX', label: 'How far the stick travels', step: 1, min: 50, max: 100, unit: '%' },
  { key: 'satY', label: 'Most it gives', step: 2, min: 20, max: 100, unit: '%' },
  { key: 'bend', label: 'Softer middle', step: 5, min: 0, max: 100, unit: '' },
];
const TUNE_LOOK = `.tune-box{border:1px solid var(--line);border-radius:10px;background:var(--panel);padding:10px 12px;overflow:auto;min-height:0}
.tune-box h2{margin:0 0 8px}
.tune-box .seg{display:grid;width:100%;margin-bottom:6px}
.tune-box .seg button{padding:0 8px;font-size:13px}
.tune-graph{position:relative;height:96px;border:1px solid var(--line);border-radius:8px;background:var(--panel);margin:2px 0 8px}
.tune-graph canvas{position:absolute;inset:0;width:100%;height:100%}
.tune{display:grid;gap:4px}
.tune-row{display:grid;grid-template-columns:minmax(0,1fr) 30px 52px 30px;align-items:center;gap:6px;font-size:12.5px}
.tune-row>span{line-height:1.15}
.tune-row .btn.small{height:26px;padding:0;font-family:var(--hud);font-size:17px;line-height:1;letter-spacing:0}
.tune-row output{text-align:center;font-family:var(--mono);color:var(--accent)}
:root[style*="--accent-hi:"] .tune-row output{color:var(--accent-hi)}
.tune-foot{display:flex;align-items:center;gap:10px;margin-top:8px}`;
function tuneBox(section, { call, info, set = () => 'faOff', note = () => '', changed = () => {}, axes = ['pitch', 'roll', 'yaw'], rows = TUNE.map(t => t.key) }) {
  const shownRows = TUNE.filter(t => rows.includes(t.key));
  let axis = axes[0], pending = null, timer = 0;   // pending: { key, curves } changed by hand and not sent yet
  const before = {};   // per curve set: { curves, own } as they were before the first change on this page, and whether they were the player's
  const clone = v => JSON.parse(JSON.stringify(v));
  const setName = () => set() === 'faOn' ? 'Flight Assist on' : 'Flight Assist off';
  const el = section && build();

  function build() {
    const { h } = Studio;
    if (!document.getElementById('tune-look')) document.head.append(h('style', { id: 'tune-look' }, TUNE_LOOK));
    // The rows are made once and only their numbers change after: a row made afresh under the pointer (each save brings the
    // curves back) would lose a click pressed on the old one. The buttons are live once the curves are known.
    // (Each + and − says which row it is to a screen reader, so no two buttons on a page answer to the same name.)
    const made = shownRows.map(t => ({ t, less: h('button', { class: 'btn small', title: 'Less', 'aria-label': 'Less: ' + t.label, disabled: true, onclick: () => nudge(t.key, -1) }, '−'),
      more: h('button', { class: 'btn small', title: 'More', 'aria-label': 'More: ' + t.label, disabled: true, onclick: () => nudge(t.key, 1) }, '+'), shown: h('output') }));
    const parts = {
      rows: made,
      which: h('span', { class: 'small muted' }, setName()),
      axes: h('div', { class: 'seg', id: 'tune-axis' }, axes.map(a => h('button', { 'data-axis': a, onclick: () => show(a) }, a[0].toUpperCase() + a.slice(1)))),
      canvas: h('canvas', { id: 'tune-graph' }),
      back: h('button', { class: 'btn small', id: 'tune-back', disabled: true, title: 'The curves as they were before your first change on this page', onclick: putBack }, 'Put back'),
      saved: h('span', { class: 'small muted', id: 'tune-saved' }),
      note: h('p', { class: 'small muted', id: 'tune-note' }),
    };
    section.replaceChildren(h('h2', null, 'Curves ', parts.which), parts.axes,
      h('div', { class: 'tune-graph', title: 'Your stick across, what the ship gets up; the shaded band is the dead zone, the dot your stick now' }, parts.canvas),
      h('div', { class: 'tune', id: 'tune' }, made.map(({ t, less, more, shown }) => h('div', { class: 'tune-row' }, h('span', null, t.label), less, shown, more))),
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
  function show(name) { if (axes.includes(name)) axis = name; draw(); }
  // The first change on this page keeps the curves as they were, for "Put back".
  function keep() {
    const now = info(), key = set();
    if (!before[key]) before[key] = { curves: clone(now.settings[key]), own: !!(now.saved && now.saved[key]) };
  }
  function nudge(name, dir) {
    const now = info();
    if (!now) return;
    const key = set(), all = now.settings[key], t = shownRows.find(x => x.key === name);
    if (!t) return;   // a row the box does not show
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

// ---- a round's record, and its two views after the round
// The owner's ruling: after a round the record shows both his curve, with a band where his stick spent its time, beside the
// tips, and a line graph over the round of his stick, what the curve made of it and the dot or target.
// Flight.recorder(o) keeps every reading of the sticks the app sends while a round is flown: each "flight-input" event, taken
// from the stream itself and never once an animation frame (a page draws when it can; the readings come about sixty a second
// whatever it does). For each axis: the real stick (null when Studio has no reading of it), what the curve made of it, what the
// game gets, and how far off the target was in the round's own measure. It listens from the moment it is made until stop().
//   o.axes        the axes it keeps ('pitch', 'yaw', 'roll')
//   o.curves      each axis's curve as flown, or a function giving them as they are now (the trainer's buttons change them at once)
//   o.sign        which way each axis moves the mark or turns the ship (1 or -1); unless told, as the reading's invert has it
//   o.through     what the ship gets: 'curve', the stick through those curves (unless told), or 'game', what the game gets
//   o.target      the target in words, and how it is drawn: { name, aim: true } when the stick aims (the dot game: the curve's
//                 output is the mark's place, and off the dot's distance from it, in the field's units), else { name, edge }:
//                 off in the game's own unit, drawn up to edge
//   o.off(values) how far off the target is now, per axis ({ pitch, yaw }); values: this reading's { raw, curved, game } per axis
//   o.clock()     seconds into the round;  o.active(): whether the round is being flown now (not counting down, paused or over)
//   o.listen(fn)  where the readings come from, unless the app's "flight-input" events; answers a stop
// rec.frames: every reading kept, { t, v: { pitch: { raw, curved, game, off, dead } } };  rec.stop();  rec.compact(): the record
// kept after the round, at most 300 points an axis (each the mean of its share of the readings) and where the stick spent its
// time in 20 parts of its travel: what Flight.showRecord draws, and what a round's history can keep.
const POINTS = 300, BINS = 20;
const reading = v => (v != null && isFinite(v) ? +v : null);
const inDeadZone = (x, c) => !!c && (x < 0 ? -x <= c.deadLow / 100 : x <= c.deadHigh / 100);
function recorder(o) {
  const axes = o.axes, frames = [];
  const now = () => (typeof o.curves === 'function' ? o.curves() : o.curves) || {};
  const pick = c => Object.fromEntries(axes.map(a => [a, { ...c[a] }]));
  let sign = o.sign ? { ...o.sign } : null, first = null, used = null, stopped = false;
  const take = frame => {
    if (stopped || !frame || (o.active && !o.active())) return;
    const c = now(), v = {};
    for (const a of axes) {
      const k = (frame.controls && frame.controls[a]) || {}, raw = reading(k.real);
      v[a] = { raw, curved: raw == null ? null : curve(raw, c[a]), game: reading(k.game), off: null, dead: raw != null && inDeadZone(raw, c[a]) };
    }
    const off = (o.off && o.off(v)) || {};
    for (const a of axes) v[a].off = reading(off[a]);
    if (!o.sign) sign = Object.fromEntries(axes.map(a => [a, (NATIVE[a] || 1) * (frame.controls && frame.controls[a] && frame.controls[a].invert ? -1 : 1)]));
    used = pick(c);
    if (first == null) first = JSON.stringify(used);
    frames.push({ t: o.clock ? o.clock() : 0, v });
  };
  const unlisten = (o.listen || (fn => Studio.onEvent('flight-input', fn)))(take);
  return {
    frames,
    stop: () => { if (!stopped) { stopped = true; unlisten(); } },
    compact: () => compact(frames, axes, { through: o.through || 'curve', target: o.target, sign,
      curves: used || pick(now()), changed: first != null && JSON.stringify(used) !== first }),
  };
}
function compact(frames, axes, kept) {
  const n = frames.length, m = Math.min(n, POINTS), to = (v, k) => (v == null ? null : Math.round(v * k) / k);
  const mean = (from, end, get) => {
    let sum = 0, count = 0;
    for (let i = from; i < end; i++) { const x = get(frames[i]); if (x != null) { sum += x; count++; } }
    return count ? sum / count : null;
  };
  const out = { frames: n, seconds: to(n ? frames[n - 1].t : 0, 100), ...kept, t: [], axes: {} };
  for (const a of axes) out.axes[a] = { raw: [], curved: [], game: [], off: [], band: new Array(BINS).fill(0), dead: 0, none: 0 };
  for (let j = 0; j < m; j++) {
    const from = Math.floor(j * n / m), end = Math.floor((j + 1) * n / m);
    out.t.push(to(mean(from, end, f => f.t), 100));
    for (const a of axes) for (const key of ['raw', 'curved', 'game', 'off']) out.axes[a][key].push(to(mean(from, end, f => f.v[a][key]), 1000));
  }
  for (const f of frames) for (const a of axes) {
    const x = f.v[a], A = out.axes[a];
    if (x.raw == null) { A.none++; continue; }
    A.band[Math.min(BINS - 1, Math.floor((clamp(x.raw, -1, 1) + 1) / 2 * BINS))]++;
    if (x.dead) A.dead++;
  }
  for (const a of axes) {
    const A = out.axes[a], share = k => to(k / Math.max(1, n), 10000);
    Object.assign(A, { band: A.band.map(share), dead: share(A.dead), none: share(A.none) });
  }
  return out;
}

// A record's words for one axis: how much of the round the stick sat in the dead zone, and had no reading.
const share = v => v >= 1 ? '100%' : v <= 0 ? '0%' : v < 0.005 ? 'under 1%' : Math.min(99, Math.round(100 * v)) + '%';
const recordWords = (record, a) => 'In the dead zone ' + share(record.axes[a].dead) + ' of the time'
  + (record.axes[a].none > 0 ? '; no reading of your stick ' + share(record.axes[a].none) : '');

// "Over the round", one axis: the lines from -1 to 1, drawn the way the axis moves the mark or turns the ship (up the graph is
// up, or right). stick: the stick before any curve; through: the dot game's mark (the curve's output over the most it gives,
// the middle with no reading), else what the ship got (the curve's output, or the game's when flown as the game gets it);
// target: the dot's place (the mark's plus how far the dot was from it), else how far off the target was, up to its edge.
function recordSeries(record, a) {
  const A = record.axes[a], sign = (record.sign && record.sign[a]) || 1, c = record.curves && record.curves[a];
  const stick = A.raw.map(v => (v == null ? null : sign * v));
  if (record.target && record.target.aim) {
    const most = v => Math.abs(curve(v < 0 ? -1 : 1, c)) || 1;
    const through = A.curved.map(v => (v == null ? 0 : sign * v / most(v)));
    return { stick, through, target: A.off.map((v, i) => (v == null ? null : through[i] + v)) };
  }
  const edge = (record.target && record.target.edge) || 1;
  return { stick, through: A.curved.map((v, i) => { const x = record.through === 'game' || v == null ? A.game[i] : v; return x == null ? null : sign * x; }),
    target: A.off.map(v => (v == null ? null : clamp(v / edge, -1, 1))) };
}

// Flight.showRecord(box, record, o): the record's two views in box (an empty .record element over a game's field, which it
// fills and keeps drawn at its size); with no record the box is emptied and hidden. o: { names: each axis in words ({ pitch:
// 'Up and down' }), extra: what goes at the top right (what to press next, the trainer's attempts) }.
//   "Your curve": for each axis the curve as flown, bars where the stick spent its time along its travel, the dead zone in grey.
//   "Over the round": for each axis the stick, what the curve made of it and the dot or target, over the round's time.
const RECORD_LOOK = `.record{position:absolute;inset:0;z-index:1;display:grid;grid-template-rows:auto minmax(0,1fr) auto auto minmax(0,1fr) auto;gap:4px;padding:10px 12px;background:#05070b;overflow:auto;cursor:default}
:root[style*="--bg:"] .record{background:var(--bg)}
.record-head{display:flex;align-items:center;flex-wrap:wrap;gap:2px 10px}
.record-head h3{margin:0;font-size:13px;letter-spacing:.06em;text-transform:uppercase;color:var(--text)}
.record-head .extra{margin-left:auto;display:flex;align-items:center;gap:8px;font-size:12px;color:var(--muted)}
.record-head .seg button{min-height:24px;padding:0 8px;font-size:12px;text-transform:none}
.record-row,.record-notes{display:grid;grid-auto-flow:column;grid-auto-columns:minmax(0,1fr);gap:10px}
.record-row{min-height:64px}
.record-pane{position:relative;border:1px solid var(--line);border-radius:8px;min-height:64px}
.record-pane canvas{position:absolute;inset:0;width:100%;height:100%}
.record-notes,.record-key{font-size:12px;color:var(--muted)}
.record-notes b{color:var(--text);font-weight:500}
.record-key{display:flex;flex-wrap:wrap;gap:2px 14px}
.record-key i{display:inline-block;width:14px;height:3px;border-radius:2px;vertical-align:middle;margin-right:5px}`;
const ENDS = { pitch: ['up', 'down'], yaw: ['right', 'left'], roll: ['roll right', 'roll left'] };   // the top and bottom of "Over the round"
const shown = new WeakMap(), watched = new WeakSet();
function showRecord(box, record, o = {}) {
  if (!box) return;
  box.hidden = !record;
  if (!record) { box.replaceChildren(); shown.delete(box); return; }
  const { h } = Studio;
  if (!document.getElementById('record-look')) document.head.append(h('style', { id: 'record-look' }, RECORD_LOOK));
  const axes = Object.keys(record.axes), names = { pitch: 'Pitch', yaw: 'Yaw', roll: 'Roll', ...o.names }, target = record.target || { name: 'the target' };
  const panes = { curve: {}, round: {} };
  const pane = (kind, a, label) => h('div', { class: 'record-pane' }, panes[kind][a] = h('canvas', { role: 'img', 'aria-label': label }));
  const key = (colour, text) => h('span', null, h('i', { style: 'background:var(' + colour + ')' }), text);
  const notes = [record.changed ? 'You changed the curve during this round: it is drawn as it was at the end.' : null,
    record.through === 'game' ? 'You flew "As the game gets it": the ship got what Elite gets, through Joystick Gremlin\'s curve.' : null];
  box.replaceChildren(
    h('div', { class: 'record-head' }, h('h3', null, 'Your curve'),
      h('span', { class: 'small muted' }, 'Bars: where your stick spent its time. Grey: the dead zone.', notes.filter(Boolean).map(n => ' ' + n)),
      o.extra ? h('span', { class: 'extra' }, o.extra) : null),
    h('div', { class: 'record-row' }, axes.map(a => pane('curve', a, names[a] + ': your curve, and where your stick spent its time'))),
    h('div', { class: 'record-notes' }, axes.map(a => h('span', null, h('b', null, names[a]), ' · ' + recordWords(record, a)))),
    h('div', { class: 'record-head' }, h('h3', null, 'Over the round'),
      h('span', { class: 'small muted' }, record.frames.toLocaleString('en-GB') + ' readings of your stick in ' + record.seconds.toFixed(1) + ' s')),
    h('div', { class: 'record-row' }, axes.map(a => pane('round', a, names[a] + ' over the round: your stick, what your curve made of it, and ' + target.name))),
    h('div', { class: 'record-key' }, key('--muted', 'your stick'),
      key('--accent', target.aim ? 'your mark: your stick through your curve' : record.through === 'game' ? 'what the game got' : 'what your curve made of it'),
      key('--good', target.name)));
  shown.set(box, { record, names, panes });
  if (!watched.has(box)) { watched.add(box); new ResizeObserver(() => drawRecord(box)).observe(box); }
  drawRecord(box);
}
function drawRecord(box) {
  const now = shown.get(box);
  if (!now || box.hidden) return;
  const css = getComputedStyle(document.documentElement), v = (name, fallback) => css.getPropertyValue(name).trim() || fallback;
  const ink = { accent: v('--accent', '#ff7100'), muted: v('--muted', '#8a98ad'), good: v('--good', '#3ddc84'), text: v('--text', '#e2e8f0') };
  for (const a of Object.keys(now.record.axes)) {
    curvePane(now.panes.curve[a], now.record, a, now.names[a], ink);
    roundPane(now.panes.round[a], now.record, a, now.names[a], ink);
  }
}
function curvePane(canvas, record, a, name, ink) {
  const { g, w, h } = fit(canvas);
  g.clearRect(0, 0, w, h);
  if (w < 60 || h < 40) return;
  const c = record.curves[a] || {}, band = record.axes[a].band, L = 6, R = 6, T = 18, B = 6, pw = w - L - R, ph = h - T - B;
  const X = v => L + (v + 1) / 2 * pw, Y = v => T + (1 - (v + 1) / 2) * ph;
  // where the stick spent its time: a bar for each twentieth of its travel, the most used one the whole height
  const most = Math.max(...band);
  g.fillStyle = ink.accent;
  g.globalAlpha = 0.35;
  band.forEach((part, i) => { if (part > 0) { const x0 = X(-1 + 2 * i / BINS), x1 = X(-1 + 2 * (i + 1) / BINS), bar = ph * part / most; g.fillRect(x0 + 0.5, T + ph - bar, x1 - x0 - 1, bar); } });
  g.globalAlpha = 1;
  // the dead zone, the middle and the straight line for comparison, then the curve as flown
  g.fillStyle = 'rgba(200,210,225,.2)';
  g.fillRect(X(-(c.deadLow || 0) / 100), T, Math.max(1, X((c.deadHigh || 0) / 100) - X(-(c.deadLow || 0) / 100)), ph);
  g.strokeStyle = 'rgba(255,255,255,.12)';
  g.lineWidth = 1;
  g.beginPath(); g.moveTo(L, Y(0)); g.lineTo(L + pw, Y(0)); g.moveTo(X(0), T); g.lineTo(X(0), T + ph); g.stroke();
  g.setLineDash([3, 4]); g.strokeStyle = 'rgba(255,255,255,.25)';
  g.beginPath(); g.moveTo(X(-1), Y(-1)); g.lineTo(X(1), Y(1)); g.stroke(); g.setLineDash([]);
  g.strokeStyle = ink.accent;
  g.lineWidth = 2;
  g.beginPath();
  for (let i = 0; i <= 120; i++) { const x = -1 + i / 60, y = curve(x, c); if (i === 0) g.moveTo(X(x), Y(y)); else g.lineTo(X(x), Y(y)); }
  g.stroke();
  g.fillStyle = ink.text;
  g.font = '600 12px "Segoe UI",sans-serif';
  g.fillText(name, L + 2, 13);
}
function roundPane(canvas, record, a, name, ink) {
  const { g, w, h } = fit(canvas);
  g.clearRect(0, 0, w, h);
  if (w < 60 || h < 40) return;
  const lines = recordSeries(record, a), times = record.t, end = Math.max(record.seconds, 1e-6), L = 6, R = 6, T = 18, B = 14, pw = w - L - R, ph = h - T - B;
  const X = t => L + clamp(t / end, 0, 1) * pw, Y = v => T + (1 - (clamp(v, -1, 1) + 1) / 2) * ph;
  g.strokeStyle = 'rgba(255,255,255,.12)';
  g.lineWidth = 1;
  g.beginPath(); g.moveTo(L, Y(0)); g.lineTo(L + pw, Y(0)); g.stroke();
  const line = (list, colour, width, dash) => {
    g.strokeStyle = colour;
    g.lineWidth = width;
    g.setLineDash(dash || []);
    g.beginPath();
    let open = false;   // no reading: a gap in the line
    list.forEach((v, i) => { if (v == null) { open = false; return; } if (open) g.lineTo(X(times[i]), Y(v)); else g.moveTo(X(times[i]), Y(v)); open = true; });
    g.stroke();
    g.setLineDash([]);
  };
  line(lines.target, ink.good, 1.5, [5, 3]);
  line(lines.stick, ink.muted, 1.2);
  line(lines.through, ink.accent, 2);
  g.font = '600 12px "Segoe UI",sans-serif';
  g.fillStyle = ink.text;
  g.fillText(name, L + 2, 13);
  g.font = '10px "Segoe UI",sans-serif';
  g.fillStyle = ink.muted;
  g.textAlign = 'right';
  const [top, bottom] = ENDS[a] || ['', ''];
  g.fillText(top, L + pw - 2, 13);
  g.fillText(bottom, L + pw - 2, T + ph - 3);
  g.fillText(record.seconds.toFixed(0) + ' s', L + pw, h - 3);
  g.textAlign = 'left';
  g.fillText('0 s', L, h - 3);
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
  // Q1 (b): a box told which axes and rows it shows (the dot game: pitch and yaw, no "Most it gives") never changes another.
  const twoAxes = () => {
    const sent = [], data = { settings: { faOff: curves(), faOn: curves() }, saved: { faOff: false, faOn: false } };
    const box = tuneBox(null, { call: (method, path, body) => { sent.push(JSON.parse(JSON.stringify(body))); return new Promise(() => {}); }, info: () => data,
      axes: ['pitch', 'yaw'], rows: ['deadLow', 'deadHigh', 'satX', 'bend'] });
    return { sent, data, box };
  };
  check('a box showing pitch and yaw only never turns to roll: + still changes the axis it shows', () => {
    const p = twoAxes();
    p.box.show('yaw');
    p.box.show('roll');
    p.box.nudge('bend', 1);
    p.box.save();
    return p.sent.map(s => [s.faOff.roll.bend, s.faOff.yaw.bend, s.faOff.pitch.bend]);
  }, [[20, 25, 20]]);
  check('a box without the "Most it gives" row never changes it', () => {
    const p = twoAxes();
    p.box.nudge('satY', -1);
    p.box.save();
    return [p.sent.length, p.data.settings.faOff.pitch.satY];
  }, [0, 100]);
  // Q1 (g3): why the real stick is not found. Gremlin names a stick Windows does not show: plug it in. Nothing names one: Detect.
  const vjoyRead = { binds: { axes: { pitch: { device: 'vJoy', axis: 'Y' }, roll: { device: 'vJoy', axis: 'X' }, yaw: { device: 'vJoy', axis: 'RZ' } } },
    gremlin: { axes: [{ mode: 'Default', deviceName: 'Logitech Extreme 3D', axis: 'Y', vjoyDevice: 1, vjoyAxis: 'Y' }] }, settings: { sources: {} } };
  const unknown = (...names) => ({ time: 3, pressed: [], controls: Object.fromEntries(names.map(n => [n, { game: 0.1, real: null, realUnknown: true }])) });
  check('the stick Gremlin reads is not plugged in: the line names that stick, says to plug it in, and does not point to Detect', () => {
    const line = stickLost({ ...unknown('pitch'), sticks: [{ device: '1234:BEAD', name: 'vJoy Device' }] }, vjoyRead);
    return [/can't see your Logitech Extreme 3D/.test(line), /plugged in/.test(line), /behind pitch|for pitch/.test(line), /Detect/.test(line)];
  }, [true, true, true, false]);
  check('nothing tells Studio which stick feeds a control: the line points to Detect, not to plugging in', () => {
    const line = stickLost({ ...unknown('yaw'), sticks: [{ device: '1234:BEAD', name: 'vJoy Device' }] }, vjoyRead);
    return [/behind yaw/.test(line), /Detect/.test(line), /plugged in/.test(line)];
  }, [true, true, false]);
  check('the stick Gremlin names is there but Studio cannot read that axis on it: Detect', () => {
    const line = stickLost({ ...unknown('pitch'), sticks: [{ device: '046D:C215', name: 'Logitech Extreme 3D' }] }, vjoyRead);
    return [/Detect/.test(line), /plugged in/.test(line)];
  }, [true, false]);
  check('both at once: one sentence for the stick to plug in, one for Detect', () => {
    const line = stickLost({ ...unknown('pitch', 'yaw'), sticks: [{ device: '1234:BEAD', name: 'vJoy Device' }] }, vjoyRead);
    return [/Logitech Extreme 3D, the stick Joystick Gremlin reads for pitch/.test(line), /behind yaw/.test(line), /Detect/.test(line)];
  }, [true, true, true]);
  // Q1 (g2): the "older" line on Stick setup names the curves that changed since the FA Off profile was saved.
  check('the saved FA Off profile is older: the line names the curves that changed (Flight Assist on, off, or both)', () => [
    olderLine({ outOfDate: true, older: { faOff: true, faOn: false }, hasFaOff: true }),
    olderLine({ outOfDate: true, older: { faOff: false, faOn: true }, hasFaOff: false }),
    olderLine({ outOfDate: true, older: { faOff: true, faOn: true }, hasFaOff: true }),
    olderLine({ outOfDate: false, older: { faOff: false, faOn: false }, hasFaOff: true })].map(l => l && l.replace(/:.*$/, '')), [
    'Your saved Flight Assist off profile is older than your Flight Assist off curves',
    'Your saved Flight Assist off profile is older than your Flight Assist on curves',
    'Your saved Flight Assist off profile is older than your Flight Assist on and off curves',
    null]);
  check('a press stays inside Stick setup\'s ranges', () => {
    const p = page(false);
    p.data.settings.faOff.pitch.bend = 100;
    p.box.nudge('bend', 1);
    p.box.nudge('deadLow', -1);
    p.box.nudge('deadLow', -1);
    p.box.nudge('deadLow', -1);
    return [p.data.settings.faOff.pitch.bend, p.data.settings.faOff.pitch.deadLow];
  }, [100, 0]);
  // Q2: plain names. The curve box's rows say what each part does (the names curve tools use are only in Stick setup's small
  // print), and a stick is named in words, never by Windows' hex id.
  check('the curve box\'s rows say Dead zone, How far the stick travels, Most it gives and Softer middle', () => TUNE.map(t => t.label),
    ['Dead zone, − side', 'Dead zone, + side', 'How far the stick travels', 'Most it gives', 'Softer middle']);
  check('a stick\'s name in words: Windows\' hex id never in it, the second of two alike still told apart', () => [
    { device: '046D:C215', name: 'Logitech Extreme 3D' }, { device: '231D:0200', name: 'controller 231D:0200' },
    { device: '231D:0200#2', name: 'controller 231D:0200 #2' }, { device: '046D:C215#2', name: 'Logitech Extreme 3D #2' }, { device: '044F:B10A', name: '' }].map(stickName),
    ['Logitech Extreme 3D', 'Unnamed controller', 'Unnamed controller #2', 'Logitech Extreme 3D #2', 'Unnamed controller']);
  // Elite names a controller it has no name for by Windows' id run together ("231D0200"): the bindings show it in words.
  check('a binding on a controller Elite knows only by Windows\' id ("231D0200"): in words, never the id, even before a reading of the sticks', () => [
    axisText({ device: '231D0200', axis: 'Z', inverted: true }), buttonText({ hold: [{ device: '231D0200', key: 'Joy_2' }], press: { device: '231d0200', key: 'Joy_POV1Up' } })],
    ['Unnamed controller Z axis, inverted', 'Button 2 · Unnamed controller + Hat up · Unnamed controller']);
  const idBinds = { devices: { '231D0200': ['231D:0200'], '046DC215': ['046D:C215'], '3344C0DE': ['3344:C0DE'], LogitechExtreme3DPro: ['046D:C215'], vJoy: ['1234:BEAD'] } };
  const idNames = () => deviceNames(idBinds, [{ device: '046D:C215', name: 'Logitech Extreme 3D' }, { device: '1234:BEAD', name: 'vJoy Device' }]);
  check('with a reading of the sticks: the name Windows gives the stick with that id, two unnamed told apart, the ids for small print', () => {
    const names = idNames();
    return [axisText({ device: '046DC215', axis: 'X' }, names), buttonText({ hold: [], press: { device: '231D0200', key: 'Joy_4' } }, names),
      buttonText({ hold: [], press: { device: '3344C0DE', key: 'Joy_6' } }, names), idsText(names), idsText(deviceNames({ devices: { '231D0200': [] } }, [])),
      idsText(deviceNames({ devices: { LogitechExtreme3DPro: ['046D:C215'] } }, []))];
  }, ['Logitech Extreme 3D X axis', 'Button 4 · Unnamed controller', 'Button 6 · Unnamed controller (2)',
    'Elite knows these controllers only by their ids in Windows: Unnamed controller 231D:0200, Logitech Extreme 3D 046D:C215, Unnamed controller (2) 3344:C0DE.',
    'Elite knows Unnamed controller only by its id in Windows, 231D:0200.', '']);
  check('the notes after saving for Gremlin name a controller in words', () => devicesInWords(
    'Gremlin cannot switch modes on button 4 of 231D0200, which switches Flight Assist in the game: use button 2 of LogitechExtreme3DPro.', idBinds, idNames()),
    'Gremlin cannot switch modes on button 4 of Unnamed controller, which switches Flight Assist in the game: use button 2 of Logitech Extreme 3D Pro.');

  // Q4: the round's record. A stand-in for the app's stream of readings (the recorder's own source), and readings of pitch,
  // roll and yaw ("real": the stick before any curve; "game": what the game gets).
  const source = () => {
    const fns = [];
    return { listen: fn => { fns.push(fn); return () => { const at = fns.indexOf(fn); if (at >= 0) fns.splice(at, 1); }; }, send: f => fns.slice().forEach(fn => fn(f)), listening: () => fns.length };
  };
  const reading = (time, pitch, yaw, more = {}) => ({ time, pressed: [], sticks: [{ device: '046D:C215', name: 'Logitech Extreme 3D' }], controls: {
    pitch: { real: pitch, game: pitch, invert: false, ...more.pitch }, yaw: { real: yaw, game: yaw, invert: false, ...more.yaw }, roll: { real: 0, game: 0, invert: false, ...more.roll } } });
  const flat = o => ({ deadLow: 0, deadHigh: 0, satX: 100, satY: 100, bend: 0, ...o });
  const r3 = v => v == null ? v : Math.round(v * 1000) / 1000;
  check('600 readings resting at 3% inside a 4% dead zone: the record says 100% of the time in the dead zone, and the curve gave 0 throughout', () => {
    const src = source(), dz = flat({ deadLow: 4, deadHigh: 4 });
    let t = 0;
    const rec = recorder({ axes: ['pitch', 'yaw'], curves: { pitch: dz, yaw: dz }, target: { name: 'the dot', aim: true }, clock: () => t, listen: src.listen });
    for (let i = 0; i < 600; i++) { t = i / 61; src.send(reading(5000 + i * 16, 0.03, -0.03)); }
    rec.stop();
    const r = rec.compact(), p = r.axes.pitch, y = r.axes.yaw;
    return { frames: r.frames, dead: [p.dead, y.dead], words: recordWords(r, 'pitch'), curved: [...new Set(p.curved.concat(y.curved))], raw: [...new Set(p.raw), ...new Set(y.raw)],
      band: [p.band[10], y.band[9]] };
  }, { frames: 600, dead: [1, 1], words: 'In the dead zone 100% of the time', curved: [0], raw: [0.03, -0.03], band: [1, 1] });
  check('every reading of the round is recorded once, however often the page draws (24 or 144 times a second, readings 61 a second); none while paused', () => {
    const flown = fps => {
      const src = source(), sent = [];
      let clock = 0, paused = false, newest = null;
      const rec = recorder({ axes: ['pitch', 'yaw'], curves: { pitch: flat(), yaw: flat() }, target: { name: 'the dot', aim: true }, off: () => ({ pitch: 0, yaw: 0 }),
        clock: () => clock, active: () => !paused, listen: src.listen });
      // The page: readings come every 1/61 s, whatever it does; it draws (and moves the round on) every 1/fps s. Halfway, a
      // second's pause: its 61 readings are not the round's.
      let draws = 0, i = 0, time = 0;
      while (sent.length < 600) {
        time += 1 / 61;
        while ((draws + 1) / fps <= time) { draws++; if (!paused) clock = draws / fps; }
        if (sent.length === 300 && i < 361) paused = true; else paused = false;
        newest = reading(5000 + Math.round(time * 1000), (i % 500) / 1000, 0);
        i++;
        if (!paused) sent.push(newest.controls.pitch.real);
        src.send(newest);
      }
      rec.stop();
      const got = rec.frames.map(f => f.v.pitch.raw);
      return { recorded: got.length, sameReadings: JSON.stringify(got) === JSON.stringify(sent), stillListening: src.listening() };
    };
    return [flown(24), flown(144)];
  }, [{ recorded: 600, sameReadings: true, stillListening: 0 }, { recorded: 600, sameReadings: true, stillListening: 0 }]);
  check('the record kept after a round stays under its cap: a 4-minute tunnel run (14,640 readings, three axes) keeps 300 points an axis and 20 bins an axis, under 32 KB', () => {
    const src = source(), c = { pitch: flat({ deadLow: 3, deadHigh: 3 }), roll: flat(), yaw: flat({ bend: 40 }) };
    let clock = 0;
    const rec = recorder({ axes: ['pitch', 'yaw', 'roll'], curves: () => c, through: 'curve', target: { name: 'the middle of the tunnel', edge: 120 },
      off: () => ({ pitch: -87.654321 + clock / 7, yaw: 104.123456 - clock / 3, roll: null }), clock: () => clock, listen: src.listen });
    for (let i = 0; i < 14640; i++) { clock = i / 61; src.send(reading(5000 + i * 16, -0.987654321 + (i % 200) / 101, 0.123456789 - (i % 300) / 151)); }
    rec.stop();
    const r = rec.compact(), size = JSON.stringify(r).length;
    return { frames: r.frames, seconds: r.seconds, times: r.t.length, axes: Object.values(r.axes).map(a => [a.raw.length, a.curved.length, a.game.length, a.off.length, a.band.length]),
      under32KB: size < 32768 };
  }, { frames: 14640, seconds: 239.98, times: 300, axes: [[300, 300, 300, 300, 20], [300, 300, 300, 300, 20], [300, 300, 300, 300, 20]], under32KB: true });
  check('a stick Studio cannot read is kept as no reading (null), never as the middle (0), and the record says for how long', () => {
    const src = source();
    const rec = recorder({ axes: ['pitch', 'yaw'], curves: { pitch: flat({ deadLow: 4, deadHigh: 4 }), yaw: flat() }, target: { name: 'the dot', aim: true }, clock: () => 0, listen: src.listen });
    for (let i = 0; i < 30; i++) src.send(reading(5000 + i * 16, null, 0.5, { pitch: { game: 0.2, realUnknown: true } }));
    for (let i = 30; i < 60; i++) src.send(reading(5000 + i * 16, 0.5, 0.5));
    rec.stop();
    const r = rec.compact(), first = rec.frames[0].v.pitch;
    return { first: [first.raw, first.curved, first.game], none: r.axes.pitch.none, inBand: r.axes.pitch.band.reduce((a, b) => a + b, 0), words: recordWords(r, 'pitch') };
  }, { first: [null, null, 0.2], none: 0.5, inBand: 0.5, words: 'In the dead zone 0% of the time; no reading of your stick 50%' });
  check('"Your curve": the band says where the stick spent its time along its travel, in 20 parts', () => {
    const src = source();
    const rec = recorder({ axes: ['yaw'], curves: { yaw: flat() }, target: { name: 'the dot', aim: true }, clock: () => 0, listen: src.listen });
    for (let i = 0; i < 100; i++) src.send(reading(5000 + i * 16, 0, i % 2 ? 0.75 : -0.55));
    rec.stop();
    return rec.compact().axes.yaw.band.map((v, i) => v ? i + ': ' + v : null).filter(Boolean);
  }, ['4: 0.5', '17: 0.5']);
  check('"Over the round", the dot game: your stick, your mark and the dot drawn the way the mark moves, so on the dot the last two meet', () => {
    const src = source(), c = flat({ satY: 60 });   // "Most it gives" 60: the mark still reaches the edge of the field
    const rec = recorder({ axes: ['pitch', 'yaw'], curves: { pitch: c, yaw: c }, sign: { pitch: -1, yaw: 1 }, target: { name: 'the dot', aim: true },
      off: v => ({ pitch: 0.4 - (-1) * v.pitch.raw, yaw: 0.25 - v.yaw.raw }), clock: () => 1, listen: src.listen });
    src.send(reading(5000, 0.5, 0.25));
    rec.stop();
    const r = rec.compact(), p = recordSeries(r, 'pitch'), y = recordSeries(r, 'yaw');
    return [p.stick[0], p.through[0], r3(p.target[0]), y.stick[0], y.through[0], r3(y.target[0])].map(r3);
  }, [-0.5, -0.5, 0.4, 0.25, 0.25, 0.25]);
  check('"Over the round", the trainer: what the ship got (the curve, or the game when flown as the game gets it), and the target up to its edge', () => {
    const run = through => {
      const src = source(), c = flat({ satY: 60 });
      const rec = recorder({ axes: ['pitch', 'yaw'], curves: () => ({ pitch: c, yaw: c }), through, target: { name: 'the target', edge: 21 },
        off: () => ({ pitch: 10.5, yaw: -50 }), clock: () => 1, listen: src.listen });
      src.send(reading(5000, 0.5, 0.5, { pitch: { invert: true, game: 0.1 }, yaw: { game: 0.2 } }));
      rec.stop();
      const r = rec.compact(), p = recordSeries(r, 'pitch'), y = recordSeries(r, 'yaw');
      return [r.sign, p.stick[0], p.through[0], p.target[0], y.through[0], y.target[0]].map(v => typeof v === 'number' ? r3(v) : v);
    };
    return [run('curve'), run('game')];
  }, [[{ pitch: 1, yaw: 1 }, 0.5, 0.3, 0.5, 0.3, -1], [{ pitch: 1, yaw: 1 }, 0.5, 0.1, 0.5, 0.2, -1]]);
  check('the trainer\'s curve changed during an attempt (its +/- buttons): the record says so, and keeps the curve as it was at the end', () => {
    const src = source(), c = { pitch: flat({ bend: 20 }), yaw: flat() };
    const rec = recorder({ axes: ['pitch', 'yaw'], curves: () => c, target: { name: 'the target', edge: 21 }, clock: () => 0, listen: src.listen });
    src.send(reading(5000, 0.5, 0));
    c.pitch = flat({ bend: 30 });
    src.send(reading(5016, 0.5, 0));
    rec.stop();
    const r = rec.compact();
    return [r.changed, r.curves.pitch.bend, rec.frames.map(f => r3(f.v.pitch.curved))];
  }, [true, 30, [r3(curve(0.5, flat({ bend: 20 }))), r3(curve(0.5, flat({ bend: 30 })))]]);
  return checks;
}

return { curve, stream, reader, drawInputs, stickLost, olderLine, tuneBox, recorder, showRecord, fit, keyText, buttonText, axisText, deviceText, stickName,
  deviceNames, idsText, devicesInWords, clamp, look, selftest };
})();
