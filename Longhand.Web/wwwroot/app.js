'use strict';
// Longhand in the browser. The server runs the notebook (asking Claude, saving, conversations); this page draws it:
// the ruled sheet, the ink, Claude's notes in a handwriting font, and it does the two things only a browser can,
// measure text and draw the picture Claude reads.

// Page geometry: the same numbers as the Windows app (PageLayout.cs), so a page looks alike wherever it is opened.
const PAGE_MAX = 880, LINE = 40, TOP = 120, MARGIN_X = 76, RIGHT_PAD = 36, FONT_PX = 26, TEXT_NUDGE = 3;
const PEN = 2.4, PEN_HALF = 1.2;
const HAND = '"Ink Free","Segoe Print","Bradley Hand","Caveat","Comic Neue","Comic Sans MS","Chalkboard SE",cursive';

const $ = id => document.getElementById(id);
const scroller = $('scroller'), sheet = $('sheet'), inkCanvas = $('ink'), notesLayer = $('notes');
const inkCtx = inkCanvas.getContext('2d');

const strokes = new Map();   // id -> { id, pts: [[x, y], ...] }, in page coordinates
const notes = new Map();     // id -> { id, y, hint, text, lines, drawLines, el, tick, drawings: Map }
let maxId = 0, sheetWidth = 800, erasing = false, touchWrite = false, socket = null, state = { busy: false, canAsk: false };

const dark = () => matchMedia('(prefers-color-scheme: dark)').matches;
const inkColor = () => (dark() ? '#E6E8EC' : '#22262C');
const claudeColor = () => (dark() ? '#93A9FF' : '#2C4FC4');
const textWidth = () => Math.max(200, sheetWidth - MARGIN_X - RIGHT_PAD - 8);

// ---------- talking to the server ----------

function send(message) {
  if (socket && socket.readyState === 1) socket.send(JSON.stringify(message));
}

function connect() {
  socket = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws');
  socket.onopen = () => {
    if (devMode && connectedOnce) { location.reload(); return; }
    connectedOnce = true;
    send({ t: 'hello', width: sheetWidth, zone: Intl.DateTimeFormat().resolvedOptions().timeZone });
  };
  socket.onmessage = e => {
    const m = JSON.parse(e.data);
    const handler = handlers[m.t];
    if (handler) handler(m);
  };
  socket.onclose = () => {
    toast('Disconnected from the server. Reconnecting…', 2);
    setTimeout(connect, devMode ? 500 : 1500);
  };
}

// Development only (the server was started with --www): reload when the page's files change, and when the server comes back after a restart.
let devMode = false, connectedOnce = false;

const reply = (m, value) => send({ t: 'reply', req: m.req, value });
const fail = m => send({ t: 'reply', req: m.req, error: true });

// ---------- the sheet ----------

function layout() {
  const w = Math.min(scroller.clientWidth, PAGE_MAX);
  if (w <= 0) return;
  const changed = Math.abs(w - sheetWidth) > 0.5;
  sheetWidth = w;
  sheet.style.width = w + 'px';
  for (const n of notes.values()) n.el.style.width = textWidth() + 'px';
  updateSelection();
  const h = scroller.clientHeight, dpr = devicePixelRatio || 1;
  inkCanvas.width = Math.round(w * dpr);
  inkCanvas.height = Math.round(h * dpr);
  inkCanvas.style.width = w + 'px';
  inkCanvas.style.height = h + 'px';
  inkCanvas.style.marginBottom = -h + 'px';
  updateHeight();
  redraw();
  return changed;
}

function contentBottom() {
  let bottom = TOP;
  for (const s of strokes.values()) bottom = Math.max(bottom, bounds(s).bottom);
  for (const n of notes.values()) bottom = Math.max(bottom, n.y + (n.lines + n.drawLines + 1) * LINE);
  return bottom;
}

// The sheet always runs well past the last ink, so there are fresh lines to write on.
function updateHeight() {
  const viewport = Math.max(scroller.clientHeight, 400);
  sheet.style.height = Math.max(viewport, contentBottom() + viewport * 0.75) + 'px';
}

function bounds(s) {
  if (!s.box) {
    let l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
    for (const [x, y] of s.pts) { l = Math.min(l, x); r = Math.max(r, x); t = Math.min(t, y); b = Math.max(b, y); }
    s.box = { left: l - PEN_HALF, right: r + PEN_HALF, top: t - PEN_HALF, bottom: b + PEN_HALF };
  }
  return s.box;
}

// ---------- ink ----------

function tracePath(c, pts) {
  c.beginPath();
  c.moveTo(pts[0][0], pts[0][1]);
  if (pts.length === 1) c.lineTo(pts[0][0] + 0.01, pts[0][1]);
  for (let i = 1; i < pts.length - 1; i++) {
    c.quadraticCurveTo(pts[i][0], pts[i][1], (pts[i][0] + pts[i + 1][0]) / 2, (pts[i][1] + pts[i + 1][1]) / 2);
  }
  if (pts.length > 1) c.lineTo(pts[pts.length - 1][0], pts[pts.length - 1][1]);
}

function paintStroke(c, pts, color) {
  tracePath(c, pts);
  c.strokeStyle = color;
  c.lineWidth = PEN;
  c.lineCap = c.lineJoin = 'round';
  c.stroke();
}

function viewTransform() {
  const dpr = devicePixelRatio || 1;
  inkCtx.setTransform(dpr, 0, 0, dpr, 0, -scroller.scrollTop * dpr);
}

function redraw() {
  inkCtx.setTransform(1, 0, 0, 1, 0, 0);
  inkCtx.clearRect(0, 0, inkCanvas.width, inkCanvas.height);
  viewTransform();
  const from = scroller.scrollTop - 10, to = from + scroller.clientHeight + 20, color = inkColor();
  for (const s of strokes.values()) {
    const b = bounds(s);
    if (b.bottom >= from && b.top <= to) paintStroke(inkCtx, s.pts, color);
  }
}
scroller.addEventListener('scroll', redraw, { passive: true });

let active = null; // the pointer that is writing, erasing or scrolling right now

const pagePoint = e => {
  const r = inkCanvas.getBoundingClientRect();
  return [Math.round((e.clientX - r.left) * 10) / 10, Math.round((e.clientY - r.top + scroller.scrollTop) * 10) / 10];
};

function distanceToSegment(px, py, a, b) {
  const dx = b[0] - a[0], dy = b[1] - a[1], len = dx * dx + dy * dy;
  const t = len === 0 ? 0 : Math.max(0, Math.min(1, ((px - a[0]) * dx + (py - a[1]) * dy) / len));
  return Math.hypot(px - (a[0] + t * dx), py - (a[1] + t * dy));
}

function eraseAt(x, y, erased) {
  for (const s of [...strokes.values()]) {
    const b = bounds(s);
    if (x < b.left - 10 || x > b.right + 10 || y < b.top - 10 || y > b.bottom + 10) continue;
    let hit = s.pts.length === 1 && Math.hypot(x - s.pts[0][0], y - s.pts[0][1]) < 10;
    for (let i = 1; i < s.pts.length && !hit; i++) hit = distanceToSegment(x, y, s.pts[i - 1], s.pts[i]) < 10;
    if (hit) {
      strokes.delete(s.id);
      erased.push(s.id);
    }
  }
}

