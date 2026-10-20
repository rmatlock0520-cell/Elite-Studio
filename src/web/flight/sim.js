// STUDIO FLIGHT: the trainer's ship, its exercises and their pictures. Loaded by index.html after ../studio.js and flight.js.
//
// The ship flies as Elite's ships do with Flight Assist off. The stick sets how hard the thrusters turn the ship, not how
// fast it turns: a turn goes on at the speed it has until the opposite stick stops it, up to the ship's top turn rate
// (its pitch, roll and yaw rates from the community's ship table, reached in about two thirds of a second of full stick).
// Movement is the same: thrust adds to the velocity, nothing takes it away, and the ship drifts on along it, up to its
// top speed in any direction (a boost goes past that for a few seconds). Turning is best in the "blue zone", from half to
// three quarters of top speed, and slower outside it. Forward thrust is about an eighth of the top speed per second
// (35 m/s² for a Cobra Mk III), reverse and the side thrusters weaker: the shares a datamined Cobra Mk III has.
//
// Frames: the world, and the ship's own: right +X, up +Y, nose −Z. A turn rate that is positive means nose up (pitch),
// nose right (yaw) and right wing down (roll), the way the stick commands them.
var Sim = (() => {
'use strict';
const D = Math.PI / 180;
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

// ---- vectors and quaternions ([w, x, y, z]; a quaternion turns the ship's frame into the world's)
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const len = a => Math.hypot(a[0], a[1], a[2]);
const unit = a => { const l = len(a); return l > 1e-12 ? mul(a, 1 / l) : [0, 0, -1]; };
const angle = (a, b) => Math.acos(clamp(dot(unit(a), unit(b)), -1, 1));
const qmul = (a, b) => [
  a[0] * b[0] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3],
  a[0] * b[1] + a[1] * b[0] + a[2] * b[3] - a[3] * b[2],
  a[0] * b[2] - a[1] * b[3] + a[2] * b[0] + a[3] * b[1],
  a[0] * b[3] + a[1] * b[2] - a[2] * b[1] + a[3] * b[0]];
const qnorm = q => { const l = Math.hypot(q[0], q[1], q[2], q[3]); return q.map(c => c / l); };
const qconj = q => [q[0], -q[1], -q[2], -q[3]];   // the turn back: the world's frame into the ship's
function rot(q, v) {
  const [w, x, y, z] = q;
  const tx = 2 * (y * v[2] - z * v[1]), ty = 2 * (z * v[0] - x * v[2]), tz = 2 * (x * v[1] - y * v[0]);
  return [v[0] + w * tx + (y * tz - z * ty), v[1] + w * ty + (z * tx - x * tz), v[2] + w * tz + (x * ty - y * tx)];
}
const qaxis = (axis, a) => { const s = Math.sin(a / 2), u = unit(axis); return [Math.cos(a / 2), u[0] * s, u[1] * s, u[2] * s]; };
const qexp = v => { const a = len(v); return a < 1e-12 ? [1, 0, 0, 0] : qaxis(v, a); };
const NOSE = [0, 0, -1], UP = [0, 1, 0], RIGHT = [1, 0, 0];
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const pick = list => list[Math.floor(Math.random() * list.length)];
const sign = () => Math.random() < 0.5 ? -1 : 1;

// ---- the ship
// spec: one of GET /api/flight's ships ({ name, speed, boost, pitch, roll, yaw, boostEvery }).
const RAMP = 0.65;   // seconds of full stick from still to the top turn rate
function ship(spec) {
  const rates = { pitch: spec.pitch * D, yaw: spec.yaw * D, roll: spec.roll * D };
  const forward = spec.speed * 0.125;
  return {
    spec, rates,
    accel: { pitch: rates.pitch / RAMP, yaw: rates.yaw / RAMP, roll: rates.roll / RAMP },
    thrust: { forward, reverse: forward * 0.72, side: forward * 0.57 },
    q: [1, 0, 0, 0], w: { pitch: 0, yaw: 0, roll: 0 }, p: [0, 0, 0], v: [0, 0, 0], t: 0, boostAt: -99,
  };
}
const nose = s => rot(s.q, NOSE), up = s => rot(s.q, UP), right = s => rot(s.q, RIGHT);

// How well the ship turns at its speed: best from half to three quarters of top speed (the blue zone).
function blueZone(s) {
  const share = len(s.v) / s.spec.speed;
  if (share < 0.5) return 0.6 + 0.4 * share / 0.5;
  if (share <= 0.75) return 1;
  if (share <= 1) return 1 - 0.15 * (share - 0.75) / 0.25;
  return 0.8;
}

// One small step. cmd as Flight.reader gives it. move: false keeps the ship where it is (the turning exercises).
function step(s, cmd, dt, move) {
  const zone = move ? blueZone(s) : 1;
  for (const a of ['pitch', 'yaw', 'roll']) {
    const top = s.rates[a] * zone, before = s.w[a];
    let w = before + clamp(cmd[a] || 0, -1, 1) * s.accel[a] * dt;
    // No faster than the top rate; a turn left faster than that (the speed fell out of the blue zone) is slowed down to it.
    if (Math.abs(w) > top) w = Math.sign(w) * Math.max(top, Math.min(Math.abs(w), Math.abs(before) - s.accel[a] * dt));
    s.w[a] = w;
  }
  // In the ship's frame: nose up is a turn about +X, nose right about −Y, right wing down about −Z.
  s.q = qnorm(qmul(s.q, qexp([s.w.pitch * dt, -s.w.yaw * dt, -s.w.roll * dt])));
  s.t += dt;
  if (!move) return;
  const t = s.thrust, ahead = clamp(cmd.ahead || 0, -1, 1);
  if (cmd.boost && s.t - s.boostAt >= s.spec.boostEvery) s.boostAt = s.t;
  const since = s.t - s.boostAt;
  let push = ahead >= 0 ? ahead * t.forward : ahead * t.reverse;
  if (since < 1) push += s.spec.boost - s.spec.speed;   // a boost: up to boost speed within a second
  const a = rot(s.q, [clamp(cmd.lateral || 0, -1, 1) * t.side, clamp(cmd.vertical || 0, -1, 1) * t.side, -push]);
  s.v = add(s.v, mul(a, dt));
  // Top speed in any direction; after a boost it comes back down to it over four seconds.
  const top = since < 1 ? s.spec.boost : since < 5 ? s.spec.boost - (s.spec.boost - s.spec.speed) * (since - 1) / 4 : s.spec.speed;
  const speed = len(s.v);
  if (speed > top) s.v = mul(s.v, Math.max(top, speed - 60 * dt) / speed);
  s.p = add(s.p, mul(s.v, dt));
}

// How the ship will lie in "ahead" seconds if the stick is let go now: the turn goes on as it is.
const frameIn = (s, ahead) => qnorm(qmul(s.q, qexp([s.w.pitch * ahead, -s.w.yaw * ahead, -s.w.roll * ahead])));

// ---- the exercises
// Nine, three of each kind, each harder than the last of its kind, flown in this order. Passing one (three good attempts
// of five) gives the next of Elite's nine combat ranks: a Cadet at the start, Harmless after the first, Elite after the last.
const RANKS = ['Cadet', 'Harmless', 'Mostly Harmless', 'Novice', 'Competent', 'Expert', 'Master', 'Dangerous', 'Deadly', 'Elite'];
const EXERCISES = [
  // Chase the target (in the owner's words, the test for the curves): a target off to one side flies away; swing onto it
  // fast (full stick), then hold it with small corrections. Both halves are what a curve has to give.
  { id: 'chase-1', kind: 'chase', level: 1, title: 'Chase the target', what: 'A target off to one side flies away from you. Swing onto it and stay on it: 6 of 20 seconds on target.',
    away: [25, 60], speed: 6, motion: 'line', dot: 3.5, need: 6, time: 20 },
  { id: 'tunnel-1', kind: 'tunnel', level: 1, title: 'Tunnel run', what: 'Fly the tunnel through four gentle bends without touching the wall.',
    turns: 4, bend: [20, 32], radius: 120, arc: 700, straight: [700, 1000], start: 80, time: 240 },
  { id: 'track-1', kind: 'track', level: 1, title: 'Track the target', what: 'The dot drifts. Keep the nose on it for 5 of 10 seconds.',
    speed: 5, motion: 'line', dot: 3, need: 5 },
  { id: 'chase-2', kind: 'chase', level: 2, title: 'Chase the target', what: 'Further round, faster, and curving as it goes. 7 of 20 seconds on target.',
    away: [40, 120], speed: 10, motion: 'curve', dot: 3, need: 7, time: 20 },
  { id: 'tunnel-2', kind: 'tunnel', level: 2, title: 'Tunnel run', what: 'Five sharper bends in a narrower tunnel.',
    turns: 5, bend: [35, 50], radius: 95, arc: 550, straight: [600, 900], start: 90, time: 240 },
  { id: 'track-2', kind: 'track', level: 2, title: 'Track the target', what: 'The dot curves across the sky. Keep the nose on it for 6 of 10 seconds.',
    speed: 9, motion: 'curve', dot: 2.5, need: 6 },
  { id: 'chase-3', kind: 'chase', level: 3, title: 'Chase the target', what: 'Anywhere around you, fast, and it jinks like a ship that knows you are there. 8 of 20 seconds on target.',
    away: [60, 170], speed: 15, motion: 'jink', dot: 2.5, need: 8, time: 20 },
  { id: 'tunnel-3', kind: 'tunnel', level: 3, title: 'Tunnel run', what: 'Six hard bends that twist in every direction, in a tight tunnel.',
    turns: 6, bend: [55, 75], radius: 75, arc: 450, straight: [500, 800], start: 100, twist: true, time: 240 },
  { id: 'track-3', kind: 'track', level: 3, title: 'Track the target', what: 'The dot jinks like a ship that knows you are there. 7 of 10 seconds on it.',
    speed: 14, motion: 'jink', dot: 2, need: 7 },
];
const FREE = { id: 'free', kind: 'free', level: 0, title: 'Free flight', what: 'No score: feel the momentum. Turn, let go, and watch the turn go on until you stop it. Stop and Fly start again.',
  away: [20, 35], dot: 3 };

// ---- one attempt
// start(ex, spec) sets the scene; update(a, cmd, dt) flies it a step and says when it is over: { done, ok, why }.
function start(ex, spec) {
  const s = ship(spec);
  const a = { ex, s, target: [...NOSE], time: 0, held: 0, onTime: 0, firstOn: null, crossings: {}, signs: {}, passes: {}, peak: { pitch: 0, yaw: 0, roll: 0 }, best: 180 };
  if (ex.kind === 'free') {
    // The nose somewhere off the dot, the ship rolled any way.
    const off = rand(ex.away[0], ex.away[1]) * D, around = rand(0, 2 * Math.PI);
    s.q = qnorm(qmul(qaxis([Math.cos(around), Math.sin(around), 0], off), qaxis(NOSE, rand(0, 2 * Math.PI))));
  } else if (ex.kind === 'chase') {
    // The target somewhere off the nose, and it flies away from the nose to start with: it turns about nose × target, which
    // carries it along the great circle from the nose through it, outward, however far round it is (up to nearly behind).
    const off = rand(ex.away[0], ex.away[1]) * D, around = rand(0, 2 * Math.PI);
    a.target = unit(rot(s.q, rot(qaxis([Math.cos(around), Math.sin(around), 0], off), NOSE)));
    a.axis = unit(cross(nose(s), a.target));
    a.phase = rand(0, 6);   // the line's speed wobble
    a.nextJink = 1.5;
  } else if (ex.kind === 'track') {
    a.target = unit(rot(qaxis([Math.random() - 0.5, Math.random() - 0.5, 0], 1 * D), NOSE));
    a.axis = unit([sign(), (Math.random() - 0.5) * 0.3, 0]);   // the first way the dot drifts: up or down, mostly
    a.phase = rand(0, 6);
    a.nextJink = 1.5;
  } else if (ex.kind === 'tunnel') {
    a.path = tunnel(ex);
    a.at = 0;
    s.v = mul(NOSE, ex.start);
  }
  return a;
}

// Enough seconds on target? (The steps of 1/120 s add up with a little float error either way.) And those seconds rounded
// down to tenths, under the same rule, so 5.97 s shows as 5.9 and never reads as the 6 needed.
const enough = (t, need) => t + 1e-9 >= need;
const tenths = t => (Math.floor((t + 1e-9) * 10) / 10).toFixed(1);

// Counts the turns that changed direction on each axis: one is the stop; the rest are overshoots. A turn the other way in
// the second after the target jinked is the target's doing, not an overshoot: it is not counted.
function watchTurns(a, axes) {
  const forced = a.jinkAt != null && a.time - a.jinkAt < 1;
  for (const axis of axes) {
    const w = a.s.w[axis] / D, peak = Math.abs(w);
    if (peak < 1.5) continue;
    const now = Math.sign(w);
    if (a.signs[axis] && now !== a.signs[axis] && !forced) a.crossings[axis] = (a.crossings[axis] || 0) + 1;
    a.signs[axis] = now;
  }
}

// The chase's overshoots on pitch and yaw: the nose's own pitch or yaw turn carrying it past the target, up and down or side
// to side, close to it (each pass counts one; a stop on its own side counts none). The target's side seen from the ship
// also flips when the ship rolls, or when a curving target swings round the nose; neither is an overshoot, so a flip counts
// only while that axis turns the nose toward the side the target was on (nose up, w.pitch > 0, toward a target above).
// The second after a jink is not counted either: the target may jink across the nose. a.passes keeps the side of the
// target the nose was last clearly on.
function watchPasses(a, off) {
  const ex = a.ex, near = Math.max(8, 3 * ex.dot), band = ex.dot / 4;
  const forced = a.jinkAt != null && a.time - a.jinkAt < 1;
  const t = rot(qconj(a.s.q), a.target), ahead = -t[2];   // the target in the ship's own frame
  const misses = { pitch: Math.atan2(t[1], ahead) / D, yaw: Math.atan2(t[0], ahead) / D };
  for (const axis of ['pitch', 'yaw']) {
    const e = misses[axis];
    if (Math.abs(e) < band) continue;
    const side = Math.sign(e), w = a.s.w[axis];
    const carried = Math.abs(w) / D >= 1.5 && Math.sign(w) === a.passes[axis];
    if (a.passes[axis] && side !== a.passes[axis] && off <= near && !forced && carried) a.crossings[axis] = (a.crossings[axis] || 0) + 1;
    a.passes[axis] = side;
  }
}

function update(a, cmd, dt) {
  const ex = a.ex, s = a.s;
  // How far the stick went: the stick itself (cmd.real, before any curve, whose "most it gives" may stop short of 1) or the keys.
  for (const axis of ['pitch', 'yaw', 'roll']) a.peak[axis] = Math.max(a.peak[axis], Math.abs(cmd[axis] || 0), Math.abs((cmd.real && cmd.real[axis]) || 0));
  step(s, cmd, dt, ex.kind === 'tunnel');
  a.time += dt;
  watchTurns(a, ex.kind === 'chase' ? ['roll'] : ['pitch', 'yaw', 'roll']);

  if (ex.kind === 'free') return { done: false };
  if (ex.kind === 'chase') {
    moveTarget(a, dt);
    const off = angle(nose(s), a.target) / D;
    watchPasses(a, off);
    a.best = Math.min(a.best, off);
    if (off <= ex.dot) {
      a.onTime += dt;
      if (a.firstOn === null) a.firstOn = a.time;
    }
    if (a.time >= ex.time) {
      const ok = enough(a.onTime, ex.need);
      return { done: true, ok, why: 'On target ' + tenths(a.onTime) + ' of ' + ex.time + ' s (' + ex.need + ' needed)'
        + (a.firstOn !== null ? ', first on it after ' + a.firstOn.toFixed(1) + ' s' : ', never on it: the closest was ' + a.best.toFixed(1) + '°') };
    }
    return { done: false };
  }
  if (ex.kind === 'track') {
    moveTarget(a, dt);
    const off = angle(nose(s), a.target) / D;
    if (off <= ex.dot) a.onTime += dt;
    if (a.time >= 10) {
      const ok = enough(a.onTime, ex.need);
      return { done: true, ok, why: 'On target ' + tenths(a.onTime) + ' of 10 s (' + ex.need + ' needed)' };
    }
    return { done: false };
  }
  // tunnel
  const where = follow(a.path, s.p, a.at);
  a.at = where.i;
  a.wall = where.off / ex.radius;
  if (where.off > ex.radius - 8) return { done: true, ok: false, why: 'Hit the wall ' + Math.round(where.along) + ' m in, at ' + Math.round(len(s.v)) + ' m/s' };
  if (where.i >= a.path.points.length - 2) return { done: true, ok: true, why: 'Through in ' + a.time.toFixed(1) + ' s' };
  if (where.along < -40) return { done: true, ok: false, why: 'Flew back out of the start' };
  if (a.time >= ex.time) return { done: true, ok: false, why: 'Out of time' };
  return { done: false };
}

// The dot of the track and chase exercises moves across the sky: steadily one way (line), along a slow curve (curve), or in sudden new directions (jink).
function moveTarget(a, dt) {
  const ex = a.ex, t = a.time;
  // The dot's speeds are set for a Cobra Mk III (40°/s pitch); a ship that turns slower gets a slower dot.
  let axis = a.axis, speed = ex.speed * Math.min(1, a.s.rates.pitch / (40 * D));
  if (ex.motion === 'line') speed *= 0.75 + 0.25 * Math.sin(0.6 * t + a.phase);
  else if (ex.motion === 'curve' && ex.kind === 'chase') {
    // The chase's target may be anywhere round the ship: its way of moving turns about the target itself (20°/s), so the
    // curve is smooth wherever it is. (The track's dot stays near the nose, where turning about the nose does the same.)
    a.axis = unit(rot(qaxis(a.target, 0.35 * dt), a.axis));
    axis = a.axis;
  } else if (ex.motion === 'curve') axis = rot(qaxis(NOSE, 0.35 * t + a.phase), a.axis);
  else {
    if (t >= a.nextJink) {
      a.from = a.axis;
      a.axis = unit(rot(qaxis(a.target, rand(0, 2 * Math.PI)), cross(a.target, UP)));
      a.jinkAt = t;
      a.nextJink = t + rand(1.4, 2.8);
    }
    const blend = a.from ? clamp((t - a.jinkAt) / 0.35, 0, 1) : 1;
    axis = a.from ? unit(add(mul(a.from, 1 - blend), mul(a.axis, blend))) : a.axis;
    speed *= 0.7 + 0.3 * Math.sin(1.3 * t);
  }
  // Only the part of the turn across the line of sight moves the dot.
  const across = unit(sub(axis, mul(a.target, dot(axis, a.target))));
  a.target = unit(rot(qexp(mul(across, speed * D * dt)), a.target));
}

// ---- the tunnel: its centre line in 5 m steps, each with the way along and a steady "up" for drawing its rings
function tunnel(ex) {
  const stepLength = 5, points = [], turns = [];
  let p = [0, 0, 0], along = [...NOSE], top = [...UP];
  const go = metres => {
    for (let d = 0; d < metres; d += stepLength) { points.push({ p, t: along, u: top }); p = add(p, mul(along, stepLength)); }
  };
  go(500);
  for (let i = 0; i < ex.turns; i++) {
    const bend = rand(ex.bend[0], ex.bend[1]) * D;
    const side = cross(along, top);
    // Left, right, up or down; with "twist", any direction around the way along.
    const axis = ex.twist ? rot(qaxis(along, rand(0, 2 * Math.PI)), top) : pick([top, mul(top, -1), side, mul(side, -1)]);
    const steps = Math.ceil(ex.arc * bend / stepLength), turn = qaxis(axis, bend / steps);
    turns.push({ at: points.length, bend: bend / D });
    for (let k = 0; k < steps; k++) {
      points.push({ p, t: along, u: top, turning: true });
      along = unit(rot(turn, along));
      top = unit(rot(turn, top));
      p = add(p, mul(along, stepLength));
    }
    go(rand(ex.straight[0], ex.straight[1]));
  }
  go(400);
  return { points, turns, stepLength };
}

// The point of the centre line nearest the ship, looked for around where it was last: its step, how far along, how far off.
function follow(path, p, from) {
  const pts = path.points;
  let best = from, bestOff = Infinity, bestAlong = 0;
  for (let i = Math.max(0, from - 20); i < Math.min(pts.length - 1, from + 80); i++) {
    const a = pts[i].p, b = pts[i + 1].p, ab = sub(b, a);
    const k = clamp(dot(sub(p, a), ab) / dot(ab, ab), i === 0 ? -20 : 0, 1);
    const off = len(sub(p, add(a, mul(ab, k))));
    if (off < bestOff) { bestOff = off; best = i; bestAlong = (i + k) * path.stepLength; }
  }
  return { i: best, off: bestOff, along: bestAlong };
}

// ---- pictures
const STARS = Array.from({ length: 420 }, () => ({ d: unit([Math.random() * 2 - 1, Math.random() * 2 - 1, Math.random() * 2 - 1]), b: Math.random() }));
// The scope's scale: the angle from its centre (degrees) to a share of its radius. The middle is spread out, where precision counts.
const SCOPE = [[0, 0], [5, 0.25], [20, 0.5], [60, 0.75], [180, 1]];
function squash(deg) {
  for (let i = 1; i < SCOPE.length; i++) if (deg <= SCOPE[i][0]) {
    const [a0, r0] = SCOPE[i - 1], [a1, r1] = SCOPE[i];
    return r0 + (r1 - r0) * (deg - a0) / (a1 - a0);
  }
  return 1;
}
// A camera's three directions: looking along f, with u up.
function camera(f, u) {
  f = unit(f);
  let r = cross(f, u);
  if (len(r) < 1e-6) r = cross(f, [1, 0, 0]);
  r = unit(r);
  return { f, u: cross(r, f), r };
}
// The "dot" view's camera: looking at the target, with the ship's up carried along the arc from the nose to it. The reticle
// then always sits on the side away from the target and comes in as the nose turns to it, even with the target far round
// (the ship's up squared to the target alone flips the picture over as the target passes 90° off). Close to straight
// behind, that arc swings round fast, so the picture turns toward it at most 10° a frame (a.camUp keeps the last up):
// a sudden spin there becomes a short turn, while the ship's own roll (a few degrees a frame) still turns it at once.
function dotCamera(a) {
  const s = a.s, f = a.target, n = nose(s), k = cross(n, f);
  const want = camera(f, len(k) > 1e-9 ? rot(qaxis(k, angle(n, f)), up(s)) : up(s)).u;
  let u = want;
  if (a.camUp) {
    const prev = unit(sub(a.camUp, mul(f, dot(a.camUp, f))));
    const turn = Math.atan2(dot(cross(prev, want), f), dot(prev, want));
    if (Math.abs(turn) > 10 * D) u = rot(qaxis(f, Math.sign(turn) * 10 * D), prev);
  }
  a.camUp = u;
  return camera(f, u);
}
// A direction on the scope, from its centre: the angle off the middle, round the way it lies.
function onScope(cam, d, radius) {
  const x = dot(d, cam.r), y = dot(d, cam.u), z = dot(d, cam.f);
  const off = Math.acos(clamp(z / Math.max(1e-9, Math.hypot(x, y, z)), -1, 1)) / D;
  const round = Math.atan2(y, x), r = radius * squash(off);
  return [r * Math.cos(round), -r * Math.sin(round), off];
}

// The turning exercises and free flight, on the scope. "dot" view: the dot stays in the middle and the reticle (your nose)
// moves; "nose" view: the nose stays in the middle and the dot moves, as in the cockpit. Either way the picture turns with
// the ship's roll, so pulling the stick back always moves the nose up the screen.
function drawScope(g, w, h, a, view, accent, paused) {
  const s = a.s, ex = a.ex, cx = w / 2, cy = h / 2, R = Math.max(10, Math.min(w, h) / 2 - 18);
  const cam = view === 'nose' ? camera(nose(s), up(s)) : dotCamera(a);
  g.fillStyle = Flight.look('--bg', '#05070b');   // the look's ground (see Flight.look); Velvet Ice keeps the scope's own
  g.fillRect(0, 0, w, h);
  // stars, which turn with the ship
  for (const star of STARS) {
    const [x, y, off] = onScope(cam, star.d, R);
    if (off > 150) continue;
    g.fillStyle = `rgba(200,215,235,${0.15 + star.b * 0.5})`;
    g.fillRect(cx + x, cy + y, star.b > 0.85 ? 2 : 1.2, star.b > 0.85 ? 2 : 1.2);
  }
  // the scale, in the look's quieter text shade
  const muted = Flight.look('--muted', null);
  g.strokeStyle = muted ? hexAlpha(muted, 0.22) : 'rgba(120,150,190,.22)';
  g.fillStyle = muted ? hexAlpha(muted, 0.55) : 'rgba(140,165,200,.55)';
  g.font = '11px "Segoe UI",sans-serif';
  g.lineWidth = 1;
  for (const deg of [5, 20, 60, 180]) {
    g.beginPath();
    g.arc(cx, cy, R * squash(deg), 0, 7);
    g.stroke();
    if (deg < 180) g.fillText(deg + '°', cx + R * squash(deg) * 0.71 + 3, cy - R * squash(deg) * 0.71 - 3);
  }
  g.beginPath();
  g.moveTo(cx - R, cy); g.lineTo(cx + R, cy); g.moveTo(cx, cy - R); g.lineTo(cx, cy + R);
  g.stroke();

  const dotPx = R * squash(ex.dot || 3);
  const targetAt = onScope(cam, a.target, R), noseAt = onScope(cam, nose(s), R);
  const off = angle(nose(s), a.target) / D;
  const onIt = off <= (ex.dot || 3);
  // the target: a large dot
  g.beginPath();
  g.arc(cx + targetAt[0], cy + targetAt[1], dotPx, 0, 7);
  g.fillStyle = onIt ? 'rgba(61,220,132,.55)' : 'rgba(255,74,61,.55)';
  g.fill();
  g.lineWidth = 2;
  g.strokeStyle = onIt ? '#3ddc84' : '#ff4a3d';
  g.stroke();

  // momentum: the next second and a half if the stick is let go now. "dot" view: where the nose goes; "nose" view: where
  // the dot will seem to be from the cockpit.
  g.setLineDash([4, 5]);
  g.strokeStyle = 'rgba(255,255,255,.55)';
  g.lineWidth = 1.5;
  g.beginPath();
  let last = null;
  for (let k = 0; k <= 12; k++) {
    const q = frameIn(s, k * 0.125);
    const at = view === 'nose' ? onScope(camera(rot(q, NOSE), rot(q, UP)), a.target, R) : onScope(cam, rot(q, NOSE), R);
    const x = cx + at[0], y = cy + at[1];
    if (k === 0) g.moveTo(x, y); else g.lineTo(x, y);
    last = [x, y];
  }
  g.stroke();
  g.setLineDash([]);
  if (last) { g.beginPath(); g.arc(last[0], last[1], 9, 0, 7); g.strokeStyle = 'rgba(255,255,255,.75)'; g.stroke(); }

  // the reticle: the nose, with a tick toward the ship's top
  const nx = cx + noseAt[0], ny = cy + noseAt[1];
  const topAt = onScope(cam, unit(add(nose(s), mul(up(s), Math.tan(4 * D)))), R);
  const tick = unit([topAt[0] - noseAt[0], topAt[1] - noseAt[1], 0]);
  g.strokeStyle = accent;
  g.lineWidth = 2.5;
  g.beginPath();
  g.arc(nx, ny, 16, 0, 7);
  for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) { g.moveTo(nx + dx * 16, ny + dy * 16); g.lineTo(nx + dx * 26, ny + dy * 26); }
  g.stroke();
  g.beginPath();
  g.moveTo(nx + tick[0] * 16, ny + tick[1] * 16);
  g.lineTo(nx + tick[0] * 34, ny + tick[1] * 34);
  g.lineWidth = 4;
  g.stroke();
  g.fillStyle = accent;
  g.beginPath();
  g.arc(nx, ny, 2.5, 0, 7);
  g.fill();

  // readouts: how far off, and how fast each axis turns against its top rate
  g.fillStyle = Flight.look('--text', '#e2e8f0');
  g.font = '600 15px "Bahnschrift","Segoe UI",sans-serif';
  g.fillText('NOSE ' + off.toFixed(1) + '° OFF', 14, 24);
  rateBars(g, w - 150, 16, a);
  if (paused) cover(g, w, h, paused);
}

