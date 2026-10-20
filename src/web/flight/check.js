// STUDIO FLIGHT, STICK CHECK: a guided check of how a stick rests and how far it reaches, step by step on screen.
// The player picks the device, sees its axes live as bars, and presses Start: Studio then shows one instruction at a
// time with a timer bar and a soft beep, and moves on by itself (hands off; hand on, relaxed; push forward, then back to
// the middle with the hand on; back; left; right; twist left; twist right; hands off; then all again with each pair the
// other way round, back before forward and so on; hands off). The rests come in two
// kinds: hands off, the stick by itself (the control), and hand on, relaxed, as in flight, where the hand never leaves
// the grip and its weight can move an old stick a little. From the readings it works out which axis each push moved,
// which game control that axis feeds (as the app reads it: through the player's Elite bindings and Gremlin), where it
// rests by itself and with the hand on (an old stick seldom comes back to exactly the middle), how much it shakes there,
// and how far it reaches; and from that the dead zones (and, for a stick that never reaches its end, the Saturation X)
// that Flight Assist off needs, covering both kinds of rest. A rest during which the stick was moved, or held out beyond
// any dead zone, is left out and named. At the end Studio sets those dead zones in the Flight Assist off curves by itself
// (the owner's ask: the check sets the dead zones, the trainer's exercises then tune the curves), with a button to put
// back the ones before; nothing goes to Gremlin. Each check's findings are kept on this PC (the page's own storage) and
// listed under "Earlier checks", so runs can be compared. Loaded by setup.html after ../studio.js and flight.js;
// setup.html calls FlightCheck.init and, every frame, FlightCheck.tick.
var FlightCheck = (() => {
'use strict';
const { $, h } = Studio;
const SETTLE = 1.0;       // seconds after a rest begins before the stick counts as resting: it is still springing back
const EARLY = 0.4;        // the last part of a rest is left out too: a player who knows the next step starts it early
// A rest is left out when the stick was moved during it, told from the stick's own shake by how long it lasts: a stretch
// of readings this far from the rest's own middle lasting TOUCH_RUN readings in a row (about 0.15 s), or TOUCH_SHARE of
// its readings that far out (a bump, a wiggle, a push let go of late). Shake, however wide, comes and goes and is
// measured, not left out. A stick that sat still away from the middle is kept, and the dead zone covers it: with the
// hands off an old stick sticks; with the hand on, the hand's weight moves it (in flight it stays on).
const TOUCH_FAR = 0.12;
const TOUCH_RUN = 8;
const TOUCH_SHARE = 0.2;
const HELD_FAR = 0.25;    // a rest whose middle sat further out than the largest dead zone Studio sets: held there, never a rest
// A late let-go at the start of a rest, or the next step begun early at its end, is not part of the rest (see restOn in
// finish); what is left needs LEAST_REST seconds, or the rest is too short to go by.
const LEAST_REST = 1.0;
const HAND_SHOWS = 0.03;  // the hand moves the stick when, rest for rest, it sits this much away from where it rests by itself
const STAYS = 0.03;       // an old stick stays to the side it was last pushed when, after pushes each way, it sits this much apart
const KEEP_CHECKS = 10;   // how many earlier checks the page keeps
const HISTORY = 'studio.flight.checks';
const GLITCH = 0.005;     // the odd single glitch: the dead zone covers the rest readings but the top and bottom half percent
const LEAST_REACH = 0.85; // a push stopping short of this is a hand that did not push all the way, not a worn stick
const MOUSE = 'mouse';
const CONTROLS = ['pitch', 'roll', 'yaw'];
const NAMES = { pitch: 'Pitch', roll: 'Roll', yaw: 'Yaw' };
const MOTION = { pitch: 'forward / back', roll: 'left / right', yaw: 'twist' };
const PUSHED = { forward: 'a push forward', back: 'a pull back', left: 'a push left', right: 'a push right', twistLeft: 'a twist left', twistRight: 'a twist right' };
// Each push, and which way of moving the stick it is.
const PUSHES = [
  ['forward', 'Push the stick all the way FORWARD', 'pitch'], ['back', 'Pull the stick all the way BACK', 'pitch'],
  ['left', 'Push the stick all the way LEFT', 'roll'], ['right', 'Push the stick all the way RIGHT', 'roll'],
  ['twistLeft', 'TWIST the stick all the way LEFT', 'yaw'], ['twistRight', 'TWIST the stick all the way RIGHT', 'yaw'],
];
// The two kinds of rest: hands off, the stick by itself (the control); hand on, relaxed, as in flight (the owner's ask:
// in flight the hand never comes off, and its weight moves an old stick a little, so that is measured too).
const OFF = 'off', ON = 'on';
// 5 s each, the owner's choice: time to read the step before doing it. A rest that follows a push or a change of hand
// counts from 2 s in, while the stick comes back and the hand settles.
const STEPS = [
  { say: 'Hands off the stick', sub: 'Let it rest by itself. Do not touch it.', secs: 5, rest: OFF },
  { say: 'Hand on, relaxed', sub: 'Hold the stick as you do in flight, without pushing it any way.', secs: 5, rest: ON, settle: 2 },
];
for (const round of [1, 2]) {
  // Round 2 swaps each pair (back before forward, and so on), so the hands-off rests after the two rounds follow pushes
  // to opposite sides: where the stick rests by itself is then not merely where one side's push leaves an old stick.
  for (const [kind, say, motion] of round === 1 ? PUSHES : PUSHES.map((p, i) => PUSHES[i ^ 1])) {
    STEPS.push({ say, sub: 'and hold it there' + (round === 2 ? ' (second time)' : ''), secs: 5, push: kind, motion, round });
    STEPS.push({ say: 'Back to the middle, hand on', sub: 'Let it come back, your hand resting on it as in flight.', secs: 5, rest: ON, settle: 2 });
  }
  STEPS.push({ say: 'Hands off', sub: round === 1 ? 'Take your hand off: let the stick rest by itself.' : 'The last one: hand off, let it rest.',
    secs: 5, rest: OFF, settle: 2 });
}

let env = null;          // { getInfo, getFrame, call, toast, flush } from setup.html
let device = '';         // the device chosen: a stick's id ("046D:C215"), or MOUSE
let picked = false;      // the player chose it; until then Studio's guess follows the sticks (and the app's answer) as they turn up
let known = null;        // what the device list was built from last (null: never built, so the first look builds it)
let run = null;          // the check under way
let lastTime = 0;        // the newest reading taken in
let lastSeen = 0;        // when (page clock) a reading last came during the check
let mouse = { x: 0, y: 0 };
let sound = null;

function init(e) {
  env = e;
  $('#check-start').addEventListener('click', start);
  $('#check-stop').addEventListener('click', () => stop('Stopped. Press Start to go again.'));
  $('#check-device').addEventListener('change', () => { device = $('#check-device').value; picked = true; buildBars(); });
  document.addEventListener('mousemove', ev => {
    mouse = { x: ev.clientX / Math.max(1, window.innerWidth) * 2 - 1, y: ev.clientY / Math.max(1, window.innerHeight) * 2 - 1 };
  });
  document.addEventListener('visibilitychange', () => { if (run && document.visibilityState !== 'visible') stop('Stopped: Studio was put away during the check. Press Start to go again.'); });
  drawHistory();
}

// ---- the devices, and the live bars of the one chosen
function sticks() { const f = env.getFrame(); return f ? f.sticks : []; }
const isVjoy = id => /^1234:BEAD/i.test(id || '');
// In the shell, a studio whose tab is not in front keeps its page, only hidden (and the browser still calls it visible).
function shown() {
  try { return !(window.frameElement && window.frameElement.hidden); } catch (e) { return true; }
}

// The stick most likely meant: the one whose axis the app reads pitch from (through Elite's binding, Gremlin, or the
// player's own Detect), else the real one behind Gremlin's pitch by name, else the first that is not vJoy.
function bestGuess(list) {
  const f = env.getFrame(), pitch = f && f.controls && f.controls.pitch && f.controls.pitch.real;
  if (pitch != null) {
    const feeding = list.filter(s => Object.values(s.axes).some(v => v === pitch));
    const pick = feeding.find(s => !isVjoy(s.device)) || feeding[0];
    if (pick) return pick.device;
  }
  const info = env.getInfo();
  const bound = info && info.binds && info.binds.axes.pitch;
  const through = bound && bound.device === 'vJoy' && info.gremlin.axes.find(x => x.mode === 'Default' && x.vjoyAxis === bound.axis);
  const plain = t => String(t || '').toLowerCase().replace(/[^a-z0-9]/g, '');
  const named = through && list.find(s => plain(s.name).startsWith(plain(through.deviceName)) || plain(through.deviceName).startsWith(plain(s.name)));
  return (named || list.find(s => !isVjoy(s.device)) || list[0] || { device: MOUSE }).device;
}

function refreshDevices() {
  const list = sticks();
  // The app's answer counts too: the guess behind Gremlin can only be made once it is there.
  const ids = list.map(s => s.device + '|' + s.name).join(',') + (env.getInfo() ? '|info' : '');
  if (ids === known) return;
  known = ids;
  if (!picked || (device !== MOUSE && !list.some(s => s.device === device))) device = bestGuess(list);
  $('#check-device').replaceChildren(...list.map(s => h('option', { value: s.device },
      (s.name || 'Controller') + (isVjoy(s.device) ? ' (vJoy, the virtual stick)' : '') + ' · ' + s.device)),
    h('option', { value: MOUSE }, 'Mouse'));
  $('#check-device').value = device;
  buildBars();
}

// The axes of the chosen device, by Windows' letters.
function axesNow() {
  if (device === MOUSE) return { X: mouse.x, Y: mouse.y };
  const s = sticks().find(x => x.device === device);
  return s ? s.axes : null;
}
// The game control an axis of the chosen device feeds this moment: the one whose real reading is that very axis.
function controlNow(letter, axes) {
  const f = env.getFrame();
  if (!f || device === MOUSE || !f.controls) return null;
  const same = CONTROLS.filter(c => f.controls[c] && f.controls[c].real != null && f.controls[c].real === axes[letter]);
  return same.length === 1 ? same[0] : null;
}
const OTHER = { Z: 'throttle or slider', U: 'slider', V: 'slider' };
const bars = {};
function buildBars() {
  const box = $('#check-bars');
  box.replaceChildren();
  for (const k of Object.keys(bars)) delete bars[k];
  const axes = axesNow();
  if (!axes) { box.append(h('p', { class: 'small muted' }, 'No reading from this device yet. Move it a little.')); return; }
  for (const letter of Object.keys(axes)) {
    const fill = h('i', { class: 'fill' }), dead = h('i', { class: 'dead' }), value = h('span', { class: 'n' }), name = h('span');
    box.append(h('div', { class: 'axisbar' }, name, h('div', { class: 'track' }, dead, fill, h('i', { class: 'mid' })), value));
    bars[letter] = { fill, dead, value, name };
  }
}
function drawBars() {
  const axes = axesNow();
  if (!axes) return;
  if (Object.keys(axes).join() !== Object.keys(bars).join()) { buildBars(); return; }
  const info = env.getInfo(), off = info && info.settings.faOff;
  for (const [letter, v] of Object.entries(axes)) {
    const b = bars[letter], control = controlNow(letter, axes), c = off && control && off[control];
    b.name.textContent = letter + (control ? ' · ' + NAMES[control] : OTHER[letter] ? ' · ' + OTHER[letter] : '');
    const x = Math.max(-1, Math.min(1, v));
    b.fill.style.left = (x < 0 ? 50 + x * 50 : 50) + '%';
    b.fill.style.width = Math.abs(x) * 50 + '%';
    b.value.textContent = Math.round(v * 100) + '%';
    // the Flight Assist off dead zone of the control this axis feeds, as it is now
    b.dead.hidden = !c;
    if (c) { b.dead.style.left = (50 - c.deadLow / 2) + '%'; b.dead.style.width = (c.deadLow + c.deadHigh) / 2 + '%'; }
  }
}

// ---- the check
function start() {
  if (device === MOUSE) {
    show(h('p', { class: 'warn' }, 'A mouse stays exactly where you leave it, so it needs no dead zone. Pick your stick to check it.'));
    return;
  }
  if (!axesNow()) { show(h('p', { class: 'warn' }, 'Studio has no reading from that device. Move it a little, or pick another.')); return; }
  if (!env.getInfo()) { show(h('p', { class: 'warn' }, 'Studio has not loaded your curves yet. Wait a moment, then press Start again.')); return; }
  try { sound = sound || new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { sound = null; }
  run = { device, i: -1, at: 0, records: [] };
  lastSeen = performance.now();
  show(null);
  $('#check-run').hidden = false;
  $('#check-start').disabled = true;
  $('#check-device').disabled = true;
  next(performance.now());
}

function stop(text) {
  run = null;
  $('#check-run').hidden = true;
  $('#check-start').disabled = false;
  $('#check-device').disabled = false;
  if (text) show(h('p', { class: 'muted' }, text));
}

function next(now) {
  run.i++;
  if (run.i >= STEPS.length) { finish(); return; }
  run.at = now;
  run.records.push({ step: STEPS[run.i], samples: [] });
  const step = STEPS[run.i];
  $('#check-what').textContent = step.say;
  $('#check-sub').textContent = step.sub;
  $('#check-count').textContent = 'Step ' + (run.i + 1) + ' of ' + STEPS.length;
  $('#check-next').textContent = 'Next: ' + (STEPS[run.i + 1] ? STEPS[run.i + 1].say : 'what the check found');
  $('#check-what').className = step.rest ? 'rest' : 'push';
  beep(step.push ? 780 : step.rest === ON ? 650 : 520);
}

function beep(pitch) {
  if (!sound || !$('#check-sound').checked) return;
  try {
    const o = sound.createOscillator(), g = sound.createGain();
    o.frequency.value = pitch;
    g.gain.setValueAtTime(0.0001, sound.currentTime);
    g.gain.exponentialRampToValueAtTime(0.18, sound.currentTime + 0.02);
    g.gain.exponentialRampToValueAtTime(0.0001, sound.currentTime + 0.18);
    o.connect(g).connect(sound.destination);
    o.start();
    o.stop(sound.currentTime + 0.2);
  } catch (e) {}
}

// Every frame: the device list, the bars, and the check's clock and readings.
function tick(now) {
  if (!env) return;
  refreshDevices();
  drawBars();
  if (!run) return;
  if (!shown()) { stop('Stopped: another tab was opened during the check. Press Start to go again.'); return; }
  const step = STEPS[run.i], seconds = (now - run.at) / 1000;
  const f = env.getFrame();
  if (f && f.time !== lastTime) {
    lastTime = f.time;
    lastSeen = now;
    const s = f.sticks.find(x => x.device === run.device);
    if (!s) { stop('Stopped: the device went away during the check. Plug it back in and press Start.'); return; }
    const real = {};
    for (const c of CONTROLS) real[c] = f.controls && f.controls[c] ? f.controls[c].real : null;
    run.records[run.records.length - 1].samples.push({ t: seconds, axes: { ...s.axes }, real });
  } else if (now - lastSeen > 1500) { stop('Stopped: no readings came from Studio for a moment. Press Start to go again.'); return; }
  $('#check-left').textContent = Math.max(0, Math.ceil(step.secs - seconds));
  $('#check-timer').style.width = Math.min(100, seconds / step.secs * 100) + '%';
  if (seconds >= step.secs) next(now);
}

// ---- what the readings say
const mean = list => list.reduce((a, b) => a + b, 0) / Math.max(1, list.length);
const median = list => { const s = [...list].sort((a, b) => a - b); return s.length ? s[Math.floor(s.length / 2)] : 0; };
const pct = v => (v >= 0 ? '+' : '') + (v * 100).toFixed(1) + '%';
// Where readings lie, all but the odd single glitch (the top and bottom half percent): [low, high], or null for none.
function span(values) {
  if (!values.length) return null;
  const s = [...values].sort((a, b) => a - b);
  return [s[Math.floor(GLITCH * (s.length - 1))], s[Math.ceil((1 - GLITCH) * (s.length - 1))]];
}
const spanText = s => s ? pct(s[0]) + ' to ' + pct(s[1]) : '—';
// Why a rest has to be left out, or null: it sat further out than any dead zone ('held'), or the stick was moved during
// it ('moved', see TOUCH_FAR).
function disturbance(values, base) {
  const middle = median(values);
  if (Math.abs(middle - base) > HELD_FAR) return 'held';
  let run = 0, longest = 0, out = 0;
  for (const v of values) {
    const far = Math.abs(v - middle) > TOUCH_FAR;
    run = far ? run + 1 : 0;
    longest = Math.max(longest, run);
    if (far) out++;
  }
  return longest >= TOUCH_RUN || out > values.length * TOUCH_SHARE ? 'moved' : null;
}

function finish() {
  const records = run.records, checked = run.device;
  stop(null);
  const info = env.getInfo();
  const rests = records.filter(r => r.step.rest);
  const offRests = rests.filter(r => r.step.rest === OFF), onRests = rests.filter(r => r.step.rest === ON);
  // Each rest's quiet middle: after the spring-back, before the next step is anticipated.
  const quiet = r => r.samples.filter(s => s.t >= (r.step.settle || SETTLE) && s.t <= r.step.secs - EARLY);
  if (!info || !records.some(r => r.samples.length) || !rests.every(r => quiet(r).length >= 5)) {
    show(h('p', { class: 'warn' }, 'Studio got too few readings during the check. Make sure the device is plugged in, then press Start again.'));
    return;
  }
  // A rest on one axis: its quiet part, less a let-go that came late at its start or the next step begun early at its
  // end (the readings there further than TOUCH_FAR from where the stick spent most of the rest; anything nearer is the
  // stick resting, and is kept), and whether too little was left to go by: 'late' or 'early', else null.
  const restOn = (r, l) => {
    const window = quiet(r), held = median(window.map(s => s.axes[l]));
    const near = s => Math.abs(s.axes[l] - held) <= TOUCH_FAR;
    let a = 0, b = window.length;
    while (a < b && !near(window[a])) a++;
    while (b > a && !near(window[b - 1])) b--;
    const kept = window.slice(a, b);
    const short = kept.length < 5 || kept[kept.length - 1].t - kept[0].t < LEAST_REST;
    return { values: kept.map(s => s.axes[l]), short: short ? (a >= window.length - b ? 'late' : 'early') : null };
  };
  const off = info.settings.faOff;
  const letters = Object.keys(records.find(r => r.samples.length).samples[0].axes);
  // Where the stick rests by itself on each axis: the middle of the hands-off rests' middles, so one touched rest does not move it.
  const base = {};
  for (const l of letters) base[l] = median(offRests.map(r => restOn(r, l)).filter(x => !x.short).map(x => median(x.values)));
  const problems = [];

  // 1. Which axis each way of moving the stick moved: the one that moved most over its four pushes.
  const moved = {}, size = {};
  for (const motion of CONTROLS) {
    const pushes = records.filter(r => r.step.motion === motion);
    for (const l of letters) {
      const m = mean(pushes.map(r => Math.max(0, ...r.samples.map(s => Math.abs(s.axes[l] - base[l])))));
      if (!(motion in size) || m > size[motion]) { size[motion] = m; moved[motion] = l; }
    }
    if (size[motion] < 0.4) { problems.push(`Studio did not see the stick move for ${MOTION[motion]}. Was the right device picked, and did you push all the way?`); delete moved[motion]; }
  }
  // Two ways of moving on one axis: only the one that moved it most keeps it.
  for (const a of CONTROLS) for (const b of CONTROLS)
    if (a < b && moved[a] && moved[a] === moved[b]) {
      const drop = size[a] >= size[b] ? b : a;
      problems.push(`${MOTION[a]} and ${MOTION[b]} moved the same axis (${moved[a]}), so ${MOTION[drop]} was left out. Run the check again and push only the way each step says.`);
      delete moved[drop];
    }

  // 2. The game control each such axis feeds: the one whose real reading is that axis in (nearly) every reading.
  const all = records.flatMap(r => r.samples);
  const feeds = {};
  for (const [motion, l] of Object.entries(moved)) {
    const match = CONTROLS.find(c => all.filter(s => s.real[c] != null && Math.abs(s.real[c] - s.axes[l]) < 1e-9).length >= all.length * 0.9);
    if (match) feeds[motion] = match;
    else problems.push(`Axis ${l} (${MOTION[motion]}) does not feed pitch, roll or yaw as Studio reads them, so it has no Flight Assist off curve to set. `
      + 'If it should, use Detect under "Your hands, live" to tell Studio, then run the check again.');
  }

  // 3. Per axis: where it rested by itself and with the hand on (disturbed rests left out), and how far it reaches each way.
  const suggest = {}, found = [], offCentre = [], capped = [], handMoves = [], stays = [];
  const stepNo = r => STEPS.indexOf(r.step) + 1;
  const those = (list, one, many) => list.length > 1 ? many : one;
  for (const [motion, l] of Object.entries(moved)) {
    const control = feeds[motion];
    if (!control) continue;   // nothing to set for this axis (said above), so nothing more to say about it either
    const rest = new Map(rests.map(r => [r, restOn(r, l)]));
    const valuesOf = r => rest.get(r).values, middleOf = r => median(valuesOf(r));
    const why = new Map(rests.map(r => [r, rest.get(r).short || disturbance(valuesOf(r), base[l])]));
    const offOut = offRests.filter(r => why.get(r)), onOut = onRests.filter(r => why.get(r));
    const offKept = offRests.filter(r => !why.get(r)), onKept = onRests.filter(r => !why.get(r));
    const tooManyOff = offOut.length > offRests.length / 2, tooManyOn = onOut.length > onRests.length / 2;
    // The rests left out, by step (a kind with too many left out is told about as a whole, below).
    const left = [...(tooManyOff ? [] : offOut), ...(tooManyOn ? [] : onOut)].sort((a, b) => stepNo(a) - stepNo(b));
    const reason = r => ({ late: 'not back in time', early: 'the next step started early', held: 'held at ' + pct(middleOf(r)), moved: 'moved during it' })[why.get(r)];
    if (left.length) problems.push(`Studio left out ${those(left, 'step', 'steps')} `
      + left.map(r => `${stepNo(r)} (${r.step.rest === OFF ? 'hands off' : 'hand on'}: ${reason(r)})`).join(', ') + ` on ${MOTION[motion]}.`
      + (left.some(r => why.get(r) === 'late') ? ' Let the stick come back as soon as a step says so: a rest counts from when it is back.' : '')
      + (left.some(r => why.get(r) === 'early') ? ' Wait for the beep before the next step.' : ''));
    if (tooManyOff) {
      problems.push(`Too many hands-off rests were moved, held out or too short for ${MOTION[motion]} to tell where the stick rests by itself. Run the check again, hand well off the stick when it says Hands off.`);
      continue;
    }
    if (tooManyOn) {
      const sat = onRests.filter(r => valuesOf(r).length).map(middleOf);
      problems.push(`With your hand on, the stick was moved, held more than ${HELD_FAR * 100}% out or not at rest long enough in most rests for ${MOTION[motion]}`
        + (sat.length ? ` (it sat at about ${pct(median(sat))})` : '') + ', so Studio cannot tell what a relaxed hand does. '
        + 'Run the check again, letting the stick come back to the middle each time with your hand resting lightly on it.');
      continue;
    }
    const offValues = offKept.flatMap(valuesOf), onValues = onKept.flatMap(valuesOf);
    const [lo, hi] = span(offValues.concat(onValues)), offSpan = span(offValues), onSpan = span(onValues);
    // The dead zone covers where the stick rested, hands off or on, and its shake there (all but the odd single glitch),
    // with 1% to spare, on each side.
    const needLow = Math.ceil(Math.max(0, -lo) * 100 + 1), needHigh = Math.ceil(Math.max(0, hi) * 100 + 1);
    const deadLow = Math.min(25, Math.max(1, needLow)), deadHigh = Math.min(25, Math.max(1, needHigh));
    const over = needLow > 25 || needHigh > 25;
    if (over) capped.push(MOTION[motion]);
    const covers = over ? 'not even the largest dead zone Studio sets reaches that' : 'the dead zone covers it';
    // Rests are compared within groups: those after the same last push on this axis (an old stick stays a little to the
    // side it was last pushed, so that is where it rests then), and those before any push this way.
    const lastPush = r => { for (let i = STEPS.indexOf(r.step) - 1; i >= 0; i--) if (STEPS[i].motion === motion) return STEPS[i].push; return null; };
    const handAfter = new Map();   // last push -> middles of the hand-on rests after it
    for (const r of onKept) handAfter.set(lastPush(r), [...(handAfter.get(lastPush(r)) || []), middleOf(r)]);
    const handAt = new Map([...handAfter].map(([k, ms]) => [k, median(ms)]));
    // The hand's own part, the control the owner asked for: after the same last push, hand on against hands off.
    const middles = offKept.map(middleOf);
    const shifts = offKept.filter(r => handAt.has(lastPush(r))).map(r => handAt.get(lastPush(r)) - middleOf(r));
    const shift = shifts.length ? median(shifts) : 0;
    // A hands-off rest that sat still clearly away from the others, and not where the stick sits after that same push
    // (the hand's part taken off): a hand left on the stick, or a stick that sometimes sticks there. Studio cannot tell
    // which, so it keeps it (the dead zone then covers it) and says so.
    const spread = median(middles.map(m => Math.abs(m - base[l])));
    offKept.forEach((r, i) => {
      const expect = handAt.has(lastPush(r)) ? handAt.get(lastPush(r)) - shift : null;
      if (Math.abs(middles[i] - base[l]) > Math.max(0.05, 4 * spread) && (expect == null || Math.abs(middles[i] - expect) > 0.05))
        problems.push(`With your hands off, in step ${stepNo(r)} the stick sat at ${pct(middles[i])} on ${MOTION[motion]}, away from where it sat in the other hands-off rests (${pct(base[l])}). `
          + `If your hand was on it, run the check again; if not, your stick sometimes sticks there, ${over ? 'though ' : 'and '}${covers}.`);
    });
    // A hand-on rest clearly away from the other hand-on rests after the same last push (a hand that did not relax, or
    // one that leans on the stick now and then), by how far those rests lie from each other. Kept, and said.
    const othersAt = r => { const g = onKept.filter(t => t !== r && lastPush(t) === lastPush(r)); return g.length ? median(g.map(middleOf)) : null; };
    const within = median(onKept.map(r => { const at = othersAt(r); return at == null ? 0 : Math.abs(middleOf(r) - at); }));
    onKept.forEach(r => {
      const at = othersAt(r), m = middleOf(r);
      if (at == null || Math.abs(m - at) <= Math.max(0.05, 4 * within)) return;
      problems.push(`With your hand on, in step ${stepNo(r)} the stick stayed at ${pct(m)} on ${MOTION[motion]}, away from where it sat in your other hand-on rests `
        + `${lastPush(r) ? 'after ' + PUSHED[lastPush(r)] : 'before your first push that way'} (about ${pct(at)}). If your hand was holding it there, run the check again; if not, ${covers}.`);
    });
    // Where it stays after a push each way, with the hand on: worth knowing about an old stick, and covered. Which side
    // each push went is read from the push itself.
    const sideOf = kind => Math.sign(median(records.filter(r => r.step.push === kind).map(r => {
      let far = base[l];
      for (const s of r.samples) if (Math.abs(s.axes[l] - base[l]) > Math.abs(far - base[l])) far = s.axes[l];
      return far - base[l];
    })));
    const [oneWay, otherWay] = PUSHES.filter(p => p[2] === motion).map(p => p[0]);
    if (!over && handAt.has(oneWay) && handAt.has(otherWay) && Math.abs(handAt.get(oneWay) - handAt.get(otherWay)) > STAYS)
      stays.push({ toward: (handAt.get(oneWay) - handAt.get(otherWay)) * (sideOf(oneWay) - sideOf(otherWay)) > 0,
        text: `${MOTION[motion]} (about ${pct(handAt.get(oneWay))} after ${PUSHED[oneWay]}, ${pct(handAt.get(otherWay))} after ${PUSHED[otherWay]})` });
    // Off the middle by itself: where it rests most of the time, so one rest where it stuck does not count.
    if (Math.abs(base[l]) > 0.05) offCentre.push(MOTION[motion] + ' (' + pct(base[l]) + ')');
    const selfAt = median(middles);
    if (!over && shifts.length && Math.abs(shift) > HAND_SHOWS) handMoves.push(`${MOTION[motion]} (about ${pct(selfAt + shift)} with your hand on, ${pct(selfAt)} by itself)`);
    // Reach, each way from this motion's own pushes: the far end of each push, by the side it went to.
    const ends = { '-1': [], '1': [] };
    for (const r of records.filter(r => r.step.motion === motion)) {
      let far = base[l];
      for (const s of r.samples) if (Math.abs(s.axes[l] - base[l]) > Math.abs(far - base[l])) far = s.axes[l];
      if (Math.abs(far - base[l]) >= 0.5) ends[far < base[l] ? '-1' : '1'].push(Math.abs(far));
    }
    const reachNeg = ends['-1'].length ? Math.max(...ends['-1']) : null, reachPos = ends['1'].length ? Math.max(...ends['1']) : null;
    if (reachNeg == null || reachPos == null) problems.push(`Studio did not see a full push ${reachNeg == null ? 'to the − side' : 'to the + side'} for ${MOTION[motion]}, so it did not judge how far that axis reaches.`);
    // A stick that never reaches its end: the curve should reach its top a little before that. Only when both pushes
    // to the short side stopped at the same place (a worn stick does; a hand that did not push all the way does not).
    // A push that stopped well short is a hand, not the stick: then nothing is changed and the player is asked to push further.
    let satX = null;
    if (reachNeg != null && reachPos != null) {
      const shortSide = reachNeg <= reachPos ? ends['-1'] : ends['1'], reach = Math.min(reachNeg, reachPos);
      const steady = shortSide.length >= 2 && Math.max(...shortSide) - Math.min(...shortSide) <= 0.02;
      if (reach < LEAST_REACH) problems.push(`Your pushes for ${MOTION[motion]} stopped at ${Math.round(reach * 100)}% on the ${reachNeg <= reachPos ? '−' : '+'} side. Push all the way to the stop, then run the check again; Studio changed nothing about how far that axis travels.`);
      else if (reach < 0.99 && steady) satX = Math.max(60, Math.min(100, Math.floor(reach * 100) - 2));
    }
    suggest[control] = { deadLow, deadHigh, ...(satX != null && satX < off[control].satX ? { satX } : {}) };
    found.push({ control, motion, axis: l, off: offSpan, on: onSpan, reach: [reachNeg, reachPos],
      before: [off[control].deadLow, off[control].deadHigh], set: [deadLow, deadHigh],
      need: needLow > 25 || needHigh > 25 ? [needLow, needHigh] : null, satX: suggest[control].satX || null });
  }

  const vjoy = isVjoy(checked), usable = found.length > 0 && !vjoy;
  // What the check learnt about the stick and the hand (said below the table, and kept with the check's record).
  const facts = [
    handMoves.length ? 'Your resting hand moves the stick on ' + handMoves.join(' and ') + '. In flight your hand stays on, so the dead zones cover that too.' : null,
    stays.some(s => s.toward) ? 'With your hand resting on it, your stick stays a little to the side it was last pushed on '
      + stays.filter(s => s.toward).map(s => s.text).join(' and ') + '. The dead zones cover both sides.' : null,
    stays.some(s => !s.toward) ? 'With your hand resting on it, your stick settles a little past the middle, away from the side it was last pushed, on '
      + stays.filter(s => !s.toward).map(s => s.text).join(' and ') + '. The dead zones cover both sides.' : null,
  ].filter(Boolean);
  const status = h('div', { class: 'check-status' });
  show(h('div', null,
    h('h3', null, 'What the check found'),
    vjoy ? h('p', { class: 'warn' }, 'This was vJoy, the virtual stick, whose readings already went through a stick shaper\'s curve (Joystick Gremlin\'s today). Check your real stick instead.') : null,
    problems.map(p => h('p', { class: 'warn' }, p)),
    usable ? table(found) : null,
    usable ? h('p', { class: 'small muted' }, '"Hands off" is where the stick rested by itself; "hand on, relaxed" where it sat with your hand resting on it as in flight, '
      + 'after each push too. Both include the stick\'s shake (only the odd single glitch is left out). The new dead zones cover both with 1% to spare on each side, '
      + 'for your Flight Assist off curves only. Nothing goes to Gremlin.') : null,
    usable ? facts.map(f => h('p', { class: 'small' }, f)) : null,
    usable && capped.length ? h('p', { class: 'small warn' }, 'For ' + capped.join(' and ') + ' the stick sits further out than the largest dead zone Studio sets (25%), so even that will not hold it still. '
      + 'Calibrate the stick in Windows (below), then run the check again.') : null,
    usable && offCentre.length ? h('p', { class: 'small warn' }, 'Your stick rests well away from the middle on ' + offCentre.join(' and ')
      + ', so the dead zone has to be lopsided: more stick travel before anything happens on that side. Calibrating the stick in Windows '
      + '(press Win+R, type joy.cpl, pick the stick, Properties, Settings, Calibrate) moves its middle back to where it rests, and a smaller, even dead zone will do. Run this check again afterwards. '
      + 'Mind that calibrating changes what your current Gremlin profile gets too: its own lopsided dead zones were made for the stick as it rests now.') : null,
    usable ? status : null,
    h('div', { class: 'row' }, h('button', { class: 'btn', onclick: start }, 'Run the check again'))));
  if (!usable) return;
  const stick = sticks().find(s => s.device === checked);
  const record = { at: Date.now(), device: (stick && stick.name) || checked, found, notes: facts.concat(problems), state: 'not set' };
  remember(record);
  setDeadZones(suggest, status, record);
}

// The findings as a table: for the check just run, and for each earlier one.
function table(found) {
  return h('div', { class: 'tablebox' }, h('table', null,
    h('thead', null, h('tr', null, h('th', null, 'Control'), h('th', { class: 'r' }, 'Hands off'), h('th', { class: 'r' }, 'Hand on, relaxed'),
      h('th', { class: 'r' }, 'Reaches − / +'), h('th', { class: 'r' }, 'Dead zone before − / +'), h('th', { class: 'r' }, 'New dead zone − / +'))),
    h('tbody', null, found.map(f => h('tr', null,
      h('td', null, NAMES[f.control] || f.control, h('small', null, ' · ' + (MOTION[f.motion] || f.motion) + ' · axis ' + f.axis)),
      h('td', { class: 'r' }, spanText(f.off)),
      h('td', { class: 'r' }, spanText(f.on)),
      h('td', { class: 'r' }, (f.reach[0] != null ? pct(-f.reach[0]) : '?') + ' / ' + (f.reach[1] != null ? pct(f.reach[1]) : '?')),
      h('td', { class: 'r' }, f.before[0] + '% / ' + f.before[1] + '%'),
      h('td', { class: 'r ' + (f.need ? 'warn' : 'good') }, f.set[0] + '% / ' + f.set[1] + '%'
        + (f.need ? ' (needs ' + f.need[0] + '% / ' + f.need[1] + '%)' : '') + (f.satX ? ' · travel ' + f.satX + '%' : '')))))));
}

// ---- setting the dead zones, and putting back the ones before
// Changes only the given values of the Flight Assist off curves, as they are now (a slider moved since keeps its change).
// A curves-editor change still waiting to be sent goes first: sent after this, it would bring back the values from before.
async function change(values) {
  if (env.flush) await env.flush();
  const off = env.getInfo().settings.faOff, changed = { ...off };
  for (const [control, s] of Object.entries(values)) changed[control] = { ...off[control], ...s };
  await env.call('POST', '/api/flight/settings', { faOff: changed });
}

const zonesText = values => Object.entries(values).map(([c, s]) => `${NAMES[c]} ${s.deadLow}% / ${s.deadHigh}%` + (s.satX ? ` (travel ${s.satX}%)` : '')).join(', ');

async function setDeadZones(suggest, status, record) {
  const say = (cls, text, ...buttons) => status.replaceChildren(h('p', { class: cls }, text), ...buttons);
  say('muted', 'Setting your Flight Assist off dead zones…');
  const before = {};
  try {
    const off = env.getInfo().settings.faOff;
    for (const [control, s] of Object.entries(suggest)) before[control] = Object.fromEntries(Object.keys(s).map(k => [k, off[control][k]]));
    await change(suggest);
  } catch (e) {
    say('warn', 'Studio could not set the dead zones: ' + e.message, h('button', { class: 'btn', onclick: () => setDeadZones(suggest, status, record) }, 'Try again'));
    return;
  }
  record.state = 'set';
  remember(record);
  say('good', 'Set: your Flight Assist off dead zones are now ' + zonesText(suggest) + ' (they were ' + zonesText(before) + '). '
    + 'Next, fly the trainer\'s exercises: they are for tuning the curves.',
    h('button', { class: 'btn', onclick: async e => {
      e.target.disabled = true;
      try {
        await change(before);
        record.state = 'put back';
        remember(record);
        say('muted', 'Put back: your Flight Assist off dead zones are as they were before this check (' + zonesText(before) + ').');
      } catch (x) { e.target.disabled = false; env.toast(x.message, true); }
    } }, 'Put back the ones before'));
}

// ---- earlier checks, kept in the page's own storage on this PC (a private window or cleared site data starts afresh)
function history() {
  try {
    const list = JSON.parse(localStorage.getItem(HISTORY) || '[]');
    return Array.isArray(list) ? list.filter(r => r && typeof r.at === 'number' && Array.isArray(r.found)) : [];
  } catch (e) { return []; }
}
function remember(record) {
  const list = history().filter(r => r.at !== record.at);
  list.unshift(record);
  try { localStorage.setItem(HISTORY, JSON.stringify(list.slice(0, KEEP_CHECKS))); } catch (e) {}
  drawHistory();
}
const STATE = { set: 'dead zones set', 'put back': 'dead zones put back afterwards', 'not set': 'dead zones not set' };
function drawHistory() {
  const box = $('#check-history'), into = $('#check-history-list');
  if (!box || !into) return;
  const shown = [];
  for (const r of history()) {
    try {
      const when = new Date(r.at).toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
      shown.push(h('div', { class: 'check-earlier' },
        h('p', { class: 'small' }, h('b', null, when), ' · ' + (r.device || 'stick') + ' · ' + (STATE[r.state] || STATE['not set'])),
        table(r.found),
        Array.isArray(r.notes) && r.notes.length ? h('details', null, h('summary', { class: 'small muted' }, 'What it noted (' + r.notes.length + ')'),
          r.notes.map(n => h('p', { class: 'small muted' }, String(n)))) : null));
    } catch (e) { /* a record this page cannot read: left out */ }
  }
  into.replaceChildren(...shown);
  box.hidden = !shown.length;
}

function show(content) {
  const box = $('#check-result');
  box.replaceChildren(...(content ? [content] : []));
}

return { init, tick };
})();