inkCanvas.addEventListener('pointerdown', e => {
  if (active) return;
  const p = pagePoint(e);
  if (e.pointerType === 'touch' && !touchWrite) {
    // A finger scrolls; on the handles of a selected part it edits instead; a tap on a part selects it.
    const grab = selection && grabAt(p);
    if (grab) { startEdit(e, grab, p); return; }
    active = { id: e.pointerId, mode: 'pan', lastY: e.clientY, p, t0: performance.now(), travelled: 0 };
    inkCanvas.setPointerCapture(e.pointerId);
    return;
  }
  // The pen's barrel button, or a mouse's right button, makes the pen a lasso for the parts of drawings that can be edited.
  const lasso = (e.buttons & 2) !== 0 && (e.buttons & 32) === 0 && !erasing;
  if (e.pointerType === 'mouse' && e.button !== 0 && !lasso) return;
  inkCanvas.setPointerCapture(e.pointerId);
  if (lasso) {
    active = { id: e.pointerId, mode: 'lasso', pts: [p] };
    e.preventDefault();
    return;
  }
  const wipe = erasing || (e.pointerType === 'pen' && (e.buttons & 32));
  if (!wipe) {
    const grab = selection && grabAt(p);
    if (grab) { startEdit(e, grab, p); e.preventDefault(); return; }
    if (selection) clearSelection(); // writing elsewhere puts the selection away
  }
  if (wipe) {
    active = { id: e.pointerId, mode: 'erase', erased: [] };
    eraseAt(p[0], p[1], active.erased);
    redraw();
  } else {
    active = { id: e.pointerId, mode: 'draw', pts: [p], t0: performance.now() };
    viewTransform();
    paintStroke(inkCtx, [p], inkColor());
  }
  e.preventDefault();
});

inkCanvas.addEventListener('pointermove', e => {
  if (!active || active.id !== e.pointerId) return;
  if (active.mode === 'pan') {
    scroller.scrollTop -= e.clientY - active.lastY;
    active.travelled += Math.abs(e.clientY - active.lastY);
    active.lastY = e.clientY;
    return;
  }
  if (active.mode === 'edit') { moveEdit(active, pagePoint(e)); return; }
  for (const ev of (e.getCoalescedEvents ? e.getCoalescedEvents() : [e])) {
    const p = pagePoint(ev);
    if (active.mode === 'erase') {
      eraseAt(p[0], p[1], active.erased);
    } else if (active.mode === 'lasso') {
      active.pts.push(p);
    } else {
      const last = active.pts[active.pts.length - 1];
      if (Math.hypot(p[0] - last[0], p[1] - last[1]) < 0.5) continue;
      active.pts.push(p);
      viewTransform();
      paintStroke(inkCtx, [last, p], inkColor());
    }
  }
  if (active.mode === 'erase') redraw();
  if (active.mode === 'lasso') { redraw(); paintLasso(active.pts); }
});

// A tap is a pen or mouse press that hardly moves and is over quickly.
const isTap = pts => pts.every(q => Math.hypot(q[0] - pts[0][0], q[1] - pts[0][1]) < 5);
// A dot right after other ink and next to it is part of the writing (the dot of an i, a full stop), not a tap on something.
function followsInk(t0, p) {
  return lastInk && t0 - lastInk.t < 1000 && lastInk.pts.some(q => Math.hypot(q[0] - p[0], q[1] - p[1]) < 70);
}
let lastInk = null;

function finishPointer(e) {
  if (!active || active.id !== e.pointerId) return;
  const done = active;
  active = null;
  if (done.mode === 'edit') {
    finishEdit(done, pagePoint(e));
  } else if (done.mode === 'lasso') {
    lassoSelect(done.pts);
    redraw();
  } else if (done.mode === 'pan') {
    if (done.travelled < 6 && performance.now() - done.t0 < 400) tapAt(done.p);
  } else if (done.mode === 'draw') {
    const now = performance.now();
    if (now - done.t0 < 400 && isTap(done.pts) && !followsInk(done.t0, done.pts[0]) && tapAt(done.pts[0], true)) {
      redraw(); // the tap selected something: it leaves no dot
      return;
    }
    const stroke = { id: ++maxId, pts: done.pts.length === 1 ? [done.pts[0], [done.pts[0][0] + 0.1, done.pts[0][1]]] : done.pts };
    strokes.set(stroke.id, stroke);
    lastInk = { t: now, pts: done.pts.slice(-40) };
    send({ t: 'stroke', stroke });
    updateHeight();
    redraw();
  } else if (done.mode === 'erase' && done.erased.length > 0) {
    send({ t: 'erase', ids: done.erased });
  }
}
inkCanvas.addEventListener('pointerup', finishPointer);
inkCanvas.addEventListener('pointercancel', finishPointer);
inkCanvas.addEventListener('contextmenu', e => e.preventDefault());

// ---------- editing Claude's layouts ----------

// A layout is shown in a frame that runs nothing. The page reaches into the frame (it is the same origin) to find the parts the
// model marked edit (data-edit) and the containers that accept drops (data-drop). What the user does is shown at once and reported
// to the server as a structural change (this part into that container at that place, turned by so many degrees, deleted); the
// server changes the layout, which is the truth, and sends it back. Nothing goes to the model until the user asks.
//
// Tap a part with the pen (or click it) to select it: a frame appears with a knob to turn it and a cross to delete it, and dragging
// inside the frame moves it: drop it into a container that accepts drops. Hold the pen's barrel button (or the mouse's right button)
// and circle parts to select several.

let selection = null; // { d, els: [elements in d's frame] }

const frameDoc = d => d.el.contentDocument;
const frameScale = d => (d.nw > 0 && d.w < d.nw - 0.5 ? d.w / d.nw : 1);
const wrapAngle = a => ((((a + 180) % 360) + 360) % 360) - 180;

// Page pixels <-> the frame's own pixels.
const toFrame = (d, px, py) => { const s = frameScale(d); return [(px - MARGIN_X) / s, (py - d.top) / s]; };
function pageRect(d, el) {
  const r = el.getBoundingClientRect(), s = frameScale(d);
  return { left: MARGIN_X + r.left * s, top: d.top + r.top * s, right: MARGIN_X + r.right * s, bottom: d.top + r.bottom * s };
}
const area = r => Math.max(0, r.right - r.left) * Math.max(0, r.bottom - r.top);
const rectHas = (r, x, y, tol = 0) => x >= r.left - tol && x <= r.right + tol && y >= r.top - tol && y <= r.bottom + tol;

function eachDrawing(fn) {
  for (const n of notes.values()) for (const d of n.drawings.values()) if (d.edit && d.kind === 'layout' && d.w > 0 && frameDoc(d)) fn(d, n);
}

// The smallest editable part under a point of the page.
function editableAt(px, py) {
  let best = null;
  eachDrawing(d => {
    if (!rectHas({ left: MARGIN_X, top: d.top, right: MARGIN_X + d.w, bottom: d.top + d.h }, px, py, 8)) return;
    for (const el of frameDoc(d).querySelectorAll('[data-edit]')) {
      const r = pageRect(d, el);
      if (rectHas(r, px, py, 4) && (!best || area(r) < best.area)) best = { d, el, area: area(r) };
    }
  });
  return best;
}

// ----- the selection and its frame -----

const selEl = document.createElement('div');
selEl.id = 'selection';
selEl.hidden = true;
selEl.innerHTML = '<div class="knob rotate" title="Turn">↻</div><div class="knob delete" title="Delete">×</div>';
notesLayer.append(selEl);

function select(d, els) {
  // A part inside another selected part moves with it already.
  els = els.filter(a => !els.some(b => b !== a && b.contains(a)));
  if (els.length === 0) { clearSelection(); return; }
  selection = { d, els };
  updateSelection();
}

function clearSelection() {
  selection = null;
  selEl.hidden = true;
}

// The frame around the selected parts, in page pixels.
function selectionFrame() {
  if (!selection) return null;
  const { d, els } = selection;
  if (!d.w || !frameDoc(d)) return null;
  let l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
  for (const el of els) {
    if (!el.isConnected) continue;
    const q = pageRect(d, el);
    l = Math.min(l, q.left); t = Math.min(t, q.top); r = Math.max(r, q.right); b = Math.max(b, q.bottom);
  }
  if (!isFinite(l)) return null;
  const PAD = 6;
  return { left: l - PAD, top: t - PAD, right: r + PAD, bottom: b + PAD, cx: (l + r) / 2, cy: (t + b) / 2 };
}

function updateSelection() {
  const f = selectionFrame();
  if (!f) { clearSelection(); return; }
  Object.assign(selEl.style, { left: f.left + 'px', top: f.top + 'px', width: f.right - f.left + 'px', height: f.bottom - f.top + 'px' });
  selEl.hidden = false;
}