function rateBars(g, x, y, a) {
  const s = a.s, settle = a.ex.settle;
  g.font = '11px "Segoe UI",sans-serif';
  [['PITCH', 'pitch'], ['YAW', 'yaw'], ['ROLL', 'roll']].forEach(([label, axis], i) => {
    const top = s.rates[axis] / D, w = s.w[axis] / D, by = y + i * 34;
    g.fillStyle = Flight.look('--muted', '#8d99aa');
    g.fillText(label + '  ' + w.toFixed(1) + '°/s', x, by + 10);
    g.fillStyle = 'rgba(255,255,255,.08)';
    g.fillRect(x, by + 15, 130, 8);
    if (settle) {
      const band = Math.min(1, (axis === 'roll' ? 2 * settle : settle) / top) * 65;
      g.fillStyle = 'rgba(61,220,132,.3)';
      g.fillRect(x + 65 - band, by + 15, band * 2, 8);
    }
    g.fillStyle = Math.abs(w) <= (settle ? (axis === 'roll' ? 2 * settle : settle) : 0) ? '#3ddc84' : '#ffb020';
    const share = clamp(w / top, -1, 1) * 65;
    g.fillRect(x + 65 + Math.min(0, share), by + 15, Math.abs(share) || 1.5, 8);
  });
}

