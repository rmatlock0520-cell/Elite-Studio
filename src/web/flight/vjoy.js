// STUDIO FLIGHT, VJOY: the card on Options that says whether vJoy is there and ready. Elite has no curves
// for sticks, only one dead zone per axis, so flying with Studio's curves needs a stick shaper (Joystick Gremlin today;
// Gimbalyn, Studio's own, next) that hands the reshaped stick to the game through vJoy, a free virtual joystick the
// player installs themselves. Studio never installs it, and looks for it only when this page opens or the player asks
// (GET /api/flight/vjoy): a player with no stick never loads any of it. The trainer, the stick check and the flight overlay
// work without vJoy, and the card says so. Loaded by options.html after ../studio.js; options.html calls FlightVJoy.init.
var FlightVJoy = (() => {
'use strict';
const { $, h } = Studio;
// The official places to get vJoy (the page opens them in the player's browser; Studio downloads nothing itself).
const SIGNED = { href: 'https://github.com/BrunnerInnovation/vJoy/releases/tag/v2.2.2.0', text: 'vJoy 2.2.2.0, signed for Windows 10 and 11' };
const GREMLINS = { href: 'https://github.com/jshafer817/vJoy/releases/tag/v2.1.9.1', text: 'vJoy 2.1.9.1, the build Joystick Gremlin\'s guide names (Windows 10)' };
// The stick shapers: a program of these holding vJoy is how it should be; any other is in the way.
const SHAPERS = { joystick_gremlin: 'Joystick Gremlin', gimbalyn: 'Gimbalyn' };
const AXIS = { X: 'X', Y: 'Y', Z: 'Z', RX: 'Rx', RY: 'Ry', RZ: 'Rz', U: 'Slider', V: 'Slider 2 (dial)' };
const WHY = 'Elite has no curves for sticks, only one dead zone per axis. To fly with the curves and dead zones you set on Stick setup, a stick '
  + 'shaper reshapes your stick and hands it to the game through vJoy, a free virtual joystick: Joystick Gremlin works this way, and '
  + 'so will Gimbalyn, Studio\'s own. Only that needs vJoy. The trainer, the stick check and the flight overlay work without it.';

let env = null;      // { call: Studio.api (its answers are not the flight data), toast } from options.html
let busy = false;    // a search is under way
let last = null;     // the last answer

function init(e) {
  env = e;
  search();
}

async function search() {
  if (busy) return;
  busy = true;
  draw(last, true);
  try {
    last = await env.call('GET', '/api/flight/vjoy');
  } catch (e) {
    // Studio itself did not answer: nothing is known about vJoy, so nothing is said about it.
    last = { state: 'unreachable', detail: e.message, need: [], missing: [] };
  } finally {
    busy = false;
  }
  draw(last, false);
}

async function act(path, button) {
  button.disabled = true;
  try {
    const answer = await env.call('POST', path, {});   // Studio takes a POST only with a JSON body
    if (answer && answer.state) { last = answer; draw(last, false); }
    else if (path.endsWith('/configure')) env.toast('Configure vJoy is opening. Change what it needs, press Apply, then press Search again here.');
  } catch (e) {
    env.toast(e.message, true);
  } finally {
    button.disabled = false;
  }
}

// ---- what the card says
const version = v => v ? 'vJoy ' + v : 'vJoy';
const axes = list => (list || []).map(a => AXIS[a] || a).join(', ');
const shaper = name => SHAPERS[String(name || '').toLowerCase()];
const holder = name => shaper(name) || name || 'another program';
const link = l => h('a', { href: l.href, target: '_blank', rel: 'noopener' }, l.text);
const gets = signedOnly => h('ul', { class: 'small vjoy-get' }, h('li', null, link(SIGNED)), signedOnly ? null : h('li', null, link(GREMLINS)));
// What device 1 has against what is needed: Elite's own vJoy axes, or (none bound yet) the three a shaper uses.
const hasAxes = s => s.fromBinds
  ? 'Device 1 has the axes your flight bindings use on vJoy (' + axes(s.need) + ').'
  : 'Device 1 has ' + axes(s.need) + ', the axes a stick shaper uses for roll, pitch and yaw. Elite\'s pitch, roll and yaw are not bound to vJoy yet: bind them to vJoy once a stick shaper feeds it.';

// Each state: whether all is well, its headline, what to do, and which buttons it needs.
function words(s) {
  const v = version(s.version), folder = s.folder || 'its folder';
  switch (s.state) {
    case 'ready': return { ok: true, say: v + ' is ready.', more: hasAxes(s) };
    case 'inUse': return shaper(s.owner)
      ? { ok: true, say: v + ' is ready, and ' + holder(s.owner) + ' is using it now.', more: 'That is normal while ' + holder(s.owner) + ' runs: it feeds vJoy your reshaped stick. ' + hasAxes(s) }
      : { say: 'vJoy\'s device 1 is held by ' + holder(s.owner) + '.',
          more: 'A stick shaper cannot feed vJoy while ' + holder(s.owner) + ' holds it. Close ' + holder(s.owner) + ', then press Search again.' };
    case 'notFound': return { say: 'vJoy is not installed.', why: true, get: true, browse: true,
      more: (s.leftovers ? 'Pieces of an earlier vJoy are still on this PC, but not vJoy itself. ' : '')
        + 'Install vJoy from one of these pages, restart Windows if it asks, then press Search again.' };
    case 'broken': return { say: v + ' is listed as installed, but its library is missing.', get: true, browse: true,
      more: 'Studio found no vJoyInterface.dll in ' + folder + '. Install vJoy again, or point Studio to the folder it is in.' };
    case 'reboot': return { say: 'vJoy is waiting for Windows to restart.', more: 'Its installer finishes after a restart. Restart Windows, then press Search again.' };
    case 'disabled': return { say: v + ' is turned off.', configure: 'Open Configure vJoy, tick "Enable vJoy", then press Search again.' };
    case 'blocked': return { say: 'Windows blocked vJoy\'s driver.', get: 'signed',
      more: 'This vJoy\'s driver is not signed for your version of Windows, so Windows will not load it. Install the signed vJoy below, then restart Windows.' };
    case 'noDriver': return { say: v + ' is installed, but its driver is not.', get: true, more: 'Install vJoy again (run its installer and let it finish), then restart Windows if it asks.' };
    case 'problem': return { say: 'Windows reports a problem with vJoy (code ' + s.problem + ').', get: true,
      more: 'Restart Windows. If it stays, install vJoy again, or look at "vJoy Device" in Device Manager.' };
    case 'absent': return { say: v + ' is installed, but its virtual joystick is not there.',
      configure: 'Restart Windows. If it is still missing, open Configure vJoy and check that "Enable vJoy" is ticked, or install vJoy again.' };
    case 'probeFailed': return { say: v + ' is installed, but Studio could not ask it.', more: (s.detail || 'No answer came.') + ' Press Search again; if it stays, restart Windows.' };
    case 'notRunning': return { say: v + ' is installed but not running.', configure: 'Open Configure vJoy, tick "Enable vJoy", then press Search again.' };
    case 'tooOld': return { say: v + ' is too old.', get: 'signed', more: 'Studio needs vJoy 2.1.8 or newer. Install the current one below.' };
    case 'noDevice': return { say: v + ' has no device 1 set up.',
      configure: 'Open Configure vJoy, pick the 1 tab, press Add Device, tick the axes ' + axes(s.need) + ', and press Apply. Then press Search again.' };
    case 'missingAxes': return { say: 'vJoy\'s device 1 lacks ' + axes(s.missing) + '.',
      configure: (s.fromBinds ? 'Your Elite flight bindings use ' + axes(s.need) + ' on vJoy. ' : 'A stick shaper uses ' + axes(s.need) + ' for roll, pitch and yaw. ')
        + (s.owner ? 'Close ' + holder(s.owner) + ' first (it holds vJoy). ' : '')
        + 'Open Configure vJoy, tick ' + axes(s.missing) + ' for device 1, and press Apply. Then press Search again.' };
    case 'unreachable': return { say: 'Studio could not look for vJoy just now.', more: (s.detail || '') + ' Press Search again in a moment.' };
    default: return { say: 'Studio could not tell how vJoy is.', more: '' };
  }
}

function draw(s, searching) {
  const card = $('#vjoy-card');
  if (!card) return;
  const status = $('#vjoy-status');
  if (!s) {
    status.className = 'vjoy-status';
    status.textContent = 'Looking for vJoy…';
    $('#vjoy-body').replaceChildren();
    return;
  }
  const w = words(s);
  card.classList.toggle('vjoy-ok', !!w.ok);
  card.classList.toggle('vjoy-todo', !w.ok);
  status.className = 'vjoy-status ' + (w.ok ? 'good' : 'warn');
  status.textContent = searching ? 'Looking again…' : w.say;
  const again = h('button', { class: 'btn small', onclick: () => search(), disabled: searching }, 'Search again');
  const browse = h('button', { class: 'btn small', onclick: e => act('/api/flight/vjoy/folder', e.target), title: 'For a vJoy installed somewhere Windows does not list' }, 'Choose its folder…');
  const forget = h('button', { class: 'btn small', onclick: e => act('/api/flight/vjoy/forget', e.target), title: 'Look again only where Windows says vJoy is' }, 'Forget the folder I chose');
  const configure = h('button', { class: 'btn small', onclick: e => act('/api/flight/vjoy/configure', e.target), title: 'vJoy\'s own settings program; Windows asks for permission' }, 'Open Configure vJoy');
  // The steps in Configure vJoy, and where it is when Studio cannot find it.
  const steps = w.configure ? w.configure + (s.configure ? '' : ' (Studio cannot find Configure vJoy in vJoy\'s folder: open it from the Start menu, or install vJoy again with its configuration program.)') : null;
  const where = s.dll ? s.dll.replace(/\\x64\\vJoyInterface\.dll$/i, '').replace(/\\vJoyInterface\.dll$/i, '') : s.folder;
  const facts = [where && 'in ' + where, s.chosen && 'the folder you chose', s.probe && s.probe.devices && s.probe.devices.length > 1 && 'devices ' + s.probe.devices.join(', '),
    s.driverMatch === false && 'its library and driver are different versions (that usually still works)'].filter(Boolean);
  // (replaceChildren would write a null out as the word "null", so the parts left out are dropped first)
  $('#vjoy-body').replaceChildren(...[
    w.why ? h('p', { class: 'small muted' }, WHY) : null,   // why it is needed, before how to get it
    w.more ? h('p', { class: 'small' }, w.more) : null,
    steps ? h('p', { class: 'small' }, steps) : null,
    w.get ? gets(w.get === 'signed') : null,
    facts.length ? h('p', { class: 'small muted' }, facts.join(' · ')) : null,
    h('div', { class: 'row' }, again, w.configure && s.configure ? configure : null, w.browse ? browse : null, s.chosen ? forget : null)].filter(Boolean));
}

return { init, search };
})();