// What a press at a page point lands on: the delete cross, the turn knob, or the inside of the frame (to move).
function grabAt(p) {
  const f = selectionFrame();
  if (!f) return null;
  if (Math.hypot(p[0] - f.right, p[1] - f.top) <= 15) return 'delete';
  if (Math.hypot(p[0] - f.cx, p[1] - (f.top - 30)) <= 15) return 'rotate';
  if (p[0] >= f.left && p[0] <= f.right && p[1] >= f.top && p[1] <= f.bottom) return 'move';
  return null;
}

// A tap at a page point: selects the part under it (true), or, for a touch, puts the selection away.
function tapAt(p, keepIfNothing) {
  const hit = editableAt(p[0], p[1]);
  if (hit) { select(hit.d, [hit.el]); return true; }
  if (!keepIfNothing) clearSelection();
  return false;
}

// ----- the lasso -----

function paintLasso(pts) {
  if (pts.length < 2) return;
  viewTransform();
  inkCtx.save();
  inkCtx.setLineDash([6, 5]);
  inkCtx.lineWidth = 1.5;
  inkCtx.strokeStyle = claudeColor();
  inkCtx.beginPath();
  inkCtx.moveTo(pts[0][0], pts[0][1]);
  for (const q of pts) inkCtx.lineTo(q[0], q[1]);
  inkCtx.closePath();
  inkCtx.stroke();
  inkCtx.restore();
}

function insidePolygon(x, y, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i], [xj, yj] = poly[j];
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

// The editable parts that the loop mostly encloses (in one layout: the one with the most).
function lassoSelect(poly) {
  if (poly.length < 3 || isTap(poly)) { // a barrel press without a loop selects what is under it
    tapAt(poly[0]);
    return;
  }
  let best = null;
  eachDrawing(d => {
    const els = [];
    for (const el of frameDoc(d).querySelectorAll('[data-edit]')) {
      const r = pageRect(d, el);
      let inside = 0;
      for (let i = 0; i < 5; i++) for (let j = 0; j < 5; j++)
        if (insidePolygon(r.left + ((r.right - r.left) * (i + 0.5)) / 5, r.top + ((r.bottom - r.top) * (j + 0.5)) / 5, poly)) inside++;
      if (inside >= 13) els.push(el);
    }
    if (els.length > 0 && (!best || els.length > best.els.length)) best = { d, els };
  });
  if (best) select(best.d, best.els); else clearSelection();
}

// ----- moving, turning and deleting -----

const rotationOf = el => parseFloat(el.dataset.rot || '0') || 0;
const turned = (el, degrees) => { el.style.transform = degrees ? `rotate(${degrees}deg)` : ''; };

function startEdit(e, kind, p) {
  inkCanvas.setPointerCapture(e.pointerId);
  const { d, els } = selection, f = selectionFrame();
  const items = els.map(el => ({ el, rot: rotationOf(el), style: el.getAttribute('style') }));
  active = {
    id: e.pointerId, mode: 'edit', kind, d, items, start: p, drop: null, moved: false, degrees: 0,
    center: [f.cx, f.cy], angle: Math.atan2(p[1] - f.cy, p[0] - f.cx),
  };
  if (kind === 'move') for (const it of items) Object.assign(it.el.style, { pointerEvents: 'none', opacity: '0.55', position: 'relative', zIndex: '5' });
}

// The container that accepts drops under a point of the page (the smallest one, and never one inside what is being dragged),
// and where among its parts the point falls: before which part, and so which place.
function dropAt(a, p) {
  const doc = frameDoc(a.d);
  let best = null;
  for (const t of doc.querySelectorAll('[data-drop]')) {
    if (a.items.some(it => it.el === t || it.el.contains(t))) continue;
    const r = pageRect(a.d, t);
    if (rectHas(r, p[0], p[1]) && (!best || area(r) < best.area)) best = { t, area: area(r) };
  }
  if (!best) return null;
  const t = best.t, row = t.classList.contains('row');
  const kids = [...t.children].filter(c => c.classList.contains('n') && !a.items.some(it => it.el === c));
  let index = kids.length;
  for (let i = 0; i < kids.length; i++) {
    const r = pageRect(a.d, kids[i]), mid = row ? (r.left + r.right) / 2 : (r.top + r.bottom) / 2;
    if ((row ? p[0] : p[1]) < mid) { index = i; break; }
  }
  return { t, index, kids, row };
}

// A line where the part would land.
function showDropMark(a, drop) {
  const doc = frameDoc(a.d);
  let mark = doc.getElementById('dropmark');
  if (!drop) { if (mark) mark.remove(); return; }
  if (!mark) {
    mark = doc.createElement('div');
    mark.id = 'dropmark';
    mark.style.cssText = 'position:absolute;background:currentColor;border-radius:2px;pointer-events:none;z-index:9';
    doc.body.append(mark);
  }
  const { t, index, kids, row } = drop, scroll = doc.documentElement;
  const r = (kids[index] || kids[kids.length - 1] || t).getBoundingClientRect(), tr = t.getBoundingClientRect();
  if (kids.length === 0) Object.assign(mark.style, { left: tr.left + 8 + 'px', top: tr.top + 8 + 'px', width: Math.max(10, tr.width - 16) + 'px', height: '3px' });
  else if (row) Object.assign(mark.style, { left: (index < kids.length ? r.left - 7 : r.right + 5) + scroll.scrollLeft + 'px', top: r.top + 'px', width: '3px', height: r.height + 'px' });
  else Object.assign(mark.style, { left: r.left + 'px', top: (index < kids.length ? r.top - 5 : r.bottom + 3) + scroll.scrollTop + 'px', width: r.width + 'px', height: '3px' });
}

function moveEdit(a, p) {
  if (a.kind === 'delete') return;
  if (a.kind === 'move') {
    const s = frameScale(a.d), dx = (p[0] - a.start[0]) / s, dy = (p[1] - a.start[1]) / s;
    for (const it of a.items) it.el.style.transform = `translate(${dx}px, ${dy}px)` + (it.rot ? ` rotate(${it.rot}deg)` : '');
    a.drop = dropAt(a, p);
    showDropMark(a, a.drop);
    a.moved = a.moved || Math.hypot(p[0] - a.start[0], p[1] - a.start[1]) > 4;
  } else {
    const angle = Math.atan2(p[1] - a.center[1], p[0] - a.center[0]);
    a.degrees += wrapAngle(((angle - a.angle) * 180) / Math.PI);
    a.angle = angle;
    for (const it of a.items) turned(it.el, Math.round((it.rot + a.degrees) * 10) / 10);
    a.moved = a.moved || Math.abs(a.degrees) > 0.5;
  }
  updateSelection();
}

function reportEdit(d, changes) {
  send({ t: 'edit', note: d.note, drawing: d.id, changes });
}

function finishEdit(a, p) {
  if (a.kind === 'delete') {
    if (selection && grabAt(p) === 'delete') deleteSelection();
    return;
  }
  showDropMark(a, null);
  const restore = () => { for (const it of a.items) { if (it.style === null) it.el.removeAttribute('style'); else it.el.setAttribute('style', it.style); } };
  if (!a.moved) { restore(); return; }
  if (a.kind === 'move') {
    if (!a.drop) { restore(); updateSelection(); return; } // dropped where nothing accepts it: it goes back
    const { t, index, kids } = a.drop, before = kids[index] || null;
    restore();
    for (const it of a.items) t.insertBefore(it.el, before); // shown at once; the server's layout follows
    reportEdit(a.d, a.items.map((it, k) => ({ id: it.el.id, op: 'move', to: t.id, index: index + k })));
  } else {
    for (const it of a.items) { it.el.dataset.rot = String(it.rot + a.degrees); }
    reportEdit(a.d, a.items.map(it => ({ id: it.el.id, op: 'rotate', deg: Math.round(a.degrees * 10) / 10 })));
  }
  updateSelection();
}