// The tunnel, seen from the cockpit: its rings ahead, the nose in the middle, and the green marker where the ship
// really goes (its velocity), which with Flight Assist off is seldom where the nose points.
function drawTunnel(g, w, h, a, accent, paused) {
  const s = a.s, ex = a.ex, path = a.path, cx = w / 2, cy = h / 2;
  const cam = camera(nose(s), up(s)), F = (h / 2) / Math.tan(35 * D);
  const project = p => {
    const rel = sub(p, s.p), z = dot(rel, cam.f);
    return z < 2 ? null : [cx + F * dot(rel, cam.r) / z, cy - F * dot(rel, cam.u) / z, z];
  };
  g.fillStyle = Flight.look('--bg', '#05070b');
  g.fillRect(0, 0, w, h);
  for (const star of STARS) {
    const z = dot(star.d, cam.f);
    if (z < 0.2) continue;
    g.fillStyle = `rgba(200,215,235,${0.1 + star.b * 0.35})`;
    g.fillRect(cx + F * dot(star.d, cam.r) / z, cy - F * dot(star.d, cam.u) / z, 1.3, 1.3);
  }
  const pts = path.points, from = Math.max(0, a.at - 4), to = Math.min(pts.length - 1, a.at + 480);
  const sides = 20, around = Array.from({ length: sides + 1 }, (_, k) => k * 2 * Math.PI / sides);
  let previous = null;
  for (let i = from - (from % 10); i <= to; i += 10) {
    if (i < 0) continue;
    const pt = pts[i], side = cross(pt.t, pt.u);
    const ring = around.map(t => project(add(pt.p, add(mul(pt.u, Math.cos(t) * ex.radius), mul(side, Math.sin(t) * ex.radius)))));
    const far = clamp(1 - (i - a.at) * path.stepLength / 2500, 0.08, 1);
    const end = i >= pts.length - 12;
    g.strokeStyle = end ? `rgba(61,220,132,${far})` : pt.turning ? `rgba(255,176,32,${far})` : hexAlpha(accent, far * 0.8);
    g.lineWidth = i % 50 === 0 ? 1.6 : 0.8;
    g.beginPath();
    let open = false;
    for (const q of ring) { if (!q) { open = false; continue; } if (open) g.lineTo(q[0], q[1]); else g.moveTo(q[0], q[1]); open = true; }
    g.stroke();
    // lines along the walls, from the ring before
    if (previous) {
      g.strokeStyle = hexAlpha(accent, far * 0.35);
      g.lineWidth = 0.8;
      g.beginPath();
      for (let k = 0; k < sides; k += 4) if (ring[k] && previous[k]) { g.moveTo(previous[k][0], previous[k][1]); g.lineTo(ring[k][0], ring[k][1]); }
      g.stroke();
    }
    previous = ring;
  }
  // a guide: the middle of the tunnel 300 m on
  const guide = project(pts[Math.min(pts.length - 1, a.at + 60)].p);
  if (guide) { g.strokeStyle = 'rgba(255,255,255,.7)'; g.lineWidth = 1.5; g.beginPath(); g.moveTo(guide[0], guide[1] - 7); g.lineTo(guide[0] + 7, guide[1]); g.lineTo(guide[0], guide[1] + 7); g.lineTo(guide[0] - 7, guide[1]); g.closePath(); g.stroke(); }

  // where the ship is going: the velocity marker
  const speed = len(s.v);
  if (speed > 1) {
    const d = s.v, z = dot(d, cam.f), x = dot(d, cam.r), y = dot(d, cam.u);
    let mx, my, behind = z <= 0.05 * speed;
    if (!behind) { mx = cx + F * x / z; my = cy - F * y / z; }
    if (behind || Math.abs(mx - cx) > w / 2 - 20 || Math.abs(my - cy) > h / 2 - 20) {
      const k = Math.min((w / 2 - 24) / Math.max(1e-6, Math.abs(x)), (h / 2 - 24) / Math.max(1e-6, Math.abs(y)));
      mx = cx + x * k; my = cy - y * k; behind = true;
    }
    g.strokeStyle = '#3ddc84';
    g.lineWidth = 2;
    g.beginPath();
    g.arc(mx, my, 9, 0, 7);
    g.moveTo(mx - 9, my); g.lineTo(mx - 18, my); g.moveTo(mx + 9, my); g.lineTo(mx + 18, my); g.moveTo(mx, my - 9); g.lineTo(mx, my - 16);
    g.stroke();
    if (behind) { g.fillStyle = '#3ddc84'; g.font = '11px "Segoe UI",sans-serif'; g.fillText('drift', mx + 12, my + 18); }
  }
  // the nose
  g.strokeStyle = accent;
  g.lineWidth = 2;
  g.beginPath();
  g.moveTo(cx - 22, cy); g.lineTo(cx - 8, cy); g.moveTo(cx + 8, cy); g.lineTo(cx + 22, cy); g.moveTo(cx, cy - 22); g.lineTo(cx, cy - 8); g.moveTo(cx, cy + 8); g.lineTo(cx, cy + 22);
  g.stroke();

  // readouts
  const wall = a.wall || 0;
  if (wall > 0.7) { g.fillStyle = `rgba(255,60,50,${(wall - 0.7) * 0.9})`; g.fillRect(0, 0, w, h); }
  g.fillStyle = Flight.look('--text', '#e2e8f0');
  g.font = '600 15px "Bahnschrift","Segoe UI",sans-serif';
  g.fillText(Math.round(speed) + ' m/s', 14, 24);
  g.font = '12px "Segoe UI",sans-serif';
  g.fillStyle = Flight.look('--muted', '#8d99aa');
  const next = path.turns.find(t => t.at > a.at);
  const done = Math.min(1, a.at / (pts.length - 1));
  g.fillText(pts[a.at].turning ? 'In a bend' : next ? 'Bend in ' + Math.round((next.at - a.at) * path.stepLength) + ' m: ' + Math.round(next.bend) + '°' : 'Straight to the end', 14, 44);
  g.fillText('Wall ' + Math.round(Math.max(0, ex.radius - (a.wall || 0) * ex.radius)) + ' m away', 14, 62);
  g.fillStyle = 'rgba(255,255,255,.1)';
  g.fillRect(14, h - 22, w - 28, 6);
  g.fillStyle = accent;
  g.fillRect(14, h - 22, (w - 28) * done, 6);
  rateBars(g, w - 150, 16, a);
  if (paused) cover(g, w, h, paused);
}

