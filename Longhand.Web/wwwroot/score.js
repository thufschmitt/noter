'use strict';
// Longhand Score in the browser: write music on the staves with a pen or mouse; when the pen pauses, the server reads the ink
// (Longhand.Core's MusicInk) and the page shows it engraved. Ink it can't read stays, in blue, and is tried again with what comes
// next. The eraser rubs out ink and notation alike; so does scribbling over a symbol.

const $ = id => document.getElementById(id);
const PAUSE = 700;  // how long the pen rests before what it wrote is read
const ERASE_REACH = 10;

let pageWidth = 1000, pageHeight = 1200, scale = 1;
let pending = [];   // strokes not read yet: { id, points: [[x, y], ...] }
let current = null; // the stroke being written, or the eraser's path
let erasing = false, touchWrites = false, nextId = 1;
let readTimer = 0, reading = null, canUndo = false;

const canvas = $('scoreInk');
const ctx = canvas.getContext('2d');

async function api(path, body) {
  const r = await fetch('/api/score' + path, body === undefined ? {} : {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Longhand': '1' }, body: JSON.stringify(body),
  });
  if (!r.ok) throw new Error('The server said ' + r.status);
  return r.json();
}

function show(state) {
  $('engraving').innerHTML = state.svg;
  canUndo = state.canUndo;
  if (state.width !== pageWidth || state.height !== pageHeight) {
    pageWidth = state.width; pageHeight = state.height;
    fit();
  }
}

// ----- the page fitted to the window -----

function fit() {
  const avail = Math.min(window.innerWidth, document.documentElement.clientWidth || window.innerWidth);
  scale = Math.min(1, avail / pageWidth);
  const sheet = $('sheet'), page = $('page');
  sheet.style.width = pageWidth * scale + 'px';
  sheet.style.height = (pageHeight + 90) * scale + 'px';
  page.style.width = pageWidth + 'px';
  page.style.height = pageHeight + 'px';
  page.style.transform = `scale(${scale})`;
  const dpr = window.devicePixelRatio || 1;
  canvas.style.width = pageWidth + 'px';
  canvas.style.height = pageHeight + 'px';
  canvas.width = Math.round(pageWidth * scale * dpr);
  canvas.height = Math.round(pageHeight * scale * dpr);
  ctx.setTransform(scale * dpr, 0, 0, scale * dpr, 0, 0);
  redraw();
}

function inkColour() { return getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#2C4FC4'; }

function drawStroke(points, colour) {
  if (points.length === 0) return;
  ctx.strokeStyle = colour; ctx.fillStyle = colour;
  ctx.lineWidth = 2.6; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  if (points.length === 1) { ctx.beginPath(); ctx.arc(points[0][0], points[0][1], 1.6, 0, Math.PI * 2); ctx.fill(); return; }
  ctx.beginPath();
  ctx.moveTo(points[0][0], points[0][1]);
  for (let i = 1; i < points.length; i++) ctx.lineTo(points[i][0], points[i][1]);
  ctx.stroke();
}

function redraw() {
  ctx.save(); ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, canvas.width, canvas.height); ctx.restore();
  const colour = inkColour();
  for (const s of pending) drawStroke(s.points, colour);
  if (current && !erasing) drawStroke(current.points, colour);
  if (current && erasing) drawStroke(current.points, 'rgba(128, 128, 128, .35)');
}

// ----- writing -----

function pointOf(e) {
  const r = canvas.getBoundingClientRect();
  return [(e.clientX - r.left) / scale, (e.clientY - r.top) / scale];
}

canvas.addEventListener('pointerdown', e => {
  if (e.pointerType === 'touch' && !touchWrites) return; // a finger scrolls
  if (e.button !== 0 && e.button !== 5) return;
  e.preventDefault();
  canvas.setPointerCapture(e.pointerId);
  clearTimeout(readTimer);
  // The pen's eraser end (button 5) erases whatever tool is on.
  erasing = $('eraser').classList.contains('on') || e.button === 5 || (e.buttons & 32) !== 0;
  current = { id: nextId++, points: [pointOf(e)], pointer: e.pointerId };
  redraw();
});