function deleteSelection() {
  if (!selection) return;
  const { d, els } = selection, changes = els.map(el => ({ id: el.id, op: 'delete' }));
  for (const el of els) el.remove();
  clearSelection();
  reportEdit(d, changes);
}

// ---------- Claude's notes ----------

function addNote(m) {
  const el = document.createElement('div');
  el.className = 'note' + (m.hint ? ' hint' : '') + (m.typed ? ' typed' : '');
  el.textContent = m.text;
  el.style.left = MARGIN_X + 'px';
  el.style.width = textWidth() + 'px';
  el.style.top = m.y + TEXT_NUDGE + 'px';
  const tick = document.createElement('div');
  tick.className = 'tick' + (m.hint ? ' hint' : '') + (m.typed ? ' typed' : '');
  tick.style.left = MARGIN_X - 31 + 'px';
  tick.style.top = m.y + LINE * 0.35 + 'px';
  tick.style.height = '4px';
  notesLayer.append(tick, el);
  notes.set(m.id, { id: m.id, y: m.y, hint: m.hint, text: m.text, lines: 1, drawLines: 0, el, tick, drawings: new Map() });
}

function removeNote(n) {
  if (selection && n.drawings.has(selection.d.id)) clearSelection();
  n.el.remove();
  n.tick.remove();
  for (const d of n.drawings.values()) d.el.remove();
}

// ---------- blocks: Claude's tables, laid-out text and drawings ----------

// The server sends each block as a whole document with a stand-in for the ink colour, which follows the theme.
const inked = (doc, color) => doc.split('%INK%').join(color);

// A block is shown in a frame that runs no scripts and loads nothing (the document's own policy says so too). It shares the
// page's origin only so the page can measure it; it takes no input, so writing over it reaches the ink as usual.
function showBlock(d) {
  d.loaded = new Promise(resolve => d.el.addEventListener('load', resolve, { once: true }));
  d.el.srcdoc = inked(d.doc, claudeColor());
}

function colorDrawings() {
  for (const n of notes.values()) for (const d of n.drawings.values()) showBlock(d);
}

// Width over height of a block laid out at the given width, as tall as its content.
async function blockAspect(d, width) {
  await d.loaded;
  const doc = d.el.contentDocument;
  if (!doc) return 0;
  if (doc.fonts) await doc.fonts.ready;
  Object.assign(d.el.style, { width: width + 'px', height: '1px', transform: '' });
  const height = Math.max(doc.documentElement.scrollHeight, doc.body ? doc.body.scrollHeight : 0, 1);
  return width / height;
}

// ---------- what the server tells the page ----------

const handlers = {
  clear() {
    clearSelection();
    strokes.clear();
    for (const n of notes.values()) removeNote(n);
    notes.clear();
    redraw();
  },
  note: addNote,
  text(m) {
    const n = notes.get(m.id);
    if (!n) return;
    n.text = m.text;
    n.el.textContent = m.text;
  },
  noteRemoved(m) {
    const n = notes.get(m.id);
    if (!n) return;
    removeNote(n);
    notes.delete(m.id);
    updateHeight();
  },
  drawing(m) {
    const n = notes.get(m.note);
    if (!n) return;
    const el = document.createElement('iframe');
    el.className = 'drawing';
    el.title = m.alt;
    el.setAttribute('sandbox', 'allow-same-origin');
    el.setAttribute('scrolling', 'no');
    el.tabIndex = -1;
    notesLayer.append(el);
    const d = { id: m.id, note: m.note, doc: m.doc, kind: m.kind, text: m.text, edit: m.edit, el, top: 0, w: 0, h: 0, nw: 0, nh: 0 };
    n.drawings.set(m.id, d);
    showBlock(d);
  },
  // The block was changed (by an edit, by the model, or by undoing one): show it again. The server measures it next. What was
  // selected stays selected, once the new frame has loaded.
  drawingChanged(m) {
    const n = notes.get(m.note), d = n && n.drawings.get(m.id);
    if (!d) return;
    const keep = selection && selection.d === d ? selection.els.map(el => el.id).filter(Boolean) : [];
    if (keep.length) clearSelection();
    Object.assign(d, { doc: m.doc, kind: m.kind, text: m.text, edit: m.edit });
    d.el.title = m.alt;
    showBlock(d);
    if (keep.length) d.loaded.then(() => {
      const doc = frameDoc(d);
      if (doc && !selection) select(d, keep.map(id => doc.getElementById(id)).filter(Boolean));
    });
  },
  drawingsCleared(m) {
    const n = notes.get(m.note);
    if (!n) return;
    if (selection && n.drawings.has(selection.d.id)) clearSelection();
    for (const d of n.drawings.values()) d.el.remove();
    n.drawings.clear();
  },
  place(m) {
    const n = notes.get(m.id);
    if (!n) return;
    n.y = m.y;
    n.lines = m.lines;
    n.drawLines = m.drawLines;
    n.el.style.top = m.y + TEXT_NUDGE + 'px';
    n.tick.style.top = m.y + LINE * 0.35 + 'px';
    n.tick.style.height = Math.max(4, (m.lines + m.drawLines) * LINE - LINE * 0.35 + 4) + 'px';
    for (const p of m.drawings) {
      const d = n.drawings.get(p.id);
      if (!d) continue;
      Object.assign(d, { top: p.top, w: p.w, h: p.h, nw: p.nw, nh: p.nh });
      // Laid out at its natural size, then scaled down if it is taller than a block may be.
      Object.assign(d.el.style, { top: p.top + 'px', width: p.nw + 'px', height: p.nh + 'px', transform: p.w < p.nw - 0.5 ? `scale(${p.w / p.nw})` : '' });
    }
    updateHeight();
    updateSelection();
  },
  page(m) {
    strokes.clear();
    for (const s of m.strokes) strokes.set(s.id, s);
    maxId = Math.max(maxId, m.maxId);
    updateHeight();
    scroller.scrollTop = 0;
    redraw();
  },
  move(m) {
    for (const id of m.ids) {
      const s = strokes.get(id);
      if (!s) continue;
      for (const p of s.pts) p[1] += m.dy;
      s.box = null;
    }
    updateHeight();
    redraw();
  },
  delete(m) {
    for (const id of m.ids) strokes.delete(id);
    redraw();
  },
  add(m) {
    for (const s of m.strokes) {
      strokes.set(s.id, s);
      maxId = Math.max(maxId, s.id);
    }
    updateHeight();
    redraw();
  },
  async measure(m) {
    try {
      if (document.fonts) await document.fonts.ready;
      const n = notes.get(m.note);
      if (!n) return reply(m, 1);
      n.el.style.width = m.width + 'px';
      n.el.textContent = m.text;
      const lines = Math.round(n.el.getBoundingClientRect().height / LINE);
      const aspects = {};
      for (const id of m.blocks || []) {
        const d = n.drawings.get(id);
        if (d) aspects[id] = await blockAspect(d, m.width);
      }
      reply(m, { lines, aspects });
    } catch (e) {
      fail(m);
    }
  },
  async snapshot(m) {
    try {
      reply(m, await makeSnapshot(m));
    } catch (e) {
      console.error(e);
      fail(m);
    }
  },
  toast: m => toast(m.message, m.seconds),
  state(m) {
    state = m;
    $('askLabel').textContent = m.busy ? 'Stop' : 'Ask';
    $('spinner').hidden = !m.busy;
    $('askIcon').style.display = m.busy ? 'none' : '';
    $('ask').disabled = !m.canAsk;
  },
  visible(m) {
    const limit = scroller.scrollTop + scroller.clientHeight - 90;
    if (m.bottom > limit) scroller.scrollTo({ top: m.bottom - scroller.clientHeight + 90, behavior: 'smooth' });
  },
  conversations(m) {
    conversationState = m;
    if (!$('flyout').hidden) renderConversations();
  },
  dev() { devMode = true; },
  reload() { location.reload(); },
  needKey: () => openSettings(true),
  // A typed note couldn't be sent: its words come back into the box.
  typed(m) {
    showTypeBar(true);
    const box = $('typeInput');
    box.value = box.value ? m.text + '\n' + box.value : m.text;
  },
};