function hexAlpha(hex, alpha) {
  const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex || '');
  return m ? `rgba(${parseInt(m[1], 16)},${parseInt(m[2], 16)},${parseInt(m[3], 16)},${alpha})` : `rgba(255,113,0,${alpha})`;
}

function cover(g, w, h, text) {
  const ground = Flight.look('--bg', null);
  g.fillStyle = ground ? hexAlpha(ground, 0.6) : 'rgba(5,7,11,.6)';
  g.fillRect(0, 0, w, h);
  g.fillStyle = Flight.look('--text', '#e2e8f0');
  g.textAlign = 'center';
  g.font = '600 26px "Bahnschrift","Segoe UI",sans-serif';
  const lines = String(text).split('\n');
  lines.forEach((line, i) => {
    if (i === 1) g.font = '14px "Segoe UI",sans-serif';
    g.fillText(line, w / 2, h / 2 - 10 + i * 26);
  });
  g.textAlign = 'left';
}

function draw(g, w, h, a, view, accent, paused) {
  if (a.ex.kind === 'tunnel') drawTunnel(g, w, h, a, accent, paused);
  else drawScope(g, w, h, a, a.ex.kind === 'track' && view === 'auto' ? 'nose' : view === 'nose' ? 'nose' : 'dot', accent, paused);
}

