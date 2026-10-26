// ELITE STUDIO NAV, 3D MAP: the drawing of the known-systems heat map, with the browser's own 3D (WebGL2).
//
// heatmap.html hands this file a "scene": the sectors of the block, the cubes of one opened sector or nebula,
// the cubes counted one by one, star dots, nebulae, the commander and a marker. Every colour value in it is
// already worked out by the page (0..1 along the colour ramp, -1 = nothing known, -2 = too few to judge), so
// this file only draws, moves the camera and says what is under the pointer. It never talks to the app or to
// any outside service, and it needs no other script: window.HeatGrid, when there, supplies the grid's numbers
// (also written out below) and the game's own cube of a chosen star dot (without it, that cube is not outlined).
//
//   const map = HeatGL.create(canvas, overlayCanvas, { view });
//   map.setScene(scene);                      // see heatmap.html: { origin, sectors, area, counted, stars, nebulae, player, marker }
//   map.setRamp(HeatColour.ramp(stops));      // 256 colours, RGBA bytes, cold to hot
//   map.setView({ opacity: .5 });             // { opacity, ghostOpacity, look, standOut, showEmpty, hideBelow, hideAbove, holdHeight, markers, nebulae, cubeLevel }
//   map.onPick(hit => ...); map.onHover((hit, at) => ...); map.flyTo({ x, y, z, r });
//   HeatGL.supported() says first whether 3D is worth trying at all; map.measure() then says how fast it really is
//   (it waits while the page is hidden, because nothing is drawn then). create() throws when there is no 3D after all.
//   Besides the calls above, the map has the camera's buttons and memory: overview(), me(), back(), forward(),
//   canGo(), camera(), setCamera(c, instant), onCamera(cb), and select(hit), project(x, y, z), measure().
//
// The free map camera (spec v2, 1 to 3). The camera always looks at a focus point (drawn as a faint cross) and
// circles it: left-drag turns all the way round, and up or down to just short of straight up or down. W/S go
// forward and back the way the camera looks ("Hold height" keeps that level), A/D left and right, R/F and E/Q up
// and down in the galaxy's own up; Shift is twice as fast. In the Top view W/S go north and south (up the screen)
// and R/F step the layers (onLayerStep); in the Side view W/S go north and south along the line of sight, bringing
// the next row of sectors to the focus. Keys belong to the map unless a text box has them (tick boxes and buttons do
// not keep them). The arrow keys are not the camera's (spec v3): the page moves the highlight with them (axisFor,
// stepFrom below), and turning is the mouse's and the pad's turn buttons'.
// Right- or middle-drag slides the focus across the screen (in the flat views any drag does), the wheel zooms
// towards the pointer. A click on a sector, cube, star dot, nebula or find chooses it (onPick) and flies the focus
// there in about 0.6 s, keeping the angle; a double-click flies closer; a click on empty space does nothing (no
// onPick call, the choice stays, a flight under way goes on). Back and Forward walk the camera's history, the view
// mode included. In the flat views a click takes, of the sectors along its line of sight, the one at the focus:
// the one whose name is written there.
//   map.setMode('3d' | 'top' | 'side')        the view: 3D, Top (2D: from above, north up, east right) or Side
//                                             (2D: from the south looking north, up up, east right). The flat
//                                             views draw with no perspective, cannot turn, and pick exactly like 3D.
//                                             Returns the mode now; map.mode() reads it, map.onMode(cb) hears changes
//                                             (also from Back/Forward), and camera() / onCamera / setCamera carry it
//                                             as c.mode. create(..., { mode }) starts in one.
//   map.setMarkers({ you, target, routeEnd, finds })   each place { x, y, z, name, plusMinus? } or null; you may
//                                             carry system and heading (without it, the scene's player is you);
//                                             finds is a list. A name decoded from its letters has plusMinus (ly):
//                                             its label says so and its cube of doubt is outlined. Markers not
//                                             passed keep what they were. The page's "Markers" switch is
//                                             setView({ markers: false }): no You, Target, Route end or finds then.
//   map.setMarkers({ route })                 the route check's stops (spec v4), in route order, the starting system
//                                             left out: each { x, y, z, name, state } with state 'waiting' |
//                                             'checking' | 'recorded' | 'not-recorded' | 'failed'. Drawn as small dots
//                                             in their state's colour (map.stopColour(state)) joined by a faint line,
//                                             only while the markers are shown; a stop with no place is left out.
//                                             A stop not recorded also gets a gold ring, drawn over its find's diamond.
//                                             pick() answers { kind: 'stop', index, item, x, y, z } over a dot.
//   map.setMarkers({ reach })                 "Within reach" (spec v5 C): { x, y, z, r, label?, systems } or null. A
//                                             see-through sphere of radius r (ly) round the place (a faint disc as
//                                             seen, ringed, with its three circles in 3D) and a dot for each system
//                                             { x, y, z, name, state, must }: state 'recorded' | 'name-only' |
//                                             'not-recorded' (map.reachColour(state, must) gives its colour), must
//                                             (a system that must exist): a hollow ring. Under the names and the other
//                                             marks, only while the markers are shown. pick() answers { kind: 'reach',
//                                             index, item, x, y, z } over a dot.
//   map.setNebulaMode('all' | 'near' | 'off') the nebulae drawn: all, those within 1,500 ly of the focus (the
//                                             start), or none. Names only for the 12 nearest the focus on screen.
//                                             The same as setView({ nebulae }).
//   map.focusOn(target, closer)               flies the focus to what flyTo() accepts (also 'target', 'routeEnd'),
//                                             keeping the angle, at a distance that frames it (closer: nearer).
//   map.nudge({ turn, move, zoom, level, fast })   one step for the on-screen pad: turn 'left'|'right'|'up'|'down'
//                                             or [x, y] (10 degrees each, the way the view turns; nothing in the flat
//                                             views); move 'forward'|'back'|'left'|'right'|'up'|'down' or
//                                             [right, up, forward] (a fifth of the distance to the focus each; in the
//                                             Top view forward is up the screen and up/down step the layers, in the
//                                             Side view forward is north); zoom +1 nearer, -1 farther;
//                                             level: true eases back to 3D at a 35 degree look down.
//   map.hold(name, on)                        a pad button held down (call it from the button's pointerdown): one
//                                             step at once, then after 0.3 s on and on until hold(name, false) or the
//                                             pointer is let go anywhere. From a click or a key it is the one step.
//                                             Names: turnLeft, turnRight, turnUp, turnDown, forward, back, left,
//                                             right, up, down, zoomIn, zoomOut. hold(null) lets go of them all.
//                                             (Up and Down in the Top view: one layer a press, no more.)
//   map.onLayerStep(cb)                       cb(+1 | -1): the layer above or below, asked for in the Top view.
// A nebula in the scene may carry spheres: [[x, y, z, r], ...] or [{ x, y, z, r }, ...]; it is drawn and picked as
// those spheres. pick() also answers { kind: 'find', index, item, x, y, z } over a find's diamond.
//
// Reaching the cubes inside, and the highlight (spec v3, 1):
// - Clicking the same spot again (within 8 px, later than a double-click's 0.4 s, the camera not moved in between by
//   anything but that click's own flight, and nothing else chosen meanwhile) chooses the NEXT cube or sector farther
//   along the same line of sight, the line as it was at the first click; after the last one it comes round to the
//   first. So a cube deep inside a block is a few clicks away without aiming. Such a click leaves the camera where the
//   first one's flight took it, so the white outline is seen stepping back behind the cubes in front (a fresh click
//   centres). A change of what can be clicked in between (a level of cubes, "Show empty cubes", the range, new data)
//   works the line out again from the first click's camera, so the next click never reaches something no longer drawn.
//   Every click's choice carries along: { at, of } (where it is in that order, 0 = what one click chooses) and t (how
//   far along the line, in ly).
// - A nebula's dashed sphere gives way to the cube grid the player works in: a click near the edge of one whose line
//   passes through a cube of the opened area (or a counted cube) takes that cube, and the nebula comes last on the
//   line. Its middle still comes first, except while one level of cubes is shown: then only that level is clicked.
//   map.hitsAt(x, y)                          what a click at that screen point goes through, in that order: first
//                                             what one click chooses (as pick()), then the cubes and sectors farther
//                                             along the line, nearest first, then round to those in front of the first
//                                             (after a star dot, find or nebula under the pointer: what a click would
//                                             choose without it, and on from there). Each is a pick result with along
//                                             and t. [] over empty space.
// - The highlight is what is chosen. Its edges are drawn over everything, on the names' canvas, so no cube in front
//   hides it, and its faces get a little light added, so the player can always see where it is.
//   map.setHighlight(hit)                     chooses without flying and without onPick: a pick result, { kind: 'cube',
//                                             i, j, k } (a cube of the opened area), { kind: 'sector', ix, iy, iz }, or
//                                             anything with a box { x0, y0, z0, size }; null chooses nothing. Gives where
//                                             its middle is on the screen, { x, y }, or null. select(hit) is the same
//                                             without the answer; map.highlight() reads it back, filled in (its box, its
//                                             middle). Moving it never moves the camera: focusOn(map.highlight()) does.
//   map.axisFor(dir)                          which way an arrow key moves the highlight with the camera as it is now,
//                                             dir 'left' | 'right' | 'up' | 'down' | 'higher' | 'lower': { step: [dx, dy,
//                                             dz] (one of the galaxy's six directions, x east, y up, z north), axis 0 | 1 |
//                                             2, sign, word ('east', 'north', 'up', ...) }. In 3D left and right go along
//                                             whichever of east-west and north-south lies nearest the screen's left-right,
//                                             up and down along the other, away from and towards the viewer. Top: up is
//                                             north, right east. Side: up is up, right east. Higher and lower (Page Up and
//                                             Page Down): up and down in every view.
//   map.stepFrom(hit, dir)                    the box next to hit that way (dir as axisFor, or [dx, dy, dz]), one of its
//                                             own size along: { step, word, box: { x0, y0, z0, size }, sector: { ix, iy,
//                                             iz } (the one the box is in), crosses (into another sector than hit's),
//                                             inBlock (that sector is one of the scene's), inArea (a cube of the opened
//                                             area; i, j, k then), hit (what the map has there, ready for setHighlight: the
//                                             area's cube, a counted cube, or for a sector-sized step the sector; null when
//                                             the map has nothing there) }, or null when hit has no box. Whether to go on
//                                             into the next sector's cubes is the page's to decide: it knows which
//                                             sectors have cubes (detailed map, survey, counted cubes).
//   setView({ cubeLevel: j })                 only level j (0 = the bottom; the j of the area's cubes) of the opened
//                                             area's cubes is drawn and can be clicked, with the counted cubes and star
//                                             dots inside the area at that level; the area's other levels are not there
//                                             at all, so its inner cubes can be clicked. null, -1 or a level the area
//                                             does not have: every level. stats().level says which is in force.
//   map.ownsKey(event)                        whether a key press is free for the map and its page: not typed into a box
//                                             or a dialog, no Ctrl, Alt or Windows key, and (the arrows, Page Up, Page
//                                             Down) not kept by a slider, radio buttons or a part of the page marked
//                                             data-keeps-arrows. The page takes the arrows with it. data-keeps-arrows
//                                             with key codes ("ArrowUp ArrowDown") keeps only those, and only while that
//                                             part itself has the keyboard: a button inside it keeps none.
//   map.whereIs(x, y, z)                      where a place is on the screen, also off it: { x, y, behind } (x, y
//                                             beyond the edges when it is off them; behind the eye, the side of the
//                                             screen it is nearest). For pointing at a highlight that is off the map.
// For HeatGL.selftest() only: create(canvas, null, { headless: [width, height], clock: () => ms }) makes a map that
// never touches the graphics card and never draws, so the checks run the real picking, stepping and filtering on a
// canvas nobody sees; clock stands in for the time between clicks.
//
// Why it is drawn the way it is:
// - Only when something changed: the pointer, a key, a camera flight, new data or new settings. The app's shell
//   keeps pages it has hidden alive, and Elite needs the graphics card, so a page nobody looks at draws nothing:
//   no frame while the canvas has no size or the page is hidden.
// - No edge smoothing (antialias off) and at most 1.5 canvas pixels per screen pixel. On a 4K screen with Windows
//   scaling the page would otherwise draw over three times the pixels, and edge smoothing there was measured at
//   up to 200 ms a frame instead of 8.
// - See-through cubes only look right when drawn from the farthest to the nearest. For cubes of one size on one
//   grid, ordering by the distance from the eye to each cube's middle is exact, so the sectors are ordered among
//   themselves and the cubes of the one opened area among themselves (a two-pass radix sort, about 2 ms for 30,000),
//   and only when the eye has moved. Shapes that are not grid cubes get their own passes: solid cubes first (they
//   fill the depth buffer), then the see-through boxes, then lines, then star dots, then the light on the highlight,
//   then the names on a flat canvas laid over the 3D one, and the highlight's edges on that canvas over the names.
//   The graphics card draws its lines one pixel wide whatever it is asked, which is too thin to find a chosen cube
//   by among hundreds of outlines: on the flat canvas they are drawn thick, with a dark edge, over everything.
// - The game's axes (x east, y up, z north towards the core) are mirror images of what WebGL expects, so the camera
//   works in the game's own numbers with a mirrored basis: seen from above, north is up and east is right, exactly
//   like the flat map, with Colonia up and to the left of Sol. HeatGL.selftest() checks that, among other things.
// - The two flat views use the same drawing with the perspective taken out (an orthographic camera). Their eye sits
//   far back along the line of sight, so nothing above the focus is cut off, and they show the focus at the same
//   size as the 3D view did, so switching keeps the scale. Clicks walk straight lines through the same grids.
// - The You mark pulses softly. Only the small square of the names' canvas round it is touched for that, about 14
//   times a second, and only while the mark is on screen: never while the page is hidden or the map scrolled out of
//   view; the 3D drawing is not redrawn for it. Without motion (the system's "reduce motion" setting) it does not
//   pulse at all.
// - The graphics card can take the drawing away (alt-tab out of a full-screen game, a driver reset). Everything
//   drawn is kept in plain JavaScript arrays, so when the browser gives the card back the page rebuilds and goes on.
//
// ("var" on purpose, as in studio.js: it makes HeatGL reachable as window.HeatGL.)
var HeatGL = (() => {
'use strict';

// The grid, from heat-grid.js when it was loaded first, else the same numbers written out (selftest() compares them).
const GRID = window.HeatGrid || {};
const SECTOR_LY = typeof GRID.SECTOR === 'number' ? GRID.SECTOR : 1280;   // the game's sector: 1,280 ly on every side
const GRID_ORIGIN = Array.isArray(GRID.ORIGIN) && GRID.ORIGIN.length === 3 ? GRID.ORIGIN.slice() : [-49985, -40985, -24105];   // the corner of sector (0,0,0)
const SEAM_Y = typeof GRID.SEAM_Y === 'number' ? GRID.SEAM_Y : -25;   // the floor of the layer we live in, a few ly below the galaxy's middle (about -21)
const FOV = 50 * Math.PI / 180;         // the camera's view angle, top to bottom
const MAX_RATIO = 1.5;                  // canvas pixels per screen pixel, at most
const PICK_PX = 8;                      // how near the pointer has to be to a star dot or a nebula, in screen pixels
const STOP_PX = 6;                      // and to a stop of the route check: nearer, so a route across the map leaves its cubes clickable
const STOP_DOT = 3.6;                   // a stop's dot, in screen pixels (radius)
const STOP_RING = 8;                    // the ring round a stop not recorded, outside the find's diamond that lies on it (radius)
const REACH_DOT = 3;                    // a dot of Within reach, in screen pixels (radius): a little smaller than a stop's, there can be thousands
const REACH_PALE = 0.5;                 // a system recorded by its name only: its green this faint
const FLY_MS = 900;                     // one camera flight
const CLICK_FLY_MS = 600;               // the shorter flight after a click: the player is reading the map, not travelling
const DOUBLE_MS = 400;                  // a second click this soon (and this near, DOUBLE_PX) makes a double-click
const DOUBLE_PX = 8;
const DEEPER_PX = 8;                    // a later click this near the last one goes one deeper along its line of sight
const LIGHT_CUBE = 0.16, LIGHT_SECTOR = 0.06;   // the light added to the highlight's faces: a cube's, and a whole sector's (big, so less)
const MIN_DIST = 15, MAX_DIST = 90000;  // how near and how far the camera may be from the point it circles
const PITCH_MAX = Math.PI / 2 - 0.01;   // turning stops just short of straight down (or up): past it the view would flip over
const LEVEL_PITCH = 35 * Math.PI / 180; // "Level": a gentle look down, the way most of the map reads best
const TURN_STEP = 10 * Math.PI / 180;   // one press of a pad's turn button
const TURN_RATE = 1.4;                  // turning with a key held down, radians a second (about 80 degrees)
const MODES = ['3d', 'top', 'side'];    // the views: 3D, and the two flat ones
const ORTHO_BACK = 100000;              // how far back the flat views' eye sits: behind everything there is to draw
const NEAR_LY = 1500;                   // "Nebulae: Near" draws those within this many ly of the focus
const NEBULA_NAMES = 12;                // nebula names written at once, the nearest to the focus first
const PULSE_MS = 70, PULSE_PERIOD = 1800;   // the You mark's soft pulse: a new picture this often, one pulse this long
const PULSE_REACH = 32;                 // screen pixels round the You mark its pulse reaches (a ring of 25, its edge, the ticks)
const STARS_NEAR = 400;                 // star dots show within this many ly of the eye, fading out to 1.75 times as far
const SOFTWARE = /swiftshader|basic render driver|llvmpipe|softpipe/i;   // drawing without the graphics card: about 2 frames a second
// The keys the camera takes (held down: on and on). No arrow keys: those move the highlight, and are the page's.
const MOVE_KEYS = ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyR', 'KeyF', 'KeyE', 'KeyQ'];
// The keys a slider, a list or the side strip may need for itself (ownsKey()).
const PAGE_KEYS = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown'];
// The same settings in supported() and create(): the first getContext() on a canvas fixes them for good.
const CONTEXT = { antialias: false, alpha: false, depth: true, stencil: false, premultipliedAlpha: true, preserveDrawingBuffer: false, powerPreference: 'default' };

// ============================================================ shaders
// One unit cube, drawn once per box with the box's corner, size and colour value (an "instance"). Colours come
// out premultiplied (colour times opacity), which is what the blending below expects.
const CUBE_VS = `#version 300 es
layout(location=0) in vec3 aCorner;   // the unit cube, 0..1
layout(location=1) in vec3 aNormal;
layout(location=2) in vec4 aBox;      // per box: corner (ly, from the scene's base point) and size
layout(location=3) in vec4 aVal;      // per box: colour value (0..1, or -2 grey), opacity, shading, gap between boxes (share of the size)
uniform mat4 uVP;
uniform sampler2D uRamp;              // 256 x 1 colours, cold to hot
uniform vec3 uGrey;
out vec4 vColor;
void main() {
  vec3 p = aBox.xyz + aBox.w * (aVal.w + aCorner * (1.0 - 2.0 * aVal.w));
  gl_Position = uVP * vec4(p, 1.0);
  vec3 c = aVal.x < -1.5 ? uGrey : texture(uRamp, vec2(clamp(aVal.x, 0.0, 1.0) * (255.0 / 256.0) + 0.5 / 256.0, 0.5)).rgb;
  float light = 0.6 + 0.4 * abs(dot(aNormal, vec3(0.37, 0.86, 0.35)));   // top faces a little brighter than the sides
  c *= mix(1.0, light, aVal.z);
  vColor = vec4(c * aVal.y, aVal.y);
}`;
const COLOR_FS = `#version 300 es
precision mediump float;
in vec4 vColor;
out vec4 outColor;
void main() { outColor = vColor; }`;
// Lines: a position and a colour (4 bytes) per end.
const LINE_VS = `#version 300 es
layout(location=0) in vec3 aPos;
layout(location=1) in vec4 aColor;
uniform mat4 uVP;
out vec4 vColor;
void main() {
  gl_Position = uVP * vec4(aPos, 1.0);
  vColor = vec4(aColor.rgb * aColor.a, aColor.a);
}`;
// Star dots: round points that grow as the eye comes near, and fade out beyond STARS_NEAR. The flat views have no
// eye near the stars (it sits far back), so there how near the camera is zoomed in stands for it (uFlat).
const STAR_VS = `#version 300 es
layout(location=0) in vec4 aStar;     // position (ly, from the base point) and kind (0 fully recorded, 1 partly, 2 never scanned, 3 other)
uniform mat4 uVP;
uniform vec3 uEye;
uniform float uNear, uFar, uRatio, uFlat;
uniform vec3 uKinds[4];
out vec4 vColor;
void main() {
  float d = uFlat > 0.0 ? uFlat : distance(aStar.xyz, uEye);
  float a = 1.0 - smoothstep(uNear, uFar, d);
  gl_Position = a <= 0.0 ? vec4(2.0, 2.0, 2.0, 1.0) : uVP * vec4(aStar.xyz, 1.0);   // outside the screen: dropped
  gl_PointSize = clamp(2600.0 / max(d, 1.0), 2.0, 7.0) * uRatio;
  vColor = vec4(uKinds[int(aStar.w)] * a, a);
}`;
const STAR_FS = `#version 300 es
precision mediump float;
in vec4 vColor;
out vec4 outColor;
void main() {
  vec2 c = gl_PointCoord * 2.0 - 1.0;
  float r = dot(c, c);
  if (r > 1.0) discard;
  outColor = vColor * (1.0 - smoothstep(0.55, 1.0, r));
}`;

// ============================================================ maths that need no graphics card (also used by selftest)
const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const sub3 = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const len3 = a => Math.hypot(a[0], a[1], a[2]);
const clamp = (v, lo, hi) => v < lo ? lo : v > hi ? hi : v;
// A real number, strictly: the global isFinite() would let null through as 0, and a text "12" would later be
// glued to other numbers instead of added.
const fin = v => typeof v === 'number' && isFinite(v);
const num = v => fin(v) ? v : NaN;

// The box of a chosen sector or cube as [x0, y0, z0, size], or null: star dots, finds, route stops, the dots of Within
// reach and nebulae are no boxes.
const boxOf = hit => hit && hit.kind !== 'star' && hit.kind !== 'find' && hit.kind !== 'stop' && hit.kind !== 'reach' && hit.kind !== 'nebula'
  && [hit.x0, hit.y0, hit.z0].every(fin) && hit.size > 0 ? [hit.x0, hit.y0, hit.z0, +hit.size] : null;
// Whether two choices are the same thing in the same place: by their box (the page sends its choice back made anew,
// without the map's own numbers in it), else by kind and middle.
function samePlace(a, b) {
  if (a === b) return true;
  if (!a || !b || a.kind !== b.kind) return false;
  const p = boxOf(a), q = boxOf(b);
  if (p || q) return !!p && !!q && p.every((v, k) => Math.abs(v - q[k]) < 0.01);
  return [a.x, a.y, a.z].every(fin) && [0, 1, 2].every(k => Math.abs([a.x, a.y, a.z][k] - [b.x, b.y, b.z][k]) < 0.01);
}

// A nebula's spheres as [x, y, z, r]: the list the page gave (most nebulae are one sphere, ICZ is 16 spread over
// some 175 ly), else the one sphere round its middle; none for a nebula with no size. Drawn, picked and outlined as
// all of them: one sphere at their middle would miss most of where such a nebula's stars are. The app may send a
// sphere as [x, y, z, r] or as { x, y, z, r }: both are taken.
function spheresOf(nb) {
  const list = nb && Array.isArray(nb.spheres) ? nb.spheres
    .map(s => Array.isArray(s) ? s : s && typeof s === 'object' ? [s.x, s.y, s.z, s.r] : null)
    .filter(s => s && s.length === 4 && s.every(fin) && s[3] > 0) : [];
  if (list.length) return list;
  return nb && [nb.x, nb.y, nb.z, nb.r].every(fin) && nb.r > 0 ? [[nb.x, nb.y, nb.z, nb.r]] : [];
}
// How far a nebula reaches from its middle, over all its spheres: what a flight to it frames.
function reachOf(nb) {
  const c = [num(nb.x), num(nb.y), num(nb.z)];
  let r = num(nb.r) > 0 ? +nb.r : 0;
  for (const s of spheresOf(nb)) r = Math.max(r, c.every(fin) ? len3(sub3(s, c)) + s[3] : s[3]);
  return r;
}
// Whether a nebula is drawn: 'all', 'off', or 'near': some sphere of it comes within NEAR_LY of the focus.
function nebulaShown(nb, focus, mode) {
  if (mode === 'off') return false;
  if (mode !== 'near' || !focus) return true;
  return spheresOf(nb).some(s => len3(sub3(s, focus)) - s[3] <= NEAR_LY);
}

// ---- the camera's own sums (no graphics card needed, so selftest() can check them)
// cam = { t: the focus [x,y,z], yaw, pitch, dist, mode }. yaw 0 looks north; pitch 90 degrees looks straight down.
// The flat views look one fixed way whatever yaw and pitch say (those are kept for coming back to 3D).
function anglesOf(cam) {
  if (cam.mode === 'top') return { yaw: 0, pitch: Math.PI / 2 };   // from above: north up, east right
  if (cam.mode === 'side') return { yaw: 0, pitch: 0 };             // from the south, looking north: up up, east right
  return { yaw: cam.yaw, pitch: cam.pitch };
}
// The camera's directions in the game's axes: d from the focus towards the eye, f the way it looks, r right on
// screen, u up on screen, and level: forward along the ground (for "Hold height").
function axesOf(cam) {
  const { yaw, pitch } = anglesOf(cam);
  const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
  const d = [cp * sy, sp, -cp * cy];
  return { d, f: [-d[0], -d[1], -d[2]], r: [cy, 0, sy], u: [-sp * sy, cp, sp * cy], level: [-sy, 0, cy] };
}
const wrapAngle = a => a - 2 * Math.PI * Math.floor((a + Math.PI) / (2 * Math.PI));
// Turns the camera round its focus: the focus itself never moves. yawBy > 0 turns the view to the left; pitchBy > 0
// tips it to look further down. Full turns left and right; up and down stop just short of straight.
function turnCam(cam, yawBy, pitchBy) {
  cam.yaw = wrapAngle(cam.yaw + yawBy);
  cam.pitch = clamp(cam.pitch + pitchBy, -PITCH_MAX, PITCH_MAX);
}
// Moves the focus (the camera comes along): right on screen, up in the galaxy's own up, and forward the way the
// camera looks, level when "Hold height" is on. In the Top view forward is up the screen (north), as on a paper map:
// along the line of sight nothing on screen would change. In the Side view up the screen is the galaxy's up already
// (R and F), so there forward is the way the camera looks, north: it brings the next row of sectors to the focus, and
// the names written are that row's (labels()), as is what a click takes (sectorRank).
function moveCam(cam, right, up, ahead, holdHeight) {
  const a = axesOf(cam), fwd = cam.mode === 'top' ? a.u : cam.mode === 'side' ? a.f : holdHeight ? a.level : a.f;
  for (let k = 0; k < 3; k++) cam.t[k] += a.r[k] * right + (k === 1 ? up : 0) + fwd[k] * ahead;
}
// Slides the focus across the screen (a drag): the place under the pointer follows it. lyPerPx: the scale at the focus.
function slideCam(cam, dx, dy, lyPerPx) {
  const a = axesOf(cam);
  for (let k = 0; k < 3; k++) cam.t[k] += (-dx * a.r[k] + dy * a.u[k]) * lyPerPx;
}

// Which way an arrow key moves the highlight, for a camera as it is (spec v3, 1). The highlight moves from cube to
// cube on the galaxy's own grid, so each key has to mean one of its six directions: the one nearest what the key
// means on the screen. In 3D, left and right go along whichever of east-west and north-south lies nearest the
// screen's left-right; up and down along the other one, away from the viewer and towards them (the way the camera
// looks, laid flat), which is how a map on a table reads. The flat views look one fixed way: from above, up is north
// and right east, as on the flat map; from the side, up is up and right east. Higher and lower (Page Up, Page Down)
// are up and down in the galaxy in every view. Gives { step: [dx, dy, dz], axis, sign, word }, or null for a
// direction it does not know.
const STEP_WORDS = [['west', 'east'], ['down', 'up'], ['south', 'north']];
function screenStep(cam, dir) {
  let axis, sign;
  if (dir === 'higher' || dir === 'lower') { axis = 1; sign = dir === 'higher' ? 1 : -1; }
  else if (dir === 'left' || dir === 'right' || dir === 'up' || dir === 'down') {
    const across = dir === 'left' || dir === 'right', back = dir === 'left' || dir === 'down' ? -1 : 1;
    if (cam.mode === 'top') { axis = across ? 0 : 2; sign = back; }
    else if (cam.mode === 'side') { axis = across ? 0 : 1; sign = back; }
    else {
      // r: right on the screen; level: the way the camera looks, laid flat. Both lie flat and at right angles, so
      // when east-west is the nearer to one, north-south is the nearer to the other. Exactly half way (45 degrees):
      // left and right take east-west.
      const { r, level } = axesOf(cam), eastWest = Math.abs(r[0]) >= Math.abs(r[2]);
      axis = across ? (eastWest ? 0 : 2) : (eastWest ? 2 : 0);
      sign = (across ? r[axis] : level[axis]) < 0 ? -back : back;
    }
  } else return null;
  const step = [0, 0, 0];
  step[axis] = sign;
  return { step, axis, sign, word: STEP_WORDS[axis][sign > 0 ? 1 : 0] };
}

// The unit cube as 36 corners (6 faces, 2 triangles each) with their outward normals. Each triangle turns
// anticlockwise seen from outside in the game's own axes; the mirrored camera turns that into clockwise on
// screen, hence gl.frontFace(gl.CW) below.
function cubeGeometry() {
  const out = [];
  for (const [axis, sign] of [[0, 1], [0, -1], [1, 1], [1, -1], [2, 1], [2, -1]]) {
    const s = sign > 0 ? 1 : 0, u = (axis + 1) % 3, w = (axis + 2) % 3;
    const q = [[0, 0], [1, 0], [1, 1], [0, 0], [1, 1], [0, 1]];
    if (!s) q.reverse();
    const n = [0, 0, 0];
    n[axis] = sign;
    for (const [a, b] of q) {
      const p = [0, 0, 0];
      p[axis] = s; p[u] = a; p[w] = b;
      out.push(p[0], p[1], p[2], n[0], n[1], n[2]);
    }
  }
  return new Float32Array(out);
}

// The camera for one frame. cam = { t: the point it circles [x,y,z], yaw, pitch, dist, mode }; base = the point all
// positions sent to the graphics card are measured from (keeps the numbers small, so nothing shakes up close).
// yaw 0 looks north; pitch 90 degrees looks straight down. The three directions are in the game's axes:
//   d: from the point circled towards the eye; r: right on screen; u: up on screen.
// Seen from above (pitch 90, yaw 0) r is east (+x) and u is north (+z): the flat map's way round.
// The flat views ('top', 'side') have no perspective: they show a slab dist * tan(FOV / 2) high on each side of the
// focus, which is what the 3D view shows at the focus's own distance, so a switch keeps the scale. Their eye is
// ORTHO_BACK behind the focus, out beyond anything drawn. back: the distance from the eye to the focus.
// sortEye: where the see-through boxes are ordered from; in the flat views a point so far back that its distance
// to each box is, to well under a light year, how deep the box lies along the line of sight.
function view3(cam, aspect, base) {
  const { d, r, u, f } = axesOf(cam), flat = cam.mode === 'top' || cam.mode === 'side';
  const back = flat ? ORTHO_BACK : cam.dist;
  const eye = [cam.t[0] + back * d[0], cam.t[1] + back * d[1], cam.t[2] + back * d[2]];
  const e = sub3(eye, base);
  const ty = 1 / Math.tan(FOV / 2);
  const near = flat ? 1 : clamp(cam.dist * 0.01, 1, 200), far = flat ? 2 * ORTHO_BACK : cam.dist * 2 + 30000;
  const v = [r[0], u[0], d[0], 0, r[1], u[1], d[1], 0, r[2], u[2], d[2], 0, -dot3(r, e), -dot3(u, e), -dot3(d, e), 1];
  const half = cam.dist / ty;   // the flat views: half the height shown, in ly
  const p = flat
    ? [1 / (half * aspect), 0, 0, 0, 0, 1 / half, 0, 0, 0, 0, -2 / (far - near), 0, 0, 0, -(far + near) / (far - near), 1]
    : [ty / aspect, 0, 0, 0, 0, ty, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0];
  const m = new Float64Array(16);
  for (let c = 0; c < 4; c++) for (let row = 0; row < 4; row++) {
    let s = 0;
    for (let k = 0; k < 4; k++) s += p[k * 4 + row] * v[c * 4 + k];
    m[c * 4 + row] = s;
  }
  const sortEye = flat ? [0, 1, 2].map(k => e[k] + d[k] * 1e7) : e;
  return { m, m32: new Float32Array(m), eye, e, r, u, d, f, ty, aspect, near, far, base, flat, dist: cam.dist, back, sortEye, focus: cam.t.slice() };
}

// Where a place in the galaxy lands on a w x h screen: [x, y, depth] in screen pixels (depth: ly in front of the
// eye along the line of sight), or null behind the eye.
function toScreen(v, p, w, h) {
  const m = v.m, x = p[0] - v.base[0], y = p[1] - v.base[1], z = p[2] - v.base[2];
  const cw = m[3] * x + m[7] * y + m[11] * z + m[15];
  const depth = v.flat ? v.f[0] * (x - v.e[0]) + v.f[1] * (y - v.e[1]) + v.f[2] * (z - v.e[2]) : cw;
  if (depth <= v.near * 0.5) return null;
  const cx = m[0] * x + m[4] * y + m[8] * z + m[12], cy = m[1] * x + m[5] * y + m[9] * z + m[13];
  return [(cx / cw + 1) / 2 * w, (1 - cy / cw) / 2 * h, depth];
}

// Where a stretch of line between two places lands on the screen: [x0, y0, x1, y1] in screen pixels, or null when
// it lies wholly behind the eye. A stretch that passes behind the eye is cut where it does (at the near plane), so
// the highlight of a sector the camera stands in still shows its edges round the screen instead of none at all.
// (Depth along the line of sight changes evenly along a straight line, in both kinds of camera, so the cut is exact.)
function lineOnScreen(v, a, b, w, h) {
  const depth = p => {
    const x = p[0] - v.base[0], y = p[1] - v.base[1], z = p[2] - v.base[2], m = v.m;
    return v.flat ? v.f[0] * (x - v.e[0]) + v.f[1] * (y - v.e[1]) + v.f[2] * (z - v.e[2]) : m[3] * x + m[7] * y + m[11] * z + m[15];
  };
  const da = depth(a), db = depth(b), near = v.near;
  if (da <= near && db <= near) return null;
  const towards = (p, q, s) => [0, 1, 2].map(k => p[k] + (q[k] - p[k]) * s);
  if (da < near) a = towards(a, b, (near - da) / (db - da));
  else if (db < near) b = towards(b, a, (near - db) / (da - db));
  const p = toScreen(v, a, w, h), q = toScreen(v, b, w, h);
  return p && q ? [p[0], p[1], q[0], q[1]] : null;
}

// Screen pixels per light year for something at this depth (in the flat views, the same everywhere).
const pxPerLy = (v, depth, h) => v.ty / (v.flat ? v.dist : depth) * h / 2;

// How a sector met along a line of sight ranks for a click, smaller first (t: where the line enters it, size: the
// sector's). In 3D the nearest. In the flat views every sector along the line shares one spot on screen (from the
// side, a whole row from south to north), and the name written there is the one nearest the focus (labels()): so a
// click takes that one too, by how far its middle lies from the focus's plane (v.back along the line from its start).
const sectorRank = (v, t, size) => v.flat ? Math.abs(t + size / 2 - v.back) : t;

// Where a marker's name goes (You, Target, Route end): right of its mark when that covers nothing in placed, else a
// line below or above, else left of the mark, else two lines down or up; spots inside the w x h screen first. When
// nothing is free, right of the mark all the same: these names are never left out. placed: boxes [x0, y0, x1, y1]
// already taken (the marks themselves among them); tw, th: the name's size; gap: from the mark's middle to the name.
// Gives [left, top, box].
function besideMark(placed, x, y, tw, th, gap, w, h) {
  const spot = (left, lines) => {
    const l = left ? x - gap - tw : x + gap, t = y - th / 2 + lines * (th + 2);
    return [l, t, [l - 2, t - 1, l + tw + 2, t + th + 1]];
  };
  const free = b => !placed.some(p => b[0] < p[2] && b[2] > p[0] && b[1] < p[3] && b[3] > p[1]);
  const inside = b => b[0] >= 0 && b[1] >= 0 && b[2] <= w && b[3] <= h;
  const spots = [[false, 0], [false, 1], [false, -1], [true, 0], [false, 2], [false, -2], [true, 1], [true, -1]].map(([l, n]) => spot(l, n));
  return spots.find(s => free(s[2]) && inside(s[2])) || spots.find(s => free(s[2])) || spots[0];
}

// The line of sight through a screen pixel: from the eye, a unit direction in the game's axes. In the flat views
// every line of sight runs the same way, each from its own point beside the eye.
function rayAt(v, px, py, w, h) {
  const a = (px / w * 2 - 1) * v.aspect / v.ty, b = (1 - py / h * 2) / v.ty;
  if (v.flat) return { o: [0, 1, 2].map(k => v.eye[k] + (a * v.r[k] + b * v.u[k]) * v.dist), d: v.f.slice() };
  const d = [0, 1, 2].map(k => v.f[k] + a * v.r[k] + b * v.u[k]), l = len3(d);
  return { o: v.eye, d: d.map(x => x / l) };
}

// Where a line enters and leaves a box: [tIn, tOut] along it, or null when it misses.
function boxHit(o, d, min, max) {
  let t0 = -Infinity, t1 = Infinity;
  for (let k = 0; k < 3; k++) {
    if (Math.abs(d[k]) < 1e-12) { if (o[k] < min[k] || o[k] > max[k]) return null; continue; }
    let a = (min[k] - o[k]) / d[k], b = (max[k] - o[k]) / d[k];
    if (a > b) { const t = a; a = b; b = t; }
    if (a > t0) t0 = a;
    if (b < t1) t1 = b;
  }
  return t0 <= t1 && t1 >= 0 ? [t0, t1] : null;
}

// Walks a line of sight through a grid of equal cubes, nearest first (Amanatides and Woo): visit(i, j, k, tIn)
// for each cube it passes through, within cubes lo..hi (inclusive) of the grid with corner g and cube size cell.
// visit returns true to stop. This is what makes "what is under the pointer" cost hundredths of a millisecond.
function walk(o, d, g, cell, lo, hi, visit) {
  const min = [0, 1, 2].map(k => g[k] + lo[k] * cell), max = [0, 1, 2].map(k => g[k] + (hi[k] + 1) * cell);
  const span = boxHit(o, d, min, max);
  if (!span) return;
  let t = Math.max(span[0], 0);
  const c = [0, 0, 0], step = [0, 0, 0], tMax = [0, 0, 0], tDelta = [0, 0, 0];
  for (let k = 0; k < 3; k++) {
    const p = o[k] + d[k] * t;
    c[k] = clamp(Math.floor((p - g[k]) / cell), lo[k], hi[k]);
    if (d[k] > 0) { step[k] = 1; tMax[k] = t + (g[k] + (c[k] + 1) * cell - p) / d[k]; tDelta[k] = cell / d[k]; }
    else if (d[k] < 0) { step[k] = -1; tMax[k] = t + (g[k] + c[k] * cell - p) / d[k]; tDelta[k] = -cell / d[k]; }
    else { tMax[k] = Infinity; tDelta[k] = Infinity; }
  }
  for (let n = 0; n < 4096; n++) {
    if (visit(c[0], c[1], c[2], t)) return;
    const k = tMax[0] < tMax[1] ? (tMax[0] < tMax[2] ? 0 : 2) : (tMax[1] < tMax[2] ? 1 : 2);
    if (tMax[k] > span[1]) return;
    t = tMax[k];
    c[k] += step[k];
    if (c[k] < lo[k] || c[k] > hi[k]) return;
    tMax[k] += tDelta[k];
  }
}

// Farthest first: fills order[0..n) with box numbers, the farthest box from the eye first. centres holds x,y,z per
// box. The distances are squeezed into 16-bit keys over this set's own near..far span (finer than a tenth of a
// light year for one sector) and sorted with two 8-bit counting passes: no comparisons, a few array sweeps.
// dist (the distance of each box, in drawing order) is filled in too.
function farFirst(centres, n, eye, order, dist) {
  const raw = new Float64Array(n), keys = new Uint16Array(n), tmp = new Uint32Array(n), count = new Uint32Array(256);
  let lo = Infinity, hi = -Infinity;
  for (let i = 0; i < n; i++) {
    const x = centres[i * 3] - eye[0], y = centres[i * 3 + 1] - eye[1], z = centres[i * 3 + 2] - eye[2];
    const dd = Math.sqrt(x * x + y * y + z * z);
    raw[i] = dd;
    if (dd < lo) lo = dd;
    if (dd > hi) hi = dd;
  }
  const scale = 65535 / ((hi - lo) || 1);
  for (let i = 0; i < n; i++) { keys[i] = (hi - raw[i]) * scale; order[i] = i; }
  const pass = (shift, from, to) => {
    count.fill(0);
    for (let i = 0; i < n; i++) count[(keys[from[i]] >> shift) & 255]++;
    for (let b = 0, at = 0; b < 256; b++) { const c = count[b]; count[b] = at; at += c; }
    for (let i = 0; i < n; i++) { const k = from[i]; to[count[(keys[k] >> shift) & 255]++] = k; }
  };
  pass(0, order, tmp);
  pass(8, tmp, order);
  if (dist) for (let i = 0; i < n; i++) dist[i] = raw[order[i]];
}

// A colour as 4 bytes (red, green, blue, opacity) packed in one number, as the line buffer holds it.
const rgba = (c, a) => (c[0] | c[1] << 8 | c[2] << 16 | Math.round(clamp(a, 0, 1) * 255) << 24) >>> 0;

// Any CSS colour ("#ff7100", "rgb(...)", a theme variable's value) as [r, g, b], or the fallback.
function cssColour(text, fallback) {
  try {
    const c = document.createElement('canvas').getContext('2d');
    c.fillStyle = '#010203';
    c.fillStyle = String(text || '').trim();
    const s = c.fillStyle;
    if (s === '#010203' && String(text).trim() !== '#010203') return fallback;
    if (s[0] === '#') return [1, 3, 5].map(k => parseInt(s.slice(k, k + 2), 16));
    const m = s.match(/[\d.]+/g);
    return m ? m.slice(0, 3).map(Number) : fallback;
  } catch (e) { return fallback; }
}

// The renderer's own name, e.g. "ANGLE (NVIDIA, NVIDIA GeForce RTX 2070 ...)".
function rendererName(gl) {
  try {
    const ext = gl.getExtension('WEBGL_debug_renderer_info');
    return String((ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)) || '');
  } catch (e) { return ''; }
}

// A colour ramp to start with, before the page sends its own: dark violet through red to pale yellow.
function startRamp() {
  const stops = [[40, 11, 84], [101, 21, 110], [188, 55, 84], [249, 142, 9], [252, 255, 164]];
  const px = new Uint8Array(1024);
  for (let i = 0; i < 256; i++) {
    const t = i / 255 * (stops.length - 1), k = Math.min(stops.length - 2, Math.floor(t)), f = t - k;
    for (let c = 0; c < 3; c++) px[i * 4 + c] = Math.round(stops[k][c] + (stops[k + 1][c] - stops[k][c]) * f);
    px[i * 4 + 3] = 255;
  }
  return px;
}

// Lines being collected for one buffer: 16 bytes per end (x, y, z as numbers measured from base, colour as 4 bytes).
class Lines {
  constructor(base) { this.base = base; this.cap = 4096; this.n = 0; this.alloc(); }
  alloc(old) {
    const buf = new ArrayBuffer(this.cap * 16);
    if (old) new Uint8Array(buf).set(new Uint8Array(old, 0, this.n * 16));
    this.buf = buf; this.f = new Float32Array(buf); this.u = new Uint32Array(buf);
  }
  end(x, y, z, c) {
    if (this.n >= this.cap) { this.cap *= 2; this.alloc(this.buf); }
    const o = this.n++ * 4;
    this.f[o] = x - this.base[0]; this.f[o + 1] = y - this.base[1]; this.f[o + 2] = z - this.base[2]; this.u[o + 3] = c;
  }
  line(a, b, c) { this.end(a[0], a[1], a[2], c); this.end(b[0], b[1], b[2], c); }
  // The 12 edges of a box.
  box(x0, y0, z0, size, c) {
    const x1 = x0 + size, y1 = y0 + size, z1 = z0 + size;
    for (const [y, z] of [[y0, z0], [y1, z0], [y0, z1], [y1, z1]]) this.line([x0, y, z], [x1, y, z], c);
    for (const [x, z] of [[x0, z0], [x1, z0], [x0, z1], [x1, z1]]) this.line([x, y0, z], [x, y1, z], c);
    for (const [x, y] of [[x0, y0], [x1, y0], [x0, y1], [x1, y1]]) this.line([x, y, z0], [x, y, z1], c);
  }
  // Three circles round a centre (flat, north-south upright and east-west upright): a wire sphere. Dashed when asked.
  sphere(x, y, z, r, c, dashed) {
    const N = 72;
    for (let plane = 0; plane < 3; plane++) for (let i = 0; i < N; i += dashed ? 2 : 1) {
      const a = i / N * 2 * Math.PI, b = (i + 1) / N * 2 * Math.PI;
      const at = t => plane === 0 ? [x + r * Math.cos(t), y, z + r * Math.sin(t)]
        : plane === 1 ? [x, y + r * Math.cos(t), z + r * Math.sin(t)] : [x + r * Math.cos(t), y + r * Math.sin(t), z];
      this.line(at(a), at(b), c);
    }
  }
}

// Stands in for Lines when only the cubes are made again: the lines already made are kept.
const NO_LINES = { end() {}, line() {}, box() {}, sphere() {} };

// ============================================================ can this PC draw it?
// Answers { ok, code, reason, renderer }. Tried on a canvas of its own, so the page's canvas stays free for the
// flat view when the answer is no (a canvas passed in, as supported(canvas), is left alone for the same reason:
// asking it would tie it to 3D for good). The page still times the first frames (map.measure()) before trusting
// it: a slow graphics card shows only then.
function supported() {
  const no = (code, reason, renderer) => ({ ok: false, code, reason, renderer: renderer || '' });
  if (window.Studio && window.Studio.boxed) return no('overlay', 'Inside the game overlay Studio shows the flat view, so it never draws 3D over the running game.');
  if (new URLSearchParams(location.search).has('no3d')) return no('asked', 'The flat view was asked for (no3d in the address).');
  let gl = null;
  try { gl = document.createElement('canvas').getContext('webgl2', CONTEXT); } catch (e) { gl = null; }
  if (!gl) return no('no-webgl2', 'This PC or its graphics driver does not offer the 3D drawing Studio needs (WebGL2), so Studio shows the flat view.');
  const renderer = rendererName(gl);
  const lose = gl.getExtension('WEBGL_lose_context');
  if (lose) lose.loseContext();   // give the graphics card back at once: this was only a question
  if (SOFTWARE.test(renderer)) return no('software', 'This PC draws 3D without its graphics card (' + renderer + '), which is far too slow, so Studio shows the flat view.', renderer);
  return { ok: true, code: 'ok', reason: '', renderer };
}

// ============================================================ the markers' shapes
// Drawn on top of everything, each with a dark edge so it reads on bright cubes, from ops as the map places them on
// screen ({ kind, p: [x, y], ... }), in this order:
//   the route check's stops: the faint line through them, then small dots in their states' colours, first, so every
//   other mark lies over them;
//   You: a ring facing the camera (a circle on screen: the same size however far), four ticks, a centre dot, a soft
//        ring growing out of it and fading (the pulse), and a dashed drop line to the galaxy's middle to read the
//        height by;
//   Target: a cyan ring with four slanted ticks; Route end: a cyan square; your finds: small green diamonds;
//   last, a gold ring round each stop not recorded (ring: true). Such a stop is always one of your finds too (the
//   check keeps it as one, at the same place), whose green diamond covers its dot, and green is what the route check
//   calls recorded: the ring round the diamond reads as both, your find and not recorded on this route.
// ink: the colours as create() reads them; calm: no pulse; phase: how far through its pulse the You mark is (0 to 1).
// Module-level, so the self-test can draw on a canvas of its own and read the pixels back.
function paintMarks(g, ops, phase, ink, calm) {
  const dark = 'rgba(6,8,12,.85)';
  const css = c => 'rgb(' + c.join(',') + ')';
  const cssA = (c, a) => 'rgba(' + c.join(',') + ',' + a + ')';
  const circle = (x, y, r) => { g.beginPath(); g.arc(x, y, r, 0, 2 * Math.PI); };
  const ticks = (x, y, from, to, turn) => {
    g.beginPath();
    for (let q = 0; q < 4; q++) {
      const a = turn + q * Math.PI / 2, c = Math.cos(a), s = Math.sin(a);
      g.moveTo(x + c * from, y + s * from); g.lineTo(x + c * to, y + s * to);
    }
  };
  const twice = (path, colour, width) => {   // the dark edge first, the colour over it
    path(); g.lineWidth = width + 2.5; g.strokeStyle = dark; g.stroke();
    path(); g.lineWidth = width; g.strokeStyle = colour; g.stroke();
  };
  for (const m of ops) {
    if (m.kind !== 'route') continue;
    const path = () => { g.beginPath(); for (const [ax, ay, bx, by] of m.segs) { g.moveTo(ax, ay); g.lineTo(bx, by); } };
    path(); g.lineWidth = 3; g.strokeStyle = 'rgba(6,8,12,.5)'; g.stroke();
    path(); g.lineWidth = 1.2; g.strokeStyle = cssA(ink.text, 0.5); g.stroke();
  }
  for (const m of ops) {
    if (m.kind !== 'stop') continue;
    circle(m.p[0], m.p[1], STOP_DOT);
    g.fillStyle = css(m.colour); g.fill();
    g.lineWidth = 1.2; g.strokeStyle = dark; g.stroke();
  }
  for (const m of ops) {
    if (m.kind === 'stop' || m.kind === 'route') continue;
    const [x, y] = m.p;
    if (m.kind === 'you') {
      const foot = m.foot;
      if (foot && Math.hypot(foot[0] - x, foot[1] - y) > 4) {
        g.setLineDash([4, 3]);
        twice(() => { g.beginPath(); g.moveTo(x, y); g.lineTo(foot[0], foot[1]); }, cssA(ink.accent, 0.8), 1.2);
        g.setLineDash([]);
        twice(() => { g.beginPath(); g.ellipse(foot[0], foot[1], 5, 2.5, 0, 0, 2 * Math.PI); }, cssA(ink.accent, 0.8), 1.2);
      }
      const r = 10;
      if (!calm) {   // the pulse: a ring that grows out of the mark and fades
        circle(x, y, r + 2 + phase * 13);
        g.lineWidth = 2; g.strokeStyle = cssA(ink.accent, 0.55 * (1 - phase)); g.stroke();
      }
      twice(() => circle(x, y, r), css(ink.accent), 2);
      twice(() => ticks(x, y, r + 2, r + 7, 0), css(ink.accent), 2);
      circle(x, y, 2.2); g.fillStyle = css(ink.accent); g.fill();
    } else if (m.kind === 'target') {
      twice(() => circle(x, y, 7), css(ink.target), 1.8);
      twice(() => ticks(x, y, 9, 13, Math.PI / 4), css(ink.target), 1.8);
    } else if (m.kind === 'routeEnd') {
      twice(() => { g.beginPath(); g.rect(x - 6, y - 6, 12, 12); }, css(ink.target), 1.8);
      circle(x, y, 1.8); g.fillStyle = css(ink.target); g.fill();
    } else if (m.kind === 'find') {
      g.beginPath();
      g.moveTo(x, y - 5.5); g.lineTo(x + 5.5, y); g.lineTo(x, y + 5.5); g.lineTo(x - 5.5, y); g.closePath();
      g.fillStyle = css(ink.find); g.fill();
      g.lineWidth = 1.5; g.strokeStyle = dark; g.stroke();
    }
  }
  for (const m of ops) {
    if (m.kind === 'stop' && m.ring) twice(() => circle(m.p[0], m.p[1], STOP_RING), css(m.colour), 2);
  }
}

// Within reach (spec v5 C), drawn on the names' canvas before the names, so everything else lies over it: the sphere of
// the range as a faint disc with a ring at its edge (disc: { p: [x, y], r } in screen pixels, or null), then a small dot
// for each system ({ p, colour, alpha, hollow }): filled for a system the lists know, a hollow ring for one that must
// exist (where it is exactly no list says). Module-level, so the self-test can draw on a canvas of its own.
function paintReach(g, disc, dots, accent) {
  const dark = 'rgba(6,8,12,.85)';
  if (disc) {
    g.beginPath(); g.arc(disc.p[0], disc.p[1], disc.r, 0, 2 * Math.PI);
    g.fillStyle = 'rgba(' + accent.join(',') + ',.07)'; g.fill();
    g.lineWidth = 3; g.strokeStyle = 'rgba(6,8,12,.5)'; g.stroke();
    g.lineWidth = 1.2; g.strokeStyle = 'rgba(' + accent.join(',') + ',.6)'; g.stroke();
  }
  for (const d of dots) {
    g.beginPath(); g.arc(d.p[0], d.p[1], REACH_DOT + (d.hollow ? 0.4 : 0), 0, 2 * Math.PI);
    const ink = 'rgba(' + d.colour.join(',') + ',' + (d.alpha == null ? 1 : d.alpha) + ')';
    if (d.hollow) {
      g.lineWidth = 3.4; g.strokeStyle = dark; g.stroke();
      g.lineWidth = 1.6; g.strokeStyle = ink; g.stroke();
    } else {
      g.fillStyle = ink; g.fill();
      g.lineWidth = 1; g.strokeStyle = dark; g.stroke();
    }
  }
}

// ============================================================ the map
function create(canvas, overlay, opts) {
  opts = opts || {};
  // headless (selftest() only): a map with no graphics card and a size of its own, which never draws.
  const headless = Array.isArray(opts.headless) && opts.headless.length === 2 && opts.headless.every(v => v > 0) ? opts.headless.slice() : null;
  const clock = typeof opts.clock === 'function' ? opts.clock : () => performance.now();
  const sizeOf = () => headless || [canvas.clientWidth, canvas.clientHeight];
  const root = getComputedStyle(document.documentElement);
  const ink = {
    background: cssColour(opts.background || root.getPropertyValue('--bg2'), [10, 14, 20]),
    accent: cssColour(root.getPropertyValue('--accent'), [255, 113, 0]),
    text: cssColour(root.getPropertyValue('--text'), [226, 232, 240]),
    line: [148, 163, 184], white: [255, 255, 255],
    nebula: [94, 234, 212], permit: cssColour(root.getPropertyValue('--bad'), [255, 90, 79]),
    target: [34, 211, 238],   // cyan: the game's target and the plotted route's end, apart from You in the accent colour
    find: cssColour(root.getPropertyValue('--good'), [74, 222, 128]),
    // The route check's stops, as the page's list colours them: grey waiting, blue being checked, green recorded,
    // gold not recorded, red not checked. (The blue is the page's own --route-checking: the shared colours have none.)
    stop: {
      waiting: cssColour(root.getPropertyValue('--muted'), [141, 153, 170]),
      checking: cssColour(root.getPropertyValue('--route-checking'), [78, 161, 255]),
      recorded: cssColour(root.getPropertyValue('--good'), [61, 220, 132]),
      'not-recorded': cssColour(root.getPropertyValue('--warn'), [255, 176, 32]),
      failed: cssColour(root.getPropertyValue('--bad'), [255, 90, 79]),
    },
  };
  // A stop's colour by its state; one the page did not name is waiting.
  const stopInk = state => Object.prototype.hasOwnProperty.call(ink.stop, state) ? ink.stop[state] : ink.stop.waiting;
  // A dot of Within reach, in the route check's colours: green recorded, the same green fainter for a system recorded
  // by its name only (its star type is not), gold not recorded; one that must exist is a hollow ring, grey until a
  // check of it says more. { colour: [r, g, b], alpha, hollow }.
  const reachInk = (state, must) => ({
    colour: (state === 'recorded' || (state === 'name-only' && !must) ? ink.stop.recorded : state === 'not-recorded' ? ink.stop['not-recorded'] : ink.stop.waiting).slice(),
    alpha: state === 'name-only' && !must ? REACH_PALE : 1, hollow: !!must });
  const css = c => 'rgb(' + c.join(',') + ')';
  const cssA = (c, a) => 'rgba(' + c.join(',') + ',' + a + ')';
  const starsNear = opts.starsWithin || STARS_NEAR;
  const plateY = typeof opts.plateY === 'number' ? opts.plateY : SEAM_Y;
  let calm = false;          // the system asks for less motion: the You mark does not pulse
  try { calm = !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches); } catch (e) { calm = false; }

  // ---- what to draw, kept in JavaScript: the truth the graphics card is rebuilt from after losing it
  let scene = tidy(null);
  let base = [0, 0, 0];
  let ramp = startRamp();
  const view = Object.assign({ opacity: 0.35, ghostOpacity: 0.08, look: 'glass', standOut: 'even', showEmpty: true, hideBelow: 0, hideAbove: 1, holdHeight: true,
    markers: true, nebulae: 'near', cubeLevel: null }, opts.view);
  let markers = { you: null, target: null, routeEnd: null, finds: [], route: [], reach: null };   // setMarkers(); you falls back to the scene's player
  let cam = null;            // { t, yaw, pitch, dist, mode }; null until the first scene or setCamera()
  let startMode = MODES.includes(opts.mode) ? opts.mode : '3d';   // the view the camera starts in
  let fly = null;            // a camera flight under way
  let hover = null, selected = null;   // pick results
  let pickCb = opts.onPick || null, hoverCb = opts.onHover || null, cameraCb = null, modeCb = null, layerCb = null;

  // Worked out from the scene and the view (build()).
  let info = [];             // per scene sector: { tier: 1 shown / 2 other layer / 0 not drawn, open, pick, corner, empty }
  const sectorAt = new Map();          // "ix,iy,iz" -> sector number
  let block = null;          // { lo, hi } sector indices covering the scene's sectors
  // { corner, cell, n, level (the one level shown, or -1), slot (Int32Array: cube number + 1 per grid place),
  //   state (Int8Array: 0 empty, 1 shown, 2 hidden by the range, 3 on a level not shown: not there at all) }
  let area = null;
  let areaDrawn = 0;         // the opened area's filled cubes drawn (stats(): what the level leaves)
  let countedOk = [];        // per counted cube: 0 not drawn, 1 filled, 2 outline (nothing known), 3 outline (hidden by the range)
  let batches = { solid: null, sectors: null, area: null };   // cube instances, see batch()
  let lines = null, marks = null, starData = null;
  let light = null;          // the light on the highlight's faces: one cube instance (8 numbers), or null
  let needBuild = 2;         // 0 nothing to make, 1 the cubes only (an opacity or the look changed), 2 cubes and lines
  let needMarks = true, needStars = true, sortedEye = null;
  let nebKey = null;         // which nebulae the nebula lines were made for (they change as the focus moves, on Near)
  let markOps = [];          // the markers as last placed on screen, redrawn on their own for the pulse
  let labelCache = null, pulseTimer = 0;
  let inView = true;         // the map is on screen, not scrolled out of the page's column (nothing pulses then)

  // ---- the graphics card's side
  let gl = null, R = null, lost = false, disposed = false, renderer = '';
  let raf = 0, ratio = 1;
  let frames = 0, lastMs = 0, sortMs = 0;
  const synced = [];         // the first frames, timed until the card had finished them
  let measuring = [];        // promises waiting for measure()
  const octx = overlay ? overlay.getContext('2d') : null;
  if (overlay) overlay.style.pointerEvents = 'none';   // the pointer belongs to the 3D canvas underneath
  canvas.style.touchAction = 'none';                   // a finger turns the view instead of scrolling the page

  function program(vs, fs, names) {
    const make = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS) && !gl.isContextLost()) throw new Error('3D map shader: ' + gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, make(gl.VERTEX_SHADER, vs));
    gl.attachShader(p, make(gl.FRAGMENT_SHADER, fs));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS) && !gl.isContextLost()) throw new Error('3D map program: ' + gl.getProgramInfoLog(p));
    const u = {};
    for (const n of names) u[n] = gl.getUniformLocation(p, n);
    return { p, u };
  }

  // Everything the graphics card holds, made from scratch: at the start and after the card was given back.
  function initGL() {
    gl = canvas.getContext('webgl2', CONTEXT);
    if (!gl) throw new Error('This PC does not offer the 3D drawing Studio needs (WebGL2).');
    renderer = rendererName(gl);
    R = {
      cube: program(CUBE_VS, COLOR_FS, ['uVP', 'uRamp', 'uGrey']),
      line: program(LINE_VS, COLOR_FS, ['uVP']),
      star: program(STAR_VS, STAR_FS, ['uVP', 'uEye', 'uNear', 'uFar', 'uRatio', 'uFlat', 'uKinds']),
      px: new Uint8Array(4),
    };
    R.cubeVB = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, R.cubeVB);
    gl.bufferData(gl.ARRAY_BUFFER, cubeGeometry(), gl.STATIC_DRAW);
    const cubes = () => {
      const vao = gl.createVertexArray(), buf = gl.createBuffer();
      gl.bindVertexArray(vao);
      gl.bindBuffer(gl.ARRAY_BUFFER, R.cubeVB);
      gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
      gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.enableVertexAttribArray(2); gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 32, 0); gl.vertexAttribDivisor(2, 1);
      gl.enableVertexAttribArray(3); gl.vertexAttribPointer(3, 4, gl.FLOAT, false, 32, 16); gl.vertexAttribDivisor(3, 1);
      return { vao, buf, n: 0 };
    };
    const strokes = () => {
      const vao = gl.createVertexArray(), buf = gl.createBuffer();
      gl.bindVertexArray(vao);
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 16, 0);
      gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 4, gl.UNSIGNED_BYTE, true, 16, 12);
      return { vao, buf, n: 0 };
    };
    R.solid = cubes(); R.sectors = cubes(); R.area = cubes(); R.light = cubes();
    R.lines = strokes(); R.marks = strokes(); R.nebs = strokes();
    R.stars = { vao: gl.createVertexArray(), buf: gl.createBuffer(), n: 0 };
    gl.bindVertexArray(R.stars.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, R.stars.buf);
    gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 4, gl.FLOAT, false, 16, 0);
    gl.bindVertexArray(null);
    R.ramp = gl.createTexture();
    uploadRamp();
    needBuild = 2;
    needMarks = needStars = true;
    sortedEye = null;
    nebKey = null;
  }

  function uploadRamp() {
    if (!gl || lost) return;
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, R.ramp);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 256, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, ramp);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  }

  // ---- the scene as the page sent it, with the gaps filled in so the rest need not check
  function tidy(s) {
    s = s || {};
    const list = a => Array.isArray(a) ? a : [];
    const origin = Array.isArray(s.origin) && s.origin.length === 3 && s.origin.every(fin) ? s.origin.map(Number) : GRID_ORIGIN;
    const a = s.area && Array.isArray(s.area.corner) && s.area.cubeLy > 0 && s.area.n > 0 ? s.area : null;
    return {
      origin, sectorLy: s.sectorLy > 0 ? +s.sectorLy : SECTOR_LY,
      sectors: list(s.sectors), area: a, counted: list(s.counted), stars: list(s.stars), nebulae: list(s.nebulae),
      player: s.player && [s.player.x, s.player.y, s.player.z].every(fin) ? s.player : null,
      marker: s.marker && [s.marker.x, s.marker.y, s.marker.z].every(fin) ? s.marker : null,
    };
  }

  const sectorCorner = sec => [0, 1, 2].map(k => scene.origin[k] + scene.sectorLy * [sec.ix, sec.iy, sec.iz][k]);

  // How see-through a filled box is. "Busy stands out" makes hot cubes more solid and cold ones fainter;
  // "Unexplored stands out" the other way round; "Even" gives every box the slider's opacity.
  function opacityOf(value, tier) {
    const o = tier === 1 ? view.opacity : view.ghostOpacity;
    let a = o;
    if (value >= 0 && view.standOut === 'busy') a = o * 0.35 + (Math.min(1, o * 2.2) - o * 0.35) * value;
    else if (value >= 0 && view.standOut === 'unexplored') a = Math.min(1, o * 2.2) + (o * 0.35 - Math.min(1, o * 2.2)) * value;
    return clamp(view.look === 'glow' ? a * 0.6 : a, 0, 1);
  }

  // The range filter. hideBelow / hideAbove are normally on the same 0..1 scale as the values (the page maps its
  // "hide fewer than / more than" numbers through the same scale). A number above 1 is taken as a real count of
  // known systems instead, and compared with the item's own "known" when it has one. Any item with hidden: true
  // (the page's own range filter, in real numbers) is hidden whatever the filter says. Empty and grey items are not
  // on the scale, so the range never hides them. A hidden item loses its fill and keeps only the faint outline of
  // an empty one, as on the flat view, so the block keeps its shape.
  function filtered(item, value) {
    if (item.hidden) return true;
    if (!(value >= 0)) return false;
    const lo = num(view.hideBelow), hi = num(view.hideAbove), known = num(item.known);
    if (lo > 0 && (lo <= 1 ? value < lo : known < lo)) return true;
    if (hi >= 0 && (hi <= 1 ? hi < 1 && value > hi : known > hi)) return true;
    return false;
  }

  // The one level of the opened area's cubes on show (setView cubeLevel: 0 = the bottom), or -1 for all of them. A
  // level the area does not have (a size change left an old one behind) shows them all rather than none.
  const levelOf = n => { const L = view.cubeLevel; return typeof L === 'number' && Number.isInteger(L) && L >= 0 && L < n ? L : -1; };
  // Whether one level being shown leaves this out: a box (size > 0) or a point (size 0) inside the opened area, not
  // on that level. A box counts as on it when it reaches into it (a counted cube of 160 ly over cubes of 40 ly); a
  // point by the cube it lies in, the way the page sorts stars into cubes (HeatGrid.cubeIndex: one on the face
  // between two levels in the upper one, one on the area's top face in the top level). Anything outside the area is
  // never left out: the level is the area's own.
  function sliceHides(x, y, z, size) {
    if (!area || area.level < 0) return false;
    const s = size > 0 ? size : 0, c = area.corner, cell = area.cell, n = area.n, span = n * cell;
    const mid = [x + s / 2, y + s / 2, z + s / 2];
    if (![0, 1, 2].every(k => mid[k] >= c[k] && mid[k] <= c[k] + span)) return false;
    const lo = c[1] + area.level * cell, hi = lo + cell;
    if (s > 0) return y + s <= lo + 1e-6 || y >= hi - 1e-6;
    return clamp(Math.floor((y - c[1]) / cell), 0, n - 1) !== area.level;
  }

  // Cube instances being collected: 8 numbers per box (corner, size, value, opacity, shading, gap) and its middle.
  function batch() { return { box: [], mid: [], n: 0, data: null, dist: null, mids: null }; }
  function addBox(b, x0, y0, z0, size, value, alpha, shade, gap) {
    b.box.push(x0 - base[0], y0 - base[1], z0 - base[2], size, value, alpha, shade, gap);
    b.mid.push(x0 + size / 2 - base[0], y0 + size / 2 - base[1], z0 + size / 2 - base[2]);
    b.n++;
  }
  function seal(b) { b.data = new Float32Array(b.box); b.mids = new Float64Array(b.mid); b.dist = new Float64Array(b.n); b.box = b.mid = null; return b; }

  // Turns the scene and the view into cube instances and lines. Runs after new data or new settings, never
  // because the camera moved. cubesOnly: only the opacity or the look changed, which no line depends on, so the
  // lines already made are kept (with every empty cube outlined they are most of the work: 60 ms against 12).
  function build(cubesOnly) {
    const solidLook = view.look === 'solid', glow = view.look === 'glow';
    const shade = solidLook ? 1 : glow ? 0.3 : 0.6;
    const solid = batch(), sect = batch(), cubes = batch();
    const L = cubesOnly ? NO_LINES : new Lines(base);
    const S = scene.sectorLy;
    info = [];
    sectorAt.clear();
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];

    // The sectors: the shown layer bright, the other layers faint, the opened one as an outline only.
    scene.sectors.forEach((sec, s) => {
      const tier = sec.layerShown ? 1 : sec.ghost ? 2 : 0;
      const corner = sectorCorner(sec), value = num(sec.value);
      const it = { tier, open: !!sec.outlineOnly, pick: false, corner, empty: false };
      info.push(it);
      if (![sec.ix, sec.iy, sec.iz].every(fin)) return;
      sectorAt.set(sec.ix + ',' + sec.iy + ',' + sec.iz, s);
      [sec.ix, sec.iy, sec.iz].forEach((v, k) => { if (v < lo[k]) lo[k] = v; if (v > hi[k]) hi[k] = v; });
      if (!tier) return;
      if (it.open) { it.pick = true; L.box(corner[0], corner[1], corner[2], S, rgba(ink.accent, 0.95)); return; }
      const blank = !(value >= 0) && value !== -2;
      if (blank || filtered(sec, value)) {   // nothing known, or hidden by the range: no fill, an outline only when empty boxes are shown
        it.empty = blank;
        if (view.showEmpty) { it.pick = true; L.box(corner[0], corner[1], corner[2], S, rgba(ink.line, tier === 1 ? 0.55 : 0.16)); }
        return;
      }
      it.pick = true;
      if (solidLook && tier === 1) addBox(solid, corner[0], corner[1], corner[2], S, value, 1, shade, 0.004);
      else addBox(sect, corner[0], corner[1], corner[2], S, value, opacityOf(value, tier), shade, 0.004);
      L.box(corner[0], corner[1], corner[2], S, rgba(ink.line, tier === 1 ? 0.3 : 0.09));
    });
    block = fin(lo[0]) ? { lo, hi } : null;

    // The cubes of the one opened sector or nebula. A cube missing from the list has nothing known in it (or, in a
    // busy sector, has not been counted yet). a.ghost: the area's layer is not the bright one, so its cubes are as
    // faint as that layer's sectors. a.grid: every cube's outline is drawn, and can be clicked, whatever "Show empty
    // cubes" says: a busy sector's cubes are not empty, only not counted yet.
    area = null;
    areaDrawn = 0;
    const a = scene.area;
    if (a) {
      const n = Math.max(1, Math.round(a.n)), cell = +a.cubeLy, corner = a.corner.map(Number), tierOf = a.ghost ? 2 : 1;
      const slot = new Int32Array(n * n * n), state = new Int8Array(n * n * n);
      // One level of cubes (setView cubeLevel): the others are not there at all, neither drawn nor clickable, so the
      // cubes inside the block can be reached. The grid's place number is i + n * (j + n * k): its level j is
      // floor(place / n) % n.
      const level = levelOf(n);
      if (level >= 0) for (let idx = 0; idx < state.length; idx++) if (Math.floor(idx / n) % n !== level) state[idx] = 3;
      (a.cubes || []).forEach((c, q) => {
        if (c.i >= 0 && c.i < n && c.j >= 0 && c.j < n && c.k >= 0 && c.k < n) slot[c.i + n * (c.j + n * c.k)] = q + 1;
      });
      for (let idx = 0; idx < slot.length; idx++) {
        if (!slot[idx] || state[idx] === 3) continue;
        const c = a.cubes[slot[idx] - 1], value = num(c.value);
        if (!(value >= 0) && value !== -2) continue;   // empty: state 0
        if (filtered(c, value)) { state[idx] = 2; continue; }
        state[idx] = 1;
        areaDrawn++;
        const x0 = corner[0] + c.i * cell, y0 = corner[1] + c.j * cell, z0 = corner[2] + c.k * cell;
        if (solidLook && !a.ghost) addBox(solid, x0, y0, z0, cell, value, 1, shade, 0.05);
        else addBox(cubes, x0, y0, z0, cell, value, opacityOf(value, tierOf), shade, 0.05);
      }
      // Empty cubes (and cubes the range hides) as faint outlines. Each edge once, however many such cubes share
      // it, so shared edges are not drawn brighter than lone ones. With one level shown, that level's only.
      if ((view.showEmpty || a.grid) && !cubesOnly) {
        const m = n + 1, seen = new Uint8Array(m * m * m * 3), c = rgba(ink.line, a.ghost ? 0.08 : 0.2);
        const edge = (i, j, k, axis) => {
          const key = ((i + m * (j + m * k)) * 3) + axis;
          if (seen[key]) return;
          seen[key] = 1;
          const x = corner[0] + i * cell, y = corner[1] + j * cell, z = corner[2] + k * cell;
          L.end(x, y, z, c);
          L.end(axis === 0 ? x + cell : x, axis === 1 ? y + cell : y, axis === 2 ? z + cell : z, c);
        };
        for (let k = 0; k < n; k++) for (let j = level >= 0 ? level : 0; j < (level >= 0 ? level + 1 : n); j++) for (let i = 0; i < n; i++) {
          const st = state[i + n * (j + n * k)];
          if (st === 1 || st === 3) continue;
          for (let a = 0; a < 4; a++) {   // the 4 edges along each axis: the corners (0,0), (1,0), (0,1), (1,1) across it
            const u = a & 1, w = a >> 1;
            edge(i, j + u, k + w, 0);
            edge(i + u, j, k + w, 1);
            edge(i + u, j + w, k, 2);
          }
        }
      }
      area = { corner, cell, n, level, slot, state, grid: !!a.grid };
    }

    // Cubes counted one by one in busy sectors: always solid, whatever the look, with a light edge.
    countedOk = scene.counted.map(c => {
      const value = num(c.value), size = +c.size;
      if (!(size > 0) || ![c.x0, c.y0, c.z0].every(fin)) return 0;
      if (sliceHides(+c.x0, +c.y0, +c.z0, size)) return 0;   // inside the opened area, on a level not shown: not there
      const blank = !(value >= 0) && value !== -2;
      if (blank || filtered(c, value)) {
        if (!view.showEmpty) return 0;
        L.box(c.x0, c.y0, c.z0, size, rgba(ink.white, blank ? 0.45 : 0.25));
        return blank ? 2 : 3;
      }
      addBox(solid, c.x0, c.y0, c.z0, size, value, 1, 1, 0.02);
      L.box(c.x0, c.y0, c.z0, size, rgba(ink.white, 0.3));
      return 1;
    });

    // (The nebulae have lines of their own, buildNebulae(): on "Near" they come and go as the focus moves.)

    // A faint plate at the galaxy's middle, under the block: the floor of our layer, where the two busy layers meet.
    if (block) {
      const x0 = scene.origin[0] + block.lo[0] * S, x1 = scene.origin[0] + (block.hi[0] + 1) * S;
      const z0 = scene.origin[2] + block.lo[2] * S, z1 = scene.origin[2] + (block.hi[2] + 1) * S;
      const c = rgba(ink.line, 0.07), step = S / 4;
      for (let x = x0; x <= x1 + 0.5; x += step) L.line([x, plateY, z0], [x, plateY, z1], c);
      for (let z = z0; z <= z1 + 0.5; z += step) L.line([x0, plateY, z], [x1, plateY, z], c);
    }

    // (The commander's heading and the markers are drawn with the outlines of what is pointed at, buildMarks():
    // they change without the rest of the scene changing.)

    // The cube of the star the page marked (a search result): a bright outline.
    const mk = scene.marker;
    if (mk && mk.cube && [mk.cube.x0, mk.cube.y0, mk.cube.z0].every(fin) && mk.cube.size > 0) L.box(mk.cube.x0, mk.cube.y0, mk.cube.z0, +mk.cube.size, rgba(ink.white, 0.9));

    batches = { solid: seal(solid), sectors: seal(sect), area: seal(cubes) };
    if (!cubesOnly) lines = L;
    needBuild = 0;
    needMarks = true;
    sortedEye = null;   // new boxes: put them in order again
    if (gl && !lost) {
      putCubes(R.solid, batches.solid.data, batches.solid.n);
      putCubes(R.sectors, batches.sectors.data, batches.sectors.n);
      putCubes(R.area, batches.area.data, batches.area.n);
      if (!cubesOnly) putLines(R.lines, L);
    }
  }

  // The scene and the view worked out, if a change has not been yet: before a frame, and before anything that reads
  // what build() works out (what is under the pointer, the cube next to the highlight, the numbers in stats()), so a
  // click right after a new scene or a new level, before the next frame, finds what is there now.
  function ensureBuilt() {
    if (disposed) return;   // its buffers are given back: nothing to fill
    if (needBuild) build(needBuild === 1 && !!lines);
    if (needStars) buildStars();
  }

  function putCubes(g, data, n) { g.n = n; gl.bindBuffer(gl.ARRAY_BUFFER, g.buf); gl.bufferData(gl.ARRAY_BUFFER, data.length ? data : new Float32Array(8), gl.DYNAMIC_DRAW); }
  function putLines(g, L) { g.n = L.n; gl.bindBuffer(gl.ARRAY_BUFFER, g.buf); gl.bufferData(gl.ARRAY_BUFFER, L.n ? new Uint8Array(L.buf, 0, L.n * 16) : new Uint8Array(16), gl.DYNAMIC_DRAW); }

  // The star dots, rebuilt only when the scene brings new ones or another level of cubes is shown (the dots on the
  // levels not shown go with their cubes). After build(), which works out the area and its level.
  function buildStars() {
    const kinds = { full: 0, part: 1, never: 2 };
    const list = scene.stars.filter(s => fin(s.x) && fin(s.y) && fin(s.z) && !sliceHides(s.x, s.y, s.z, 0));
    starData = new Float32Array(list.length * 4);
    list.forEach((s, i) => {
      starData[i * 4] = s.x - base[0]; starData[i * 4 + 1] = s.y - base[1]; starData[i * 4 + 2] = s.z - base[2];
      starData[i * 4 + 3] = s.state in kinds ? kinds[s.state] : 3;
    });
    needStars = false;
    if (gl && !lost) {
      R.stars.n = list.length;
      gl.bindBuffer(gl.ARRAY_BUFFER, R.stars.buf);
      gl.bufferData(gl.ARRAY_BUFFER, starData.length ? starData : new Float32Array(4), gl.STATIC_DRAW);
    }
  }

  // The game's own cube a star dot belongs to (from its id64, by heat-grid.js), as { x0, y0, z0, size }, or null.
  // The page may hand back a chosen star by its position only, so the star's id64 is then looked up in the scene.
  function starCube(hit) {
    const G = window.HeatGrid;
    if (!G || typeof G.place !== 'function') return null;
    let id = hit.id64 != null ? hit.id64 : hit.item && hit.item.id64 != null ? hit.item.id64 : null;
    if (id == null) {
      const s = scene.stars.find(s => +s.x === +hit.x && +s.y === +hit.y && +s.z === +hit.z);
      if (s && s.id64 != null) id = s.id64;
    }
    const p = id == null ? null : G.place(String(id));   // id64 always as text: it is too big for a JavaScript number
    if (!p || !Array.isArray(p.corner) || !(p.cubeLy > 0)) return null;
    // A cube that does not hold the star (a database's wrong position) would point at the wrong place: none then.
    const at = [+hit.x, +hit.y, +hit.z];
    if (!at.every((v, k) => v >= p.corner[k] - 1 && v <= p.corner[k] + p.cubeLy + 1)) return null;
    return { x0: p.corner[0], y0: p.corner[1], z0: p.corner[2], size: p.cubeLy };
  }

  // You: the markers' own place, else the scene's player.
  const you = () => markers.you || scene.player;
  // Target and route end, unless the route ends where the target is (one mark then, saying both).
  const sameSpot = (a, b) => !!a && !!b && ((a.name && a.name === b.name) || len3(sub3([a.x, a.y, a.z], [b.x, b.y, b.z])) < 0.5);
  const routeEndShown = () => markers.routeEnd && !sameSpot(markers.routeEnd, markers.target) ? markers.routeEnd : null;
  // The route check's stops joined in route order (the stops the page sent with a place, so a stop with none is
  // stepped over): the pairs of places the faint line runs between. None while the markers are hidden.
  function routeSegments() {
    if (view.markers === false) return [];
    const r = markers.route, out = [];
    for (let i = 1; i < r.length; i++) out.push([[+r[i - 1].x, +r[i - 1].y, +r[i - 1].z], [+r[i].x, +r[i].y, +r[i].z]]);
    return out;
  }

  // The outlines of what the pointer is over and of what was clicked (a chosen star: the cube the game puts it in),
  // the commander's heading, and the cube of doubt round a target placed from its name. Small, so rebuilt on every change.
  // A chosen sector or cube (the highlight) is not outlined here: its edges go over everything on the names' canvas
  // (labels()), and here it gets only the light on its faces.
  function buildMarks() {
    const L = new Lines(base);
    const outline = (hit, c, chosen) => {
      if (!hit) return;
      if (hit.kind === 'nebula') for (const [x, y, z, r] of spheresOf(hit)) L.sphere(x, y, z, r, c, false);
      else if (hit.kind === 'star') { const b = chosen && starCube(hit); if (b) L.box(b.x0, b.y0, b.z0, b.size, rgba(ink.accent, 0.8)); }
      else if (!chosen && hit.size > 0) L.box(hit.x0, hit.y0, hit.z0, hit.size, c);
    };
    outline(hover, rgba(ink.white, 0.5), false);
    outline(selected, rgba(ink.white, 0.95), true);
    // The light: the box once more, white, added over what is drawn there (see draw()). A whole sector gets less,
    // or the block would wash out round it.
    const b = boxOf(selected);
    light = b ? new Float32Array([b[0] - base[0], b[1] - base[1], b[2] - base[2], b[3], -2, b[3] >= scene.sectorLy ? LIGHT_SECTOR : LIGHT_CUBE, 0.6, 0]) : null;
    if (gl && !lost) { if (light) putCubes(R.light, light, 1); else R.light.n = 0; }
    if (view.markers !== false) {
      // The commander: a dashed line the way they travel (the drop line to the middle is on the names' canvas, on top).
      const me = you(), h = !me ? NaN : fin(me.heading) ? me.heading : scene.player ? num(scene.player.heading) : NaN;
      if (me && h >= -360 && h <= 360) {
        const dx = Math.sin(h * Math.PI / 180), dz = Math.cos(h * Math.PI / 180), reach = scene.sectorLy / 2, dashes = 8;
        for (let i = 0; i < dashes; i++) {
          const a0 = (i / dashes) * reach + 20, a1 = ((i + 0.55) / dashes) * reach + 20;
          L.line([me.x + dx * a0, me.y, me.z + dz * a0], [me.x + dx * a1, me.y, me.z + dz * a1], rgba(ink.white, 0.75));
        }
      }
      // A target or route end placed from its name lies somewhere in the cube its name gives: that cube, faintly.
      for (const m of [markers.target, routeEndShown()]) {
        const pm = m ? num(m.plusMinus) : NaN;
        if (pm > 0) L.box(m.x - pm, m.y - pm, m.z - pm, 2 * pm, rgba(ink.target, 0.55));
      }
      // Within reach: the sphere's three circles, faint, in your colour, so it reads as a ball round you in 3D (the
      // disc that fills it as seen is on the names' canvas: labels()).
      const rc = markers.reach;
      if (rc) L.sphere(rc.x, rc.y, rc.z, rc.r, rgba(ink.accent, 0.3), false);
    }
    marks = L;
    needMarks = false;
    if (gl && !lost) putLines(R.marks, L);
  }

  // The nebulae on show: every one, the ones near the focus, or none (view.nebulae). By number in scene.nebulae.
  function nebulaeOnShow() {
    const focus = cam ? cam.t : null, mode = view.nebulae || 'near', out = [];
    scene.nebulae.forEach((nb, i) => { if (nebulaShown(nb, focus, mode)) out.push(i); });
    return out;
  }
  // Their wire spheres, in their own colour, red where the game asks for a permit. Not filled: their systems are not
  // spread into the boxes. One made of several spheres (ICZ) is drawn as all of them. Made again only when the set
  // on show changes, which on "Near" is when the focus comes within reach of one or leaves it.
  function buildNebulae(list) {
    const L = new Lines(base);
    for (const i of list) {
      const nb = scene.nebulae[i], c = rgba(nb.permit ? ink.permit : ink.nebula, 0.6);
      for (const [x, y, z, r] of spheresOf(nb)) L.sphere(x, y, z, r, c, true);
    }
    nebKey = list.join(',');
    if (gl && !lost) putLines(R.nebs, L);
  }

  // Puts the see-through boxes back to front for the eye where it is now. Only when the eye has moved.
  function sortFor(v) {
    const e = v.sortEye;
    if (sortedEye && Math.hypot(e[0] - sortedEye[0], e[1] - sortedEye[1], e[2] - sortedEye[2]) < 0.001 * cam.dist + 0.01) return;
    sortedEye = e.slice();
    const t0 = performance.now();
    for (const [b, g] of [[batches.sectors, R.sectors], [batches.area, R.area]]) {
      if (!b || b.n < 1) continue;
      const order = new Uint32Array(b.n), out = new Float32Array(b.n * 8);
      farFirst(b.mids, b.n, e, order, b.dist);
      for (let i = 0; i < b.n; i++) out.set(b.data.subarray(order[i] * 8, order[i] * 8 + 8), i * 8);
      gl.bindBuffer(gl.ARRAY_BUFFER, g.buf);
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, out);
    }
    sortMs = performance.now() - t0;
  }

  function drawCubes(g, first, count) {
    if (count <= 0) return;
    gl.bindVertexArray(g.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, g.buf);
    gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 32, first * 32);   // WebGL2 has no "start at instance N", so the start moves instead
    gl.vertexAttribPointer(3, 4, gl.FLOAT, false, 32, first * 32 + 16);
    gl.drawArraysInstanced(gl.TRIANGLES, 0, 36, count);
  }

  // ---- one frame
  function draw() {
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (disposed || lost || !gl || !cam || document.hidden || !w || !h) return false;   // nobody can see it: draw when they can
    const t0 = performance.now();
    ratio = Math.min(window.devicePixelRatio || 1, MAX_RATIO);
    const pw = Math.max(1, Math.round(w * ratio)), ph = Math.max(1, Math.round(h * ratio));
    if (canvas.width !== pw || canvas.height !== ph) { canvas.width = pw; canvas.height = ph; }
    ensureBuilt();
    if (needMarks) buildMarks();
    const nebs = nebulaeOnShow();
    if (nebs.join(',') !== nebKey) buildNebulae(nebs);
    // Glow adds light, so its order does not matter. Glass needs the order, and so does Solid: there the shown
    // layer is solid, but the faint other layers are still see-through.
    const v = view3(cam, w / h, base), glow = view.look === 'glow';
    if (!glow) sortFor(v);

    gl.viewport(0, 0, pw, ph);
    gl.clearColor(ink.background[0] / 255, ink.background[1] / 255, ink.background[2] / 255, 1);
    gl.depthMask(true);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST);
    gl.depthFunc(gl.LEQUAL);
    gl.enable(gl.CULL_FACE);
    gl.cullFace(gl.BACK);
    gl.frontFace(gl.CW);   // the mirrored camera, see cubeGeometry()

    gl.useProgram(R.cube.p);
    gl.uniformMatrix4fv(R.cube.u.uVP, false, v.m32);
    gl.uniform1i(R.cube.u.uRamp, 0);
    gl.uniform3f(R.cube.u.uGrey, 104 / 255, 114 / 255, 128 / 255);   // HeatColour.GREY_COLOUR (#687280), the legend's grey
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, R.ramp);

    // (1) Solid cubes: counted cubes, and everything shown in the Solid look. They write depth, so what follows hides behind them.
    gl.disable(gl.BLEND);
    drawCubes(R.solid, 0, R.solid.n);

    // (2) and (3) See-through sectors, and the opened area's cubes, farthest first. The area goes in among the
    // sectors at the distance of its own middle, so sectors in front of it are drawn over it and those behind under it.
    gl.depthMask(false);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, glow ? gl.ONE : gl.ONE_MINUS_SRC_ALPHA);
    const nS = R.sectors.n, nA = R.area.n;
    let split = nS;
    if (!glow && nA && nS && area) {
      const m = [0, 1, 2].map(k => area.corner[k] + area.n * area.cell / 2 - base[k]);
      const dA = len3(sub3(m, v.sortEye));
      split = 0;
      while (split < nS && batches.sectors.dist[split] >= dA) split++;
    }
    drawCubes(R.sectors, 0, split);
    drawCubes(R.area, 0, nA);
    drawCubes(R.sectors, split, nS - split);

    // (4) Lines: outlines, nebulae, the middle plate, the heading, the marked cube, then hover and selection.
    gl.disable(gl.CULL_FACE);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.useProgram(R.line.p);
    gl.uniformMatrix4fv(R.line.u.uVP, false, v.m32);
    for (const g of [R.lines, R.nebs, R.marks]) if (g.n) { gl.bindVertexArray(g.vao); gl.drawArrays(gl.LINES, 0, g.n); }

    // (5) Star dots: tested against the solid cubes, not sorted.
    if (R.stars.n) {
      gl.useProgram(R.star.p);
      gl.uniformMatrix4fv(R.star.u.uVP, false, v.m32);
      gl.uniform3f(R.star.u.uEye, v.e[0], v.e[1], v.e[2]);
      gl.uniform1f(R.star.u.uNear, starsNear);
      gl.uniform1f(R.star.u.uFar, starsNear * 1.75);
      gl.uniform1f(R.star.u.uRatio, ratio);
      gl.uniform1f(R.star.u.uFlat, v.flat ? Math.max(cam.dist, 1) : 0);
      gl.uniform3fv(R.star.u.uKinds, new Float32Array([0.89, 0.91, 0.94, 0.49, 0.83, 0.99, ink.accent[0] / 255, ink.accent[1] / 255, ink.accent[2] / 255, 0.58, 0.64, 0.72]));
      gl.bindVertexArray(R.stars.vao);
      gl.drawArrays(gl.POINTS, 0, R.stars.n);
    }

    // (6) The light on the highlight: its box in white, added to what is drawn there, so the chosen cube is a little
    // brighter than its neighbours. Front faces only (each pixel once), and behind solid cubes in front of it hidden
    // by them like everything else: its edges, drawn over everything on the names' canvas, show where it is then.
    if (R.light.n) {
      gl.enable(gl.CULL_FACE);
      gl.blendFunc(gl.ONE, gl.ONE);
      gl.useProgram(R.cube.p);
      gl.uniform3f(R.cube.u.uGrey, 1, 1, 1);   // the value -2 takes this "grey": white, for this box only (set again each frame)
      drawCubes(R.light, 0, 1);
    }
    gl.bindVertexArray(null);
    gl.depthMask(true);

    // (7) Names, on the flat canvas over the 3D one, and the highlight's edges over them.
    labels(v, w, h);

    frames++;
    // The first frames are timed until the graphics card has really finished them (reading one pixel back waits
    // for it), so a slow card shows in the numbers; after that the waiting would only slow the page down.
    if (synced.length < 12) { gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, R.px); synced.push(performance.now() - t0); }
    lastMs = performance.now() - t0;
    if (measuring.length && synced.length >= 10) { const done = measuring; measuring = []; done.forEach(f => f(stats())); }
    return true;
  }

  // ---- names and marks on the overlay canvas
  // The names are written on a canvas of their own (labelCache), which is copied onto the overlay with the markers
  // drawn over it (paintOverlay). So the You mark's pulse redraws the markers alone, not every name.
  function labels(v, w, h) {
    if (!octx) return;
    const pw = Math.round(w * ratio), ph = Math.round(h * ratio);
    if (overlay.width !== pw || overlay.height !== ph) { overlay.width = pw; overlay.height = ph; }
    if (!labelCache) labelCache = document.createElement('canvas');
    if (labelCache.width !== pw || labelCache.height !== ph) { labelCache.width = pw; labelCache.height = ph; }
    const g = labelCache.getContext('2d');
    g.setTransform(ratio, 0, 0, ratio, 0, 0);
    g.clearRect(0, 0, w, h);
    g.textBaseline = 'middle';
    const placed = [], late = [];
    const at = (x, y, z) => toScreen(v, [x, y, z], w, h);
    const whole = n => window.Studio && Studio.whole ? Studio.whole(n) : Math.round(n).toLocaleString('en-GB');
    // A name at a screen point, unless it would cover one already written (the more important ones are written first).
    // o.gap: how far right of the point a name with o.right starts (beside a mark, not on it). o.beside: a marker's
    // name, always written, beside its mark wherever it covers nothing already placed (besideMark). o.late: written
    // after all the others, so it lies on top (the markers' names: their room is taken first, their ink put down last).
    const say = (text, x, y, o) => {
      o = o || {};
      if (!text) return false;
      const font = (o.bold ? '600 ' : '') + (o.size || 12) + 'px "Segoe UI","Studio Text",system-ui,sans-serif';   // "Studio Text": studio.css's look-alike where Segoe UI is missing
      g.font = font;
      const icon = o.lock ? 12 : 0, tw = g.measureText(text).width + icon, th = (o.size || 12) + 3;
      const [left, top] = o.beside ? besideMark(placed, x, y, tw, th, o.gap || 8, w, h) : [o.right ? x + (o.gap || 8) : x - tw / 2, y - th / 2];
      const box = [left - 2, top - 1, left + tw + 2, top + th + 1], mid = top + th / 2;
      if (box[2] < 0 || box[0] > w || box[3] < 0 || box[1] > h) return false;
      if (!o.force && !o.beside && placed.some(p => box[0] < p[2] && box[2] > p[0] && box[1] < p[3] && box[3] > p[1])) return false;
      placed.push(box);
      const write = () => {
        g.font = font;
        if (o.lock) padlock(left, mid - 6, o.color);
        g.lineWidth = 3;
        g.strokeStyle = 'rgba(6,8,12,.85)';   // a dark edge, so names read on bright cubes too
        g.strokeText(text, left + icon, mid);
        g.fillStyle = o.color || css(ink.text);
        g.fillText(text, left + icon, mid);
      };
      if (o.late) late.push(write); else write();
      return true;
    };
    const padlock = (x, y, colour) => {
      g.strokeStyle = g.fillStyle = colour;
      g.lineWidth = 1.5;
      g.beginPath(); g.arc(x + 4.5, y + 4.5, 2.8, Math.PI, 0); g.stroke();
      g.fillRect(x + 0.5, y + 4.5, 8, 6.5);
    };
    const ring = (hit, alpha) => {
      if (!hit || (hit.kind !== 'star' && hit.kind !== 'nebula' && hit.kind !== 'find' && hit.kind !== 'stop' && hit.kind !== 'reach')) return;
      const p = at(hit.x, hit.y, hit.z);
      if (!p) return;
      g.beginPath(); g.arc(p[0], p[1], hit.kind === 'find' ? 9 : 7, 0, 2 * Math.PI);
      g.lineWidth = 1.5; g.strokeStyle = 'rgba(255,255,255,' + alpha + ')'; g.stroke();
    };

    // Within reach first, under everything else on this canvas: the sphere of the range as seen (in 3D the outline of a
    // ball is a circle a little bigger than its radius at that distance; none while the eye is inside it, where the
    // three circles round it show it), its range written over its top edge, then a dot for each system on the screen.
    const rc = view.markers !== false ? markers.reach : null;
    if (rc) {
      const c = at(rc.x, rc.y, rc.z);
      let disc = null;
      if (c) {
        const far = len3(sub3([rc.x, rc.y, rc.z], v.eye));
        const r = v.flat ? rc.r * pxPerLy(v, 0, h) : far > rc.r * 1.05 ? v.ty * h / 2 * rc.r / Math.sqrt(far * far - rc.r * rc.r) : 0;
        if (r > 0) disc = { p: c, r };
      }
      const dots = [];
      for (const m of rc.systems) {
        const p = at(m.x, m.y, m.z);
        if (!p || p[0] < -6 || p[1] < -6 || p[0] > w + 6 || p[1] > h + 6) continue;
        dots.push(Object.assign({ p }, reachInk(m.state, m.must)));
      }
      paintReach(g, disc, dots, ink.accent);
      if (disc && disc.r > 24 && rc.label) say(rc.label, disc.p[0], disc.p[1] - disc.r - 9, { size: 11, color: css(ink.accent) });
    }

    // The focus: a small faint cross, so the player sees what the camera turns round.
    const f = cam && at(cam.t[0], cam.t[1], cam.t[2]);
    if (f) {
      const arm = (x0, y0, x1, y1) => { g.moveTo(f[0] + x0, f[1] + y0); g.lineTo(f[0] + x1, f[1] + y1); };
      g.beginPath();
      arm(-8, 0, -3, 0); arm(3, 0, 8, 0); arm(0, -8, 0, -3); arm(0, 3, 0, 8);
      g.lineWidth = 3; g.strokeStyle = 'rgba(6,8,12,.45)'; g.stroke();
      g.lineWidth = 1.2; g.strokeStyle = 'rgba(255,255,255,.45)'; g.stroke();
    }

    // The markers first, so their room is kept: You, the target and the route's end (their names always written),
    // then your finds. Every mark's room is taken before any of their names is placed, so no name lies on another
    // mark; then each of the three names goes beside its own mark where it covers neither a mark nor another name (a
    // guessed system is often in the cube next to you, a few pixels away). Their shapes are drawn over everything by
    // paintOverlay().
    markOps = [];
    if (view.markers !== false) {
      const me = you(), named = [];
      const pm = me && at(me.x, me.y, me.z);
      if (pm) {
        // seen: on screen, or near enough for its pulse to show; off it, nothing pulses (pulse()).
        const seen = pm[0] > -PULSE_REACH && pm[0] < w + PULSE_REACH && pm[1] > -PULSE_REACH && pm[1] < h + PULSE_REACH;
        markOps.push({ kind: 'you', p: pm, foot: at(me.x, plateY, me.z), seen });
        placed.push([pm[0] - 18, pm[1] - 18, pm[0] + 18, pm[1] + 18]);
        const name = me.system || me.name;
        named.push(['You' + (name ? ' · ' + name : ''), pm, { gap: 21, bold: true }]);
      }
      const tg = markers.target, end = routeEndShown();
      for (const [m, kind, word] of [[tg, 'target', 'Target'], [end, 'routeEnd', 'Route end']]) {
        const p = m && at(m.x, m.y, m.z);
        if (!p) continue;
        markOps.push({ kind, p });
        placed.push([p[0] - 14, p[1] - 14, p[0] + 14, p[1] + 14]);
        let text = word + (m.name ? ' · ' + m.name : '');
        if (kind === 'target' && sameSpot(markers.routeEnd, tg)) text += ' · route end';
        if (num(m.plusMinus) > 0) text += ' · position from the name ±' + whole(m.plusMinus) + ' ly';
        named.push([text, p, { gap: 17, color: css(ink.target) }]);
      }
      const finds = markers.finds, shown = [];
      finds.forEach((m, index) => {
        const p = at(m.x, m.y, m.z);
        if (!p) return;
        markOps.push({ kind: 'find', p, index });
        placed.push([p[0] - 7, p[1] - 7, p[0] + 7, p[1] + 7]);
        shown.push([p, m]);
      });
      for (const [text, p, o] of named) say(text, p[0], p[1], Object.assign({ beside: true, late: true }, o));
      for (const [p, m] of shown) say(m.name, p[0], p[1], { right: true, gap: 9, size: 11, late: true, color: css(ink.find) });
      // The route check's stops: a faint line from stop to stop, so the dots read as one route, and a small dot each,
      // in its state's colour, all under the marks above (drawMarkers). On this canvas, not the 3D one's one-pixel
      // lines: with a dark edge it reads over bright cubes and the dark background alike. A stretch passing behind
      // the eye is cut there. No name and no room kept: a route can have hundreds of stops, and the sectors' names
      // may run over the dots (which are drawn over them). The pointer over one says its name.
      const segs = [];
      for (const [a, b] of routeSegments()) { const q = lineOnScreen(v, a, b, w, h); if (q) segs.push(q); }
      if (segs.length) markOps.push({ kind: 'route', segs });
      markers.route.forEach((m, index) => {
        const p = at(m.x, m.y, m.z);
        if (p) markOps.push({ kind: 'stop', p, index, colour: stopInk(m.state), ring: m.state === 'not-recorded' });
      });
    }

    // The marked star (a search result), then what was clicked or pointed at.
    const mk = scene.marker;
    if (mk) {
      const p = at(mk.x, mk.y, mk.z);
      if (p) {
        g.beginPath();
        g.moveTo(p[0], p[1] - 7); g.lineTo(p[0] + 7, p[1]); g.lineTo(p[0], p[1] + 7); g.lineTo(p[0] - 7, p[1]); g.closePath();
        g.fillStyle = css(ink.accent); g.fill();
        g.lineWidth = 1.5; g.strokeStyle = '#06080c'; g.stroke();
        placed.push([p[0] - 8, p[1] - 8, p[0] + 8, p[1] + 8]);
        say(mk.name || mk.label, p[0], p[1], { right: true, force: true });
      }
    }
    ring(hover, 0.6);
    ring(selected, 1);

    // Nebulae on show, named above their middle at the top of their highest sphere, with a padlock where the game
    // asks for a permit. Only the NEBULA_NAMES nearest the focus among those on screen: a block can hold dozens,
    // and their names would bury everything else.
    const named = [];
    for (const i of nebulaeOnShow()) {
      const nb = scene.nebulae[i], spheres = spheresOf(nb);
      if (![nb.x, nb.y, nb.z].every(fin) || !spheres.length) continue;
      const p = at(+nb.x, Math.max(...spheres.map(s => s[1] + s[3])), +nb.z);
      if (p && p[0] >= 0 && p[0] <= w && p[1] >= 0 && p[1] <= h) named.push([cam ? len3(sub3([+nb.x, +nb.y, +nb.z], cam.t)) : 0, p, nb]);
    }
    named.sort((a, b) => a[0] - b[0]);
    for (const [, p, nb] of named.slice(0, NEBULA_NAMES)) say(nb.label || nb.name, p[0], p[1] - 8, { color: css(nb.permit ? ink.permit : ink.nebula), lock: !!nb.permit });
    // Counted cubes, with their number ("10,000+" when Spansh stopped counting).
    scene.counted.forEach((c, i) => {
      if ((countedOk[i] !== 1 && countedOk[i] !== 2) || c.label == null) return;   // not the ones the range hides
      const s = c.size / 2, p = at(c.x0 + s, c.y0 + s, c.z0 + s);
      if (p) say(String(c.label), p[0], p[1], { bold: true });
    });
    // The sectors of the shown layer, with their count once the box is big enough on screen. In the flat views the
    // sectors along the line of sight share one spot on screen (from the side a whole row): the name written there is
    // the opened sector's, else the one nearest the focus, so it is the row the player is looking at.
    const sectorOrder = scene.sectors.map((sec, s) => s);
    if (v.flat && cam) {
      const S = scene.sectorLy, rank = s => {
        const it = info[s];
        return !it ? Infinity : (it.open ? -1e9 : 0) + len3(sub3(it.corner.map(c => c + S / 2), cam.t));
      };
      const ranks = sectorOrder.map(rank);
      sectorOrder.sort((a, b) => ranks[a] - ranks[b]);
    }
    sectorOrder.forEach(s => {
      const sec = scene.sectors[s], it = info[s];
      if (!it || it.tier !== 1 || (!it.pick && !it.open) || !sec.name) return;
      const S = scene.sectorLy, p = at(it.corner[0] + S / 2, it.corner[1] + S / 2, it.corner[2] + S / 2);
      if (!p) return;
      const wide = S * pxPerLy(v, p[2], h);   // the box's size on screen, in pixels
      if (say(sec.name, p[0], p[1], { bold: it.open }) && wide > 120 && fin(sec.known)) say(whole(sec.known), p[0], p[1] + 15, { size: 11, color: 'rgba(226,232,240,.75)' });
    });
    // The opened area's cubes, once each cube is big enough on screen to hold its number. Nearest first: from
    // above the cubes stand eight deep, and the number to read is the one of the cube in front.
    if (area && scene.area.cubes) {
      const c = area.cell, n = area.n, todo = [];
      for (let idx = 0; idx < area.slot.length; idx++) {
        if (area.state[idx] !== 1) continue;
        const cube = scene.area.cubes[area.slot[idx] - 1];
        if (cube.label == null) continue;
        const i = idx % n, j = Math.floor(idx / n) % n, k = Math.floor(idx / (n * n));
        const p = at(area.corner[0] + (i + 0.5) * c, area.corner[1] + (j + 0.5) * c, area.corner[2] + (k + 0.5) * c);
        if (p && c * pxPerLy(v, p[2], h) >= 40) todo.push([p, cube.label]);
      }
      todo.sort((a, b) => a[0][2] - b[0][2]);
      for (const [p, text] of todo.slice(0, 400)) say(String(text), p[0], p[1], { size: 11 });
    }

    for (const write of late) write();

    // The highlight: the chosen sector or cube's twelve edges over everything, the cubes in front of it and the
    // names included, thick and with a dark edge, so it is found at once among hundreds of faint outlines.
    const hb = boxOf(selected);
    if (hb) {
      const [x0, y0, z0, s] = hb, x1 = x0 + s, y1 = y0 + s, z1 = z0 + s, segs = [];
      const edge = (a, b) => { const q = lineOnScreen(v, a, b, w, h); if (q) segs.push(q); };
      for (const [y, z] of [[y0, z0], [y1, z0], [y0, z1], [y1, z1]]) edge([x0, y, z], [x1, y, z]);
      for (const [x, z] of [[x0, z0], [x1, z0], [x0, z1], [x1, z1]]) edge([x, y0, z], [x, y1, z]);
      for (const [x, y] of [[x0, y0], [x1, y0], [x0, y1], [x1, y1]]) edge([x, y, z0], [x, y, z1]);
      if (segs.length) {
        g.beginPath();
        for (const [ax, ay, bx, by] of segs) { g.moveTo(ax, ay); g.lineTo(bx, by); }
        g.lineCap = 'round';
        g.lineWidth = 4; g.strokeStyle = 'rgba(6,8,12,.75)'; g.stroke();
        g.lineWidth = 1.8; g.strokeStyle = 'rgba(255,255,255,.95)'; g.stroke();
        g.lineCap = 'butt';
      }
    }
    paintOverlay();
    if (!calm && !pulseTimer && markOps.some(m => m.kind === 'you' && m.seen)) pulseTimer = setTimeout(pulse, PULSE_MS);
  }

  // The names as last written, with the markers over them; the markers' pulse (the You mark's ring, how far through
  // its pulse it is) from the time. around: a screen point [x, y]: only the square round it that the pulse reaches is
  // painted again, the rest of the overlay is as it was.
  function paintOverlay(around) {
    if (!octx || !labelCache) return;
    octx.save();
    octx.setTransform(1, 0, 0, 1, 0, 0);
    if (around) {
      const r = PULSE_REACH * ratio;
      const x0 = Math.max(0, Math.floor(around[0] * ratio - r)), y0 = Math.max(0, Math.floor(around[1] * ratio - r));
      const x1 = Math.min(overlay.width, Math.ceil(around[0] * ratio + r)), y1 = Math.min(overlay.height, Math.ceil(around[1] * ratio + r));
      if (x1 <= x0 || y1 <= y0) { octx.restore(); return; }
      octx.beginPath();
      octx.rect(x0, y0, x1 - x0, y1 - y0);
      octx.clip();
      octx.clearRect(x0, y0, x1 - x0, y1 - y0);
      octx.drawImage(labelCache, x0, y0, x1 - x0, y1 - y0, x0, y0, x1 - x0, y1 - y0);
    } else {
      octx.clearRect(0, 0, overlay.width, overlay.height);
      octx.drawImage(labelCache, 0, 0);
    }
    octx.setTransform(ratio, 0, 0, ratio, 0, 0);
    drawMarkers(octx, calm ? 0 : (performance.now() % PULSE_PERIOD) / PULSE_PERIOD);
    octx.restore();
  }
  // The You mark's pulse: the small square round the mark redrawn about 14 times a second, while the mark is on
  // screen and anyone can see it. It stops when the page hides, the map loses its size or is scrolled out of view, or
  // You is off screen (a flat view zoomed on another area, You turned away from); the next frame starts it again.
  function pulse() {
    pulseTimer = 0;
    const me = markOps.find(m => m.kind === 'you' && m.seen);
    if (disposed || lost || calm || document.hidden || !inView || !canvas.clientWidth || !me) return;
    paintOverlay(me.p);
    pulseTimer = setTimeout(pulse, PULSE_MS);
  }

  // The markers as last placed, drawn on top of everything (paintMarks).
  function drawMarkers(g, phase) { paintMarks(g, markOps, phase, ink, calm); }

  // ---- what is under the pointer
  // Star dots and nebulae are found on screen (within 8 pixels); cubes and sectors by walking the line of sight
  // through their grids. When several are on the line, the first group that has one wins, and within a group the
  // nearest:
  //   0 filled cubes (counted, or of the opened area);
  //   1 the opened area's other cubes (empty, not counted yet, or hidden by the range) and counted cubes with no fill;
  //   2 the opened sector itself;
  //   3 the other sectors of the shown layer;
  //   4 everything else.
  // So the cube grid the player is working in is never taken by the see-through sector in front of it (from the
  // camera's usual slant, the next sector south covers the near half of an opened one). Among sectors in the flat
  // views, not the nearest but the one at the focus wins, whose name is the one written there (sectorRank).
  // That is what one click (and the pointer) takes. Clicking the same spot again goes on along the line (spec v3):
  // rayRing() puts everything on it in the order the clicks go through it.
  function pickAt(px, py) {
    const [w, h] = sizeOf();
    if (!cam || !w || !h) return null;
    ensureBuilt();
    const v = view3(cam, w / h, base);
    const first = onScreenAt(v, cam, px, py, w, h);
    if (first && first.kind !== 'nebula') return first;
    const { o, d } = rayAt(v, px, py, w, h), win = firstOf(alongRay(v, o, d));
    if (first && !givesWay(first, win)) return first;
    return win ? Object.assign(win.make(), { t: win.t, screen: [px, py] }) : first;
  }
  // A nebula found on the screen gives way to the cubes the player is working in (the owner's "it selects the one
  // outside"). With Nebulae on, dashed spheres cover much of the map, so a click that only touches the edge of one
  // while its line passes through a cube of the opened area or a counted cube (groups 0 and 1) takes that cube. With
  // one level of cubes shown, only that level is meant to be clicked, so there even a nebula's middle gives way.
  // Clicking the same spot again reaches the nebula after everything else on the line. Star dots and finds never
  // give way: they are small, and the pointer is right on them.
  const givesWay = (first, win) => !!first && first.kind === 'nebula' && !!win && win.tier <= 1 && (!!first.edge || (!!area && area.level >= 0));

  // What a click at a screen point goes through, in order (hitsAt(), and clicking the same spot again): first what one
  // click there chooses, then the cubes and sectors farther along the line of sight, nearest first, then round to
  // those in front of the first. A star dot, find or nebula under the pointer comes first of all, and after it what a
  // click would have chosen without it (the cube the player is working in, not the sector nearest the eye), and so on.
  // Each made into a pick result with along { at, of } and t (ly along the line). c: the camera to look with; clicking
  // again keeps the list the first click made (release()), so it walks that line as it was.
  function rayRing(c, px, py, w, h) {
    if (!c || !w || !h) return [];
    ensureBuilt();
    const v = view3(c, w / h, base), screen = [px, py];
    const first = onScreenAt(v, c, px, py, w, h);
    const { o, d } = rayAt(v, px, py, w, h), found = alongRay(v, o, d);
    // Along the line. Where two start at the same place (an opened sector and its first cube) the bigger first, so
    // going on from a sector leads into its cubes. (Places along a line far behind the flat views' eye are some
    // 100,000 ly out: "the same place" is judged to a millionth of that.)
    found.sort((a, b) => Math.abs(a.t - b.t) > 1e-6 * Math.max(1, Math.abs(a.t)) ? a.t - b.t : b.size - a.size || a.tier - b.tier);
    const win = firstOf(found);
    if (!first && !win) return [];
    const k = win ? found.indexOf(win) : 0, ring = found.slice(k).concat(found.slice(0, k));
    const out = ring.map(x => Object.assign(x.make(), { t: x.t, screen }));
    // What is found on the screen goes first; a nebula that gives way to the cubes (givesWay), last.
    if (first) {
      Object.assign(first, { t: dot3(sub3([first.x, first.y, first.z], o), d) });
      if (givesWay(first, win)) out.push(first); else out.unshift(first);
    }
    out.forEach((hit, k) => { hit.along = { at: k, of: out.length }; });
    return out;
  }

  // Star dots, finds, the route check's stops and nebulae: found on the screen, within a few pixels of the point,
  // before any line of sight is walked. c: the camera (in the flat views its distance stands for how near the stars are).
  function onScreenAt(v, c, px, py, w, h) {
    const screen = [px, py];
    // Star dots near enough to the eye to be drawn (in the flat views: zoomed in far enough), and not on a level of
    // cubes left out (sliceHides): those are not drawn.
    let best = null, bestD = PICK_PX;
    scene.stars.forEach((s, i) => {
      if (!fin(s.x) || !fin(s.y) || !fin(s.z) || (v.flat ? c.dist : len3(sub3([s.x, s.y, s.z], v.eye))) > starsNear * 1.4 || sliceHides(s.x, s.y, s.z, 0)) return;
      const p = toScreen(v, [s.x, s.y, s.z], w, h);
      const d = p ? Math.hypot(p[0] - px, p[1] - py) : Infinity;
      if (d <= bestD) { bestD = d; best = { kind: 'star', index: i, item: s, x: +s.x, y: +s.y, z: +s.z, screen }; }
    });
    if (best) return best;
    // Your finds: the green diamonds, a little bigger than a star dot.
    let findD = PICK_PX + 2;
    if (view.markers !== false) markers.finds.forEach((m, i) => {
      const p = toScreen(v, [m.x, m.y, m.z], w, h);
      const d = p ? Math.hypot(p[0] - px, p[1] - py) : Infinity;
      if (d <= findD) { findD = d; best = { kind: 'find', index: i, item: m, x: +m.x, y: +m.y, z: +m.z, screen }; }
    });
    if (best) return best;
    // The route check's stops: their small dots, found only nearer than a star dot (STOP_PX), since a route can cross
    // much of the map and its dots should not take the clicks meant for the cubes round them. A stop that is one of
    // your finds too is found as the find, above.
    let stopD = STOP_PX;
    if (view.markers !== false) markers.route.forEach((m, i) => {
      const p = toScreen(v, [m.x, m.y, m.z], w, h);
      const d = p ? Math.hypot(p[0] - px, p[1] - py) : Infinity;
      if (d <= stopD) { stopD = d; best = { kind: 'stop', index: i, item: m, x: +m.x, y: +m.y, z: +m.z, screen }; }
    });
    if (best) return best;
    // Within reach's dots: as near as a stop's (there can be thousands, round the cubes the player works in).
    let reachD = STOP_PX;
    if (view.markers !== false && markers.reach) markers.reach.systems.forEach((m, i) => {
      const p = toScreen(v, [m.x, m.y, m.z], w, h);
      const d = p ? Math.hypot(p[0] - px, p[1] - py) : Infinity;
      if (d <= reachD) { reachD = d; best = { kind: 'reach', index: i, item: m, x: +m.x, y: +m.y, z: +m.z, screen }; }
    });
    if (best) return best;
    // A nebula's middle, or the middle or edge of one of its spheres as drawn: only the nebulae on show. edge: true
    // when the pointer is nearest the edge of a sphere, not a middle (such a hit gives way to cubes: givesWay).
    const onShow = new Set(nebulaeOnShow());
    scene.nebulae.forEach((nb, i) => {
      if (!onShow.has(i)) return;
      const spheres = spheresOf(nb);
      if (![nb.x, nb.y, nb.z].every(fin) || !spheres.length) return;
      let score = Infinity, edge = false;
      const p = toScreen(v, [+nb.x, +nb.y, +nb.z], w, h);
      if (p) score = Math.hypot(p[0] - px, p[1] - py);
      for (const [x, y, z, r] of spheres) {
        const q = toScreen(v, [x, y, z], w, h);
        if (!q) continue;
        const d = Math.hypot(q[0] - px, q[1] - py), rim = Math.abs(d - r * pxPerLy(v, q[2], h));
        if (d < score) { score = d; edge = false; }
        if (rim < score) { score = rim; edge = true; }
      }
      if (score <= bestD) { bestD = score; best = { kind: 'nebula', index: i, item: nb, x: +nb.x, y: +nb.y, z: +nb.z, r: +nb.r, spheres, screen, edge }; }
    });
    return best;
  }

  // Every sector and cube the line of sight o + t d passes through that a click can choose, in no order, as
  // { tier (the groups above), t (where the line enters it, ly), rank (what a click ranks it by within its group),
  // size, make (its pick result) }. Nothing is left out for lying behind something else: going deeper needs them all.
  // One place, one entry: a counted cube where the opened area has its own cube of that size is kept once, as the
  // better group's (the counted one when filled: it has the count), the counted one when both are as good.
  function alongRay(v, o, d) {
    const found = [], at = new Map();
    const offer = (tier, t, rank, x0, y0, z0, size, make) => {
      const key = [x0, y0, z0, size].map(Math.round).join();
      const was = at.get(key);
      if (was) { if (tier < was.tier) Object.assign(was, { tier, t, rank, make }); return; }
      const c = { tier, t, rank, size, make };
      at.set(key, c);
      found.push(c);
    };
    scene.counted.forEach((c, i) => {
      if (!countedOk[i]) return;
      const hit = boxHit(o, d, [c.x0, c.y0, c.z0], [c.x0 + c.size, c.y0 + c.size, c.z0 + c.size]);
      if (hit) { const t = Math.max(hit[0], 0); offer(countedOk[i] === 1 ? 0 : 1, t, t, c.x0, c.y0, c.z0, +c.size, () => boxResult('counted', i, c, c.x0, c.y0, c.z0, +c.size)); }
    });
    if (area) {
      const n = area.n, cell = area.cell;
      walk(o, d, area.corner, cell, [0, 0, 0], [n - 1, n - 1, n - 1], (i, j, k, t) => {
        const idx = i + n * (j + n * k), st = area.state[idx];
        if (st === 3) return false;   // on a level of cubes not shown: not there at all
        const x0 = area.corner[0] + i * cell, y0 = area.corner[1] + j * cell, z0 = area.corner[2] + k * cell;
        const make = () => {
          const q = area.slot[idx] - 1, cube = q >= 0 ? scene.area.cubes[q] : null;
          return Object.assign(boxResult('cube', q, cube, x0, y0, z0, cell), { i, j, k, empty: st === 0 });
        };
        if (st === 1) offer(0, t, t, x0, y0, z0, cell, make);
        else if (view.showEmpty || area.grid) offer(1, t, t, x0, y0, z0, cell, make);   // an outline: empty, not counted yet, or hidden by the range
        return false;
      });
    }
    if (block) {
      const S = scene.sectorLy;
      walk(o, d, scene.origin, S, block.lo, block.hi, (ix, iy, iz, t) => {
        const s = sectorAt.get(ix + ',' + iy + ',' + iz);
        if (s === undefined || !info[s].pick) return false;
        const it = info[s], sec = scene.sectors[s];
        offer(it.open ? 2 : it.tier === 1 ? 3 : 4, t, sectorRank(v, t, S), it.corner[0], it.corner[1], it.corner[2], S,
          () => Object.assign(boxResult('sector', s, sec, it.corner[0], it.corner[1], it.corner[2], S), { ix, iy, iz, open: it.open, empty: it.empty }));
        return false;
      });
    }
    return found;
  }
  // What one click chooses of them: the first group that has any, and in it the best ranked (the nearest; among
  // sectors in the flat views the one at the focus).
  const firstOf = found => found.reduce((best, c) => !best || c.tier < best.tier || (c.tier === best.tier && c.rank < best.rank) ? c : best, null);
  const boxResult = (kind, index, item, x0, y0, z0, size) => ({ kind, index, item, x0, y0, z0, size, x: x0 + size / 2, y: y0 + size / 2, z: z0 + size / 2 });
  const sameHit = (a, b) => a === b || (!!a && !!b && a.kind === b.kind && a.index === b.index && a.i === b.i && a.j === b.j && a.k === b.k && a.ix === b.ix && a.iy === b.iy && a.iz === b.iz);

  // ---- the camera
  const snapshot = () => ({ t: cam.t.slice(), dist: cam.dist, yaw: cam.yaw, pitch: cam.pitch, mode: cam.mode });
  const history = [];
  let place = -1, settleTimer = 0;

  function sameView(a, b) {
    return !!a && !!b && (a.mode || '3d') === (b.mode || '3d') && len3(sub3(a.t, b.t)) < 1 + 0.01 * a.dist && Math.abs(a.dist - b.dist) < 0.01 * a.dist
      && Math.abs(a.yaw - b.yaw) < 0.01 && Math.abs(a.pitch - b.pitch) < 0.01;
  }
  // The view (3D, Top, Side) changes at once, not in a flight: there is no halfway between perspective and none.
  // The page hears of it straight away, for its View switch.
  function setModeNow(m) {
    if (!cam || cam.mode === m || !MODES.includes(m)) return;
    cam.mode = m;
    if (modeCb) modeCb(m);
  }
  // Remembers where the camera is for Back and Forward (like a browser's history: a new place drops the "forward" ones).
  function remember() {
    if (!cam) return;
    const s = snapshot();
    if (sameView(history[place], s)) return;
    history.splice(place + 1);
    history.push(s);
    if (history.length > 60) history.shift();
    place = history.length - 1;
  }
  // After the camera has come to rest: remember it (unless Back or Forward put it there) and tell the page. Still
  // moving when the time comes (a flight, a drag, a key held down): nothing yet, the end of that move calls this again.
  function settled(keep) {
    clearTimeout(settleTimer);
    settleTimer = setTimeout(() => {
      if (fly || drag || moving()) return;
      if (keep !== false) remember();
      if (cameraCb && cam) cameraCb(snapshot());
    }, 350);
  }

  // How far back the camera has to be to see a sphere of radius r whole.
  const frameDist = r => clamp(r / Math.sin(FOV / 2) * 1.1, MIN_DIST * 2, MAX_DIST);

  function overviewGoal() {
    let lo = null, hi = null;
    const grow = (a, b) => { lo = lo ? lo.map((v, k) => Math.min(v, a[k])) : a.slice(); hi = hi ? hi.map((v, k) => Math.max(v, b[k])) : b.slice(); };
    for (const sec of scene.sectors) if ([sec.ix, sec.iy, sec.iz].every(fin)) { const c = sectorCorner(sec); grow(c, c.map(v => v + scene.sectorLy)); }
    if (!lo && scene.area) { const a = scene.area, s = a.n * a.cubeLy; grow(a.corner, a.corner.map(v => v + s)); }
    if (!lo && scene.player) { const p = scene.player; grow([p.x - 640, p.y - 640, p.z - 640], [p.x + 640, p.y + 640, p.z + 640]); }
    if (!lo) return { t: [0, 0, 0], dist: 6000, yaw: 0, pitch: 0.75 };
    const r = len3(sub3(hi, lo)) / 2;
    return { t: lo.map((v, k) => (v + hi[k]) / 2), dist: frameDist(r) * 0.8, yaw: 0, pitch: 0.75 };
  }

  // What flyTo() accepts: 'overview'; 'me'; 'target'; 'routeEnd'; [x,y,z]; { x, y, z } with any of r (frame a
  // sphere), dist, yaw, pitch, mode, plusMinus (frame the cube of doubt); a box { x0, y0, z0, size } or
  // { corner:[x,y,z], size }; a sector { ix, iy, iz }; a marker { x, y, z, cube }; or anything pick() returned (a
  // nebula of several spheres is framed whole). instant: true jumps there without the flight.
  function aim(target) {
    if (target === 'overview') return overviewGoal();
    if (target === 'me' || target === 'player' || target === 'you') { const me = you(); return me ? aim({ x: me.x, y: me.y, z: me.z, r: 1000 }) : null; }
    if (target === 'target' || target === 'routeEnd') return markers[target] ? aim(Object.assign({}, markers[target], { kind: 'marker' })) : null;
    if (Array.isArray(target)) target = { x: target[0], y: target[1], z: target[2] };
    if (!target || typeof target !== 'object') return null;
    let t = null, r = num(target.r);
    if ([target.x0, target.y0, target.z0].every(fin) && target.size > 0) {
      const s = +target.size;
      t = [target.x0 + s / 2, target.y0 + s / 2, target.z0 + s / 2];
      if (!(r > 0)) r = s * 0.87;
    } else if (Array.isArray(target.corner) && target.size > 0) {
      const s = +target.size;
      t = target.corner.map(v => +v + s / 2);
      if (!(r > 0)) r = s * 0.87;
    } else if (![target.x, target.y, target.z].every(fin) && [target.ix, target.iy, target.iz].every(fin)) {
      const S = scene.sectorLy;
      t = sectorCorner(target).map(v => v + S / 2);
      if (!(r > 0)) r = S * 0.87;
    } else if ([target.x, target.y, target.z].every(fin)) {
      t = [+target.x, +target.y, +target.z];
      if (!(r > 0) && target.cube && target.cube.size > 0) r = +target.cube.size;
      if (!(r > 0) && num(target.plusMinus) > 0) r = Math.max(2 * target.plusMinus, 80);
      if (!(r > 0) && (target.kind === 'star' || target.kind === 'find' || target.kind === 'stop' || target.kind === 'marker')) r = 120;
      if (target.kind === 'nebula' && spheresOf(target).length > 1) r = Math.max(r > 0 ? r : 0, reachOf(target));
    }
    if (!t) return null;
    const dist = num(target.dist) > 0 ? +target.dist : r > 0 ? frameDist(r) : Math.min(cam ? cam.dist : 2000, 900);
    return { t, dist: clamp(dist, MIN_DIST, MAX_DIST), yaw: num(target.yaw), pitch: num(target.pitch), mode: target.mode, instant: !!target.instant };
  }

  // Eases the camera to a goal in about 0.9 s (ms: another time); on a long hop it rises on the way, so the player
  // sees where it goes. A goal with another view mode (Back to a Top view) switches first, then flies.
  function travel(goal, keep, instant, ms) {
    const mode = MODES.includes(goal.mode) ? goal.mode : null;
    if (!cam) {
      cam = { t: goal.t.slice(), dist: goal.dist, yaw: fin(goal.yaw) ? goal.yaw : 0, pitch: fin(goal.pitch) ? goal.pitch : 0.75, mode: mode || startMode };
      remember();
      redraw();
      return;
    }
    // So Back returns to where the flight started. A flight that takes over from one under way (a double-click, Me
    // pressed mid-flight) remembers nothing: the one before remembered where it started, and the camera never stopped
    // where it is now.
    if (keep !== false && !fly) remember();
    if (mode) setModeNow(mode);
    const from = snapshot();
    const to = { t: goal.t.slice(), dist: goal.dist, yaw: fin(goal.yaw) ? goal.yaw : from.yaw, pitch: fin(goal.pitch) ? clamp(goal.pitch, -Math.PI / 2, Math.PI / 2) : from.pitch, mode: cam.mode };
    let turn = (to.yaw - from.yaw) % (2 * Math.PI);   // the short way round
    if (turn > Math.PI) turn -= 2 * Math.PI;
    if (turn < -Math.PI) turn += 2 * Math.PI;
    to.yaw = from.yaw + turn;
    if (instant || goal.instant) { fly = null; Object.assign(cam, to); settled(keep); redraw(); return; }
    const hop = len3(sub3(from.t, to.t)), far = Math.max(from.dist, to.dist);
    fly = { start: performance.now(), from, to, keep, ms: ms > 0 ? ms : FLY_MS, rise: hop > 1.5 * far ? Math.min(hop * 0.45, MAX_DIST / 3) : 0 };
    schedule();
  }

  function flyTo(target) {
    const goal = aim(target);
    if (!goal) return false;
    travel(goal, true);
    return true;
  }

  // A click's flight: the focus to what was chosen in about 0.6 s, the angle kept, at a distance that frames it.
  // A camera already about that far stays as far (no zooming in and out for nothing). closer (a double-click): well
  // inside that, and nearer than the camera was before the first click (a number: that distance).
  function focusOn(target, closer) {
    const goal = aim(target);
    if (!goal) return false;
    goal.yaw = goal.pitch = NaN;
    goal.mode = null;
    const asked = target && typeof target === 'object' && num(target.dist) > 0;
    if (closer) goal.dist = clamp(Math.min(goal.dist * 0.4, fin(closer) ? closer * 0.6 : Infinity), MIN_DIST, MAX_DIST);
    else if (cam && !asked && cam.dist >= goal.dist * 0.6 && cam.dist <= goal.dist * 1.6) goal.dist = cam.dist;
    travel(goal, true, false, CLICK_FLY_MS);
    return true;
  }

  // One step of the on-screen pad (see the top of this file). Instant, so quick presses add up exactly.
  const TURNS = { left: [-1, 0], right: [1, 0], up: [0, 1], down: [0, -1] };
  const MOVES = { forward: [0, 0, 1], back: [0, 0, -1], left: [-1, 0, 0], right: [1, 0, 0], up: [0, 1, 0], down: [0, -1, 0] };
  function nudge(o) {
    if (!cam || !o || typeof o !== 'object') return false;
    const fast = o.fast ? 2 : 1;
    if (o.level) {   // back to 3D, eased to a gentle look down, the focus and the turn kept
      travel({ t: cam.t, dist: cam.dist, yaw: cam.yaw, pitch: LEVEL_PITCH, mode: '3d' }, true, false, CLICK_FLY_MS);
      return true;
    }
    const list = (v, names, n) => typeof v === 'string' ? (Object.prototype.hasOwnProperty.call(names, v) ? names[v] : null)
      : Array.isArray(v) && v.length >= n && v.slice(0, n).every(fin) ? v : null;
    const turn = list(o.turn, TURNS, 2);
    let move = list(o.move, MOVES, 3), did = false, layered = false;
    if (move && cam.mode === 'top' && move[1]) {   // from above, up and down step the layers (they would show nothing)
      if (layerCb) { layerCb(move[1] > 0 ? 1 : -1); layered = true; }
      move = move[0] || move[2] ? [move[0], 0, move[2]] : null;
    }
    if (turn && cam.mode === '3d') { turnCam(cam, -turn[0] * TURN_STEP * fast, -turn[1] * TURN_STEP * fast); did = true; }
    if (move) { const by = Math.max(cam.dist, 100) * 0.2 * fast; moveCam(cam, move[0] * by, move[1] * by, move[2] * by, view.holdHeight); did = true; }
    if (fin(o.zoom) && o.zoom) { cam.dist = clamp(cam.dist * Math.pow(0.8, o.zoom * fast), MIN_DIST, MAX_DIST); did = true; }
    if (did) { fly = null; settled(true); redraw(); }
    return did || layered;
  }

  // Moves the camera on by one animation step: the flight, and keys (or pad buttons) held down. True while
  // something still moves.
  let lastStep = 0;
  function step(now) {
    const dt = Math.min(0.1, lastStep ? (now - lastStep) / 1000 : 0);
    lastStep = now;
    let going = false;
    if (fly && cam) {
      const t = Math.min(1, (now - fly.start) / fly.ms), s = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
      for (let k = 0; k < 3; k++) cam.t[k] = fly.from.t[k] + (fly.to.t[k] - fly.from.t[k]) * s;
      cam.dist = fly.from.dist + (fly.to.dist - fly.from.dist) * s + fly.rise * Math.sin(Math.PI * s);
      cam.yaw = fly.from.yaw + (fly.to.yaw - fly.from.yaw) * s;
      cam.pitch = fly.from.pitch + (fly.to.pitch - fly.from.pitch) * s;
      if (t >= 1) { const keep = fly.keep; fly = null; settled(keep); } else going = true;
    }
    if (cam && moving()) {
      // W/S forward and back, A/D sideways, R/F (or E/Q) up and down in the galaxy; Shift twice as fast. With "hold
      // height" forward stays level, so W never sinks the camera into the layer below. The pad's buttons count as the
      // same keys ('pad:' in front); its turn buttons held down turn (they alone: the arrow keys move the highlight,
      // and the flat views cannot turn).
      const on = code => held.has(code) || held.has('pad:' + code);
      const along = (plus, minus) => (on(plus) ? 1 : 0) - (on(minus) ? 1 : 0);
      const fast = held.has('Shift') ? 2 : 1, speed = Math.max(cam.dist, 100) * 0.8 * fast * dt;
      const right = along('KeyD', 'KeyA'), ahead = along('KeyW', 'KeyS');
      // From above, up and down run along the line of sight and show nothing: there they step the layers (keydown).
      const up = cam.mode === 'top' ? 0 : (on('KeyR') || on('KeyE') ? 1 : 0) - (on('KeyF') || on('KeyQ') ? 1 : 0);
      if (cam.mode === '3d') {
        const by = TURN_RATE * fast * dt;
        turnCam(cam, along('TurnLeft', 'TurnRight') * by, along('TurnDown', 'TurnUp') * by);
      }
      if (right || up || ahead) moveCam(cam, right * speed, up * speed, ahead * speed, view.holdHeight);
      const zoom = along('ZoomOut', 'ZoomIn');
      if (zoom) cam.dist = clamp(cam.dist * Math.exp(zoom * 1.2 * fast * dt), MIN_DIST, MAX_DIST);
      going = true;
    }
    return going;
  }

  // ---- drawing on demand
  function schedule() { if (!raf && !disposed) raf = requestAnimationFrame(tick); }
  function tick(now) {
    raf = 0;
    const moving = step(now);
    const drawn = draw();
    if (!moving) lastStep = 0;
    if ((moving || measuring.length) && drawn) schedule();
  }
  function redraw() { schedule(); }

  // ---- the pointer and the keys
  let drag = null, lastClick = null;
  // The last click's line of sight, for going deeper along it: { x, y (its spot), cam (the camera then), size (the
  // map's then), hit (what it chose), goal (where its flight takes the camera), ring (what lies on that line, in order) }.
  let deeper = null;
  const held = new Set();   // keys held down by their code ('KeyW'), 'Shift', and the pad's ('pad:KeyW', 'pad:TurnLeft')
  const moving = () => { for (const k of held) if (k !== 'Shift') return true; return false; };
  const listeners = [];
  const listen = (target, type, fn, o) => { target.addEventListener(type, fn, o); listeners.push([target, type, fn, o]); };
  const spot = e => { const b = canvas.getBoundingClientRect(); return { x: e.clientX - b.left, y: e.clientY - b.top, clientX: e.clientX, clientY: e.clientY, button: e.button, shiftKey: e.shiftKey }; };

  listen(canvas, 'pointerdown', e => {
    if (!cam) return;
    if (e.button === 1) e.preventDefault();   // the middle button slides the map, instead of the browser's own scrolling
    // A flight under way goes on until this turns out to be a drag (pointermove): a click on empty space leaves the
    // camera be, and the second click of a double-click takes over from the first one's flight where it is.
    // The flat views cannot turn, so there the left button slides too.
    drag = { x: e.clientX, y: e.clientY, button: e.button, moved: 0, pan: e.button === 2 || e.button === 1 || (e.button === 0 && (e.shiftKey || cam.mode !== '3d')) };
    try { canvas.setPointerCapture(e.pointerId); } catch (x) {}
    canvas.style.cursor = 'grabbing';
  });
  listen(canvas, 'pointermove', e => {
    if (!cam) return;
    if (!drag) {   // just pointing: say what is under the pointer
      const at = spot(e), hit = pickAt(at.x, at.y);
      if (!sameHit(hit, hover)) { hover = hit; needMarks = true; redraw(); }
      if (hoverCb) hoverCb(hit, at);
      return;
    }
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    drag.x = e.clientX; drag.y = e.clientY;
    drag.moved += Math.abs(dx) + Math.abs(dy);
    if (drag.moved < 3) return;   // a click with a shaky hand is still a click
    fly = null;   // a drag: the hand takes the camera from any flight
    if (!drag.pan) {   // left button: turn round the focus, which stays where it is
      turnCam(cam, -dx * 0.005, dy * 0.005);
    } else {   // right (or middle, or Shift+left, or any drag in a flat view): slide across the screen, so the place under the pointer follows it
      slideCam(cam, dx, dy, 2 * cam.dist * Math.tan(FOV / 2) / (canvas.clientHeight || 1));
    }
    if (hover) { hover = null; needMarks = true; if (hoverCb) hoverCb(null, spot(e)); }
    redraw();
  });
  const release = e => {
    if (!drag) return;
    const d = drag;
    drag = null;
    canvas.style.cursor = '';
    if (d.moved >= 3) { settled(true); return; }
    if (e.type !== 'pointerup' || d.button !== 0) return;
    const at = spot(e), now = clock(), last = lastClick;
    // The second click of a double-click: closer to what the first one chose. Not picked again: the first click's
    // flight has already moved the map under the pointer.
    if (last && now - last.time < DOUBLE_MS && Math.hypot(at.x - last.x, at.y - last.y) < DOUBLE_PX) {
      lastClick = null;
      if (last.hit) focusOn(last.hit, last.dist);
      return;
    }
    // A click: choose what is under the pointer, and fly there. The same spot clicked again goes one deeper (spec v3):
    // the next cube or sector along the line of sight as it was at the first click (that click's own flight has
    // moved the map under the pointer since, which is why the line is kept rather than looked up again), round to
    // the first after the last. Only while the camera is where that flight takes it and nothing else was chosen
    // meanwhile: a drag, the wheel, a key, a button or an arrow moving the highlight make the next click a fresh one.
    const dp = deeper;
    const again = !!dp && Math.hypot(at.x - dp.x, at.y - dp.y) <= DEEPER_PX && sameView(fly ? fly.to : snapshot(), dp.goal) && samePlace(selected, dp.hit);
    const look = again ? dp.cam : snapshot(), [w, h] = again ? dp.size : sizeOf(), x = again ? dp.x : at.x, y = again ? dp.y : at.y;
    // Clicking again goes on along the list the first click made: worked out anew, what lies on the line could change
    // under it (a nebula shown only near the point the camera turns round drops out once the camera has flown on).
    // When what can be clicked has changed since (setView, setScene: a level of cubes, "Show empty cubes"), the list
    // is gone and the line is worked out again from the first click's camera; and whatever on a kept list can no
    // longer be clicked is passed over all the same, so a click never reaches a cube on a level not drawn.
    let ring = again && dp.ring ? dp.ring : rayRing(look, x, y, w, h);
    if (again && dp.ring) {
      ensureBuilt();
      const still = ring.filter(clickable);
      if (still.length < ring.length) { ring = still; ring.forEach((hit, k) => { hit.along = { at: k, of: ring.length }; }); }
    }
    let k = 0;
    if (again) { const was = ring.findIndex(r => samePlace(r, selected)); k = was < 0 ? 0 : (was + 1) % ring.length; }
    const hit = ring[k] || null;
    lastClick = { time: now, x: at.x, y: at.y, hit, dist: cam.dist };
    if (!hit) return;   // empty space: nothing happens, and what was chosen stays chosen
    hit.screen = [at.x, at.y];
    selected = hit;
    needMarks = true;
    // A fresh click centres what it chose. Clicking again leaves the camera where the first click's flight took it:
    // flown to the centre each time, every choice along the line would be drawn at the same place and size, and the
    // player would see nothing happen but the card. Kept still, the outline steps back behind the cubes in front.
    // (C, a double-click or a fresh click centres.)
    if (!again) focusOn(hit);
    deeper = { x, y, cam: look, size: [w, h], hit, goal: fly ? fly.to : snapshot(), ring };
    redraw();
    if (pickCb) pickCb(hit, at);
  };
  // Whether a choice kept on a line of sight can still be clicked now: a cube on a level of cubes not shown (or no
  // longer outlined), a counted cube or a star dot left out, a sector no longer drawn cannot. (The second guard:
  // setView and setScene already forget the list kept when what can be clicked changes.)
  function clickable(hit) {
    if (!hit) return false;
    if (hit.kind === 'cube') {
      if (!area || !(Math.abs(hit.size - area.cell) < 1e-6) || ![hit.i, hit.j, hit.k].every(v => Number.isInteger(v) && v >= 0 && v < area.n)) return false;
      if ([hit.x0, hit.y0, hit.z0].some((v, a) => Math.abs(area.corner[a] + [hit.i, hit.j, hit.k][a] * area.cell - v) > 0.01)) return false;
      const st = area.state[hit.i + area.n * (hit.j + area.n * hit.k)];
      return st === 1 || (st !== 3 && (!!view.showEmpty || area.grid));
    }
    if (hit.kind === 'counted') return !!countedOk[hit.index];
    if (hit.kind === 'sector') { const s = sectorAt.get(hit.ix + ',' + hit.iy + ',' + hit.iz); return s !== undefined && !!info[s].pick; }
    if (hit.kind === 'star') return hit.index < scene.stars.length && !sliceHides(hit.x, hit.y, hit.z, 0);
    if (hit.kind === 'nebula') return view.nebulae !== 'off';
    if (hit.kind === 'find') return view.markers !== false;
    if (hit.kind === 'stop') return view.markers !== false && hit.index < markers.route.length;
    if (hit.kind === 'reach') return view.markers !== false && !!markers.reach && hit.index < markers.reach.systems.length;
    return true;
  }
  listen(canvas, 'pointerup', release);
  listen(canvas, 'pointercancel', release);
  listen(canvas, 'pointerleave', e => { if (!drag && hover) { hover = null; needMarks = true; redraw(); if (hoverCb) hoverCb(null, spot(e)); } });
  listen(canvas, 'contextmenu', e => e.preventDefault());
  // The wheel zooms towards the place under the pointer: that place stays under the pointer while everything grows.
  listen(canvas, 'wheel', e => {
    if (!cam) return;
    e.preventDefault();
    fly = null;
    const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 400 : 1;   // the wheel may count in lines or pages instead of pixels
    const want = clamp(cam.dist * Math.exp(e.deltaY * unit * 0.0015), MIN_DIST, MAX_DIST), k = want / cam.dist;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (w && h) {
      const at = spot(e), v = view3(cam, w / h, base), ray = rayAt(v, at.x, at.y, w, h);
      const along = v.back / Math.max(1e-6, dot3(ray.d, v.f));   // where the pointer's line crosses the plane of the point circled
      const p = [0, 1, 2].map(i => ray.o[i] + ray.d[i] * along);
      for (let i = 0; i < 3; i++) cam.t[i] = p[i] + (cam.t[i] - p[i]) * k;
    }
    cam.dist = want;
    settled(true);
    redraw();
  }, { passive: false });
  // Keys pressed while typing, or inside one of Studio's questions (a dialog over the page), are not the map's. A tick
  // box, a button or a slider is no typing: after "Hold height" or "Markers" is clicked, the map's keys still work.
  const typing = e => {
    const t = e.target;
    if (!t) return false;
    if (t.isContentEditable || /^(TEXTAREA|SELECT)$/.test(t.tagName || '')) return true;
    if (t.tagName === 'INPUT' && !NOT_TYPED.test(t.type || 'text')) return true;
    return !!(t.closest && t.closest('dialog,[role="dialog"],[role="alertdialog"]'));
  };
  const NOT_TYPED = /^(checkbox|radio|button|submit|reset|color|range|image|file)$/i;
  const LAYER_KEYS = { KeyR: 1, KeyE: 1, KeyF: -1, KeyQ: -1 };   // in the Top view: the layer above, the layer below
  // The arrow keys (and Page Up, Page Down) belong to whatever has the keyboard and uses them (a slider, radio
  // buttons, a list, a part of the page marked data-keeps-arrows): they move the highlight (the page, with ownsKey())
  // only when nothing else took them. A part marked with the keys it uses (the side strip: "ArrowUp ArrowDown", it
  // steps the layers; the list of names: up and down scroll it) keeps only those, and only while it has the keyboard
  // itself: a name or a button clicked inside it keeps none, so the arrows never go dead after a click in the list.
  const arrowsFree = e => {
    const t = e.target;
    if (!t || t === window || t === document || t === document.body || t === document.documentElement || t === canvas) return true;
    if (t.tagName === 'INPUT' && /^(range|radio)$/i.test(t.type || '')) return false;
    const keeper = t.closest ? t.closest('[data-keeps-arrows]') : null;
    if (keeper) {
      const keys = String(keeper.getAttribute('data-keeps-arrows') || '').trim();
      if (!keys) return false;   // marked with no keys: it keeps them all
      if (keeper === t) return !keys.split(/\s+/).includes(e.code);
    }
    return t.tagName !== 'CANVAS' && !(t.getAttribute && t.getAttribute('role'));
  };
  // A key press free for the map and its page (see the top of this file).
  const ownsKey = e => !!e && !!cam && !e.defaultPrevented && !typing(e) && !e.ctrlKey && !e.altKey && !e.metaKey && sizeOf()[0] > 0
    && (!PAGE_KEYS.includes(e.code) || arrowsFree(e));
  listen(window, 'keydown', e => {
    if (e.key === 'Shift') { held.add('Shift'); return; }
    if (!MOVE_KEYS.includes(e.code) || !ownsKey(e)) return;
    e.preventDefault();
    // From above, up and down would only move along the line of sight, which shows nothing: they step the layers, one a press.
    if (cam.mode === 'top' && LAYER_KEYS[e.code]) { if (!e.repeat && layerCb) layerCb(LAYER_KEYS[e.code]); return; }
    if (!held.has(e.code)) { held.add(e.code); fly = null; schedule(); }
  });
  listen(window, 'keyup', e => {
    if (e.key === 'Shift') { held.delete('Shift'); return; }
    if (held.delete(e.code) && !moving()) settled(true);
  });
  listen(window, 'blur', () => { pressed = false; padTimers.forEach(clearTimeout); padTimers.clear(); if (moving()) settled(true); held.clear(); });

  // The on-screen pad's buttons held down: one step at once (a click is one step), then on and on, as a key held.
  // (Its turn buttons have codes of their own: no key turns the map any more.)
  const PAD = {
    turnLeft: ['TurnLeft', { turn: 'left' }], turnRight: ['TurnRight', { turn: 'right' }],
    turnUp: ['TurnUp', { turn: 'up' }], turnDown: ['TurnDown', { turn: 'down' }],
    forward: ['KeyW', { move: 'forward' }], back: ['KeyS', { move: 'back' }], left: ['KeyA', { move: 'left' }], right: ['KeyD', { move: 'right' }],
    up: ['KeyR', { move: 'up' }], down: ['KeyF', { move: 'down' }], zoomIn: ['ZoomIn', { zoom: 1 }], zoomOut: ['ZoomOut', { zoom: -1 }],
  };
  const padTimers = new Map();
  let pressed = false;   // a pointer button is down somewhere on the page: only then can a pad button be held
  function hold(name, on) {
    if (name == null || name === 'all') { letGo(); return true; }
    const p = Object.prototype.hasOwnProperty.call(PAD, name) ? PAD[name] : null;
    if (!p || !cam) return false;
    // From above, Up and Down step the layers: one a press, never on and on (six layers go by in a moment).
    if (cam.mode === 'top' && (name === 'up' || name === 'down')) { if (on !== false) nudge(p[1]); return true; }
    const code = 'pad:' + p[0];
    clearTimeout(padTimers.get(name));
    padTimers.delete(name);
    if (on === false) { if (held.delete(code) && !moving()) settled(true); return true; }
    if (held.has(code)) return true;
    nudge(p[1]);
    // Called from a click or a key (Enter on the button) there is no release to wait for: the one step is all.
    if (pressed) padTimers.set(name, setTimeout(() => { padTimers.delete(name); if (!pressed) return; held.add(code); fly = null; schedule(); }, 300));
    return true;
  }
  // Every pad button let go: also whenever the pointer is let go anywhere, so a button can never stay held down
  // (a pointer slid off it, a page that missed the release).
  function letGo() {
    padTimers.forEach(clearTimeout);
    padTimers.clear();
    let any = false;
    for (const k of [...held]) if (k.startsWith('pad:')) { held.delete(k); any = true; }
    if (any && !moving()) settled(true);
  }
  listen(window, 'pointerdown', () => { pressed = true; }, true);
  listen(window, 'pointerup', () => { pressed = false; letGo(); }, true);
  listen(window, 'pointercancel', () => { pressed = false; letGo(); }, true);
  listen(document, 'visibilitychange', () => { if (!document.hidden) redraw(); });
  // The graphics card taken away, and given back.
  listen(canvas, 'webglcontextlost', e => {
    e.preventDefault();   // tells the browser this page wants the drawing back when it can be had
    lost = true;
    if (raf) { cancelAnimationFrame(raf); raf = 0; }
  });
  listen(canvas, 'webglcontextrestored', () => {
    lost = false;
    try { initGL(); } catch (x) { lost = true; return; }
    redraw();
  });
  // A new size, including coming (back) on show: a hidden page's canvas has no size, and draws nothing until then.
  const resized = typeof ResizeObserver === 'function' ? new ResizeObserver(() => redraw()) : null;
  if (resized) resized.observe(canvas);
  // Scrolled out of the page's column and back: the pulse stops while nobody can see it, and the next frame starts it.
  const watched = typeof IntersectionObserver === 'function' ? new IntersectionObserver(seen => {
    inView = seen.some(s => s.isIntersecting);
    if (inView) redraw();
  }) : null;
  if (watched) watched.observe(canvas);

  // No 3D after all (or a shader the driver refuses): leave the page as it was, and let it show the flat view.
  // (A headless map for the self-test has no graphics card to ask.)
  if (!headless) try { initGL(); } catch (e) {
    for (const [t, type, fn, o] of listeners) t.removeEventListener(type, fn, o);
    if (resized) resized.disconnect();
    if (watched) watched.disconnect();
    throw e;
  }

  // ---- what the page uses
  // The numbers of the drawing as it is (worked out first if a change waits): the cubes and lines are what goes to
  // the graphics card; areaCubes the opened area's filled cubes among them, level the one level shown (-1: all).
  function stats() {
    ensureBuilt();
    const middle = synced.slice(2, 10).sort((a, b) => a - b);
    const median = middle.length === 8 ? (middle[3] + middle[4]) / 2 : null;
    const b = batches;
    return {
      renderer, software: SOFTWARE.test(renderer), frames, frameMs: synced.slice(), medianMs: median,   // median of frames 3..10, timed to the end
      lastMs, sortMs, pixelRatio: ratio, size: [canvas.width, canvas.height], lost,
      cubes: b.solid ? b.solid.n + b.sectors.n + b.area.n : 0, lines: lines ? lines.n / 2 : 0, stars: starData ? starData.length / 4 : 0,
      areaCubes: areaDrawn, level: area ? area.level : -1,
      routeStops: markers.route.length, routeLines: routeSegments().length,   // the route check's stops kept, and the lines between them drawn now
      // Within reach: its dots kept, and the radius of its sphere drawn now (0: none, or the markers hidden).
      reachDots: markers.reach ? markers.reach.systems.length : 0, reachSphere: markers.reach && view.markers !== false ? markers.reach.r : 0,
      mode: cam ? cam.mode : startMode, nebulae: nebKey ? nebKey.split(',').length : 0,
    };
  }
  // A marker's place as the page sent it, when it has one.
  const placeOf = p => p && typeof p === 'object' && [p.x, p.y, p.z].every(fin) ? p : null;
  // Within reach as the page sent it: a place and a radius, with the systems that have a place; null for none.
  const withinOf = r => placeOf(r) && fin(r.r) && r.r > 0
    ? { x: +r.x, y: +r.y, z: +r.z, r: +r.r, label: typeof r.label === 'string' ? r.label : '', systems: Array.isArray(r.systems) ? r.systems.filter(placeOf) : [] } : null;

  // A choice filled in from what the map knows, so the page can name one briefly: a cube of the opened area by its
  // place in the grid ({ kind: 'cube', i, j, k }), a sector by its numbers ({ kind: 'sector', ix, iy, iz }), and the
  // middle of any box. A copy: the page's own object is left as it was. (The scene is worked out first only when it
  // is needed: the page sends its choice with its box after every new scene, and a hidden page should not build.)
  function complete(hit) {
    if (!hit || typeof hit !== 'object') return null;
    const h = Object.assign({}, hit);
    if (!boxOf(h) && (h.kind === 'cube' || h.kind === 'sector')) ensureBuilt();
    if (h.kind === 'cube' && !boxOf(h) && area && [h.i, h.j, h.k].every(v => Number.isInteger(v) && v >= 0 && v < area.n)) {
      const n = area.n, idx = h.i + n * (h.j + n * h.k), q = area.slot[idx] - 1, c = area.cell;
      Object.assign(h, { x0: area.corner[0] + h.i * c, y0: area.corner[1] + h.j * c, z0: area.corner[2] + h.k * c, size: c });
      if (!('item' in h)) Object.assign(h, { index: q, item: q >= 0 ? scene.area.cubes[q] : null, empty: area.state[idx] === 0 });
    } else if (h.kind === 'sector' && !boxOf(h) && [h.ix, h.iy, h.iz].every(fin)) {
      const c = sectorCorner(h), s = sectorAt.get(h.ix + ',' + h.iy + ',' + h.iz);
      Object.assign(h, { x0: c[0], y0: c[1], z0: c[2], size: scene.sectorLy });
      if (s !== undefined && !('item' in h)) Object.assign(h, { index: s, item: scene.sectors[s], open: info[s].open, empty: info[s].empty });
    }
    const b = boxOf(h);
    if (b && ![h.x, h.y, h.z].every(fin)) Object.assign(h, { x: b[0] + b[3] / 2, y: b[1] + b[3] / 2, z: b[2] + b[3] / 2 });
    return h;
  }

  // The box next to a chosen one, one of its own size along a direction (see stepFrom at the top of this file).
  function stepFrom(hit, dir) {
    ensureBuilt();
    const h = complete(hit), b = boxOf(h);
    const s = Array.isArray(dir) && dir.length === 3 && dir.every(fin) ? { step: dir.map(Number), word: '' } : cam ? screenStep(cam, dir) : null;
    if (!b || !s) return null;
    const size = b[3], S = scene.sectorLy, o = scene.origin;
    const box = { x0: b[0] + s.step[0] * size, y0: b[1] + s.step[1] * size, z0: b[2] + s.step[2] * size, size };
    // The sector a box lies in, by its middle (a box bigger than a sector, which the game has not, by its middle too).
    const sectorOfBox = (x0, y0, z0) => [x0, y0, z0].map((v, k) => Math.floor((v + size / 2 - o[k]) / S));
    const [ix, iy, iz] = sectorOfBox(box.x0, box.y0, box.z0), own = sectorOfBox(b[0], b[1], b[2]);
    const sec = sectorAt.get(ix + ',' + iy + ',' + iz);
    const out = { step: s.step, word: s.word, box, sector: { ix, iy, iz }, crosses: own[0] !== ix || own[1] !== iy || own[2] !== iz,
      inBlock: sec !== undefined, inArea: false, hit: null };
    // A cube of the opened area, when the area has cubes of this size on this grid.
    if (area && Math.abs(size - area.cell) < 1e-6) {
      const g = [box.x0, box.y0, box.z0].map((v, k) => Math.round((v - area.corner[k]) / size));
      if (g.every((v, k) => v >= 0 && v < area.n && Math.abs(area.corner[k] + v * size - [box.x0, box.y0, box.z0][k]) < 0.01)) {
        Object.assign(out, { inArea: true, i: g[0], j: g[1], k: g[2] });
        out.hit = complete({ kind: 'cube', i: g[0], j: g[1], k: g[2] });
      }
    }
    // A counted cube there has the count: it goes before the area's own cube (as a click there would take it).
    const q = scene.counted.findIndex((c, i) => countedOk[i] && Math.abs(c.size - size) < 1e-6
      && Math.abs(c.x0 - box.x0) < 0.01 && Math.abs(c.y0 - box.y0) < 0.01 && Math.abs(c.z0 - box.z0) < 0.01);
    if (q >= 0) { const c = scene.counted[q]; out.hit = boxResult('counted', q, c, +c.x0, +c.y0, +c.z0, +c.size); }
    // A step of a whole sector: the next sector, when it is one of the scene's.
    if (!out.hit && Math.abs(size - S) < 1e-6 && sec !== undefined) {
      const it = info[sec];
      out.hit = Object.assign(boxResult('sector', sec, scene.sectors[sec], it.corner[0], it.corner[1], it.corner[2], S), { ix, iy, iz, open: it.open, empty: it.empty });
    }
    return out;
  }

  const api = {
    setScene(s) {
      scene = tidy(s);
      // The point everything is measured from on the graphics card: the middle of the block, in whole tens of ly.
      const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
      for (const sec of scene.sectors) if ([sec.ix, sec.iy, sec.iz].every(fin)) sectorCorner(sec).forEach((v, k) => { lo[k] = Math.min(lo[k], v); hi[k] = Math.max(hi[k], v + scene.sectorLy); });
      const mid = fin(lo[0]) ? lo.map((v, k) => (v + hi[k]) / 2) : scene.area ? scene.area.corner : scene.player ? [scene.player.x, scene.player.y, scene.player.z] : [0, 0, 0];
      base = mid.map(v => Math.round(v / 10) * 10);
      // A pointed-at or chosen sector or cube keeps its outline if it is still there; anything else is let go.
      const still = hit => hit && (hit.kind === 'sector' ? scene.sectors.some(x => x.ix === hit.ix && x.iy === hit.iy && x.iz === hit.iz)
        : hit.kind === 'cube' && scene.area && hit.size === scene.area.cubeLy && hit.x0 === scene.area.corner[0] + hit.i * hit.size);
      if (!still(hover)) hover = null;
      if (!still(selected)) selected = null;
      if (deeper) deeper.ring = null;   // what lies on the last click's line is worked out again (release())
      needBuild = 2;
      needStars = needMarks = true;
      nebKey = null;   // new nebulae, and measured from a new base point
      if (!cam) travel(overviewGoal(), true, true);
      redraw();
    },
    setRamp(bytes) {
      if (!bytes || bytes.length < 1024) return;
      ramp = new Uint8Array(bytes.buffer ? bytes.slice(0, 1024) : Array.from(bytes).slice(0, 1024));
      uploadRamp();
      redraw();
    },
    // Only what a change touches is made again: the outlines depend on "show empty", the range and the level of
    // cubes shown, the cubes on those and on the opacities and the look; "hold height" only steers the camera. markers
    // (the "Markers" switch) touches the marks; nebulae ('all' | 'near' | 'off') is worked out again on the next
    // frame; the level of cubes takes the star dots on the other levels away too.
    setView(v) {
      const was = Object.assign({}, view);
      Object.assign(view, v || {});
      if (!['all', 'near', 'off'].includes(view.nebulae)) view.nebulae = ['all', 'near', 'off'].includes(was.nebulae) ? was.nebulae : 'near';
      if (view.cubeLevel === undefined) view.cubeLevel = null;
      const changed = keys => keys.some(k => view[k] !== was[k]);
      let any = false;
      if (changed(['showEmpty', 'hideBelow', 'hideAbove'])) { needBuild = 2; any = true; }
      else if (changed(['opacity', 'ghostOpacity', 'look', 'standOut'])) { needBuild = Math.max(needBuild, 1); any = true; }
      if (changed(['cubeLevel'])) { needBuild = 2; needStars = true; any = true; }
      if (changed(['markers'])) { needMarks = true; any = true; }
      if (changed(['nebulae'])) any = true;
      // What can be clicked changed: the next click on the same spot works out the line again, from the first click's
      // camera, instead of walking a list that still holds cubes on levels no longer drawn (release()).
      if (deeper && changed(['cubeLevel', 'showEmpty', 'hideBelow', 'hideAbove', 'markers', 'nebulae'])) deeper.ring = null;
      if (any) redraw();
    },
    flyTo,
    pick: pickAt,
    onPick(cb) { pickCb = typeof cb === 'function' ? cb : null; },
    onHover(cb) { hoverCb = typeof cb === 'function' ? cb : null; },
    redraw,
    stats,
    dispose() {
      disposed = true;
      if (raf) cancelAnimationFrame(raf);
      clearTimeout(settleTimer);
      clearTimeout(pulseTimer);
      padTimers.forEach(clearTimeout);
      padTimers.clear();
      held.clear();
      for (const [t, type, fn, o] of listeners) t.removeEventListener(type, fn, o);
      if (resized) resized.disconnect();
      if (watched) watched.disconnect();
      if (gl && !lost && R) {
        for (const g of [R.solid, R.sectors, R.area, R.light, R.lines, R.marks, R.nebs, R.stars]) { gl.deleteBuffer(g.buf); gl.deleteVertexArray(g.vao); }
        gl.deleteBuffer(R.cubeVB); gl.deleteTexture(R.ramp);
        for (const p of [R.cube, R.line, R.star]) gl.deleteProgram(p.p);
      }
      if (octx) octx.clearRect(0, 0, overlay.width, overlay.height);
      measuring.forEach(f => f(stats()));
      measuring = [];
    },
    // Beyond the list in the spec, for the page's buttons and its memory of the camera:
    measure() {   // draws until frames 3..10 are timed; resolves with stats() (medianMs above 50 = too slow for 3D)
      return new Promise(done => { if (synced.length >= 10 || disposed) done(stats()); else { measuring.push(done); schedule(); } });
    },
    select(hit) { selected = complete(hit); needMarks = true; redraw(); },
    overview() { travel(overviewGoal(), true); },
    me() { return flyTo('me'); },
    // Back and Forward first drop a "remember this" still waiting, which would otherwise cut off the way forward.
    back() { clearTimeout(settleTimer); remember(); if (place <= 0) return false; place--; travel(history[place], false); return true; },
    forward() { clearTimeout(settleTimer); if (place >= history.length - 1) return false; place++; travel(history[place], false); return true; },
    canGo() { return { back: place > 0 || (!!cam && place >= 0 && !sameView(history[place], snapshot())), forward: place < history.length - 1 }; },
    camera() { return cam ? snapshot() : null; },
    setCamera(c, instant) {
      if (!c || !Array.isArray(c.t) || !c.t.every(fin) || !(c.dist > 0)) return false;
      travel({ t: c.t.map(Number), dist: clamp(+c.dist, MIN_DIST, MAX_DIST), yaw: num(c.yaw), pitch: num(c.pitch), mode: c.mode }, true, instant !== false);
      return true;
    },
    onCamera(cb) { cameraCb = typeof cb === 'function' ? cb : null; },
    project(x, y, z) {
      const [w, h] = sizeOf();
      if (!cam || !w || !h) return null;
      const p = toScreen(view3(cam, w / h, base), [x, y, z], w, h);
      return p ? { x: p[0], y: p[1] } : null;
    },
    // Where a place is on the screen, also when it is off it (x, y beyond the edges) or behind the eye (behind: true,
    // and x, y a point beyond the edge on the side it is nearest: where the view would turn to see it). The page
    // points at a highlight the arrows moved off the map with it.
    whereIs(x, y, z) {
      const [w, h] = sizeOf();
      if (!cam || !w || !h || ![x, y, z].every(fin)) return null;
      const v = view3(cam, w / h, base), p = toScreen(v, [x, y, z], w, h);
      if (p) return { x: p[0], y: p[1], behind: false };
      const q = sub3([x, y, z], v.eye), across = dot3(q, v.r), up = dot3(q, v.u), l = Math.hypot(across, up) || 1;
      return { x: w / 2 + across / l * w, y: h / 2 - up / l * h, behind: true };
    },
    // Spec v2: the free camera, the flat views and the markers (see the top of this file).
    setMode(m) {
      if (!MODES.includes(m)) return api.mode();
      if (!cam) { startMode = m; return m; }
      if (cam.mode !== m) {
        clearTimeout(settleTimer);
        remember();   // so Back returns to the view before the switch
        fly = null;
        setModeNow(m);
        settled(true);
        redraw();
      }
      return m;
    },
    mode() { return cam ? cam.mode : startMode; },
    onMode(cb) { modeCb = typeof cb === 'function' ? cb : null; },
    // In the Top view R/F (E/Q) and the pad's Up and Down call this with +1 (the layer above) or -1 (below), one call
    // a press, instead of moving along the line of sight: the page knows the layers.
    onLayerStep(cb) { layerCb = typeof cb === 'function' ? cb : null; },
    setMarkers(m) {
      if (!m || typeof m !== 'object') return;
      for (const key of ['you', 'target', 'routeEnd']) if (key in m) markers[key] = placeOf(m[key]);
      if ('finds' in m) markers.finds = Array.isArray(m.finds) ? m.finds.filter(placeOf) : [];
      if ('route' in m) markers.route = Array.isArray(m.route) ? m.route.filter(placeOf) : [];
      if ('reach' in m) markers.reach = withinOf(m.reach);
      if (hover && (hover.kind === 'find' || hover.kind === 'stop' || hover.kind === 'reach')) hover = null;   // the list it pointed into is gone
      if (deeper && ('finds' in m || 'route' in m || 'reach' in m)) deeper.ring = null;
      needMarks = true;
      redraw();
    },
    setNebulaMode(m) { api.setView({ nebulae: m }); return view.nebulae; },
    // The colour of a route check stop's dot ([r, g, b]) for its state, so the page's list can be checked against it.
    stopColour(state) { return stopInk(state).slice(); },
    // How a dot of Within reach is drawn for its state ({ colour: [r, g, b], alpha, hollow }), for the same check.
    reachColour(state, must) { return reachInk(state, must); },
    focusOn,
    nudge,
    hold,
    // Spec v3: reaching the cubes inside, and the highlight (see the top of this file).
    hitsAt(x, y) { const [w, h] = sizeOf(); return cam ? rayRing(snapshot(), x, y, w, h) : []; },
    setHighlight(hit) {
      selected = complete(hit);
      needMarks = true;
      redraw();
      return selected && [selected.x, selected.y, selected.z].every(fin) ? api.project(selected.x, selected.y, selected.z) : null;
    },
    highlight() { return selected ? Object.assign({}, selected) : null; },
    axisFor(dir) { return cam ? screenStep(cam, dir) : null; },
    stepFrom,
    ownsKey,
  };
  return api;
}