// ---------- the picture Claude reads ----------

// The new ink in black on white; with a context, what it was written over too: earlier writing in grey, Claude's replies in blue.
// This is the same recipe as the Windows app's InkSnapshot, drawn here because the browser has the handwriting font.
async function makeSnapshot(m) {
  const fresh = m.ids.map(id => strokes.get(id)).filter(Boolean);
  const earlier = m.earlier.map(id => strokes.get(id)).filter(Boolean);
  const replies = m.replies.map(id => notes.get(id)).filter(Boolean);

  const rects = [];
  const grow = (l, t, r, b) => rects.push({ l, t, r, b });
  for (const s of [...earlier, ...fresh]) { const b = bounds(s); grow(b.left, b.top, b.right, b.bottom); }
  const ink = rects.reduce((a, r) => ({ l: Math.min(a.l, r.l), t: Math.min(a.t, r.t), r: Math.max(a.r, r.r), b: Math.max(a.b, r.b) }), { l: Infinity, t: Infinity, r: -Infinity, b: -Infinity });
  const parts = [];
  for (const n of replies) {
    if (n.text.length > 0) {
      const lines = wrap(n.text, textWidth());
      parts.push({ kind: 'text', lines, x: MARGIN_X, y: n.y + TEXT_NUDGE });
      grow(MARGIN_X, n.y + TEXT_NUDGE, MARGIN_X + textWidth(), n.y + TEXT_NUDGE + lines.length * LINE);
    }
    for (const d of n.drawings.values()) {
      parts.push({ kind: 'drawing', d, x: MARGIN_X, y: d.top });
      grow(MARGIN_X, d.top, MARGIN_X + d.w, d.top + d.h);
    }
  }
  const area = rects.reduce((a, r) => ({ l: Math.min(a.l, r.l), t: Math.min(a.t, r.t), r: Math.max(a.r, r.r), b: Math.max(a.b, r.b) }), { l: Infinity, t: Infinity, r: -Infinity, b: -Infinity });
  if (!isFinite(area.l)) throw new Error('nothing to draw');

  // Enlarge small writing so it reads clearly; keep the long side within the API's limit.
  const width = Math.max(1, area.r - area.l), height = Math.max(1, area.b - area.t), longSide = Math.max(width, height);
  const k = Math.min(Math.min(2.5, Math.max(1, 1400 / longSide)), 7000 / longSide);
  const PAD = 32;
  const canvas = document.createElement('canvas');
  canvas.width = Math.ceil(width * k) + 2 * PAD;
  canvas.height = Math.ceil(height * k) + 2 * PAD;
  const c = canvas.getContext('2d');
  c.fillStyle = '#fff';
  c.fillRect(0, 0, canvas.width, canvas.height);
  c.setTransform(k, 0, 0, k, PAD - area.l * k, PAD - area.t * k);

  for (const p of parts) {
    if (p.kind === 'text') {
      c.font = `${FONT_PX}px ${HAND}`;
      c.fillStyle = '#2C4FC4';
      c.textBaseline = 'middle';
      p.lines.forEach((line, i) => c.fillText(line, p.x, p.y + i * LINE + LINE / 2));
    } else {
      await paintBlock(c, p.d, p.x, p.y);
    }
  }
  for (const s of earlier) paintStroke(c, s.pts, '#8C9098');
  for (const s of fresh) paintStroke(c, s.pts, '#000');
  return canvas.toDataURL('image/png').split(',')[1];
}

// A block in the picture, as it is now (edits included), in the light ink, drawn as an image so it looks as it does on the page. A
// layout is its document in a foreignObject; a picture is the SVG itself. A browser that won't let a page read back such a picture
// gets the block's text in a box instead.
async function paintBlock(c, d, x, y) {
  try {
    let svg;
    if (d.kind === 'layout') {
      // The snapshot is drawn on white: the palette's light colours, whatever the theme.
      const parsed = new DOMParser().parseFromString(inked(d.doc, '#2C4FC4').replace(/prefers-color-scheme:\s*dark/g, 'prefers-color-scheme: none'), 'text/html');
      parsed.querySelectorAll('meta').forEach(e => e.remove());
      const xhtml = new XMLSerializer().serializeToString(parsed.documentElement);
      svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${d.nw}" height="${d.nh}"><foreignObject x="0" y="0" width="100%" height="100%">${xhtml}</foreignObject></svg>`;
    } else {
      const root = d.el.contentDocument && d.el.contentDocument.querySelector('svg');
      if (!root) throw new Error('not shown');
      const size = root.getBoundingClientRect(), copy = root.cloneNode(true);
      copy.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
      copy.setAttribute('width', size.width);
      copy.setAttribute('height', size.height);
      if (!copy.hasAttribute('viewBox')) copy.setAttribute('viewBox', `0 0 ${size.width} ${size.height}`);
      copy.setAttribute('style', `color:#2C4FC4;overflow:visible;font-family:${HAND.replace(/"/g, "'")}`);
      svg = new XMLSerializer().serializeToString(copy);
    }
    const img = await loadImage('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg));
    const probe = document.createElement('canvas');
    probe.width = probe.height = 1;
    probe.getContext('2d').drawImage(img, 0, 0, 1, 1);
    probe.toDataURL(); // throws where the picture would spoil the snapshot
    c.drawImage(img, x, y, d.w, d.h);
  } catch (e) {
    c.strokeStyle = '#2C4FC4';
    c.lineWidth = 1.5;
    c.strokeRect(x, y, d.w, d.h);
    c.font = `20px ${HAND}`;
    c.fillStyle = '#2C4FC4';
    c.textBaseline = 'top';
    wrap(d.text, d.w - 16).forEach((line, i) => { if (i * 28 + 36 < d.h) c.fillText(line, x + 8, y + 8 + i * 28); });
  }
}

const loadImage = url => new Promise((resolve, reject) => {
  const img = new Image();
  img.onload = () => resolve(img);
  img.onerror = () => reject(new Error('a drawing could not be loaded'));
  img.src = url;
});

// Breaks text into lines of at most maxWidth, the way the page wraps it.
function wrap(text, maxWidth) {
  const c = document.createElement('canvas').getContext('2d');
  c.font = `${FONT_PX}px ${HAND}`;
  const lines = [];
  for (const paragraph of text.split('\n')) {
    let line = '';
    for (const word of paragraph.split(' ')) {
      const trial = line ? line + ' ' + word : word;
      if (line && c.measureText(trial).width > maxWidth) {
        lines.push(line);
        line = word;
      } else {
        line = trial;
      }
    }
    lines.push(line);
  }
  return lines;
}

// ---------- toolbar ----------

let toastTimer = 0;
function toast(message, seconds = 3.5) {
  const box = $('toast');
  box.textContent = message;
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, seconds * 1000);
}

function setErasing(on) {
  erasing = on;
  $('pen').classList.toggle('on', !on);
  $('eraser').classList.toggle('on', on);
  inkCanvas.style.cursor = on ? 'cell' : 'crosshair';
}
$('pen').onclick = () => setErasing(false);
$('eraser').onclick = () => setErasing(true);
$('undo').onclick = () => send({ t: 'undo' });
$('touch').onclick = () => {
  touchWrite = !touchWrite;
  $('touch').classList.toggle('on', touchWrite);
  toast(touchWrite ? 'Finger writing is on. Scroll with the mouse wheel.' : 'Fingers scroll the page again. Write with a pen or mouse.');
};
$('ask').onclick = () => send({ t: 'ask' });

// ---------- typing ----------

function showTypeBar(on) {
  $('typebar').hidden = !on;
  document.body.classList.toggle('typing', on);
  $('keyboard').classList.toggle('on', on);
  if (on) $('typeInput').focus();
}

function sendTyped() {
  const box = $('typeInput'), text = box.value.trim();
  if (!text) return;
  send({ t: 'type', text });
  box.value = '';
}