canvas.addEventListener('pointermove', e => {
  if (!current || e.pointerId !== current.pointer) return;
  const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
  const colour = erasing ? 'rgba(128, 128, 128, .35)' : inkColour();
  for (const ev of events.length ? events : [e]) {
    const p = pointOf(ev), last = current.points[current.points.length - 1];
    if (Math.hypot(p[0] - last[0], p[1] - last[1]) < 0.5) continue;
    current.points.push(p);
    drawStroke([last, p], colour);
  }
});

function finish(e) {
  if (!current || e.pointerId !== current.pointer) return;
  const done = current;
  current = null;
  if (erasing) {
    erase(done.points);
  } else {
    pending.push({ id: done.id, points: done.points });
    scheduleRead();
  }
  redraw();
}
canvas.addEventListener('pointerup', finish);
canvas.addEventListener('pointercancel', e => { if (current && e.pointerId === current.pointer) { current = null; redraw(); } });

function scheduleRead() {
  clearTimeout(readTimer);
  readTimer = setTimeout(read, PAUSE);
}

/** Sends the ink waiting to be read; what the server turned into notation comes off the page. */
async function read() {
  if (pending.length === 0) return;
  if (reading) { await reading; if (pending.length) scheduleRead(); return; }
  const sent = pending.slice();
  reading = (async () => {
    try {
      const state = await api('/read', { strokes: sent.map(s => ({ id: s.id, points: s.points })) });
      const used = new Set(state.extra.used);
      pending = pending.filter(s => !used.has(s.id));
      show(state);
      redraw();
      if (state.extra.summary) toast(state.extra.summary);
    } catch (err) {
      toast("Couldn't read the ink: " + err.message);
    }
  })();
  await reading;
  reading = null;
}

/** Rubs out the ink waiting under the eraser's path here, and the notation under it on the server. */
async function erase(path) {
  const near = s => s.points.some(p => path.some(q => Math.hypot(p[0] - q[0], p[1] - q[1]) <= ERASE_REACH));
  pending = pending.filter(s => !near(s));
  redraw();
  try { show(await api('/erase', { path, reach: ERASE_REACH })); }
  catch (err) { toast("Couldn't erase: " + err.message); }
}

async function undo() {
  if (pending.length) { pending.pop(); redraw(); return; }
  try { show(await api('/undo', {})); }
  catch (err) { toast("Couldn't undo: " + err.message); }
}

// ----- playing -----

const audio = $('audio');
function setPlaying(on) {
  $('play').classList.toggle('on', on);
  $('playLabel').textContent = on ? 'Stop' : 'Play';
  $('playIcon').innerHTML = on ? '<rect x="6" y="6" width="12" height="12" rx="1"/>' : '<path d="M7 4v16l13-8z"/>';
}
async function togglePlay() {
  if (!audio.paused) { audio.pause(); audio.currentTime = 0; setPlaying(false); return; }
  await read(); // what was just written is part of it
  audio.src = '/api/score/play.wav?at=' + Date.now();
  setPlaying(true);
  try { await audio.play(); } catch (err) { setPlaying(false); toast("Couldn't play: " + err.message); }
}
audio.addEventListener('ended', () => setPlaying(false));
audio.addEventListener('error', () => setPlaying(false));

// ----- the tray -----

let toastTimer = 0;
function toast(text, seconds = 2.5) {
  const t = $('toast');
  t.textContent = text; t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, seconds * 1000);
}

function tool(which) {
  $('pen').classList.toggle('on', which === 'pen');
  $('eraser').classList.toggle('on', which === 'eraser');
}
$('pen').onclick = () => tool('pen');
$('eraser').onclick = () => tool('eraser');
$('undo').onclick = undo;
$('touch').onclick = () => { touchWrites = !touchWrites; $('touch').classList.toggle('on', touchWrites); canvas.style.touchAction = touchWrites ? 'pinch-zoom' : 'pan-y pinch-zoom'; };
$('clear').onclick = async () => {
  pending = []; redraw();
  try { show(await api('/clear', {})); toast('Cleared. Undo brings it back.'); }
  catch (err) { toast("Couldn't clear: " + err.message); }
};
$('play').onclick = togglePlay;

document.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); undo(); }
  else if (e.key === ' ' && e.target === document.body) { e.preventDefault(); togglePlay(); }
  else if (e.key === 'Escape') tool('pen');
});

window.addEventListener('resize', fit);
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', redraw);

(async () => {
  try { show(await api('')); }
  catch (err) { toast("Couldn't load the score: " + err.message, 8); }
  fit();
})();