// ---- what to change, after a set
// From how the attempts went: in the chase, overshoots on an axis ask for a softer middle on that axis, a target never
// caught for more stick (or more at full stick); the tunnel's crashes and the tracking's misses for a different way of flying.
// spec: the ship flown (its top speed says what "fast" is).
// A tip that changes a curve says which (axis, field) and by how much (step, kept within min..max), not to what: the set's
// tips are worked out once, and change() gives the change from the curves as they are when it is shown or taken. Each has
// an id that stays the same for that piece of advice. feel: the +/- buttons are worth a word (only when they change what
// the trainer feels, which the page knows).
const LABELS = { satY: 'Saturation Y', bend: 'Bend' };
function change(tip, curves) {
  const c = tip.field && curves && curves[tip.axis];
  if (!c) return null;
  const from = c[tip.field], to = clamp(from + tip.step, tip.min, tip.max);
  // Only the way the tip says: "less" never raises a value already under its floor (set lower by hand), "more" never lowers.
  if (tip.step > 0 ? to <= from : to >= from) return null;
  return { change: { [tip.field]: to }, button: LABELS[tip.field] + ' ' + from + ' → ' + to };
}
function advise(ex, attempts, curves, spec) {
  const tips = [];
  const n = attempts.length || 1;
  const failed = attempts.filter(t => !t.ok).length;
  const names = { pitch: 'Pitch', yaw: 'Yaw', roll: 'Roll' };
  if (ex.kind === 'chase' && failed) {
    // Never got onto it: a stick not pushed far, or one pushed far whose curve gives too little at the end.
    const missed = attempts.filter(t => t.firstOn == null);
    let raised = null;   // the axis a tip below gives more at full stick: no tip for less on the same report
    if (missed.length > n / 2) {
      if (missed.every(t => Math.max(t.peak.pitch, t.peak.yaw, t.peak.roll) < 0.8)) {
        tips.push({ id: 'push', text: 'You never pushed the stick far. Full stick swings the nose round fastest: push hard to start the swing; when the ring at the end of the dashed line reaches the target (with Nose in the middle: reaches the middle), stop pushing and push the other way as the nose closes in. Letting go does not stop the turn.' });
      } else if (curves.pitch && curves.pitch.satY < 100) {
        raised = 'pitch';
        tips.push({ id: 'reach:pitch', text: 'You pushed hard but never caught it. More at full stick turns the ship faster.', axis: 'pitch', field: 'satY', step: 10, min: 20, max: 100 });
      } else {
        tips.push({ id: 'slow', text: 'This ship turns slowly at full stick. Roll so the target is above the nose, then pitch to it: pitch is the fastest turn.' });
      }
    }
    // Got onto it but kept sliding off: overshoots, each correction carrying the nose past the target, ask for a softer
    // middle. (Pitch and yaw count the nose's passes, every one an overshoot; roll counts its turns, the first being the stop.)
    const caught = attempts.filter(t => t.firstOn != null);
    for (const axis of ['pitch', 'yaw', 'roll']) {
      const overshoots = caught.reduce((sum, t) => sum + Math.max(0, (t.crossings[axis] || 0) - (axis === 'roll' ? 1 : 0)), 0) / Math.max(1, caught.length);
      const c = curves[axis];
      if (!caught.length || overshoots < 3 || !c) continue;
      const id = 'over:' + axis;
      if (c.bend < 70) tips.push({ id, text: `${names[axis]} overshoots ${overshoots.toFixed(1)} times an attempt: each correction goes too far and needs one the other way. A softer middle makes the small ones easier.`, axis, field: 'bend', step: 10, min: 0, max: 100 });
      else if (c.satY > 40 && axis !== raised) tips.push({ id, text: `${names[axis]} still overshoots with a soft middle. Less at full stick makes it gentler.`, axis, field: 'satY', step: -10, min: 40, max: 100 });
      else tips.push({ id, text: `${names[axis]} still overshoots with a soft middle${c.satY <= 40 ? ' and little at full stick' : ''}: make the corrections smaller and slower, and stop each one with a touch the other way.` });
    }
    // Half or more caught (a set of four can split two and two): what to do once on it.
    if (caught.length >= n / 2) tips.push({ id: 'match', feel: true, text: 'Once on it, match its turn: when the reticle moves with the target, hold the stick still and make only small corrections.' });
  }
  if (ex.kind === 'track') {
    const yawHeavy = attempts.filter(t => t.peak.yaw > 0.8).length > n / 2;
    if (failed && yawHeavy) tips.push({ id: 'yaw-slow', text: 'Yaw is slow on most ships (a Cobra yaws 10°/s, pitches 40°/s). Roll so the dot is above the nose, then pitch to it.' });
    if (failed) tips.push({ id: 'match-dot', text: 'Match the dot\'s turn, do not chase it: once the reticle moves with the dot, hold the stick still and make only small corrections.' });
  }
  if (ex.kind === 'tunnel' && failed) {
    tips.push({ id: 'drift', text: 'With Flight Assist off the ship keeps going the way it went. Turn the nose early, then thrust (forward and the side thrusters) until the green velocity marker sits in the tunnel ahead.' });
    const fast = 0.5 * ((spec && spec.speed) || 280);
    if (attempts.some(t => !t.ok && t.why.startsWith('Hit the wall') && t.speedAtEnd > fast)) tips.push({ id: 'fast', text: 'You were fast at the crash. Slow down before a bend (reverse thrust), turn, then speed up on the straight.' });
  }
  return tips;
}

return { EXERCISES, FREE, RANKS, start, update, draw, advise, change, D, len };
})();