$('keyboard').onclick = () => showTypeBar($('typebar').hidden);
$('typeSend').onclick = sendTyped;
$('typeInput').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); sendTyped(); }
  else if (e.key === 'Escape') showTypeBar(false);
});
document.addEventListener('keydown', e => {
  if (e.target.closest && e.target.closest('dialog, input, textarea')) return;
  if ((e.ctrlKey || e.metaKey) && e.key === 'z') { e.preventDefault(); send({ t: 'undo' }); }
  else if (selection && (e.key === 'Delete' || e.key === 'Backspace')) { e.preventDefault(); deleteSelection(); }
  else if (selection && e.key === 'Escape') clearSelection();
  else if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); send({ t: 'ask' }); }
});

// ---------- conversations ----------

let conversationState = { current: null, items: [] }, armedDelete = null, renaming = null, showArchived = false;

const archiveIcon = '<svg viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4"/></svg>';
const unarchiveIcon = '<svg viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M12 18v-6M9 15l3-3 3 3"/></svg>';

function when(iso) {
  const d = new Date(iso), now = new Date();
  return d.toDateString() === now.toDateString()
    ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

function closeFlyout() { $('flyout').hidden = true; }

// The flyout shows the main list, or (one tap away, at its foot) the archived conversations. Archiving keeps a conversation; only
// from the archive can one be deleted for good.
function renderConversations() {
  const list = $('conversationList');
  list.textContent = '';
  const archived = conversationState.items.filter(c => c.archived);
  if (showArchived && archived.length === 0) showArchived = false;
  $('newConversation').hidden = showArchived;
  if (showArchived) {
    const back = document.createElement('button');
    back.className = 'row section';
    back.innerHTML = '<svg viewBox="0 0 24 24"><path d="M15 18l-6-6 6-6"/></svg>Archived conversations';
    back.title = 'Back to the conversations';
    back.onclick = () => { showArchived = false; armedDelete = renaming = null; renderConversations(); };
    list.append(back);
  }
  for (const c of conversationState.items) {
    if (!!c.archived !== showArchived) continue;
    const line = document.createElement('div');
    line.className = 'line' + (c.id === conversationState.current ? ' current' : '');
    if (renaming === c.id) {
      const input = document.createElement('input');
      input.value = c.title; input.maxLength = 80; input.placeholder = 'Name';
      const commit = () => { send({ t: 'rename', id: c.id, name: input.value }); renaming = null; renderConversations(); };
      input.onkeydown = e => {
        if (e.key === 'Enter') commit();
        else if (e.key === 'Escape') { renaming = null; renderConversations(); }
      };
      const ok = button('✓', 'Save the name (an empty name goes back to the automatic one)', commit);
      line.append(input, ok);
      list.append(line);
      setTimeout(() => { input.focus(); input.select(); });
      continue;
    }
    const open = document.createElement('button');
    open.className = 'open';
    open.innerHTML = '<b></b><small></small>';
    open.firstChild.textContent = c.title;
    open.lastChild.textContent = (c.id === conversationState.current ? 'Open now · ' : '') + when(c.updated);
    open.onclick = () => { closeFlyout(); send({ t: 'open', id: c.id }); };
    const rename = button('✎', 'Rename', () => { renaming = c.id; armedDelete = null; renderConversations(); });
    if (!c.archived) {
      const archive = button('', 'Archive (it stays under Archived)', () => { armedDelete = null; send({ t: 'archive', id: c.id, archived: true }); });
      archive.innerHTML = archiveIcon;
      archive.className = 'archive';
      line.append(open, rename, archive);
    } else {
      const restore = button('', 'Back to the conversations', () => { armedDelete = null; send({ t: 'archive', id: c.id, archived: false }); });
      restore.innerHTML = unarchiveIcon;
      restore.className = 'unarchive';
      const armed = armedDelete === c.id;
      const del = button(armed ? 'Delete?' : '🗑', armed ? 'Tap again to delete this conversation for good' : 'Delete', () => {
        if (!armed) { armedDelete = c.id; renderConversations(); return; }
        armedDelete = null;
        send({ t: 'delete', id: c.id });
      });
      line.append(open, rename, restore, del);
    }
    list.append(line);
  }
  if (!showArchived && archived.length > 0) {
    const more = document.createElement('button');
    more.className = 'row section archived';
    more.innerHTML = archiveIcon + '<span></span><svg viewBox="0 0 24 24" class="chevron"><path d="M9 18l6-6-6-6"/></svg>';
    more.querySelector('span').textContent = `Archived (${archived.length})`;
    more.onclick = () => { showArchived = true; armedDelete = renaming = null; renderConversations(); };
    list.append(more);
  }
}

function button(label, title, onclick) {
  const b = document.createElement('button');
  b.textContent = label; b.title = title; b.onclick = onclick;
  return b;
}

$('conversations').onclick = e => {
  e.stopPropagation();
  const flyout = $('flyout');
  flyout.hidden = !flyout.hidden;
  if (!flyout.hidden) { armedDelete = renaming = null; showArchived = false; renderConversations(); }
};
$('newConversation').onclick = () => { closeFlyout(); send({ t: 'new' }); };
document.addEventListener('pointerdown', e => { if (!e.target.closest('#flyout, #conversations')) closeFlyout(); });

// ---------- settings and symbols ----------

const api = async (method, path, body) => {
  const r = await fetch(path, { method, headers: { 'X-Longhand': '1', 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = r.headers.get('content-type')?.includes('json') ? await r.json() : null;
  if (!r.ok) throw new Error(data?.error || 'The server said no.');
  return data;
};

async function openSettings(needKey) {
  const s = await (await fetch('/api/settings')).json();
  $('keyInput').value = '';
  $('keyInput').placeholder = s.hasSignIn ? 'Signed in. Paste a new auth.json to replace it.' : '{"tokens":{"access_token":"…","refresh_token":"…","account_id":"…"}}';
  $('keyNote').textContent = s.signInSource === 'environment' ? 'The sign-in comes from the LONGHAND_CODEX_SIGNIN environment variable of the server; one pasted here is used instead.' : needKey ? 'Sign in with ChatGPT to get answers.' : '';
  $('removeKey').hidden = s.signInSource !== 'saved';
  $('version').textContent = 'Longhand version ' + s.version;
  const select = $('modelSelect');
  select.textContent = '';
  // The model belongs to the open conversation; the server says which it is with the conversation list.
  const current = conversationState?.model || s.model;
  for (const m of s.models) select.append(new Option(m.label, m.id, false, m.id === current));
  resetSymbolForm();
  await loadSymbols();
  if (!$('settings').open) $('settings').showModal();
  if (needKey) $('keyInput').focus();
}
$('settingsButton').onclick = () => openSettings(false);
$('settingsClose').onclick = () => $('settings').close();
$('removeKey').onclick = async () => { await api('PUT', '/api/settings', { signOut: true }); $('settings').close(); toast('Signed out.'); };
$('settingsSave').onclick = async () => {
  const key = $('keyInput').value.trim();
  try {
    if (key) await api('PUT', '/api/settings', { signIn: key });
    const model = $('modelSelect').value;
    if (model && model !== conversationState?.model) send({ t: 'model', id: model });
    $('settings').close();
    if (key) toast('Signed in with ChatGPT.');
  } catch (e) { toast(e.message); }
};

let symbols = [], editingSymbol = null, armedSymbol = null, removeDrawing = false;
const pad = $('pad'), padCtx = pad.getContext('2d');
let padStrokes = [], padActive = null;

function drawPad() {
  padCtx.clearRect(0, 0, pad.width, pad.height);
  for (const pts of padStrokes) paintStroke(padCtx, pts, inkColor());
}
function padPoint(e) {
  const r = pad.getBoundingClientRect();
  return [(e.clientX - r.left) * pad.width / r.width, (e.clientY - r.top) * pad.height / r.height];
}
pad.onpointerdown = e => { pad.setPointerCapture(e.pointerId); padActive = [padPoint(e)]; padStrokes.push(padActive); drawPad(); e.preventDefault(); };
pad.onpointermove = e => { if (padActive) { padActive.push(padPoint(e)); drawPad(); } };
pad.onpointerup = pad.onpointercancel = () => { padActive = null; };

// The drawing is saved as black ink on white, cropped to the ink, so it reads the same however it was drawn.
function padPng() {
  let l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
  for (const pts of padStrokes) for (const [x, y] of pts) { l = Math.min(l, x); r = Math.max(r, x); t = Math.min(t, y); b = Math.max(b, y); }
  const PAD = 16, w = Math.max(r - l, 8), h = Math.max(b - t, 8), k = Math.min(3, 300 / Math.max(w, h));
  const c = document.createElement('canvas');
  c.width = Math.ceil(w * k) + 2 * PAD; c.height = Math.ceil(h * k) + 2 * PAD;
  const x = c.getContext('2d');
  x.fillStyle = '#fff'; x.fillRect(0, 0, c.width, c.height);
  x.setTransform(k, 0, 0, k, PAD - l * k, PAD - t * k);
  for (const pts of padStrokes) paintStroke(x, pts, '#000');
  return c.toDataURL('image/png');
}

function resetSymbolForm() {
  editingSymbol = null; removeDrawing = false; padStrokes = []; drawPad();
  $('symName').value = $('symMeaning').value = '';
  $('symbolFormTitle').textContent = 'New symbol';
  $('saveSymbol').textContent = 'Add symbol';
  for (const id of ['cancelSymbol', 'dropSaved', 'savedImage', 'savedLabel']) $(id).hidden = true;
}

async function loadSymbols() {
  symbols = await (await fetch('/api/symbols')).json();
  const list = $('symbolList');
  list.textContent = '';
  if (symbols.length === 0) list.textContent = 'No symbols yet.';
  for (const s of symbols) {
    const row = document.createElement('div');
    row.className = 'sym';
    const img = document.createElement('img');
    if (s.hasDrawing) img.src = `/api/symbols/${s.id}.png?${Date.now()}`; else img.style.visibility = 'hidden';
    const text = document.createElement('div');
    text.className = 'text';
    text.innerHTML = '<b></b><span></span>';
    text.firstChild.textContent = s.name;
    text.lastChild.textContent = s.meaning;
    const edit = button('✎', 'Edit', () => {
      resetSymbolForm();
      editingSymbol = s.id;
      $('symName').value = s.name; $('symMeaning').value = s.meaning;
      $('symbolFormTitle').textContent = 'Edit symbol';
      $('saveSymbol').textContent = 'Save changes';
      $('cancelSymbol').hidden = false;
      if (s.hasDrawing) { $('savedImage').src = `/api/symbols/${s.id}.png?${Date.now()}`; for (const id of ['savedImage', 'savedLabel', 'dropSaved']) $(id).hidden = false; }
      $('symbolMessage').textContent = '';
    });
    const armed = armedSymbol === s.id;
    const del = button(armed ? 'Delete?' : '🗑', armed ? 'Tap again to delete this symbol' : 'Delete', async () => {
      if (!armed) { armedSymbol = s.id; loadSymbols(); return; }
      armedSymbol = null;
      try { await api('DELETE', '/api/symbols/' + s.id); if (editingSymbol === s.id) resetSymbolForm(); } catch (e) { $('symbolMessage').textContent = e.message; }
      loadSymbols();
    });
    row.append(img, text, edit, del);
    list.append(row);
  }
}

$('clearPad').onclick = () => { padStrokes = []; drawPad(); };
$('dropSaved').onclick = () => { removeDrawing = true; for (const id of ['savedImage', 'savedLabel', 'dropSaved']) $(id).hidden = true; };
$('cancelSymbol').onclick = () => { resetSymbolForm(); $('symbolMessage').textContent = ''; };
$('saveSymbol').onclick = async () => {
  try {
    await api('PUT', '/api/symbols', {
      id: editingSymbol, name: $('symName').value, meaning: $('symMeaning').value,
      drawing: padStrokes.length > 0 ? padPng() : undefined, removeDrawing,
    });
    $('symbolMessage').textContent = 'Saved.';
    resetSymbolForm();
    await loadSymbols();
  } catch (e) { $('symbolMessage').textContent = e.message; }
};

// ---------- connected apps ----------

let appsData = { apps: [], nextcloud: {} }, tokenFor = null, watching = 0;

const encode = encodeURIComponent;
const appsSay = (text, link) => {
  const box = $('appsMessage');
  box.textContent = text || '';
  if (link) {
    const a = document.createElement('a');
    a.href = link; a.target = '_blank'; a.rel = 'noopener'; a.textContent = ' Open it here.';
    box.append(a);
  }
};

async function loadApps() {
  appsData = await (await fetch('/api/apps')).json();
  renderApps();
  renderNextcloud();
  return appsData;
}

// Keeps looking until the browser tab where you approved the sign-in has finished, or the time is up.
async function waitFor(done, ms = 10 * 60 * 1000) {
  const mine = ++watching;
  for (let waited = 0; waited < ms && mine === watching; waited += 1500) {
    await new Promise(r => setTimeout(r, 1500));
    if (!$('apps').open) return false;
    if (done(await loadApps())) return true;
  }
  return false;
}
addEventListener('message', e => { if (e.origin === location.origin && e.data === 'longhand-oauth' && $('apps').open) loadApps(); });

function renderApps() {
  const list = $('appList');
  list.textContent = '';
  if (appsData.apps.length === 0) list.textContent = 'No apps yet. Find one in the registry below, or add one by address.';
  for (const a of appsData.apps) {
    const row = document.createElement('div');
    row.className = 'app';
    const status = a.access === 'open' ? 'No sign-in needed'
      : a.access === 'token' ? (a.ready ? 'Access token saved' : 'Needs an access token')
      : (a.ready ? 'Signed in' : 'Not signed in');
    const text = document.createElement('div');
    text.className = 'text';
    text.innerHTML = '<b></b><small></small>';
    text.firstChild.textContent = a.name;
    text.lastChild.textContent = status;
    row.append(text);
    if (a.access !== 'open') {
      const label = a.access === 'token' ? (a.ready ? 'Remove token' : 'Add token') : (a.ready ? 'Sign out' : 'Sign in');
      row.append(button(label, label, async () => {
        if (a.ready) { await api('POST', `/api/apps/${encode(a.name)}/signout`); loadApps(); }
        else if (a.access === 'token') { tokenFor = a.name; renderApps(); }
        else signIn(a);
      }));
    }
    row.append(button('🗑', 'Remove', async () => { await api('DELETE', `/api/apps/${encode(a.name)}`); loadApps(); }));
    list.append(row);
    if (tokenFor === a.name) {
      const box = document.createElement('div');
      box.className = 'stack';
      const input = document.createElement('input');
      input.type = 'password'; input.placeholder = 'Access token from the service'; input.autocomplete = 'off';
      const save = button('Save token', 'Save token', async () => {
        if (!input.value.trim()) return;
        try { await api('POST', `/api/apps/${encode(a.name)}/token`, { token: input.value }); tokenFor = null; loadApps(); }
        catch (e) { appsSay(e.message); }
      });
      const cancel = button('Cancel', 'Cancel', () => { tokenFor = null; renderApps(); });
      const buttons = document.createElement('div');
      buttons.className = 'buttons';
      buttons.append(save, cancel);
      box.append(input, buttons);
      list.append(box);
    }
  }
}

// The sign-in page opens in another tab and sends the browser back to this server when you approve. The window is opened
// straight from the click (a window opened later is blocked as a pop-up), and pointed at the sign-in page when it is known.
async function signIn(app) {
  const win = window.open('', '_blank');
  try {
    appsSay(`Contacting ${app.name}…`);
    const { url } = await api('POST', `/api/apps/${encode(app.name)}/signin`);
    if (win) { win.location = url; appsSay(`Finish signing in to ${app.name} in the other tab. This page continues when you come back.`); }
    else appsSay('Your browser blocked the sign-in window.', url);
    if (await waitFor(d => d.apps.find(x => x.name === app.name)?.ready)) appsSay(`Signed in to ${app.name}.`);
  } catch (e) {
    if (win) win.close();
    appsSay(`Couldn't sign in to ${app.name}: ${e.message}`);
  }
}

async function addApp(name, url, tokenRequired) {
  try {
    appsSay('Checking how it lets you in…');
    const { app } = await api('POST', '/api/apps', { name, url, tokenRequired });
    appsSay(app.access === 'oauth' ? `Added ${app.name}. Sign in to use it.`
      : app.access === 'token' ? `Added ${app.name}. It needs an access token: get one from the service, then tap Add token.`
      : `Added ${app.name}. It needs no sign-in.`);
    await loadApps();
    return true;
  } catch (e) {
    appsSay(`Couldn't add ${name}: ${e.message}`);
    return false;
  }
}

// ----- Nextcloud -----

function renderNextcloud() {
  const box = $('nextcloud');
  const n = appsData.nextcloud;
  box.textContent = '';
  if (n.connected) {
    const line = document.createElement('p');
    line.className = 'note-text';
    line.textContent = `Connected as ${n.user} on ${n.host}`;
    const buttons = document.createElement('div');
    buttons.className = 'buttons';
    buttons.append(
      button('Check', 'Check', async () => {
        try {
          const { calendars } = await api('POST', '/api/nextcloud/check');
          appsSay(calendars.length === 0 ? 'No calendars found.' : 'The model can use: ' + calendars.join(', ') + '.');
        } catch (e) { appsSay(e.message); }
      }),
      button('Disconnect', 'Disconnect', async () => { await api('POST', '/api/nextcloud/disconnect'); appsSay('Disconnected from Nextcloud.'); loadApps(); }),
    );
    box.append(line, buttons);
    return;
  }
  const intro = document.createElement('p');
  intro.className = 'note-text';
  intro.textContent = "Lets the model read your calendar, and add, change or delete events when you ask it to. You approve the connection in your browser; no password is typed here.";
  const server = document.createElement('input');
  server.placeholder = 'Your Nextcloud, like cloud.example.com';
  const connect = button(n.pending ? 'Waiting for you to approve…' : 'Connect', 'Connect', async () => {
    const win = window.open('', '_blank');
    try {
      appsSay('Contacting Nextcloud…');
      const { url } = await api('POST', '/api/nextcloud/connect', { server: server.value });
      if (win) { win.location = url; appsSay('Approve the connection in the other tab. This page continues when you have.'); }
      else appsSay('Your browser blocked the window.', url);
      renderNextcloud();
      await waitFor(d => d.nextcloud.connected || (!d.nextcloud.pending && d.nextcloud.error));
      const d = appsData.nextcloud;
      if (d.connected) {
        const { calendars } = await api('POST', '/api/nextcloud/check');
        appsSay(calendars.length === 0 ? 'Connected, but no calendars were found.' : 'Connected. The model can use: ' + calendars.join(', ') + '.');
      } else if (d.error) appsSay("Couldn't connect to Nextcloud: " + d.error);
    } catch (e) {
      if (win) win.close();
      appsSay(e.message);
    }
  });
  connect.disabled = !!n.pending;
  box.append(intro, server, connect);
}

// ----- the registry -----

let registry = { query: '', cursor: null, shown: 0, hidden: 0 };

function resolveUrl(server, values) {
  let url = server.url;
  for (const v of server.variables) {
    const value = ((values[v.name] || '').trim() || (v.default || '')).trim();
    if (!value && v.required) return null;
    url = url.split('{' + v.name + '}').join(value);
  }
  return url.includes('{') ? null : url;
}

function resultRow(server) {
  const box = document.createElement('div');
  box.className = 'hit';
  const top = document.createElement('div');
  top.className = 'top';
  const text = document.createElement('div');
  text.className = 'text';
  text.innerHTML = '<b></b><small></small><span></span>';
  text.children[0].textContent = server.title;
  text.children[1].textContent = server.host + (server.tokenRequired ? ' · needs an access token' : '');
  text.children[2].textContent = server.description;
  const add = button('Add', 'Add', async () => {
    if (server.variables.length === 0) { add.disabled = true; await addApp(server.name, server.url, server.tokenRequired); add.disabled = false; return; }
    // The address has blanks (an account or region name): ask for them first.
    add.disabled = true;
    const form = document.createElement('div');
    form.className = 'stack';
    const getters = {};
    for (const v of server.variables) {
      const label = document.createElement('small');
      label.textContent = (v.description || v.name) + (v.required ? ' *' : '');
      form.append(label);
      if (v.choices.length > 0) {
        const select = document.createElement('select');
        for (const c of v.choices) select.append(new Option(c, c, false, c === v.default));
        form.append(select);
        getters[v.name] = () => select.value;
      } else {
        const input = document.createElement('input');
        input.value = v.default || ''; input.placeholder = v.name;
        form.append(input);
        getters[v.name] = () => input.value;
      }
    }
    const confirm = button('Add ' + server.title, 'Add', async () => {
      const values = {};
      for (const k of Object.keys(getters)) values[k] = getters[k]();
      const url = resolveUrl(server, values);
      if (!url) { appsSay('Fill in the starred boxes with plain values (no slashes or spaces).'); return; }
      confirm.disabled = true;
      await addApp(server.name, url, server.tokenRequired);
      form.remove();
      add.disabled = false;
    });
    form.append(confirm);
    box.append(form);
  });
  top.append(text, add);
  box.append(top);
  return box;
}

async function searchRegistry(more) {
  const search = $('registrySearch'), moreButton = $('registryMore');
  search.disabled = moreButton.disabled = true;
  try {
    if (!more) {
      registry = { query: $('registryQuery').value, cursor: null, shown: 0, hidden: 0 };
      $('registryResults').textContent = '';
    }
    $('registryNote').textContent = 'Searching…';
    const params = new URLSearchParams({ q: registry.query });
    if (more && registry.cursor) params.set('cursor', registry.cursor);
    const r = await fetch('/api/registry?' + params);
    const page = await r.json();
    if (!r.ok) throw new Error(page.error || 'The registry did not answer.');
    for (const s of page.servers) $('registryResults').append(resultRow(s));
    registry.shown += page.servers.length;
    registry.hidden += page.hidden;
    registry.cursor = page.nextCursor;
    moreButton.hidden = !registry.cursor;
    $('registryNote').textContent = registry.shown === 0 && !registry.cursor ? 'Nothing found.'
      : `${registry.shown} found` + (registry.hidden > 0 ? `; ${registry.hidden} more can't be used here (they run on your own PC, or need a kind of sign-in this app can't do).` : '.');
  } catch (e) {
    $('registryNote').textContent = e.message;
  }
  search.disabled = moreButton.disabled = false;
}

$('registrySearch').onclick = () => searchRegistry(false);
$('registryMore').onclick = () => searchRegistry(true);
$('registryQuery').addEventListener('keydown', e => { if (e.key === 'Enter') searchRegistry(false); });
$('appPreset').onchange = e => {
  const [name, url] = e.target.value.split('|');
  if (name) { $('appName').value = name; $('appUrl').value = url; }
};
$('appAdd').onclick = async () => {
  const add = $('appAdd');
  add.disabled = true;
  if (await addApp($('appName').value, $('appUrl').value.trim(), false)) { $('appName').value = $('appUrl').value = ''; $('appPreset').value = ''; }
  add.disabled = false;
};
$('appsButton').onclick = async () => {
  tokenFor = null;
  appsSay('');
  await loadApps();
  $('apps').showModal();
};
$('appsClose').onclick = () => $('apps').close();
$('apps').addEventListener('close', () => { watching++; });

// ---------- start ----------

$('date').textContent = new Date().toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' }).toUpperCase();
let resizeTimer = 0;
addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if (layout()) send({ t: 'width', width: sheetWidth }); }, 150);
});
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { colorDrawings(); redraw(); });
layout();
connect();
