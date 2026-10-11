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
// Studio.h, Studio.options), numbers and times in words (Studio.whole, Studio.count, Studio.credits,
// Studio.tons, Studio.ly, Studio.ago, and the dots between the parts of a line: Studio.dot, Studio.wideDot),
// the message at the bottom (Studio.toast), the dialog (Studio.ask, and Studio.nameLength for a name it asks
// for), the clipboard (Studio.copy, Studio.copyName), the whole-page message (Studio.cover), a From box that
// follows the game (Studio.fromBox), keeping one of the app's pictures on the page (Studio.follow) and going
// to another studio's page with something for it to do (Studio.open, or Studio.go for a link that does).
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

return { served, boxed, api, onEvent, follow, open, go, $, h, options, fromBox, whole, count, credits, tons, ly, ago, dot, wideDot, toast, ask, nameLength, copy, copyName, cover };
})();