// ============================================================ self-test (no graphics card needed)
// heatmap.html?selftest runs this with HeatGrid.selftest(). Returns { ok, checks: [{ name, ok, detail }] }.
function selftest() {
  const checks = [];
  const check = (name, ok, detail) => checks.push({ name, ok: !!ok, detail: detail || '' });
  const W = 800, H = 600;
  const above = view3({ t: [0, 0, 0], yaw: 0, pitch: Math.PI / 2, dist: 30000 }, W / H, [0, 0, 0]);
  const east = toScreen(above, [1000, 0, 0], W, H), north = toScreen(above, [0, 0, 1000], W, H);
  check('Seen from above, east is to the right', east && east[0] > W / 2 + 1 && Math.abs(east[1] - H / 2) < 0.5, east && east.map(v => v.toFixed(1)).join(', '));
  check('Seen from above, north (towards the core) is up', north && north[1] < H / 2 - 1 && Math.abs(north[0] - W / 2) < 0.5, north && north.map(v => v.toFixed(1)).join(', '));
  const colonia = toScreen(above, [-9530.5, -910.28, 19808.13], W, H);
  check('Colonia is up and to the left of Sol, as on the flat map', colonia && colonia[0] < W / 2 && colonia[1] < H / 2, colonia && colonia.map(v => v.toFixed(1)).join(', '));
  // The top face of a cube, seen from above, must turn clockwise on screen: that is what gl.frontFace(gl.CW) keeps.
  const g = cubeGeometry(), top = [];
  for (let i = 0; i < 36 && top.length < 3; i++) if (g[i * 6 + 4] === 1) top.push([g[i * 6] * 100, g[i * 6 + 1] * 100, g[i * 6 + 2] * 100]);
  const s = top.map(p => toScreen(above, p, W, H));
  const area = s.every(Boolean) ? (s[1][0] - s[0][0]) * (-(s[2][1] - s[0][1])) - (-(s[1][1] - s[0][1])) * (s[2][0] - s[0][0]) : 0;
  check('Faces facing the camera turn clockwise on screen', area < 0, 'signed area ' + area.toFixed(2));
  // A point drawn at a pixel is found again by the line of sight through that pixel.
  const tilted = view3({ t: [-167.09, 26.72, -55.38], yaw: 0.7, pitch: 0.5, dist: 3000 }, W / H, [-170, 30, -60]);
  const p = [-100, 200, -300], at = toScreen(tilted, p, W, H), ray = at && rayAt(tilted, at[0], at[1], W, H);
  let miss = Infinity;
  if (ray) { const q = sub3(p, ray.o), t = dot3(q, ray.d); miss = len3(sub3(q, ray.d.map(v => v * t))); }
  check('Pointing at a drawn point finds it again', miss < 0.05, 'missed by ' + miss.toFixed(4) + ' ly');
  // The back-to-front order.
  const n = 2000, centres = new Float64Array(n * 3), order = new Uint32Array(n), dist = new Float64Array(n), eye = [123, -45, 678];
  let seed = 7;
  const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  for (let i = 0; i < n * 3; i++) centres[i] = (rnd() - 0.5) * 2560;
  farFirst(centres, n, eye, order, dist);
  let sorted = new Set(order).size === n;
  for (let i = 1; i < n; i++) if (dist[i] > dist[i - 1] + 0.1) sorted = false;
  check('See-through boxes are put farthest first', sorted);
  // Walking down through a column of sectors meets the layers from the top down, without gaps.
  const met = [];
  walk([-700, 5000, -400], [0, -1, 0], GRID_ORIGIN, SECTOR_LY, [37, 29, 17], [39, 34, 19], (ix, iy, iz) => { met.push([ix, iy, iz].join(',')); return false; });
  check('Looking straight down through the column meets layers 34 to 29 in order', met.join(' ') === '38,34,18 38,33,18 38,32,18 38,31,18 38,30,18 38,29,18', met.join(' '));
  // A nebula of several spheres is all of them; one with none given is its one sphere; one with no size, nothing.
  const icz = spheresOf({ x: 61, y: -124, z: 35, r: 40, spheres: [[11, -124, 35, 40], [106, -124, 35, 40], [60, 0, 0, NaN]] });
  check('A nebula of several spheres is drawn as each of them', icz.length === 2 && icz[1][0] === 106, JSON.stringify(icz));
  check('A nebula of one sphere is drawn round its middle', JSON.stringify(spheresOf({ x: 1, y: 2, z: 3, r: 10 })) === '[[1,2,3,10]]' && spheresOf({ x: 1, y: 2, z: 3, r: 0 }).length === 0);
  // The sector you are in, by the same numbers as Galaxy.cs and heat-grid.js.
  const you = [-167.09, 26.72, -55.38].map((v, k) => Math.floor((v - GRID_ORIGIN[k]) / SECTOR_LY));
  check('You are in sector 38, 32, 18 (Wredguia)', you.join(',') === '38,32,18', you.join(','));
  if (window.HeatGrid && window.HeatGrid.SECTOR) check('heat-grid.js uses the same grid', window.HeatGrid.SECTOR === SECTOR_LY && String(window.HeatGrid.ORIGIN) === String(GRID_ORIGIN), String(window.HeatGrid.ORIGIN));

  // ---- spec v2: the free camera and the flat views
  const pt = p => p ? p.map(v => v.toFixed(1)).join(', ') : 'not on screen';
  const centred = p => !!p && Math.hypot(p[0] - W / 2, p[1] - H / 2) < 0.01;
  // Turning round the focus, more than a full turn and up and down to both stops: the focus never moves, stays in
  // the middle of the screen, and the eye stays as far from it.
  const orbit = { t: [-167.09, 26.72, -55.38], yaw: 0.3, pitch: 0.6, dist: 2500, mode: '3d' }, t0 = orbit.t.slice();
  let offMiddle = 0, offDist = 0, highest = -Infinity, lowest = Infinity, yawFrom = orbit.yaw, turned = 0;
  for (let i = 0; i < 80; i++) {
    turnCam(orbit, 0.1, i < 40 ? 0.08 : -0.08);
    let step = orbit.yaw - yawFrom;
    if (step < -Math.PI) step += 2 * Math.PI;
    turned += step;
    yawFrom = orbit.yaw;
    highest = Math.max(highest, orbit.pitch);
    lowest = Math.min(lowest, orbit.pitch);
    const v = view3(orbit, W / H, [-170, 30, -60]), c = toScreen(v, orbit.t, W, H);
    offMiddle = Math.max(offMiddle, c ? Math.hypot(c[0] - W / 2, c[1] - H / 2) : Infinity);
    offDist = Math.max(offDist, Math.abs(len3(sub3(v.eye, orbit.t)) - orbit.dist));
  }
  check('Turning round the focus (8 radians round, up and down to the stops) leaves the focus where it was',
    orbit.t.every((v, k) => v === t0[k]) && offMiddle < 0.01 && offDist < 1e-6 && turned > 2 * Math.PI,
    'turned ' + turned.toFixed(2) + ' rad; focus off the middle by ' + offMiddle.toFixed(4) + ' px, distance off by ' + offDist.toExponential(1) + ' ly');
  check('Turning up and down stops just short of straight down and straight up',
    Math.abs(highest - PITCH_MAX) < 1e-9 && Math.abs(lowest + PITCH_MAX) < 1e-9, 'from ' + lowest.toFixed(4) + ' to ' + highest.toFixed(4) + ' rad');
  // Moving in all three directions.
  const moved = (cam, right, up, ahead, hold) => { const c = Object.assign({}, cam, { t: [0, 0, 0] }); moveCam(c, right, up, ahead, hold); return c.t; };
  const tilt = { yaw: 0.4, pitch: 0.5, dist: 1000, mode: '3d' }, look = axesOf(tilt).f;
  const fwd = moved(tilt, 0, 0, 100, false), lvl = moved(tilt, 0, 0, 100, true), rise = moved(tilt, 0, 50, 0, true), side = moved(tilt, 100, 0, 0, true);
  check('W goes the way the camera looks; with Hold height it stays level', len3(sub3(fwd, look.map(v => v * 100))) < 1e-9 && Math.abs(lvl[1]) < 1e-9 && Math.abs(len3(lvl) - 100) < 1e-9 && lvl[0] * look[0] + lvl[2] * look[2] > 0,
    pt(fwd) + ' / ' + pt(lvl));
  check('R goes up in the galaxy\'s own up; D goes right on screen and stays level', len3(sub3(rise, [0, 50, 0])) < 1e-9 && Math.abs(side[1]) < 1e-9 && Math.abs(len3(side) - 100) < 1e-9, pt(rise) + ' / ' + pt(side));
  check('In the Top view, W slides north (up the screen)', len3(sub3(moved({ yaw: 2, pitch: 0.3, dist: 1000, mode: 'top' }, 0, 0, 100, true), [0, 0, 100])) < 1e-9);
  const sideW = moved({ yaw: 2, pitch: 0.3, dist: 1000, mode: 'side' }, 0, 0, 100, true), sideR = moved({ yaw: 2, pitch: 0.3, dist: 1000, mode: 'side' }, 0, 50, 0, true);
  check('In the Side view, W goes north into the screen (to the next row) and R up the screen', len3(sub3(sideW, [0, 0, 100])) < 1e-9 && len3(sub3(sideR, [0, 50, 0])) < 1e-9,
    pt(sideW) + ' / ' + pt(sideR));
  // The Top view: from above whatever the 3D camera was turned to, east right, north (+z) up, no perspective, and
  // nothing above or below the focus cut off.
  const focus = [-167, 27, -55], baseAt = [-170, 30, -60];
  const topV = view3({ t: focus, yaw: 1.1, pitch: 0.3, dist: 3000, mode: 'top' }, W / H, baseAt);
  const tE = toScreen(topV, [833, 27, -55], W, H), tN = toScreen(topV, [-167, 27, 945], W, H);
  const tHigh = toScreen(topV, [-167, 6000, -55], W, H), tLow = toScreen(topV, [-167, -6000, -55], W, H);
  check('Top view: east (+x) is to the right', tE && tE[0] > W / 2 + 1 && Math.abs(tE[1] - H / 2) < 0.01, pt(tE));
  check('Top view: north (+z, towards the core) is up', tN && tN[1] < H / 2 - 1 && Math.abs(tN[0] - W / 2) < 0.01, pt(tN));
  check('Top view: flat, with nothing above or below the focus cut off', centred(tHigh) && centred(tLow) && tHigh[2] < tLow[2], pt(tHigh) + ' / ' + pt(tLow));
  // The Side view: from the south looking north, up (+y) up, east right, north straight into the screen.
  const sideV = view3({ t: focus, yaw: -2, pitch: 1.2, dist: 3000, mode: 'side' }, W / H, baseAt);
  const sUp = toScreen(sideV, [-167, 1027, -55], W, H), sE = toScreen(sideV, [833, 27, -55], W, H), sN = toScreen(sideV, [-167, 27, 945], W, H), sMid = toScreen(sideV, focus, W, H);
  check('Side view: up (+y) is up', sUp && sUp[1] < H / 2 - 1 && Math.abs(sUp[0] - W / 2) < 0.01, pt(sUp));
  check('Side view: east (+x) is to the right', sE && sE[0] > W / 2 + 1 && Math.abs(sE[1] - H / 2) < 0.01, pt(sE));
  check('Side view: north lies straight ahead, behind the focus', centred(sN) && sMid && sN[2] > sMid[2] + 999, pt(sN));
  // A switch keeps the scale: the flat views show the focus at the size the 3D view showed it.
  const flatOf = mode => view3({ t: [0, 0, 0], yaw: 0, pitch: Math.PI / 2, dist: 2000, mode }, W / H, [0, 0, 0]);
  const in3d = toScreen(flatOf('3d'), [100, 0, 0], W, H), inTop = toScreen(flatOf('top'), [100, 0, 0], W, H);
  check('Switching between 3D and Top keeps the scale at the focus', in3d && inTop && Math.abs(in3d[0] - inTop[0]) < 0.01, pt(in3d) + ' / ' + pt(inTop));
  // Faces facing the camera turn clockwise in the flat views too (the culling stays right).
  const facing = (v, n) => {
    const tri = [];
    for (let i = 0; i < 36 && tri.length < 3; i++) if (g[i * 6 + 3] === n[0] && g[i * 6 + 4] === n[1] && g[i * 6 + 5] === n[2]) tri.push([g[i * 6] * 100, g[i * 6 + 1] * 100, g[i * 6 + 2] * 100]);
    const q = tri.map(p => toScreen(v, p, W, H));
    return q.every(Boolean) ? (q[1][0] - q[0][0]) * (-(q[2][1] - q[0][1])) - (-(q[1][1] - q[0][1])) * (q[2][0] - q[0][0]) : 0;
  };
  const fTop = facing(view3({ t: [50, 50, 50], yaw: 0, pitch: 0, dist: 500, mode: 'top' }, W / H, [0, 0, 0]), [0, 1, 0]);
  const fSide = facing(view3({ t: [50, 50, 50], yaw: 0, pitch: 0, dist: 500, mode: 'side' }, W / H, [0, 0, 0]), [0, 0, -1]);
  check('Faces facing the camera turn clockwise in the Top and Side views too', fTop < 0 && fSide < 0, 'signed areas ' + fTop.toFixed(1) + ', ' + fSide.toFixed(1));
  // Clicking in the flat views: the line through the pixel finds the point drawn there, and walking it through the
  // grid (as every click does), ranked as pickAt ranks the sectors met, finds the sector clicked: of those along the
  // line (from above a column, from the side a row from south to north), the one at the focus, whose name is the one
  // written there. Also 5 ly either side of an edge.
  const flatAt = topV, fp = [-100, 200, -300], fs = toScreen(flatAt, fp, W, H), fr = fs && rayAt(flatAt, fs[0], fs[1], W, H);
  let fmiss = Infinity;
  if (fr) { const q = sub3(fp, fr.o), t = dot3(q, fr.d); fmiss = len3(sub3(q, fr.d.map(v => v * t))); }
  check('Top view: pointing at a drawn point finds it again', fmiss < 0.05, 'missed by ' + fmiss.toFixed(4) + ' ly');
  const S = SECTOR_LY, lo = [37, 31, 17], hi = [39, 32, 19];
  const middleOf = (ix, iy, iz) => [ix, iy, iz].map((v, k) => GRID_ORIGIN[k] + (v + 0.5) * S);
  const blockMid = middleOf(38, 32, 18), blockBase = blockMid.map(v => Math.round(v / 10) * 10);
  const clicked = (v, p) => {
    const q = toScreen(v, p, W, H);
    if (!q) return 'not on screen';
    const ray = rayAt(v, q[0], q[1], W, H);
    let got = 'nothing', best = Infinity;
    walk(ray.o, ray.d, GRID_ORIGIN, S, lo, hi, (ix, iy, iz, t) => {
      const rank = sectorRank(v, t, S);
      if (rank < best) { best = rank; got = [ix, iy, iz].join(','); }
      return false;
    });
    return got;
  };
  const topPick = view3({ t: blockMid, yaw: 0.5, pitch: 0.5, dist: 4000, mode: 'top' }, W / H, blockBase);
  const edgeX = GRID_ORIGIN[0] + 39 * S, row = middleOf(39, 32, 18);
  const tp = [clicked(topPick, middleOf(39, 31, 19)), clicked(topPick, middleOf(37, 32, 17)), clicked(topPick, [edgeX + 5, row[1], row[2]]), clicked(topPick, [edgeX - 5, row[1], row[2]])];
  check('Top view: a click on a square finds that sector (of its column, the layer at the focus)', tp.join(' ') === '39,32,19 37,32,17 39,32,18 38,32,18', tp.join(' '));
  // From the side all three rows of a layer share a spot on screen: the click takes the row at the focus (the middle
  // one here, as after Overview or Me), whose name is written there, not the southmost.
  const sidePick = view3({ t: blockMid, yaw: 2.5, pitch: -0.4, dist: 4000, mode: 'side' }, W / H, blockBase);
  const edgeY = GRID_ORIGIN[1] + 32 * S, col = middleOf(38, 32, 19);
  const sp = [clicked(sidePick, middleOf(37, 31, 19)), clicked(sidePick, middleOf(39, 32, 18)), clicked(sidePick, [col[0], edgeY + 5, col[2]]), clicked(sidePick, [col[0], edgeY - 5, col[2]])];
  check('Side view: a click on a square finds the sector of the row at the focus, the one named there', sp.join(' ') === '37,31,18 39,32,18 38,32,18 38,31,18', sp.join(' '));
  // The focus moved one row north (W in the Side view): a click there takes that row.
  const sideNorth = view3({ t: middleOf(38, 32, 19), yaw: 0, pitch: 0, dist: 4000, mode: 'side' }, W / H, blockBase);
  check('Side view: with the focus on the north row, a click takes the north row', clicked(sideNorth, middleOf(39, 32, 17)) === '39,32,19', clicked(sideNorth, middleOf(39, 32, 17)));
  // In 3D the nearest along the line wins, as before.
  check('3D: of the sectors along a click\'s line, the nearest wins', sectorRank(tilted, 10, S) < sectorRank(tilted, 20, S));
  // The names of You, the target and the route's end: two marks 5 px apart get names that cover neither each other
  // nor either mark; a mark at the right edge has its name on its left.
  const marksAt = [[400, 300, 18, 21, 170], [404, 305, 14, 17, 230]], taken = marksAt.map(([x, y, r]) => [x - r, y - r, x + r, y + r]), boxes = [];
  for (const [x, y, , gap, tw] of marksAt) { const b = besideMark(taken, x, y, tw, 15, gap, W, H)[2]; boxes.push(b); taken.push(b); }
  const overlaps = (a, b) => a[0] < b[2] && a[2] > b[0] && a[1] < b[3] && a[3] > b[1];
  check('Two marks 5 px apart: their names cover neither each other nor either mark',
    !overlaps(boxes[0], boxes[1]) && boxes.every(b => marksAt.every(([x, y, r]) => !overlaps(b, [x - r, y - r, x + r, y + r]))),
    boxes.map(b => b.map(v => v.toFixed(0)).join(',')).join(' / '));
  const atEdge = besideMark([[776, 286, 804, 314]], 790, 300, 120, 15, 17, W, H);
  check('A mark at the right edge has its name on its left', atEdge[2][2] <= 776 && atEdge[2][0] >= 0, atEdge[2].map(v => v.toFixed(0)).join(','));
  // Nebulae: spheres sent as objects, framing all of them, and Near / All / Off.
  const objs = spheresOf({ x: 0, y: 0, z: 0, r: 40, spheres: [{ x: -60, y: 0, z: 0, r: 40 }, { x: 60, y: 0, z: 0, r: 40 }, { x: 1 }] });
  check('A nebula\'s spheres may come as { x, y, z, r } too', objs.length === 2 && objs[1][0] === 60, JSON.stringify(objs));
  check('A flight to a nebula of several spheres frames them all', reachOf({ x: 0, y: 0, z: 0, r: 40, spheres: [[-60, 0, 0, 40], [60, 0, 0, 40]] }) === 100);
  const neb = { x: 2000, y: 0, z: 0, r: 100 };
  check('Nebulae: Near shows one within 1,500 ly of the focus and not one farther; Off none; All every one',
    nebulaShown(neb, [450, 0, 0], 'near') && !nebulaShown(neb, [350, 0, 0], 'near') && !nebulaShown(neb, [2000, 0, 0], 'off') && nebulaShown(neb, [-50000, 0, 0], 'all'));

  // ---- spec v3: the arrow keys move the highlight, along the galaxy's direction nearest what each means on screen
  const words = (c, dirs) => dirs.map(d => (screenStep(c, d) || {}).word).join(' ');
  const four = ['right', 'left', 'up', 'down'];
  const turnedTo = [[0, 0.6, 'east west north south'], [0.5, 1.4, 'east west north south'], [-0.6, -0.4, 'east west north south'],
    [Math.PI / 4, 0.6, 'east west north south'], [Math.PI / 2, 0.6, 'north south west east'], [2, 0.2, 'north south west east'],
    [Math.PI, 0.6, 'west east south north'], [-Math.PI / 2, 0.9, 'south north east west']];
  const wrong = turnedTo.filter(([yaw, pitch, want]) => words({ yaw, pitch, mode: '3d' }, four) !== want);
  check('3D: the arrows go right, left, away and towards along the nearest of east-west and north-south, the camera turned eight ways',
    !wrong.length, wrong.length ? wrong.map(([yaw, , want]) => 'yaw ' + yaw.toFixed(2) + ': ' + words({ yaw, pitch: 0.6, mode: '3d' }, four) + ', want ' + want).join('; ') : 'right left up down, for each');
  check('Top view: up is north, right east; Side view: up is up, right east (whatever the 3D camera was turned to)',
    words({ yaw: 2, pitch: 0.3, mode: 'top' }, four) === 'east west north south' && words({ yaw: -1, pitch: 1, mode: 'side' }, four) === 'east west up down',
    words({ yaw: 2, pitch: 0.3, mode: 'top' }, four) + ' / ' + words({ yaw: -1, pitch: 1, mode: 'side' }, four));
  check('Page Up and Page Down (higher, lower) go up and down in every view',
    ['3d', 'top', 'side'].every(mode => words({ yaw: 1, pitch: 0.5, mode }, ['higher', 'lower']) === 'up down' && String(screenStep({ yaw: 1, pitch: 0.5, mode }, 'higher').step) === '0,1,0'));
  check('No key turns the camera any more: its keys are W A S D, R F and E Q; the arrows are the page\'s',
    MOVE_KEYS.length === 8 && !MOVE_KEYS.some(k => /^(Arrow|Page)/.test(k)), MOVE_KEYS.join(' '));
  // The highlight's edges are drawn over everything; one that passes behind the eye is cut where it does, not lost.
  const ahead = view3({ t: [0, 0, 0], yaw: 0, pitch: 0, dist: 1000, mode: '3d' }, W / H, [0, 0, 0]);
  const cut = lineOnScreen(ahead, [0, 0, 0], [0, 100, -1500], W, H);
  check('An edge of the highlight running behind the eye is cut there, not dropped; one wholly behind is left out',
    !!cut && Math.abs(cut[0] - W / 2) < 0.01 && Math.abs(cut[1] - H / 2) < 0.01 && cut[3] < 0 && lineOnScreen(ahead, [0, 0, -1200], [0, 50, -1300], W, H) === null,
    cut ? cut.map(v => v.toFixed(0)).join(', ') : 'nothing');

  // ---- spec v3 on the map itself: one made headless (no graphics card, never drawn) on a canvas nobody sees, so the
  // real clicks, picking, stepping and level filter run. A block of 3 x 3 sectors on two layers, Wredguia's sector
  // opened, with a column of 8 filled cubes of 160 ly (one on each level, i 3, k 4) and one more at the east edge.
  let m = null;
  try {
    let now = 0;
    const cv = document.createElement('canvas'), picked = [];
    m = create(cv, null, { headless: [W, H], clock: () => now });
    const at = (ix, iy, iz) => [ix, iy, iz].map((v, k) => GRID_ORIGIN[k] + v * S), corner = at(38, 32, 18);
    const mid = (i, j, k) => [i, j, k].map((v, a) => corner[a] + (v + 0.5) * 160);
    const sectors = [];
    for (let ix = 37; ix <= 39; ix++) for (let iz = 17; iz <= 19; iz++) for (const iy of [31, 32])
      sectors.push({ ix, iy, iz, name: 'Test ' + [ix, iy, iz].join(), known: 500, value: 0.5, layerShown: iy === 32, ghost: iy !== 32, outlineOnly: ix === 38 && iy === 32 && iz === 18 });
    const cubes = [];
    for (let j = 0; j < 8; j++) cubes.push({ i: 3, j, k: 4, value: 0.2 + j * 0.1, known: 10 + j });
    cubes.push({ i: 7, j: 2, k: 4, value: 0.9, known: 50 });
    const starIn = mid(3, 5, 4), starOut = at(37, 32, 18).map(v => v + 600);   // a star dot on level 5 of the area, and one in the sector west of it
    m.setScene({ origin: GRID_ORIGIN, sectorLy: S, sectors, area: { corner, cubeLy: 160, n: 8, cubes },
      stars: [{ x: starIn[0] + 7, y: starIn[1], z: starIn[2], state: 'full' }, { x: starOut[0], y: starOut[1], z: starOut[2], state: 'never' }] });
    m.onPick(hit => picked.push(hit));
    const name = hit => !hit ? 'nothing' : hit.kind === 'cube' ? 'cube ' + [hit.i, hit.j, hit.k].join() : hit.kind === 'sector' ? 'sector ' + [hit.ix, hit.iy, hit.iz].join() : hit.kind;
    // A click as the mouse makes it: a second apart, so never a double-click.
    const click = (x, y) => {
      now += 1000;
      const o = { clientX: x, clientY: y, button: 0, buttons: 1, pointerId: 1, isPrimary: true, pointerType: 'mouse' };
      cv.dispatchEvent(new PointerEvent('pointerdown', o));
      cv.dispatchEvent(new PointerEvent('pointerup', Object.assign({}, o, { buttons: 0 })));
      return name(picked[picked.length - 1]);
    };
    const cx = W / 2, cy = H / 2;
    // From above, straight down the column: its 8 cubes from the top, the sector under it, then round past the opened
    // sector (where the line came in) to the first again.
    m.setCamera({ t: [mid(3, 0, 4)[0], corner[1] + 640, mid(3, 0, 4)[2]], yaw: 0, pitch: 0.6, dist: 3000, mode: 'top' }, true);
    const want = [7, 6, 5, 4, 3, 2, 1, 0].map(j => 'cube 3,' + j + ',4').concat('sector 38,31,18', 'sector 38,32,18');
    const ring = m.hitsAt(cx, cy);
    check('Top view, over a column of 8 cubes: hitsAt gives what clicks there go through, top cube first, the sector below, then the opened one',
      ring.map(name).join(' | ') === want.join(' | ') && ring.every((hit, k) => hit.along.at === k && hit.along.of === want.length), ring.map(name).join(' | '));
    const clicks = [];
    for (let k = 0; k < 11; k++) clicks.push(click(cx + (k % 2 ? 3 : -2), cy + 1));   // a hand that shakes a little: within 8 px
    check('Clicking the same spot again reaches the cube behind, one each click, and comes round to the first after the last',
      clicks.join(' | ') === want.concat(want[0]).join(' | ') && picked[1].along.at === 1, clicks.join(' | '));
    const fresh = [click(cx, cy)];
    m.nudge({ zoom: 1 });
    fresh.push(click(cx, cy), click(cx, cy));
    m.setHighlight({ kind: 'sector', ix: 37, iy: 32, iz: 18 });
    fresh.push(click(cx, cy));
    check('After the camera moved, or something else was chosen, the next click starts again from the front',
      fresh.join(' | ') === 'cube 3,6,4 | cube 3,7,4 | cube 3,6,4 | cube 3,7,4', fresh.join(' | '));
    // The highlight set by the page from a cube's place in the grid, and stepped by the arrow keys.
    const spot = m.setHighlight({ kind: 'cube', i: 3, j: 2, k: 4 }), hl = m.highlight();
    check('The page can set the highlight by a cube\'s place in the grid: the map fills in its box and says where it is on the screen',
      !!hl && hl.x0 === corner[0] + 480 && hl.y0 === corner[1] + 320 && hl.z0 === corner[2] + 640 && hl.size === 160 && !!spot && Math.hypot(spot.x - cx, spot.y - cy) < 0.5,
      hl ? [hl.x0, hl.y0, hl.z0, hl.size].join(', ') + ' at ' + (spot ? spot.x.toFixed(1) + ', ' + spot.y.toFixed(1) : 'nowhere') : 'none');
    const higher = m.stepFrom(m.highlight(), 'higher'), north = m.stepFrom({ kind: 'cube', i: 3, j: 2, k: 4 }, 'up'), lower = m.stepFrom(hl, [0, -1, 0]);
    check('Top view: Page Up goes to the cube above, the up arrow to the cube north, in the same sector',
      !!higher && higher.inArea && [higher.i, higher.j, higher.k].join() === '3,3,4' && name(higher.hit) === 'cube 3,3,4' && !higher.crosses && higher.word === 'up'
      && !!north && north.word === 'north' && [north.i, north.j, north.k].join() === '3,2,5' && !!lower && [lower.i, lower.j, lower.k].join() === '3,1,4',
      [higher, north, lower].map(s => s ? s.word + ' ' + [s.i, s.j, s.k].join() : 'none').join(' / '));
    const edge = m.stepFrom({ kind: 'cube', i: 7, j: 2, k: 4 }, 'right'), edgeAt = at(39, 32, 18);
    check('Across a sector\'s edge: east of its east cube is the next sector\'s cube (in the block, not in the opened area: the page decides)',
      !!edge && edge.crosses && edge.inBlock && !edge.inArea && edge.hit === null && [edge.sector.ix, edge.sector.iy, edge.sector.iz].join() === '39,32,18'
      && edge.box.x0 === edgeAt[0] && edge.box.y0 === corner[1] + 320 && edge.box.size === 160,
      edge ? JSON.stringify({ sector: edge.sector, crosses: edge.crosses, inBlock: edge.inBlock, inArea: edge.inArea, x0: edge.box.x0 }) : 'none');
    const below = m.stepFrom({ kind: 'sector', ix: 38, iy: 32, iz: 18 }, 'lower'), off = m.stepFrom({ kind: 'sector', ix: 39, iy: 32, iz: 18 }, 'right');
    check('A sector steps to the next sector; off the block there is nothing to step to',
      !!below && name(below.hit) === 'sector 38,31,18' && !!off && !off.inBlock && off.hit === null, name(below && below.hit) + ' / ' + (off ? 'in block: ' + off.inBlock : 'none'));
    // One level of cubes: only it is drawn (its cubes, its empty outlines, its star dots) and only it can be clicked.
    const all = m.stats();
    m.setView({ cubeLevel: 3 });
    const one = m.stats(), slice = m.hitsAt(cx, cy).map(name);
    check('One level of cubes: only that level\'s cubes, outlines and star dots are drawn',
      all.areaCubes === 9 && one.areaCubes === 1 && one.level === 3 && one.lines < all.lines && all.stars === 2 && one.stars === 1,
      'filled cubes ' + all.areaCubes + ' then ' + one.areaCubes + ', lines ' + all.lines + ' then ' + one.lines + ', star dots ' + all.stars + ' then ' + one.stars);
    check('One level of cubes: a click there goes straight to that level\'s cube; the others cannot be clicked',
      slice.join(' | ') === 'cube 3,3,4 | sector 38,31,18 | sector 38,32,18' && name(m.pick(cx, cy)) === 'cube 3,3,4', slice.join(' | '));
    m.setView({ cubeLevel: null });
    check('Every level again with cubeLevel null', m.stats().areaCubes === 9 && m.stats().level === -1);
    // One level chosen between two clicks on the same spot (the page's Cube level, which leaves a highlight already on
    // that level where it is): the next click goes on along the line as it is now, never to a cube on a level gone.
    m.setCamera({ t: [mid(3, 0, 4)[0], corner[1] + 640, mid(3, 0, 4)[2]], yaw: 0, pitch: 0.6, dist: 3000, mode: 'top' }, true);
    const lv = [click(cx, cy)];
    m.setView({ cubeLevel: 7 });
    const lvRing = m.hitsAt(cx, cy).map(name);
    lv.push(click(cx, cy), click(cx, cy));
    m.setView({ cubeLevel: null });
    check('A level of cubes chosen between two clicks on the same spot: the next click takes the next of the line as it is now, not a cube on a hidden level',
      lv.join(' | ') === 'cube 3,7,4 | sector 38,31,18 | sector 38,32,18' && lvRing.join(' | ') === 'cube 3,7,4 | sector 38,31,18 | sector 38,32,18',
      lv.join(' | ') + ' (the line now: ' + lvRing.join(' | ') + ')');
    // 3D, a slanting line: the first is what one click takes, then farther along the line, then round to those in front.
    m.setCamera({ t: mid(3, 4, 4), yaw: 0.4, pitch: 0.6, dist: 2500, mode: '3d' }, true);
    const p = m.project(...mid(3, 5, 4)), list = p ? m.hitsAt(p.x, p.y) : [], first = p && m.pick(p.x, p.y);
    const ts = list.map(hit => hit.t), wrap = ts.findIndex((t, k) => k > 0 && t < ts[k - 1] - 1e-6);
    check('3D, a slanting line: what one click takes first, then on along the line, then round to those in front of it',
      list.length > 3 && samePlace(list[0], first) && ts.every((t, k) => !k || k === wrap || t >= ts[k - 1] - 1e-6) && (wrap < 0 || ts.slice(wrap).every(t => t <= ts[0] + 1e-6)),
      list.map(hit => name(hit) + ' @' + hit.t.toFixed(0)).join(' | '));
    check('The map\'s own arrows follow its camera (3D turned to look west: right is north)',
      (() => { m.setCamera({ t: mid(3, 4, 4), yaw: Math.PI / 2, pitch: 0.6, dist: 2500, mode: '3d' }, true); return m.axisFor('right').word === 'north' && m.axisFor('up').word === 'west'; })());
    check('Arrow keys are free for the page over the map, not on a slider or in a text box',
      m.ownsKey({ code: 'ArrowLeft', target: document.body }) && !m.ownsKey({ code: 'ArrowLeft', target: Object.assign(document.createElement('input'), { type: 'range' }) })
      && !m.ownsKey({ code: 'ArrowUp', target: document.createElement('textarea') }) && !m.ownsKey({ code: 'ArrowUp', target: document.body, ctrlKey: true }));
    // A part of the page that names the keys it uses keeps only those, and only while it has the keyboard itself.
    const keeper = document.createElement('div'), inside = document.createElement('button'), keepsAll = document.createElement('div'), strip = document.createElement('canvas');
    keeper.setAttribute('data-keeps-arrows', 'ArrowUp ArrowDown PageUp PageDown');
    strip.setAttribute('data-keeps-arrows', 'ArrowUp ArrowDown');
    keepsAll.setAttribute('data-keeps-arrows', '');
    keeper.append(inside);
    document.body.append(keeper, keepsAll, strip);
    try {
      const owns = (code, target) => m.ownsKey({ code, target });
      check('A list marked with the keys it scrolls by keeps only those, and a name or button clicked inside it keeps none; the side strip keeps up and down only',
        !owns('ArrowDown', keeper) && !owns('PageUp', keeper) && owns('ArrowLeft', keeper) && owns('ArrowDown', inside) && owns('ArrowRight', inside)
        && !owns('ArrowUp', strip) && owns('ArrowLeft', strip) && owns('PageDown', strip) && !owns('ArrowLeft', keepsAll),
        ['ArrowDown', 'PageUp', 'ArrowLeft'].map(c => c + ' on the list: ' + (owns(c, keeper) ? 'free' : 'kept')).join(', '));
    } finally { keeper.remove(); keepsAll.remove(); strip.remove(); }
    // A nebula's dashed sphere crossing the filled cubes of the opened sector: the cube is taken, the nebula comes last
    // on the line. Its middle still comes first, except with one level of cubes shown (only that level is clicked).
    const topCube = mid(3, 7, 4), sphereOver = (dx, r) => ({ name: 'Test', x: topCube[0] + dx, y: topCube[1] + 400, z: topCube[2], r });
    m.setScene({ origin: GRID_ORIGIN, sectorLy: S, sectors, area: { corner, cubeLy: 160, n: 8, cubes }, nebulae: [sphereOver(150, 150)] });
    m.setView({ nebulae: 'all' });
    m.setCamera({ t: [topCube[0], corner[1] + 640, topCube[2]], yaw: 0, pitch: 0.6, dist: 3000, mode: 'top' }, true);
    const rim = m.hitsAt(cx, cy).map(name);
    check('A click on the edge of a nebula\'s sphere over the opened sector\'s cubes takes the cube; the nebula comes last on the line',
      name(m.pick(cx, cy)) === 'cube 3,7,4' && rim[0] === 'cube 3,7,4' && rim[rim.length - 1] === 'nebula' && rim.length === 11, rim.join(' | '));
    m.setScene({ origin: GRID_ORIGIN, sectorLy: S, sectors, area: { corner, cubeLy: 160, n: 8, cubes }, nebulae: [sphereOver(0, 30)] });
    const middle = name(m.pick(cx, cy));
    m.setView({ cubeLevel: 3 });
    const sliced = m.hitsAt(cx, cy).map(name);
    m.setView({ cubeLevel: null, nebulae: 'near' });
    check('A nebula\'s middle comes first; with one level of cubes shown, that level\'s cube comes first and the nebula last',
      middle === 'nebula' && sliced.join(' | ') === 'cube 3,3,4 | sector 38,31,18 | sector 38,32,18 | nebula',
      middle + ' / ' + sliced.join(' | '));

    // ---- spec v4: the route check's stops. Four stops sent, one with no place (left out): three dots joined by two
    // lines, in route order; the dot at the focus is what the pointer finds there (before the cube it lies in), and
    // with Markers off there is neither line nor dot to find.
    m.setScene({ origin: GRID_ORIGIN, sectorLy: S, sectors, area: { corner, cubeLy: 160, n: 8, cubes } });
    m.setView({ nebulae: 'off', markers: true });
    const rFirst = mid(3, 7, 4);
    m.setMarkers({ route: [{ name: 'Stop A', x: rFirst[0], y: rFirst[1], z: rFirst[2], state: 'recorded' }, { name: 'Stop B', x: rFirst[0] + 300, y: rFirst[1], z: rFirst[2], state: 'not-recorded' },
      { name: 'No place', state: 'waiting' }, { name: 'Stop C', x: rFirst[0] + 600, y: rFirst[1], z: rFirst[2] + 100, state: 'failed' }] });
    m.setCamera({ t: rFirst, yaw: 0, pitch: 0.6, dist: 3000, mode: 'top' }, true);
    const rOn = m.stats(), rDot = m.pick(cx, cy), rBeside = m.pick(cx + 10, cy + 10);
    m.setView({ markers: false });
    const rOff = m.stats(), rHidden = m.pick(cx, cy);
    m.setView({ markers: true, nebulae: 'near' });
    check('Route check: the stops with a place are dots joined in route order by a faint line; the pointer on one finds it; Markers off shows neither',
      rOn.routeStops === 3 && rOn.routeLines === 2 && !!rDot && rDot.kind === 'stop' && rDot.item.name === 'Stop A' && name(rBeside) === 'cube 3,7,4'
      && rOff.routeLines === 0 && name(rHidden) === 'cube 3,7,4',
      'stops ' + rOn.routeStops + ', lines ' + rOn.routeLines + ' then ' + rOff.routeLines + '; at the dot: ' + (rDot ? rDot.kind + ' ' + (rDot.item && rDot.item.name) : 'nothing')
      + ', beside it: ' + name(rBeside) + ', Markers off: ' + name(rHidden));
    const rColours = ['waiting', 'checking', 'recorded', 'not-recorded', 'failed'].map(s => m.stopColour(s).join(','));
    check('Route check: each state has its own colour (blue while being checked), and a state not named is waiting',
      new Set(rColours).size === 5 && rColours[1] === '78,161,255' && m.stopColour('something else').join(',') === rColours[0], rColours.join(' / '));
    m.setMarkers({ route: [] });
    check('Route check: a route cleared takes its dots and line away', m.stats().routeStops === 0 && m.stats().routeLines === 0);
    // A stop not recorded is always one of your finds too, at the same place. Drawn on a canvas of its own (as the map
    // draws its markers): the find's green diamond lies over the stop's dot, and a gold ring round it over both, so the
    // stop still reads gold; a recorded stop gets no ring.
    const mc = document.createElement('canvas');
    mc.width = mc.height = 40;
    const mg = mc.getContext('2d', { willReadFrequently: true });
    const mInk = { text: [226, 232, 240], accent: [255, 113, 0], target: [34, 211, 238], find: [61, 220, 132] }, mGold = m.stopColour('not-recorded');
    const px = (x, y) => Array.from(mg.getImageData(x, y, 1, 1).data);
    const near = (a, b, by) => b.every((v, i) => Math.abs(a[i] - v) <= by);
    paintMarks(mg, [{ kind: 'stop', p: [20, 20], colour: mGold, ring: true }, { kind: 'find', p: [20, 20] }], 0, mInk, true);
    const mMiddle = px(20, 20), mRing = px(28, 19);
    mg.clearRect(0, 0, 40, 40);
    paintMarks(mg, [{ kind: 'stop', p: [20, 20], colour: m.stopColour('recorded'), ring: false }, { kind: 'find', p: [20, 20] }], 0, mInk, true);
    const mNone = px(28, 19);
    check('Route check: a stop not recorded reads gold over the find that lies on it (the find\'s green diamond in the middle, a gold ring round it); a recorded one has no ring',
      near(mMiddle, mInk.find, 4) && near(mRing, mGold, 28) && mRing[3] > 240 && mNone[3] === 0,
      'middle ' + mMiddle.join(',') + ', on the ring ' + mRing.join(',') + ' (gold ' + mGold.join(',') + '), recorded: ' + mNone.join(','));

    // ---- spec v5 C: Within reach. A sphere round a place and three systems sent, one with no place (left out): the
    // pointer on a dot finds it (before the cube it lies in), beside it the cube; Markers off shows nothing to find and
    // no sphere; null takes it all away.
    const wAt = mid(3, 7, 4);
    m.setMarkers({ reach: { x: wAt[0], y: wAt[1], z: wAt[2], r: 40, label: 'Within reach · 40 ly', systems: [
      { name: 'Near A', x: wAt[0], y: wAt[1], z: wAt[2], state: 'recorded' }, { name: 'Must B', x: wAt[0] + 300, y: wAt[1], z: wAt[2], state: null, must: true }, { name: 'No place', state: 'recorded' }] } });
    m.setCamera({ t: wAt, yaw: 0, pitch: 0.6, dist: 3000, mode: 'top' }, true);
    const wOn = m.stats(), wDot = m.pick(cx, cy), wBeside = m.pick(cx + 10, cy + 10);
    m.setView({ markers: false });
    const wOff = m.stats(), wHidden = m.pick(cx, cy);
    m.setView({ markers: true });
    m.setMarkers({ reach: null });
    const wGone = m.stats();
    check('Within reach: the systems with a place are dots; the pointer on one finds it; Markers off shows no sphere and nothing to find; null takes it away',
      wOn.reachDots === 2 && wOn.reachSphere === 40 && !!wDot && wDot.kind === 'reach' && wDot.item.name === 'Near A' && name(wBeside) === 'cube 3,7,4'
      && wOff.reachSphere === 0 && name(wHidden) === 'cube 3,7,4' && wGone.reachDots === 0 && wGone.reachSphere === 0,
      'dots ' + wOn.reachDots + ', sphere ' + wOn.reachSphere + ' then ' + wOff.reachSphere + '; at the dot: ' + (wDot ? wDot.kind + ' ' + (wDot.item && wDot.item.name) : 'nothing')
      + ', beside it: ' + name(wBeside) + ', Markers off: ' + name(wHidden) + ', after null: ' + wGone.reachDots);
    const wInk = ['recorded', 'name-only', 'not-recorded'].map(s => m.reachColour(s, false)), wMust = [null, 'recorded', 'not-recorded'].map(s => m.reachColour(s, true));
    check('Within reach: the route check\'s colours (green recorded, the same green fainter by name only, gold not recorded); one that must exist is hollow, grey until checked',
      wInk[0].colour.join() === m.stopColour('recorded').join() && wInk[1].colour.join() === wInk[0].colour.join() && wInk[1].alpha < 1 && wInk[0].alpha === 1
      && wInk[2].colour.join() === m.stopColour('not-recorded').join() && wInk.every(d => !d.hollow) && wMust.every(d => d.hollow && d.alpha === 1)
      && wMust[0].colour.join() === m.stopColour('waiting').join() && wMust[2].colour.join() === m.stopColour('not-recorded').join(),
      wInk.concat(wMust).map(d => d.colour.join(',') + '@' + d.alpha + (d.hollow ? ' hollow' : '')).join(' / '));
    // Drawn on a canvas of its own: the disc faint with its ring at the edge, a filled dot coloured to its middle, a
    // hollow one coloured round an empty middle.
    mg.clearRect(0, 0, 40, 40);
    paintReach(mg, null, [{ p: [10, 20], colour: m.stopColour('recorded'), alpha: 1, hollow: false }, { p: [30, 20], colour: m.stopColour('not-recorded'), alpha: 1, hollow: true }], mInk.accent);
    const wFilled = px(10, 20), wHollow = px(30, 20), wEdge = px(33, 20);
    mg.clearRect(0, 0, 40, 40);
    paintReach(mg, { p: [20, 20], r: 15 }, [], mInk.accent);
    const wInside = px(20, 20), wRim = px(35, 20);
    check('Within reach: a filled dot is coloured to its middle, a hollow one round an empty middle; the sphere is a faint disc with a ring at its edge',
      near(wFilled, m.stopColour('recorded'), 4) && wFilled[3] > 240 && wHollow[3] === 0 && near(wEdge, mGold, 40) && wEdge[3] > 200
      && wInside[3] > 0 && wInside[3] < 40 && wRim[3] > wInside[3] * 3,
      'filled ' + wFilled.join(',') + ', hollow middle ' + wHollow.join(',') + ', its ring ' + wEdge.join(',') + ', disc ' + wInside.join(',') + ', rim ' + wRim.join(','));
  } catch (e) {
    check('The map runs headless for these checks', false, e && e.message);
  } finally {
    if (m) m.dispose();
  }
  return { ok: checks.every(c => c.ok), checks };
}

return { create, supported, selftest };
})();
