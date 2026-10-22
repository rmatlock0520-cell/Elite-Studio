// STUDIO FLIGHT: what the trainer (index.html) and the stick setup page (setup.html) share.
//   Flight.curve(x, c)            a stick position through one of Studio's curves (the sums of StickCurve in Flight\Gremlin.cs)
//   Flight.stream(onFrame)         the app's readings of the sticks and keys, about sixty a second, while this page is shown
//   Flight.reader(info)            turns those readings into the ship's commands, with the player's bindings and curves
//   Flight.drawInputs(...)         the throttle hand and the stick hand drawn on a canvas, as the in-game flight overlay draws them
//   Flight.stickLost(frame)        the line to show when Studio cannot find the real stick behind pitch, roll or yaw
//   Flight.keyText / buttonText / axisText / deviceText    a binding in words
//   Flight.selftest()              checks the pictures and that line, [{ name, ok, got, want }]
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

// ---- the self-test (Flight.selftest(), from a page's console or a script): the hands picture puts the red dot where the
// ship's stick is (the trainer with the FA Off curves: the real stick through them; Stick setup: what the game gets), and
// the trainer says when it cannot find the real stick. Answers [{ name, ok, got, want }], as the Nav studio's self-tests do.
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
  return checks;
}

return { curve, stream, reader, drawInputs, stickLost, keyText, buttonText, axisText, deviceText, clamp, look, selftest };
})();
