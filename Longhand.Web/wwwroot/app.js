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
let maxId = 0, sheetWidth = 800, erasing = false, touchWrite = false, socket = null, state = { busy: false, canAsk: false, counting: false };

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
    send({ t: 'hello', width: sheetWidth });
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
  if (e.pointerType === 'touch' && !touchWrite) {
    active = { id: e.pointerId, mode: 'pan', lastY: e.clientY };
    inkCanvas.setPointerCapture(e.pointerId);
    return;
  }
  if (e.pointerType === 'mouse' && e.button !== 0) return;
  inkCanvas.setPointerCapture(e.pointerId);
  const wipe = erasing || (e.pointerType === 'pen' && (e.buttons & 32));
  const p = pagePoint(e);
  if (wipe) {
    active = { id: e.pointerId, mode: 'erase', erased: [] };
    eraseAt(p[0], p[1], active.erased);
    redraw();
  } else {
    send({ t: 'strokeStarted' });
    active = { id: e.pointerId, mode: 'draw', pts: [p] };
    viewTransform();
    paintStroke(inkCtx, [p], inkColor());
  }
  e.preventDefault();
});

inkCanvas.addEventListener('pointermove', e => {
  if (!active || active.id !== e.pointerId) return;
  if (active.mode === 'pan') {
    scroller.scrollTop -= e.clientY - active.lastY;
    active.lastY = e.clientY;
    return;
  }
  for (const ev of (e.getCoalescedEvents ? e.getCoalescedEvents() : [e])) {
    const p = pagePoint(ev);
    if (active.mode === 'erase') {
      eraseAt(p[0], p[1], active.erased);
    } else {
      const last = active.pts[active.pts.length - 1];
      if (Math.hypot(p[0] - last[0], p[1] - last[1]) < 0.5) continue;
      active.pts.push(p);
      viewTransform();
      paintStroke(inkCtx, [last, p], inkColor());
    }
  }
  if (active.mode === 'erase') redraw();
});

function finishPointer(e) {
  if (!active || active.id !== e.pointerId) return;
  const done = active;
  active = null;
  if (done.mode === 'draw') {
    const stroke = { id: ++maxId, pts: done.pts.length === 1 ? [done.pts[0], [done.pts[0][0] + 0.1, done.pts[0][1]]] : done.pts };
    strokes.set(stroke.id, stroke);
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
  n.el.remove();
  n.tick.remove();
  for (const d of n.drawings.values()) d.img.remove();
}

const drawingUrl = (svg, color) => 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg.split('currentColor').join(color));

// Claude's drawings say "currentColor" for the ink colour; an image can't inherit it, so it is filled in here.
function colorDrawings() {
  for (const n of notes.values()) for (const d of n.drawings.values()) d.img.src = drawingUrl(d.svg, claudeColor());
}

// ---------- what the server tells the page ----------

const handlers = {
  clear() {
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
    const img = new Image();
    img.className = 'drawing';
    img.alt = m.alt;
    img.src = drawingUrl(m.svg, claudeColor());
    notesLayer.append(img);
    n.drawings.set(m.id, { id: m.id, svg: m.svg, img, top: 0, w: 0, h: 0 });
  },
  drawingsCleared(m) {
    const n = notes.get(m.note);
    if (!n) return;
    for (const d of n.drawings.values()) d.img.remove();
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
      d.top = p.top; d.w = p.w; d.h = p.h;
      Object.assign(d.img.style, { top: p.top + 'px', width: p.w + 'px', height: p.h + 'px' });
    }
    updateHeight();
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
      reply(m, Math.round(n.el.getBoundingClientRect().height / LINE));
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
    $('askIcon').style.display = m.busy || m.counting ? 'none' : '';
    $('ask').disabled = !m.canAsk;
  },
  idle(m) {
    const bar = $('countdown');
    bar.style.display = m.progress === null ? 'none' : 'block';
    if (m.progress !== null) bar.style.transform = `scaleX(${Math.min(1, m.progress)})`;
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
      const img = await loadImage(drawingUrl(p.d.svg, '#2C4FC4'));
      c.drawImage(img, p.x, p.y, p.d.w, p.d.h);
    }
  }
  for (const s of earlier) paintStroke(c, s.pts, '#8C9098');
  for (const s of fresh) paintStroke(c, s.pts, '#000');
  return canvas.toDataURL('image/png').split(',')[1];
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

let typingNoted = false;

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
  typingNoted = false;
}

$('keyboard').onclick = () => showTypeBar($('typebar').hidden);
$('typeSend').onclick = sendTyped;
$('typeInput').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); sendTyped(); }
  else if (e.key === 'Escape') showTypeBar(false);
});
// Typing is writing: it holds off the pause that would otherwise ask by itself while you are still composing.
$('typeInput').addEventListener('input', () => {
  if (!typingNoted) { typingNoted = true; send({ t: 'strokeStarted' }); }
});
$('typeInput').addEventListener('blur', () => { typingNoted = false; });
document.addEventListener('keydown', e => {
  if (e.target.closest && e.target.closest('dialog, input, textarea')) return;
  if ((e.ctrlKey || e.metaKey) && e.key === 'z') { e.preventDefault(); send({ t: 'undo' }); }
  else if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); send({ t: 'ask' }); }
});

// ---------- conversations ----------

let conversationState = { current: null, items: [] }, armedDelete = null, renaming = null;

function when(iso) {
  const d = new Date(iso), now = new Date();
  return d.toDateString() === now.toDateString()
    ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

function closeFlyout() { $('flyout').hidden = true; }

function renderConversations() {
  const list = $('conversationList');
  list.textContent = '';
  for (const c of conversationState.items) {
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
    const armed = armedDelete === c.id;
    const del = button(armed ? 'Delete?' : '🗑', armed ? 'Tap again to delete this conversation for good' : 'Delete', () => {
      if (!armed) { armedDelete = c.id; renderConversations(); return; }
      armedDelete = null;
      send({ t: 'delete', id: c.id });
    });
    line.append(open, rename, del);
    list.append(line);
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
  if (!flyout.hidden) { armedDelete = renaming = null; renderConversations(); }
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
  $('keyInput').placeholder = s.hasKey ? 'A key is saved. Paste a new one to replace it.' : 'sk-ant-…';
  $('keyNote').textContent = s.keySource === 'environment' ? 'The key comes from the ANTHROPIC_API_KEY environment variable of the server; a key typed here would be overridden by it.' : needKey ? 'Add your Anthropic API key to get answers.' : '';
  $('removeKey').hidden = s.keySource !== 'saved';
  $('version').textContent = 'Longhand version ' + s.version;
  const select = $('modelSelect');
  select.textContent = '';
  for (const m of s.models) select.append(new Option(m.label, m.id, false, m.id === s.model));
  resetSymbolForm();
  await loadSymbols();
  if (!$('settings').open) $('settings').showModal();
  if (needKey) $('keyInput').focus();
}
$('settingsButton').onclick = () => openSettings(false);
$('settingsClose').onclick = () => $('settings').close();
$('removeKey').onclick = async () => { await api('PUT', '/api/settings', { removeKey: true }); $('settings').close(); toast('API key removed.'); };
$('settingsSave').onclick = async () => {
  const key = $('keyInput').value.trim();
  try {
    await api('PUT', '/api/settings', { model: $('modelSelect').value, apiKey: key || undefined });
    $('settings').close();
    if (key) { toast('API key saved.'); send({ t: 'keyChanged' }); }
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
