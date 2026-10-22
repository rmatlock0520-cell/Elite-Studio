// STUDIO FLIGHT: "Chase the dot", the rules of the two-axis aiming game. Loaded by chase.html after ../studio.js and flight.js.
//
// A dot wanders across a square field and the mark is yours, like aiming at a flying duck: the mark goes where the stick
// points, with no drift and no carried speed, and letting go puts it back in the middle at once. Up and down is pitch, left
// and right is yaw (the stick's twist). The stick goes through the curve being tried, so its dead zone, its travel and its
// softer middle are all felt; "Most it gives" is not, as the mark at full stick is always the edge of the field. The keys
// do not aim. The score is the share of the round the mark sat on the dot.
//
//   Chase.LEVELS, Chase.ROUND        the three levels, and a round's length in seconds
//   Chase.start(level, curves, o)    a new round: level (one of LEVELS, or its id), curves { pitch, yaw } as settings.json
//                                    keeps them (kept as they are now for the whole round), o = { seed, invert: { pitch, yaw }
//                                    as the game's bindings set them, set: 'faOff' | 'faOn' (carried to the summary) }
//   Chase.step(s, stick, dt)         one small step (dt seconds, a tenth at most, so a page hidden or paused for long cannot jump
//                                    the round); stick { pitch, yaw } is the real stick from -1 to 1 before any curve
//                                    (frame.controls[axis].real; null is let go, or no reading); answers { done, result: the
//                                    summary when done }
//   Chase.score(s)                   the share of the time so far on the dot, in whole percent
//   Chase.summary(s)                 { level, set, pct, streak, miss, overshoots: { pitch, yaw }, peak: { pitch, yaw } }
//   s.frames                         every step: { t, raw, curved, mark, dot } (the stick, what the curve made of it, each null
//                                    for no reading, and where the mark and the dot were), for the round's record
//   s.blind                          seconds on each axis in which a pass would be the dot's doing, not counted (watchPasses)
//   Chase.markOf(x, c)               where the mark sits on one axis (-1 to 1) for a stick position, through curve c
//   Chase.draw(g, w, h, s, accent, look, text)    the picture, on a canvas's 2D context
//   Chase.selftest(rules)            the checks (chase.html?selftest); { ok, checks: [{ name, ok, got, want }] }
var Chase = (() => {
'use strict';
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

// A small seeded random (mulberry32): the same seed gives the same round.
function random(seed) {
  let a = (seed >>> 0) || 1;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// The straight curve: no dead zone, the whole travel, all of it given, no softer middle.
const STRAIGHT = { deadLow: 0, deadHigh: 0, satX: 100, satY: 100, bend: 0 };

// The field is square, from -1 to 1 each way: full stick puts the mark at its edge on both axes, so the same stick travel
// moves the mark the same number of dot sizes up and down as left and right. The dot's middle stays within EDGE.
const EDGE = 0.9;
const ROUND = 30;   // seconds
// speed: the dot's speed on average, field units a second; turn: how hard its path bends (radians a second, at most); r: the
// dot's radius. Tuned so a relaxed hand (it points where it saw the dot 200 ms ago, smoothed over 80 ms, with a little
// wander, and never overshoots) is on the dot about 90%, 70% and 45% of the round: selftest (e).
const LEVELS = [
  { id: 'easy', name: 'Easy', speed: 0.24, turn: 0.8, r: 0.10 },
  { id: 'medium', name: 'Medium', speed: 0.25, turn: 1.2, r: 0.09 },
  { id: 'hard', name: 'Hard', speed: 0.28, turn: 1.6, r: 0.07 },
];
// Which way a stick position turns the ship before the game's invert, as Flight.reader has them: Elite's pitch axis is
// "nose down" for a stick pulled back unless inverted. Up the field is nose up, right is nose right.
const NATIVE = { pitch: -1, yaw: 1 };
const LONGEST_STEP = 0.1;   // seconds: the most one step moves the round on
// How long after the dot turns back on an axis a pass can still be its doing (watchPasses): a hand that follows it late meets
// it coming back within about half its lateness. Short, so that the rule misses few real overshoots: selftest (h).
const TURN_BACK = 0.3;      // seconds

// Where the mark sits on one axis, from -1 to 1, for a stick position x through curve c: the curve's output over the most
// that curve gives that way. So "Most it gives" never shortens the reach; the dead zone, the travel and the softer middle are
// all felt. A stick not there (null) is let go: the middle.
function markOf(x, c) {
  if (x == null || !isFinite(x)) return 0;
  const most = Math.abs(Flight.curve(x < 0 ? -1 : 1, c));
  return most > 0 ? Flight.curve(x, c) / most : 0;
}

function start(level, curves, o) {
  o = o || {};
  const L = (typeof level === 'string' ? LEVELS.find(l => l.id === level) : level) || LEVELS[1];
  const rnd = random(o.seed), within = (lo, hi) => lo + rnd() * (hi - lo);
  const invert = o.invert || {};
  // The dot starts well away from the middle, heading roughly across the field.
  const x = (rnd() < 0.5 ? -1 : 1) * within(0.5, 0.8), y = within(-0.6, 0.6);
  return {
    level: L, set: o.set || null, len: ROUND, t: 0, rnd,
    // The curves as they are now, kept for the whole round: a change on the page counts from the next round.
    curves: { pitch: { ...((curves && curves.pitch) || STRAIGHT) }, yaw: { ...((curves && curves.yaw) || STRAIGHT) } },
    sign: { pitch: NATIVE.pitch * (invert.pitch ? -1 : 1), yaw: NATIVE.yaw * (invert.yaw ? -1 : 1) },
    mark: { x: 0, y: 0, vx: 0, vy: 0 },
    dot: { x, y, vx: 0, vy: 0, h: x < 0 ? within(-0.5, 0.5) : Math.PI + within(-0.5, 0.5), w: 0, wTo: 0, next: 1, phase: within(0, 6) },
    onTime: 0, streak: 0, bestStreak: 0, missSum: 0, steps: 0,
    peak: { pitch: 0, yaw: 0 }, overshoots: { pitch: 0, yaw: 0 },
    passes: { pitch: 0, yaw: 0 }, dotWay: { pitch: 0, yaw: 0 }, turnedAt: { pitch: -Infinity, yaw: -Infinity }, blind: { pitch: 0, yaw: 0 },
    frames: [],
  };
}

function step(s, stick, dt) {
  if (s.t + 1e-9 >= s.len) return { done: true, result: summary(s) };
  if (!(dt > 0)) return { done: false };
  dt = Math.min(dt, LONGEST_STEP);
  const L = s.level, m = s.mark, d = s.dot, c = s.curves;
  // No reading (a stick let go, lost or not found) is kept as null: the mark sits in the middle, and the record says so.
  const reading = v => (v != null && isFinite(v) ? +v : null);
  const raw = { pitch: stick ? reading(stick.pitch) : null, yaw: stick ? reading(stick.yaw) : null };
  s.peak.pitch = Math.max(s.peak.pitch, Math.abs(raw.pitch || 0));
  s.peak.yaw = Math.max(s.peak.yaw, Math.abs(raw.yaw || 0));

  // The mark: where the stick points, at once. (vx, vy say how fast it moved, smoothed over 50 ms as the stick's readings
  // come about sixty a second, only for the overshoot count.)
  const x0 = m.x, y0 = m.y, k = Math.min(1, dt / 0.05);
  m.x = s.sign.yaw * markOf(raw.yaw, c.yaw);
  m.y = s.sign.pitch * markOf(raw.pitch, c.pitch);
  m.vx += ((m.x - x0) / dt - m.vx) * k;
  m.vy += ((m.y - y0) / dt - m.vy) * k;

  // The dot: it speeds up and slows down (half as slow to half as fast again as its level's speed, so a round has stretches
  // easy to stay on and stretches that are not), along a heading that bends this way and that, and turns back at the edges.
  d.next -= dt;
  if (d.next <= 0) { d.wTo = (2 * s.rnd() - 1) * L.turn; d.next = 0.8 + 1.4 * s.rnd(); }
  d.w += (d.wTo - d.w) * Math.min(1, dt * 2.5);
  d.h += d.w * dt;
  const speed = L.speed * (1 + 0.5 * Math.sin(0.9 * s.t + d.phase));
  d.x += Math.cos(d.h) * speed * dt;
  d.y += Math.sin(d.h) * speed * dt;
  if (Math.abs(d.x) > EDGE) { d.x = Math.sign(d.x) * EDGE; d.h = Math.PI - d.h; }
  if (Math.abs(d.y) > EDGE) { d.y = Math.sign(d.y) * EDGE; d.h = -d.h; }
  d.vx = Math.cos(d.h) * speed;
  d.vy = Math.sin(d.h) * speed;

  // How the round is going.
  s.t += dt;
  s.steps++;
  const off = Math.hypot(d.x - m.x, d.y - m.y);
  s.missSum += off;
  if (off <= L.r) { s.onTime += dt; s.streak += dt; s.bestStreak = Math.max(s.bestStreak, s.streak); } else s.streak = 0;
  watchPasses(s, off, dt);
  const curved = (x, curve) => (x == null ? null : Flight.curve(x, curve));
  s.frames.push({ t: s.t, raw, curved: { pitch: curved(raw.pitch, c.pitch), yaw: curved(raw.yaw, c.yaw) },
    mark: { x: m.x, y: m.y }, dot: { x: d.x, y: d.y } });
  return s.t + 1e-9 >= s.len ? { done: true, result: summary(s) } : { done: false };
}

// Overshoots, counted as the trainer counts the chase's (sim.js, watchPasses): on each axis, one when the dot changes side
// of the mark close to it while the mark's own motion carries it that way (moving toward the side the dot was on). A change
// of side just after the dot itself turned back on that axis (TURN_BACK), with the dot coming back toward the mark, is the
// dot's doing: the dot came back through a mark still on its way, which is being late, not overshooting. A mark that runs
// past a dot going away from it is always counted. passes keeps the side the dot was last clearly on; dotWay the way the dot
// last went on that axis, turnedAt when it last turned back; blind adds up the time a pass would be the dot's doing.
function watchPasses(s, off, dt) {
  const L = s.level, m = s.mark, d = s.dot, near = Math.max(0.15, 3 * L.r), band = L.r / 4;
  for (const [axis, miss, v, dv] of [['yaw', d.x - m.x, m.vx, d.vx], ['pitch', d.y - m.y, m.vy, d.vy]]) {
    const way = Math.sign(dv);
    if (way && s.dotWay[axis] && way !== s.dotWay[axis]) s.turnedAt[axis] = s.t;
    if (way) s.dotWay[axis] = way;
    const forced = s.t - s.turnedAt[axis] < TURN_BACK && !!s.passes[axis] && way === -s.passes[axis];
    if (forced) s.blind[axis] += dt;
    if (Math.abs(miss) < band) continue;
    const side = Math.sign(miss);
    const carried = Math.abs(v) >= L.r && Math.sign(v) === s.passes[axis];
    if (s.passes[axis] && side !== s.passes[axis] && off <= near && !forced && carried) s.overshoots[axis]++;
    s.passes[axis] = side;
  }
}

const score = s => (s.t > 0 ? Math.round(100 * s.onTime / s.t) : 0);

// pct: the share of the round on the dot; streak: the longest time on it, in seconds; miss: how far the mark was from the
// dot's middle on average, in dot radii (under 1 is on it); overshoots per axis; peak: the farthest the stick went each way
// (0 to 1, before any curve).
function summary(s) {
  const tenths = v => Math.round(10 * v) / 10, hundredths = v => Math.round(100 * v) / 100;
  return { level: s.level.id, set: s.set, pct: tenths(100 * s.onTime / s.len), streak: tenths(s.bestStreak),
    miss: hundredths(s.missSum / Math.max(1, s.steps) / s.level.r), overshoots: { ...s.overshoots },
    peak: { pitch: hundredths(s.peak.pitch), yaw: hundredths(s.peak.yaw) } };
}

// ---- the picture
// The square field in the middle of a w x h canvas, with a margin so a mark at the edge shows whole.
function place(w, h) {
  const k = Math.max(1, (Math.min(w, h) - 64) / 2);
  return { k, X: x => w / 2 + x * k, Y: y => h / 2 - y * k };
}
// accent: the mark's colour; look: Flight.look; text: words over the picture ("Get ready\nStarting in 3"), or none.
function draw(g, w, h, s, accent, look, text) {
  const { k, X, Y } = place(w, h);
  g.fillStyle = look('--bg', '#05070b');
  g.fillRect(0, 0, w, h);
  g.fillStyle = 'rgba(255,255,255,.03)';
  g.fillRect(X(-1), Y(1), 2 * k, 2 * k);
  g.strokeStyle = 'rgba(140,165,200,.30)';
  g.lineWidth = 1.5;
  g.strokeRect(X(-1), Y(1), 2 * k, 2 * k);
  g.strokeStyle = 'rgba(140,165,200,.12)';
  g.lineWidth = 1;
  g.beginPath();
  g.moveTo(X(0), Y(1)); g.lineTo(X(0), Y(-1)); g.moveTo(X(-1), Y(0)); g.lineTo(X(1), Y(0));
  g.stroke();

  const d = s.dot, m = s.mark, on = Math.hypot(d.x - m.x, d.y - m.y) <= s.level.r;
  g.beginPath();
  g.arc(X(d.x), Y(d.y), s.level.r * k, 0, 7);
  g.fillStyle = on ? 'rgba(61,220,132,.55)' : 'rgba(255,74,61,.55)';
  g.fill();
  g.lineWidth = 2;
  g.strokeStyle = on ? '#3ddc84' : '#ff4a3d';
  g.stroke();

  // the mark
  const nx = X(m.x), ny = Y(m.y);
  g.strokeStyle = accent;
  g.lineWidth = 2.5;
  g.beginPath();
  g.arc(nx, ny, 16, 0, 7);
  for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) { g.moveTo(nx + dx * 16, ny + dy * 16); g.lineTo(nx + dx * 26, ny + dy * 26); }
  g.stroke();
  g.fillStyle = accent;
  g.beginPath(); g.arc(nx, ny, 2.5, 0, 7); g.fill();

  // readouts
  g.fillStyle = look('--text', '#e2e8f0');
  g.font = '600 15px "Bahnschrift","Segoe UI",sans-serif';
  g.fillText('ON THE DOT ' + score(s) + '%', 14, 24);
  g.textAlign = 'right';
  g.fillText(Math.max(0, Math.ceil(s.len - s.t - 1e-9)) + ' s', w - 14, 24);
  g.textAlign = 'left';
  g.font = '12px "Segoe UI",sans-serif';
  g.fillStyle = look('--muted', '#8d99aa');
  g.fillText('The mark goes where the stick points', 14, 44);
  if (text) cover(g, w, h, text, look);
}

function cover(g, w, h, text, look) {
  g.fillStyle = 'rgba(5,7,11,.6)';
  const ground = look('--bg', null);
  if (ground && /^#[0-9a-f]{6}$/i.test(ground)) g.fillStyle = 'rgba(' + parseInt(ground.slice(1, 3), 16) + ',' + parseInt(ground.slice(3, 5), 16) + ',' + parseInt(ground.slice(5, 7), 16) + ',.6)';
  g.fillRect(0, 0, w, h);
  g.fillStyle = look('--text', '#e2e8f0');
  g.textAlign = 'center';
  g.font = '600 26px "Bahnschrift","Segoe UI",sans-serif';
  String(text).split('\n').forEach((line, i) => {
    if (i === 1) g.font = '14px "Segoe UI",sans-serif';
    g.fillText(line, w / 2, h / 2 - 10 + i * 26);
  });
  g.textAlign = 'left';
}

// ============================================================ the self-test (chase.html?selftest, or a node runner)
// The owner's rules for this game, checked with modelled hands. The hands aim through the straight curve (a hand that knows
// its curve aims the same through any curve). rules: the rules to check, these by default; a runner may pass another set
// (the staged ones) to watch the checks fail. Answers {ok, checks:[{name, ok, got, want}]}, as heat-2d.js does.
function selftest(rules) {
  const R = rules || { LEVELS, start, step, summary };
  const checks = [], STEP = 1 / 120, SEEDS = [1, 2, 3, 4, 5, 6];
  const check = (name, fn) => {
    try { const [got, want, ok] = fn(); checks.push({ name, ok: !!ok, got, want }); }
    catch (e) { checks.push({ name, ok: false, got: 'error: ' + e.message, want: 'no error' }); }
  };
  const round = (v, n = 2) => Math.round(v * 10 ** n) / 10 ** n;
  const mean = list => list.reduce((a, b) => a + b, 0) / list.length;
  const both = c => ({ pitch: { ...c }, yaw: { ...c } });
  const usual = { deadLow: 4, deadHigh: 4, satX: 92, satY: 68, bend: 20 };   // Studio's starting curve (StickCurve, Gremlin.cs)
  const level = id => R.LEVELS.find(l => l.id === id);
  const fly = (id, curves, seed, makeHand) => {
    const s = R.start(id, curves, { seed }), h = makeHand ? makeHand(s) : () => null;
    let out;
    do out = R.step(s, h(), STEP); while (!out.done);
    return out.result;
  };
  // Where full stick puts the mark each way (the sign says which way it goes): the modelled hands aim with it.
  let full = null;
  const reach = () => {
    if (!full) { const s = R.start('medium', both(STRAIGHT), { seed: 1 }); R.step(s, { pitch: 1, yaw: 1 }, STEP); full = { x: s.mark.x, y: s.mark.y }; }
    return full;
  };
  // A modelled hand: it aims where it saw the dot `lag` seconds ago and moves there smoothed over `smooth` seconds, with a
  // little wander (`noise`, in field units). With `every` it aims afresh only that often, in a quick move, and goes `gain`
  // times as far as it needs to: past the dot when gain is over 1, as an eager hand that overshoots does. It starts on the
  // dot, as the page shows the dot through its 3-2-1. Seeded, so a check answers the same every time.
  const hand = (o, seed) => s => {
    const rnd = random(seed * 7919 + 13), seen = [], p = { x: s.dot.x, y: s.dot.y }, goal = { ...p }, n = { x: 0, y: 0 }, to = { x: 0, y: 0 }, f = reach();
    let t = 0, next = 0, move = 0;
    return () => {
      seen.push({ x: s.dot.x, y: s.dot.y });
      const at = seen[Math.max(0, seen.length - 1 - Math.round(o.lag / STEP))];
      t += STEP;
      if (t >= next) { to.x = (2 * rnd() - 1) * o.noise; to.y = (2 * rnd() - 1) * o.noise; next = t + 0.25; }
      const aim = !o.every || t >= move;
      if (aim) move = t + (o.every || 0);
      for (const a of ['x', 'y']) {
        n[a] += (to[a] - n[a]) * Math.min(1, STEP / 0.1);
        if (aim) goal[a] = p[a] + (o.gain || 1) * (at[a] - p[a]);
        p[a] += (goal[a] + n[a] - p[a]) * (1 - Math.exp(-STEP / o.smooth));
      }
      return { pitch: clamp(p.y / f.y, -1, 1), yaw: clamp(p.x / f.x, -1, 1) };
    };
  };

  check('(a) let go: the mark is back in the middle at once, and does not drift', () => {
    const s = R.start('medium', both(usual), { seed: 1 });
    for (let i = 0; i < 60; i++) R.step(s, { pitch: 0.8, yaw: -0.6 }, STEP);
    const held = [round(s.mark.x), round(s.mark.y)];
    R.step(s, null, STEP);
    const back = [s.mark.x, s.mark.y];
    let drift = 0;
    for (let i = 0; i < 240; i++) { R.step(s, { pitch: 0, yaw: 0 }, STEP); drift = Math.max(drift, Math.abs(s.mark.x), Math.abs(s.mark.y)); }
    return [{ held, back, drift }, { held: 'away from the middle', back: [0, 0], drift: 0 },
      held[0] !== 0 && held[1] !== 0 && back[0] === 0 && back[1] === 0 && drift === 0];
  });

  const overshoots = o => SEEDS.map(seed => fly('medium', both(STRAIGHT), seed, hand(o, seed))).map(r => r.overshoots.pitch + r.overshoots.yaw);
  check('(b) a hand 150 ms late that never overshoots (smooth, or in jumps that stop where it saw the dot) counts fewer than 2 a Medium round', () => {
    const smooth = overshoots({ lag: 0.15, smooth: 0.08, noise: 0.01 }), jumps = overshoots({ lag: 0.15, smooth: 0.05, noise: 0.01, every: 0.35, gain: 1 });
    return [{ smooth: round(mean(smooth), 1), jumps: round(mean(jumps), 1), rounds: [smooth, jumps] }, 'fewer than 2 a round, each', mean(smooth) < 2 && mean(jumps) < 2];
  });
  check('(b) an eager hand (each jump goes as far past where it saw the dot as it was short) counts 6 or more a Medium round', () => {
    const counts = overshoots({ lag: 0.15, smooth: 0.05, noise: 0.01, every: 0.35, gain: 2 });
    return [{ perRound: round(mean(counts), 1), rounds: counts }, '6 or more a round', mean(counts) >= 6];
  });
  check('(b) the mark catching up and going past the dot counts one; the dot catching up and going past the mark none', () => {
    // A dot going straight to the right for 2 s, and a mark going the same way: faster from behind it, or slower ahead of it.
    const pass = (from, speed) => {
      const s = R.start({ id: 'test', name: 'Test', speed: 0.4, turn: 0, r: 0.09 }, both(STRAIGHT), { seed: 1 });
      Object.assign(s.dot, { x: -0.5, y: 0, h: 0, phase: 0 });
      for (let i = 1; i <= 240; i++) R.step(s, { pitch: 0, yaw: clamp(from + speed * i * STEP, -1, 1) }, STEP);
      return s.overshoots.yaw + s.overshoots.pitch;
    };
    const got = { markPasses: pass(-0.8, 0.9), dotPasses: pass(-0.2, 0.15) };
    return [got, { markPasses: 1, dotPasses: 0 }, got.markPasses === 1 && got.dotPasses === 0];
  });

  check('(c) the same stick travel moves the mark the same number of dot sizes up and down as left and right', () => {
    const r = level('medium').r;
    const got = [0.1, 0.3, 0.5, 0.8, 1].map(x => {
      const s = R.start('medium', both(usual), { seed: 1 });
      R.step(s, { pitch: x, yaw: x }, STEP);
      return [round(Math.abs(s.mark.x) / r, 3), round(Math.abs(s.mark.y) / r, 3)];
    });
    return [got, 'each pair equal (left/right, up/down)', got.every(([a, b]) => Math.abs(a - b) < 1e-6)];
  });

  check('(d) with "Most it gives" at 60 the mark reaches every place the dot goes', () => {
    const c = both({ ...usual, satY: 60 }), far = { x: 0, y: 0 };
    for (const L of R.LEVELS) for (const seed of [1, 2, 3]) {
      const s = R.start(L.id, c, { seed });
      let out;
      do { out = R.step(s, null, STEP); far.x = Math.max(far.x, Math.abs(s.dot.x)); far.y = Math.max(far.y, Math.abs(s.dot.y)); } while (!out.done);
    }
    // Where the mark can be: the stick swept from one end to the other, on both axes at once.
    const xs = [], ys = [];
    for (let i = -500; i <= 500; i++) {
      const s = R.start('medium', c, { seed: 1 });
      R.step(s, { pitch: i / 500, yaw: i / 500 }, STEP);
      xs.push(s.mark.x); ys.push(s.mark.y);
    }
    const smallest = Math.min(...R.LEVELS.map(L => L.r));
    const covers = (list, edge) => {
      const sorted = [...list].sort((a, b) => a - b);
      let gap = 0;
      for (let i = 1; i < sorted.length; i++) gap = Math.max(gap, sorted[i] - sorted[i - 1]);
      return sorted[0] <= -edge && sorted[sorted.length - 1] >= edge && gap <= smallest;
    };
    return [{ dotGoes: [round(far.x), round(far.y)], markReaches: [round(Math.max(...xs.map(Math.abs))), round(Math.max(...ys.map(Math.abs)))] },
      'the mark reaches at least as far as the dot, both ways, with no gaps', covers(xs, far.x) && covers(ys, far.y)];
  });

  check('(e) a relaxed hand (sees the dot 200 ms late, smoothed 80 ms, a little wander) scores about 90% / 70% / 45%, within 10', () => {
    const want = { easy: 90, medium: 70, hard: 45 }, got = {};
    for (const id of Object.keys(want)) got[id] = round(mean(SEEDS.map(seed => fly(id, both(STRAIGHT), seed, hand({ lag: 0.2, smooth: 0.08, noise: 0.01 }, seed)).pct)), 1);
    return [got, want, Object.keys(want).every(id => Math.abs(got[id] - want[id]) <= 10)];
  });

  check('"Most it gives" does not change where the mark goes; the dead zone, the travel and the softer middle do', () => {
    const at = (c, x) => { const s = R.start('medium', both(c), { seed: 1 }); R.step(s, { pitch: 0, yaw: x }, STEP); return round(s.mark.x, 4); };
    const got = { most60: at({ ...usual, satY: 60 }, 0.5), most100: at({ ...usual, satY: 100 }, 0.5), inDeadZone: at(usual, 0.03),
      travel60: at({ ...usual, satX: 60 }, 0.5), bend60: at({ ...usual, bend: 60 }, 0.5) };
    return [got, 'most60 = most100, inDeadZone 0, travel60 and bend60 not most100',
      got.most60 === got.most100 && got.inDeadZone === 0 && got.travel60 !== got.most100 && got.bend60 !== got.most100];
  });
  check('the curves are kept as they were at the start of the round', () => {
    const c = both(usual), s = R.start('medium', c, { seed: 1 });
    R.step(s, { pitch: 0, yaw: 0.5 }, STEP);
    const before = s.mark.x;
    c.yaw.bend = 90; c.yaw.satX = 50; c.yaw.deadHigh = 40;
    R.step(s, { pitch: 0, yaw: 0.5 }, STEP);
    return [[round(before, 4), round(s.mark.x, 4)], 'the same both times', before === s.mark.x];
  });
  check('the game\'s invert turns the mark the other way', () => {
    const at = invert => { const s = R.start('medium', both(usual), { seed: 1, invert }); R.step(s, { pitch: 0.5, yaw: 0.5 }, STEP); return [round(s.mark.x, 4), round(s.mark.y, 4)]; };
    const plain = at({}), turned = at({ pitch: true, yaw: true });
    return [{ plain, turned }, 'turned is plain the other way', plain[0] === -turned[0] && plain[1] === -turned[1] && plain[0] !== 0 && plain[1] !== 0];
  });
  check('the same seed flies the same round; another seed another', () => {
    const path = seed => { const s = R.start('hard', both(usual), { seed }); for (let i = 0; i < 1200; i++) R.step(s, null, STEP); return [round(s.dot.x, 6), round(s.dot.y, 6)]; };
    const a = path(7), b = path(7), c = path(8);
    return [{ seed7: a, again: b, seed8: c }, 'seed 7 the same twice, seed 8 elsewhere', JSON.stringify(a) === JSON.stringify(b) && JSON.stringify(a) !== JSON.stringify(c)];
  });
  check('every step keeps the raw stick, what the curve made of it, the mark and the dot', () => {
    const s = R.start('medium', both(usual), { seed: 1 });
    for (let i = 0; i < 10; i++) R.step(s, { pitch: 0.5, yaw: -0.25 }, STEP);
    const f = s.frames[s.frames.length - 1];
    return [{ frames: s.frames.length, last: f }, '10 frames, the last one as the state is now',
      s.frames.length === 10 && f.raw.pitch === 0.5 && f.raw.yaw === -0.25 && f.curved.pitch === Flight.curve(0.5, usual)
      && f.curved.yaw === Flight.curve(-0.25, usual) && f.mark.x === s.mark.x && f.mark.y === s.mark.y && f.dot.x === s.dot.x && f.dot.y === s.dot.y];
  });
  check('a round lasts 30 s, and its summary has the score, longest streak, mean miss, overshoots and peak stick', () => {
    const s = R.start('easy', both(usual), { seed: 3, set: 'faOn' });
    let out, steps = 0;
    do { out = R.step(s, { pitch: 0.2, yaw: -0.3 }, STEP); steps++; } while (!out.done);
    const r = out.result;
    return [{ steps, result: r }, { steps: 3600, result: 'level, set, pct, streak, miss, overshoots, peak' },
      steps === 3600 && r.level === 'easy' && r.set === 'faOn' && ['pct', 'streak', 'miss'].every(k => typeof r[k] === 'number')
      && typeof r.overshoots.pitch === 'number' && typeof r.overshoots.yaw === 'number' && r.peak.pitch === 0.2 && r.peak.yaw === 0.3];
  });
  // (h) The checkers' findings on these rules.
  check('(h) the rule leaves a pass uncounted as the dot\'s doing (it turned back and came into the mark) under 10% of each axis\'s time, at every level', () => {
    const got = {};
    for (const L of R.LEVELS) {
      const blind = { pitch: 0, yaw: 0 };
      let time = 0;
      for (const seed of SEEDS) {
        const s = R.start(L.id, both(STRAIGHT), { seed }), h = hand({ lag: 0.2, smooth: 0.08, noise: 0.01 }, seed)(s);
        let out;
        do out = R.step(s, h(), STEP); while (!out.done);
        blind.pitch += s.blind.pitch; blind.yaw += s.blind.yaw; time += s.t;
      }
      got[L.id] = { pitch: round(100 * blind.pitch / time, 1), yaw: round(100 * blind.yaw / time, 1) };
    }
    return [got, 'under 10 (percent of the time) on each axis, at each level', Object.values(got).every(a => a.pitch < 10 && a.yaw < 10)];
  });
  check('(h) a stick lost or let go is kept as no reading (null) in the round\'s record, never as the middle (0)', () => {
    const s = R.start('medium', both(usual), { seed: 1 });
    R.step(s, null, STEP);
    R.step(s, { pitch: null, yaw: 0.4 }, STEP);
    R.step(s, { pitch: NaN, yaw: 0 }, STEP);
    const got = s.frames.map(f => ({ raw: f.raw, curved: f.curved }));
    const want = [{ raw: { pitch: null, yaw: null }, curved: { pitch: null, yaw: null } },
      { raw: { pitch: null, yaw: 0.4 }, curved: { pitch: null, yaw: Flight.curve(0.4, usual) } },
      { raw: { pitch: null, yaw: 0 }, curved: { pitch: null, yaw: 0 } }];
    return [got, want, JSON.stringify(got) === JSON.stringify(want) && s.mark.y === 0];
  });
  check('(h) one step moves the round on a tenth of a second at most, however long the page was hidden or paused', () => {
    const s = R.start('medium', both(usual), { seed: 1 });
    const out = R.step(s, { pitch: 0, yaw: 0 }, 5);
    return [{ t: round(s.t, 9), done: out.done }, { t: 0.1, done: false }, round(s.t, 9) === 0.1 && !out.done];
  });

  check('the picture puts the dot and the mark where the rules have them', () => {
    const s = R.start('medium', both(usual), { seed: 2 });
    for (let i = 0; i < 30; i++) R.step(s, { pitch: 0.4, yaw: 0.6 }, STEP);
    const arcs = [], noop = () => {};
    const g = new Proxy({ arc: (x, y) => arcs.push([x, y]) }, { get: (o, k) => (k in o ? o[k] : noop), set: () => true });
    draw(g, 800, 500, s, '#fff', (name, fallback) => fallback, '');
    const p = place(800, 500), has = (x, y) => arcs.some(a => Math.abs(a[0] - x) < 1e-6 && Math.abs(a[1] - y) < 1e-6);
    return [{ arcs: arcs.length }, 'an arc at the dot and one at the mark', has(p.X(s.dot.x), p.Y(s.dot.y)) && has(p.X(s.mark.x), p.Y(s.mark.y))];
  });
  return { ok: checks.every(c => c.ok), checks };
}

return { LEVELS, ROUND, start, step, score, summary, markOf, draw, selftest };
})();
