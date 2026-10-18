// STUDIO NAV, 3D MAP: the galaxy's grid, as the game itself lays it out.
//
// The game cuts the galaxy into sectors, boxes 1,280 light years on a side, and every sector into smaller
// cubes (10 ly times two to the "mass code", the letter a to h in a generated name such as "Wredguia WD-K d8-65").
// The 3D map draws exactly those boxes, so this file is the same arithmetic as the app's Galaxy.cs and
// SystemAddress.cs, written again for the page: the page can then name a layer, outline a cube or find what
// the mouse is over at once, without asking the app. If the two ever disagree, the app is right and this is
// the bug; selftest() below holds this file to the values the app's own tests use.
//
// Directions are the galaxy map's: x grows east, y up, z north (towards the core). Everything here is in
// light years in those directions. A renderer that mirrors an axis to draw (WebGL counts the other way round)
// turns its mouse ray back into these directions before it asks walk() what is under the mouse.
//
// The page loads it with <script src="heat-grid.js"></script> and uses HeatGrid.*:
//   HeatGrid.sectorOf(-167.09, 26.72, -55.38)     [38, 32, 18], Wredguia
//   HeatGrid.layerName(HeatGrid.layerOf(27))      "Just above the middle"
//   HeatGrid.walk(eye, ray, { corner, size, n }, (i, j, k) => shown(i, j, k))   the first shown box under the mouse
// ("var" on purpose, as in studio.js: it makes HeatGrid reachable as window.HeatGrid.)
var HeatGrid = (() => {
'use strict';

// ---------------------------------------------------------------------------------------------------------------
// The grid. Sector 0, 0, 0 has its corner here, and the sector count runs east, up and north from it. Sol is in
// sector 39, 32, 18 (Wregoe).
const SECTOR = 1280;
const ORIGIN = Object.freeze([-49985, -40985, -24105]);
// The middle of the galaxy's disc is at about y -21 (the height of Sagittarius A*). Layers 31 and 32 meet a little
// below it, at y -25, so "just below" and "just above the middle" are as near to true as a 1,280 ly slice allows.
const MIDDLE_Y = -21;
const SEAM_Y = -25;
// Cube widths: the letter's place in the alphabet is the mass code, and a cube is 10 ly times two to it.
const MASS = 'abcdefgh';

// Layers have fixed names, never ones relative to the player ("your layer" would be wrong as soon as the view
// moves). EDAstro's sector list has systems in layers 29 to 34 only, so those six are the ones that have names.
const NAMES = { 29: 'Deep below', 30: 'Far below', 31: 'Just below the middle', 32: 'Just above the middle', 33: 'Far above', 34: 'Deep above' };
const FIRST_LAYER = 29, LAST_LAYER = 34;

// The remainder that is never negative: mod(-3, 8) is 5, where JavaScript's own -3 % 8 is -3. A cube's place
// inside its sector is counted from the sector's corner, so it must not go negative west, below or south of 0.
const mod = (a, w) => ((a % w) + w) % w;
const clamp = (v, lo, hi) => v < lo ? lo : v > hi ? hi : v;
// Three numbers from either (x, y, z) or ([x, y, z]): the API sends places as lists, the scene as x, y, z.
const xyz = (x, y, z) => Array.isArray(x) ? [+x[0], +x[1], +x[2]] : [+x, +y, +z];
// How many boxes along each side: one number for a cube of boxes, or three.
const sides = n => Array.isArray(n) ? [n[0] | 0, n[1] | 0, n[2] | 0] : [n | 0, n | 0, n | 0];

// ---------------------------------------------------------------------------------------------------------------
// Sectors and layers.

// The layer (the sector count upwards) that a height is in: layerOf(27) is 32, layerOf(-25) is 32, layerOf(-25.03) is 31.
// The bottom face belongs to the box, the top face to the one above, as in the game.
const layerOf = y => Math.floor((y - ORIGIN[1]) / SECTOR);

// The sector a place is in, as [ix, iy, iz], the way the API sends a sector.
function sectorOf(x, y, z) {
  const p = xyz(x, y, z);
  return p.map((v, a) => Math.floor((v - ORIGIN[a]) / SECTOR));
}

// The edges of a sector in light years: x0..x1, y0..y1, z0..z1 (the low faces inside, the high ones the next
// sector's), with the same as lists: corner (the low x, y, z) and centre. Takes (ix, iy, iz) or ([ix, iy, iz]).
function sectorBounds(ix, iy, iz) {
  const [i, j, k] = xyz(ix, iy, iz);
  const x0 = ORIGIN[0] + SECTOR * i, y0 = ORIGIN[1] + SECTOR * j, z0 = ORIGIN[2] + SECTOR * k;
  return { x0, y0, z0, x1: x0 + SECTOR, y1: y0 + SECTOR, z1: z0 + SECTOR, size: SECTOR,
    corner: [x0, y0, z0], centre: [x0 + SECTOR / 2, y0 + SECTOR / 2, z0 + SECTOR / 2] };
}

// A layer's name in words. Layers outside the six with names are counted from the middle, in the same words as
// Galaxy.LayerName in the app and the side strip (heat-2d.js), so a card and the strip never name one layer twice.
function layerName(iy) {
  return NAMES[iy] || (iy < FIRST_LAYER ? (32 - iy) + ' layers below the middle' : (iy - 31) + ' layers above the middle');
}

// A layer's heights and name: { iy, name, yFrom, yTo }, the same words the app's block answer uses.
function layerBounds(iy) {
  const yFrom = ORIGIN[1] + SECTOR * iy;
  return { iy, name: layerName(iy), yFrom, yTo: yFrom + SECTOR };
}
const LAYERS = Object.freeze([29, 30, 31, 32, 33, 34].map(iy => Object.freeze(layerBounds(iy))));

// ---------------------------------------------------------------------------------------------------------------
// Cubes.

// Which cube of an area a place falls in, as [i, j, k] counted east, up and north from the area's corner.
// corner: the area's low corner ([x, y, z]); left out (or null), the corner of the place's own sector.
// cubeLy: the cube's width (160 left out). n: cubes along a side (or [nx, ny, nz]); left out, as many as fit in a sector.
// A place on or past an outer face counts in the edge cube rather than in none: the area's boxes are made
// with their top faces trimmed by a hair, and a star exactly on the face still belongs to this area's count.
function cubeIndex(x, y, z, corner, cubeLy, n) {
  if (Array.isArray(x)) [x, y, z, corner, cubeLy, n] = [x[0], x[1], x[2], y, z, corner];
  const p = [+x, +y, +z];
  cubeLy = cubeLy || 160;
  const from = corner || sectorBounds(sectorOf(p)).corner;
  const count = sides(n == null ? Math.round(SECTOR / cubeLy) : n);
  return p.map((v, a) => clamp(Math.floor((v - from[a]) / cubeLy), 0, count[a] - 1));
}

// The cube of the game's own grid that holds a place: { x0, y0, z0, size }. Every width the game uses (10 to
// 1,280 ly) divides a sector exactly, so this is also the cube of that width inside the place's sector, which is
// how the app snaps a counted cube ("Count this cube" is the 160 ly cube the click is in).
function cubeAt(x, y, z, cubeLy) {
  const p = xyz(x, y, z), size = cubeLy || 160;
  const [x0, y0, z0] = p.map((v, a) => ORIGIN[a] + Math.floor((v - ORIGIN[a]) / size) * size);
  return { x0, y0, z0, size };
}

// The mass code (0 to 7) from a letter ("d"), a code (3) or a cube width in light years (80). null when it is none.
function massOf(mass) {
  if (typeof mass === 'string') { const m = MASS.indexOf(mass.trim().toLowerCase()); return m < 0 || mass.trim().length !== 1 ? null : m; }
  if (!Number.isInteger(mass)) return null;
  if (mass >= 0 && mass < 8) return mass;
  const m = Math.log2(mass / 10);
  return Number.isInteger(m) && m >= 0 && m < 8 ? m : null;
}

// The game's name for a cube inside a sector, as in "Wredguia WD-K d8": the cube's place in the sector written
// in letters, then the mass letter, then how many times the letters went round. A star's name adds "-" and its
// number ("Wredguia WD-K d8-65"); when the letters never went round the number follows the letter ("AB-C d65").
// i, j, k: the cube counted east, up and north from the sector's corner; mass: a letter, a code or a width.
// sectorName may be left out (null or ""), for the letters alone. null when the cube cannot be in a sector.
// This is CMDR Jackie Silver's reading of the names, as coded in EDTS (BSD-3, Andy Martin and Esvandiary).
function cubeName(sectorName, i, j, k, mass) {
  const m = massOf(mass), across = m == null ? 0 : SECTOR / (10 << m);
  if (m == null || ![i, j, k].every(v => Number.isInteger(v) && v >= 0 && v < across)) return null;
  // The game counts the cubes in rows of 128 whatever their size, x fastest, then y, then z.
  const offset = i + 128 * j + 16384 * k;
  const letter = n => String.fromCharCode(65 + n % 26);
  const round = Math.floor(offset / 17576);   // 26 to the third: once round all the letters
  const letters = letter(offset) + letter(Math.floor(offset / 26)) + '-' + letter(Math.floor(offset / 676)) + ' ' + MASS[m] + (round ? round : '');
  return sectorName ? sectorName + ' ' + letters : letters;
}

// Where the game's own address for a system (its id64, "SystemAddress" in the journal) puts it. The id64 goes
// in as text: it is bigger than a JavaScript number holds exactly, which is why the app sends it as text too.
// Answers { sector: [ix, iy, iz], cube: [i, j, k], mass, letter, cubeLy, halfWidth, number, corner, centre },
// where number is the system's number in its cube (the 65 of "WD-K d8-65"); null for anything that is not one.
// The layout, from the lowest bit: 3 bits of mass code; then for z, y and x in turn the cube's place in its
// sector (7 bits less the mass code) and the sector (7 bits for z and x, 6 for y); then the number.
function place(id64) {
  let rest;
  try { rest = BigInt(typeof id64 === 'string' ? id64.trim() : id64); } catch (e) { return null; }
  if (rest <= 0n) return null;
  const mass = Number(rest & 7n), bits = 7 - mass;
  rest >>= 3n;
  const take = count => {
    const value = Number(rest & ((1n << BigInt(count)) - 1n));
    rest >>= BigInt(count);
    return value;
  };
  const cz = take(bits), sz = take(7), cy = take(bits), sy = take(6), cx = take(bits), sx = take(7);
  const number = take(11 + 3 * mass);   // what is left above it is the body, which says nothing about where
  const cubeLy = 10 << mass;
  const corner = [ORIGIN[0] + sx * SECTOR + cx * cubeLy, ORIGIN[1] + sy * SECTOR + cy * cubeLy, ORIGIN[2] + sz * SECTOR + cz * cubeLy];
  return { sector: [sx, sy, sz], cube: [cx, cy, cz], mass, letter: MASS[mass], cubeLy, halfWidth: cubeLy / 2, number,
    corner, centre: corner.map(v => v + cubeLy / 2) };
}

// ---------------------------------------------------------------------------------------------------------------
// What is under the mouse.

// Walks a ray (the mouse's line of sight) through a grid of equal boxes, nearest box first, and stops at the first
// one visit() says yes to. The boxes a ray passes through are found by stepping from face to face (the method of
// Amanatides and Woo), so it costs a few dozen steps whatever the number of boxes: quick enough for every mouse move.
//   from: where the ray starts, [x, y, z] (the camera's eye); dir: its direction, [dx, dy, dz], any length.
//   grid: { corner: [x, y, z] (the low corner of box 0, 0, 0), size: a box's width, n: boxes per side or [nx, ny, nz] }
//   (n left out: as many as fit in a sector, for the cubes of one sector).
//   visit(i, j, k, t): called for each box the ray goes through, with t the distance in light years from the start
//   to where it goes in (0 when the ray starts inside). Answer true to stop there.
// Answers { i, j, k, t, at: [x, y, z] } for the box visit() stopped at (at: where the ray goes in), or null.
// Without visit, answers the list of every box passed, [{ i, j, k, t }], nearest first ([] when the ray misses).
// The same in one object: walk({ from, dir, corner, size, n, visit }).
function walk(from, dir, grid, visit) {
  if (from && !Array.isArray(from) && typeof from === 'object') {   // the one-object form
    grid = from;
    ({ dir, visit } = grid);
    from = grid.from || grid.origin;
  }
  const list = typeof visit === 'function' ? null : [];
  const miss = list || null;
  // Without a count, the grid is the cubes of one sector.
  const corner = grid.corner, size = grid.size, count = sides(grid.n == null ? Math.round(SECTOR / size) : grid.n);
  const length = Math.hypot(dir[0], dir[1], dir[2]);
  if (!(length > 0) || !(size > 0) || count.some(v => v < 1)) return miss;
  const d = [dir[0] / length, dir[1] / length, dir[2] / length];
  // Everything below is in box widths, with box 0, 0, 0 at 0..1; t is multiplied back into light years for visit().
  const o = [0, 1, 2].map(a => (from[a] - corner[a]) / size);

  // Where the ray is inside the grid's outer box at all: between t0 and t1.
  let t0 = 0, t1 = Infinity;
  for (let a = 0; a < 3; a++) {
    if (Math.abs(d[a]) < 1e-12) {   // running along the faces: in or out for good
      if (o[a] < 0 || o[a] > count[a]) return miss;
      continue;
    }
    let near = -o[a] / d[a], far = (count[a] - o[a]) / d[a];
    if (near > far) [near, far] = [far, near];
    t0 = Math.max(t0, near);
    t1 = Math.min(t1, far);
  }
  if (t0 > t1) return miss;   // passes beside the grid, or the grid is behind the eye

  // The first box: the one the ray goes into at t0. On a face between two boxes that is the one it is heading
  // for, so a ray going west from exactly x = 2 starts in box 1, not in box 2 which it only touches.
  const p = o.map((v, a) => v + d[a] * t0);
  const c = p.map((v, a) => clamp(d[a] < 0 ? Math.ceil(v) - 1 : Math.floor(v), 0, count[a] - 1));
  const step = d.map(v => v > 0 ? 1 : v < 0 ? -1 : 0);
  const across = d.map(v => v ? Math.abs(1 / v) : Infinity);   // how far along the ray one box width is, per axis
  // How far along the ray the next face is, per axis.
  const next = c.map((v, a) => step[a] > 0 ? t0 + (v + 1 - p[a]) / d[a] : step[a] < 0 ? t0 + (v - p[a]) / d[a] : Infinity);

  let t = t0;
  for (let steps = count[0] + count[1] + count[2]; steps >= 0; steps--) {
    if (list) list.push({ i: c[0], j: c[1], k: c[2], t: t * size });
    else if (visit(c[0], c[1], c[2], t * size))
      return { i: c[0], j: c[1], k: c[2], t: t * size, at: [0, 1, 2].map(a => from[a] + d[a] * t * size) };
    const a = next[0] < next[1] ? (next[0] < next[2] ? 0 : 2) : (next[1] < next[2] ? 1 : 2);
    if (next[a] >= t1) break;   // that face is the grid's outer one: the ray leaves
    t = next[a];
    c[a] += step[a];
    if (c[a] < 0 || c[a] >= count[a]) break;
    next[a] += across[a];
  }
  return miss;
}

// Where a ray goes into and comes out of one box (a counted cube, which need not sit on any grid of the scene):
// { near, far } in light years from the start (near is 0 when the ray starts inside), or null when it misses.
// min and max are the box's low and high corners, [x, y, z].
function rayBox(from, dir, min, max) {
  const length = Math.hypot(dir[0], dir[1], dir[2]);
  if (!(length > 0)) return null;
  let near = -Infinity, far = Infinity;
  for (let a = 0; a < 3; a++) {
    const d = dir[a] / length;
    if (Math.abs(d) < 1e-12) {
      if (from[a] < min[a] || from[a] > max[a]) return null;
      continue;
    }
    let t0 = (min[a] - from[a]) / d, t1 = (max[a] - from[a]) / d;
    if (t0 > t1) [t0, t1] = [t1, t0];
    near = Math.max(near, t0);
    far = Math.min(far, t1);
  }
  if (near > far || far < 0) return null;
  return { near: Math.max(0, near), far };
}

// ---------------------------------------------------------------------------------------------------------------
// The check, for heatmap.html?selftest: the values the app's own tests hold Galaxy.cs and SystemAddress.cs to,
// plus walks along rays whose boxes can be worked out by hand. Answers [{ name, ok, got, want }].
function selftest() {
  const results = [];
  const same = (got, want) => {
    if (typeof want === 'number') return typeof got === 'number' && Math.abs(got - want) < 1e-6;
    if (Array.isArray(want)) return Array.isArray(got) && got.length === want.length && want.every((w, n) => same(got[n], w));
    if (want && typeof want === 'object') return !!got && typeof got === 'object' && Object.keys(want).every(key => same(got[key], want[key]));
    return got === want;
  };
  const check = (name, test, want) => {
    let got;
    try { got = test(); } catch (e) { got = 'error: ' + e.message; }
    results.push({ name, ok: same(got, want), got, want });
  };
  const pick = (o, keys) => o && Object.fromEntries(keys.map(key => [key, o[key]]));

  // Where the player was when the grid was checked against the game, and the sectors around.
  check('You (-167.09, 26.72, -55.38) are in sector 38, 32, 18 (Wredguia)', () => sectorOf(-167.09, 26.72, -55.38), [38, 32, 18]);
  check('Sol is in sector 39, 32, 18 (Wregoe)', () => sectorOf([0, 0, 0]), [39, 32, 18]);
  check('Wredguia runs x -1345..-65, y -25..1255, z -1065..215', () => pick(sectorBounds(38, 32, 18), ['x0', 'x1', 'y0', 'y1', 'z0', 'z1']),
    { x0: -1345, x1: -65, y0: -25, y1: 1255, z0: -1065, z1: 215 });
  check('Owl Nebula is in Sphuedu (38, 33, 18)', () => sectorOf(-624.4, 1847.2, -1018.9), [38, 33, 18]);
  check('Skull Nebula is in Whuedaea (38, 30, 18)', () => sectorOf(-370.2, -1537.9, -181.7), [38, 30, 18]);
  check('Height 27 is in layer 32', () => layerOf(27), 32);
  check('Height -25 is in layer 32 (the bottom face belongs to the box)', () => layerOf(-25), 32);
  check('Height -25.03 is in layer 31', () => layerOf(-25.03), 31);
  check('Layers 31 and 32 meet at y -25', () => [layerBounds(31).yTo, layerBounds(32).yFrom, SEAM_Y], [-25, -25, -25]);
  check('The six layer names', () => [29, 30, 31, 32, 33, 34].map(layerName),
    ['Deep below', 'Far below', 'Just below the middle', 'Just above the middle', 'Far above', 'Deep above']);
  check('Layers past the six are counted from the middle, as Galaxy.LayerName does', () => [28, 35].map(layerName),
    ['4 layers below the middle', '4 layers above the middle']);
  check('mod(-3, 8) is 5, never negative', () => [mod(-3, 8), mod(11, 8), mod(-16, 8)], [5, 3, 0]);

  // Cube names and places, from the names and addresses the app's tests use.
  check('Cube 12, 0, 9 of 80 ly in Wredguia is "Wredguia WD-K d8"', () => cubeName('Wredguia', 12, 0, 9, 'd'), 'Wredguia WD-K d8');
  check('A cube whose letters never went round has no number before its star', () => cubeName(null, 0, 0, 0, 2), 'AA-A c');
  check('A cube outside its sector has no name', () => cubeName('Wredguia', 16, 0, 0, 'd'), null);
  check('"Wredguia WD-K d8-65" (-345, 15, -305) is in cube 12, 0, 9 of 80 ly', () => cubeIndex(-345, 15, -305, sectorBounds(38, 32, 18).corner, 80), [12, 0, 9]);
  check('Its sector found by itself', () => cubeIndex([-345, 15, -305], null, 80), [12, 0, 9]);
  check('A place on the far face counts in the edge cube', () => cubeIndex(-65, 1255, 215, [-1345, -25, -1065], 160, 8), [7, 7, 7]);
  check('The 160 ly cube you are in', () => cubeAt(-167.09, 26.72, -55.38, 160), { x0: -225, y0: -25, z0: -105, size: 160 });
  check('Sol 10477373803: sector 39, 32, 18, mass d', () => pick(place('10477373803'), ['sector', 'letter', 'cubeLy']), { sector: [39, 32, 18], letter: 'd', cubeLy: 80 });
  check('Suhte 5306264359626: mass c, centre -165, 35, -45', () => pick(place('5306264359626'), ['letter', 'centre', 'halfWidth']), { letter: 'c', centre: [-165, 35, -45], halfWidth: 20 });
  check('Maia 8216113749: mass f, centre -225, -185, -265', () => pick(place('8216113749'), ['letter', 'centre']), { letter: 'f', centre: [-225, -185, -265] });
  check('Text that is no address has no place', () => [place('Sol'), place('0'), place('')], [null, null, null]);
  // Generated names against the game's own addresses (stars the research checked one by one): the address must
  // land in the cube the name gives, and spelling that cube out must give the name back, number and all.
  const named = [['Synuefai YQ-A c15-1', '357589029506', [-685, -685, -405]], ['Synuefai XK-N d7-8', '285204154691', [-745, -625, -385]],
    ['Synuefai OL-D b29-0', '661557093625', [-695, -655, -435]], ['Sphuedu FH-L d8-0', '10326739275', [-745, 1775, -305]],
    ['Sphuedu KN-J d9-0', '10343516499', [-665, 1775, -225]]];
  for (const [name, id64, centre] of named) check('"' + name + '" (' + id64 + ') is the cube at ' + centre.join(', '), () => {
    const p = place(id64);
    return p && { centre: p.centre, name: cubeName(name.split(' ')[0], p.cube[0], p.cube[1], p.cube[2], p.mass) + '-' + p.number };
  }, { centre, name });
  // And the cube a decoded name stands for, from its centre: "Synuefai YQ-A c15-1" is (-685, -685, -405) give or take 20.
  check('The 40 ly cube at (-685, -685, -405) is "Synuefai YQ-A c15"', () => {
    const s = sectorOf(-685, -685, -405);
    return [s, cubeName('Synuefai', ...cubeIndex(-685, -685, -405, sectorBounds(s).corner, 40), 'c')];
  }, [[38, 31, 18], 'Synuefai YQ-A c15']);

  // Walks. The sectors around the player: 3 x 6 x 3 of them, columns 37..39 and 17..19, layers 29..34.
  const block = { corner: sectorBounds(37, 29, 17).corner, size: SECTOR, n: [3, 6, 3] };
  check('Straight down through your column: all six layers, top first',
    () => walk([-705, 5000, -425], [0, -1, 0], block).map(b => [b.i, b.j, b.k]), [[1, 5, 1], [1, 4, 1], [1, 3, 1], [1, 2, 1], [1, 1, 1], [1, 0, 1]]);
  check('It goes in at the top of layer 34, 1,185 ly down', () => walk([-705, 5000, -425], [0, -1, 0], block)[0].t, 1185);
  check('Stopping at the first sector in layer 32', () => pick(walk([-705, 5000, -425], [0, -2, 0], block, (i, j) => j === 3), ['i', 'j', 'k', 't', 'at']),
    { i: 1, j: 3, k: 1, t: 5000 - 1255, at: [-705, 1255, -425] });
  check('East from you: Wredguia, then Wregoe, then out', () => walk([-167.09, 26.72, -55.38], [1, 0, 0], block).map(b => [b.i, b.j, b.k, Math.round(b.t * 100) / 100]),
    [[1, 3, 1, 0], [2, 3, 1, 102.09]]);
  check('West from exactly the face at x -65 starts in Wredguia', () => walk([-65, 100, 0], [-1, 0, 0], block).map(b => b.i), [1, 0]);
  check('A ray that passes beside the sectors finds none', () => walk([5000, 100, 0], [0, 0, 1], block), []);
  check('Sectors behind the eye are not found', () => walk([-705, 5000, -425], [0, 1, 0], block, () => true), null);
  check('The same walk in one object', () => pick(walk({ from: [-705, 5000, -425], dir: [0, -1, 0], corner: block.corner, size: SECTOR, n: [3, 6, 3], visit: () => true }), ['i', 'j', 'k']),
    { i: 1, j: 5, k: 1 });
  // Slanted rays through the 160 ly cubes of Wredguia, held against small steps along the same ray: the walk must
  // pass every cube the steps land in, in the same order. (Where a ray crosses exactly on an edge or a corner it
  // also touches a cube along a line too thin for the steps to land in, so the walk may name one more.)
  const area = { corner: [-1345, -25, -1065], size: 160, n: 8 };
  const rays = [[[-2000, 1600, -2100], [1, -0.6, 0.9]], [[0, -300, 800], [-1, 0.35, -0.8]], [[-700, 2000, -400], [0.02, -1, 0.03]], [[-1345, 300, -1065], [1, 0, 1]]];
  rays.forEach(([from, dir], n) => check('Slanted ray ' + (n + 1) + ' passes the same cubes as small steps along it', () => {
    const walked = walk(from, dir, area).map(b => b.i + ',' + b.j + ',' + b.k);
    const length = Math.hypot(...dir), stepped = [];
    for (let t = 0; t < 6000; t += 0.25) {
      const p = from.map((v, a) => v + dir[a] / length * t);
      if (p.some((v, a) => v < area.corner[a] || v >= area.corner[a] + 1280)) continue;
      const key = cubeIndex(p, area.corner, 160, 8).join();
      if (stepped[stepped.length - 1] !== key) stepped.push(key);
    }
    let at = 0;   // the steps' cubes must turn up in the walk in the same order
    for (const key of walked) if (key === stepped[at]) at++;
    return stepped.length > 0 && at === stepped.length && walked.length <= stepped.length + 8;
  }, true));
  check('A ray through a cube: near and far', () => rayBox([0, 0, -500], [0, 0, 1], [-80, -80, -80], [80, 80, 80]), { near: 420, far: 580 });
  check('A ray from inside a cube starts at 0', () => rayBox([0, 0, 0], [1, 1, 0], [-80, -80, -80], [80, 80, 80]).near, 0);
  check('A ray past a cube misses', () => rayBox([0, 200, -500], [0, 0, 1], [-80, -80, -80], [80, 80, 80]), null);
  return results;
}

return { SECTOR, ORIGIN, MIDDLE_Y, SEAM_Y, LAYERS, mod, layerOf, sectorOf, sectorBounds, layerName, layerBounds,
  cubeIndex, cubeAt, cubeName, massOf, place, walk, rayBox, selftest };
})();
