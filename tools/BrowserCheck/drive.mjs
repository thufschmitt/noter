// Drives headless Chromium over the DevTools protocol: opens the notebook, writes with the pointer, waits for the answer, takes pictures.
import fs from 'node:fs';
// Usage: node drive.mjs <output folder> <page url> <chrome debugging port>
const [, , outDir, pageUrl, port] = process.argv;
const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const target = targets.find(t => t.type === 'page');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let seq = 0; const pending = new Map(); const logs = [];
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  else if (m.method === 'Runtime.consoleAPICalled') logs.push(m.params.args.map(a => a.value ?? a.description).join(' '));
  else if (m.method === 'Runtime.exceptionThrown') logs.push('EXCEPTION ' + JSON.stringify(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text));
};
const call = (method, params = {}) => new Promise(res => { const id = ++seq; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
const evaluate = async expr => (await call('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const shot = async name => fs.writeFileSync(`${outDir}/${name}.png`, Buffer.from((await call('Page.captureScreenshot', { format: 'png' })).result.data, 'base64'));
const until = async (expr, ms = 15000) => { for (let t = 0; t < ms; t += 100) { if (await evaluate(expr)) return true; await sleep(100); } return false; };
const mouse = (type, x, y, pointerType = 'mouse') => call('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: 1, pointerType });
// The barrel button of a pen, or the right button of a mouse: the lasso.
const barrel = (type, x, y) => call('Input.dispatchMouseEvent', { type, x, y, button: 'right', buttons: type === 'mouseReleased' ? 0 : 2, clickCount: 1, pointerType: 'mouse' });
const key = async (k, code, vk) => { await call('Input.dispatchKeyEvent', { type: 'keyDown', key: k, code, windowsVirtualKeyCode: vk }); await call('Input.dispatchKeyEvent', { type: 'keyUp', key: k, code, windowsVirtualKeyCode: vk }); };
// A part of the first block, where it is on the screen as [left, top, right, bottom] (scrolled into view first).
const rectOnScreen = async (selector) => JSON.parse(await evaluate(`(() => {
  const d = firstDrawing(), el = frameDoc(d).querySelector(${JSON.stringify(selector)});
  scroller.scrollTop = Math.max(0, d.top - 80);
  const q = pageRect(d, el), r = inkCanvas.getBoundingClientRect();
  return JSON.stringify([r.left + q.left, r.top + q.top - scroller.scrollTop, r.left + q.right, r.top + q.bottom - scroller.scrollTop]);
})()`));
const onScreen = async (selector) => { const [l, t, r, b] = await rectOnScreen(selector); return [(l + r) / 2, (t + b) / 2]; };
// The selection's frame on the screen: its knobs and its centre.
const selFrame = async () => JSON.parse(await evaluate(`(() => { const f = selectionFrame(), r = inkCanvas.getBoundingClientRect(), y = r.top - scroller.scrollTop; return JSON.stringify({ cx: r.left + f.cx, cy: y + f.cy, rotate: [r.left + f.cx, y + f.top - 30], remove: [r.left + f.right, y + f.top] }); })()`));
async function stroke(points) {
  await mouse('mousePressed', ...points[0]);
  for (const p of points.slice(1)) { await mouse('mouseMoved', ...p); await sleep(8); }
  await mouse('mouseReleased', ...points[points.length - 1]);
}
const results = [];
const check = (name, ok) => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}`); };

await call('Runtime.enable'); await call('Page.enable');
await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false });
await call('Page.navigate', { url: pageUrl });
check('the hint note appears', await until(`document.querySelector('.note.hint') !== null`));
check('the conversation list arrives', await until(`document.getElementById('flyout') !== null`));
await sleep(500);
await shot('1-empty');

// Write a few strokes on the first lines, offset by where the centred sheet starts.
const ox = await evaluate(`document.getElementById('sheet').getBoundingClientRect().left`);
await stroke([[ox + 100, 170], [ox + 100, 200], [ox + 100, 230]]);
await stroke([[ox + 100, 200], [ox + 130, 195], [ox + 140, 230]]);
await stroke([[ox + 160, 210], [ox + 175, 200], [ox + 190, 215], [ox + 175, 230], [ox + 160, 215]]);
check('writing enables Ask', await until(`!document.getElementById('ask').disabled`));
await shot('2-written');
await sleep(2600);
check('nothing is asked by itself', await evaluate(`![...document.querySelectorAll('.note')].some(n => !n.classList.contains('hint') && !n.classList.contains('typed'))`));
// A five-point star drawn beside the writing asks, instead of the Ask button (which the later steps use).
const tips = [0, 1, 2, 3, 4].map(k => [ox + 420 + 25 * Math.cos((-90 + 72 * k) * Math.PI / 180), 200 + 25 * Math.sin((-90 + 72 * k) * Math.PI / 180)]);
const starPath = [];
for (let i = 0; i < 5; i++) { const a = tips[(i * 2) % 5], b = tips[((i + 1) * 2) % 5]; for (let k = 0; k < 8; k++) starPath.push([a[0] + (b[0] - a[0]) * k / 8, a[1] + (b[1] - a[1]) * k / 8]); }
starPath.push(tips[0]);
await stroke(starPath);
check('a star drawn beside the writing gets a reply', await until(`[...document.querySelectorAll('.note')].some(n => !n.classList.contains('hint') && n.textContent.includes('model answering'))`, 20000));
await sleep(800);
check('and the star is gone from the page', await evaluate(`strokes.size === 3`));
check('the reply sits below the ink on a rule line', await evaluate(`(() => { const n = [...document.querySelectorAll('.note')].find(n => !n.classList.contains('hint')); return parseFloat(n.style.top) - 3 === 280 || parseFloat(n.style.top) - 3 >= 240; })()`));
check('the reply is back to idle (Ask disabled, label Ask)', await until(`document.getElementById('ask').disabled && document.getElementById('askLabel').textContent === 'Ask'`));
await shot('3-answered');

// Annotate the reply: write over it.
const replyTop = await evaluate(`parseFloat([...document.querySelectorAll('.note')].find(n => !n.classList.contains('hint')).style.top)`);
await stroke([[ox + 90, replyTop + 20], [ox + 400, replyTop + 24], [ox + 500, replyTop + 20]]);
await until(`!document.getElementById('ask').disabled`);
await evaluate(`document.getElementById('ask').click()`);
check('a layout arrives with the second reply', await until(`document.querySelectorAll('iframe.drawing').length === 1`, 20000));
await evaluate(`window.firstDrawing = () => [...notes.values()].flatMap(n => [...n.drawings.values()])[0]`);
await sleep(1500);
check('the layout is rendered by the browser, sized to its content, with its parts', await evaluate(`(() => { const f = document.querySelector('iframe.drawing'); const d = f.contentDocument; return parseFloat(f.style.width) > 300 && parseFloat(f.style.height) > 100 && d.querySelectorAll('[data-edit]').length === 3 && d.querySelectorAll('[data-drop]').length === 2 && d.querySelector('#todo .title').textContent === 'To do' && d.querySelector('#todo').getBoundingClientRect().width > 100; })()`));
check('nothing in the layout ran, and it runs nothing', await evaluate(`window.hacked === undefined && !document.querySelector('iframe.drawing').contentDocument.querySelector('script') && document.querySelector('iframe.drawing').getAttribute('sandbox') === 'allow-same-origin' && !firstDrawing().doc.includes('<script')`));
check('a part the model coloured is shown in that colour, with a tinted background, and the rest stays in the ink colour', await evaluate(`(() => { const d = document.querySelector('iframe.drawing').contentDocument; const c = getComputedStyle(d.getElementById('c3')), g = getComputedStyle(d.getElementById('c2')); return c.color === 'rgb(198, 40, 40)' && c.borderTopColor === 'rgb(198, 40, 40)' && c.backgroundColor !== 'rgba(0, 0, 0, 0)' && g.color === 'rgb(44, 79, 196)' && g.backgroundColor === 'rgba(0, 0, 0, 0)'; })()`));
check('the layout is in the ink colour, on a clear background', await evaluate(`(() => { const d = document.querySelector('iframe.drawing').contentDocument; return getComputedStyle(d.body).color === 'rgb(44, 79, 196)' && getComputedStyle(d.body).backgroundColor === 'rgba(0, 0, 0, 0)'; })()`));
check('the words above the block are only the words', await evaluate(`[...document.querySelectorAll('.note')].some(n => n.textContent === 'Here is your week, with a board.')`));
await shot('4-block');

// ----- editing the board: tap to select, drag into a column, turn, delete, lasso -----
// What the server last sent as the layout (not the optimistic change on the page): is this part in that container?
const inDoc = (container, id) => `!!new DOMParser().parseFromString(firstDrawing().doc, 'text/html').querySelector('#${container} #${id}')`;
await sleep(1200); // a tap right after writing is part of the writing; wait it out
const strokesBefore = await evaluate(`strokes.size`);
let [cx, cy] = await onScreen('#c1');
await mouse('mousePressed', cx, cy, 'pen'); await mouse('mouseReleased', cx, cy, 'pen');
check('tapping a part with the pen selects it, with no dot of ink', await until(`selection && selection.els.length === 1 && selection.els[0].id === 'c1' && !document.getElementById('selection').hidden`) && await evaluate(`strokes.size`) === strokesBefore);
await shot('4b-selected');
const [tx, ty] = await onScreen('#doing');
await mouse('mousePressed', cx, cy); for (let i = 1; i <= 10; i++) { await mouse('mouseMoved', cx + ((tx - cx) * i) / 10, cy + ((ty - cy) * i) / 10); await sleep(15); }
check('while dragging, the part follows the pointer and the column it would land in is marked', await evaluate(`(() => { const doc = frameDoc(firstDrawing()); const m = doc.getElementById('dropmark'); return !!m && doc.getElementById('c1').style.opacity === '0.55'; })()`));
await shot('4b2-dragging');
await mouse('mouseReleased', tx, ty);
check('dropping it into the other column moves it there, in the layout the server sends back', await until(inDoc('doing', 'c1'), 8000));
check('the move makes the page askable, though nothing was written', await until(`!document.getElementById('ask').disabled`, 5000));
await sleep(800);
check('the moved part is still selected', await evaluate(`selection && selection.els.length === 1 && selection.els[0].id === 'c1'`));
let f = await selFrame();
await mouse('mousePressed', ...f.rotate); for (let i = 1; i <= 8; i++) { const a = -Math.PI / 2 + (i * 5 * Math.PI) / 180, R = Math.hypot(f.rotate[0] - f.cx, f.rotate[1] - f.cy); await mouse('mouseMoved', f.cx + R * Math.cos(a), f.cy + R * Math.sin(a)); await sleep(10); } await mouse('mouseReleased', f.rotate[0], f.rotate[1]);
check('dragging the knob turns the part', await until(`/data-rot="(3\\d|4\\d)(\\.\\d)?"/.test(firstDrawing().doc)`, 8000));
await sleep(600);
await shot('4c-moved-and-turned');
[cx, cy] = await onScreen('#c3');
await mouse('mousePressed', cx, cy, 'pen'); await mouse('mouseReleased', cx, cy, 'pen');
await until(`selection && selection.els[0].id === 'c3'`);
f = await selFrame();
await mouse('mousePressed', ...f.remove); await mouse('mouseReleased', ...f.remove);
check('the cross deletes the part', await until(`!firstDrawing().doc.includes('Pay rent') && !selection`, 8000));
await evaluate(`document.getElementById('undo').click()`);
check('undo brings the part back (the server shows the layout again)', await until(`firstDrawing().doc.includes('Pay rent')`, 8000));
await sleep(800);
// The lasso: the pen's barrel button held, circling the whole board.
const [bl, bt, br, bb] = await rectOnScreen('.layout');
const loop = [[bl - 20, bt - 20], [br + 20, bt - 20], [br + 20, bb + 20], [bl - 20, bb + 20]];
await barrel('mousePressed', ...loop[0]);
for (let i = 1; i <= 4; i++) for (let k = 1; k <= 5; k++) { const p = loop[i - 1], q = loop[i % 4]; await barrel('mouseMoved', p[0] + (q[0] - p[0]) * k / 5, p[1] + (q[1] - p[1]) * k / 5); await sleep(8); }
await barrel('mouseReleased', ...loop[0]);
check('circling with the barrel button selects what is inside', await until(`selection && selection.els.length === 3`));
await shot('4d-lasso');
await key('Escape', 'Escape', 27);
check('Escape puts the selection away', await until(`!selection && document.getElementById('selection').hidden`));
// Writing, then a dot at once beside it, is writing: the dot is not a tap on the part under it.
[cx, cy] = await onScreen('#c2');
const inkBefore = await evaluate(`strokes.size`);
await stroke([[cx - 40, cy - 6], [cx - 20, cy + 4], [cx, cy - 4]]);
await mouse('mousePressed', cx + 14, cy, 'pen'); await mouse('mouseReleased', cx + 14, cy, 'pen');
check('a dot right after other ink stays ink', await until(`strokes.size === ${inkBefore + 2}`) && await evaluate(`!selection`));
await sleep(1200);
await evaluate(`clearSelection()`);
await until(`!document.getElementById('ask').disabled`);
// Ask: what was moved and turned goes to the model with the writing over it; the layout it sends back replaces this one.
await evaluate(`document.getElementById('ask').click()`);
check('the model answers the edits', await until(`[...document.querySelectorAll('.note')].some(n => n.textContent === 'Moved it.')`, 20000));
const sentEdits = fs.existsSync(`${outDir}/request-3.json`) ? JSON.parse(fs.readFileSync(`${outDir}/request-3.json`, 'utf8')).input.flatMap(m => (m.content || []).map(c => c.text || '')).join('\n') : '';
check('the request told it what changed, by id', sentEdits.includes('I changed your layouts by hand') && sentEdits.includes('- c1 "Call Anna": moved from #todo (position 1) to #doing (position 1 of 1), turned') && !sentEdits.includes('c3 "Pay rent"') && !sentEdits.includes('Its source now') && sentEdits.includes('<box id="c1" edit rot='));
check('and the layout was replaced where it was, by the one with the same id', await until(`document.querySelectorAll('iframe.drawing').length === 1 && firstDrawing().doc.includes('Updated by the model.') && ${inDoc('doing', 'c3')}`, 8000));
await shot('4e-replaced');
check('the picture sent with the edits showed the layout', fs.readdirSync(outDir).some(f => f.startsWith('sent-3-')));

// Annotate the drawing: the picture sent to the model shows it.
const block = await evaluate(`(() => { const f = document.querySelector('iframe.drawing'); return { top: parseFloat(f.style.top), h: parseFloat(f.style.height) }; })()`);
await stroke([[ox + 120, block.top + 30], [ox + 200, block.top + block.h / 2], [ox + 260, block.top + 40]]);
await until(`!document.getElementById('ask').disabled`);
await evaluate(`document.getElementById('ask').click()`);
check('annotating the drawing gets a reply', await until(`document.querySelectorAll('iframe.drawing').length === 2`, 20000));
// The page is saved a moment after the reply; the server then sends the conversation list again. Reload only after that.
await evaluate(`window.listBeforeSave = conversationState`);
check('the page is saved after the reply', await until(`conversationState !== window.listBeforeSave`, 5000));
await sleep(300);

// Reload: the conversation comes back.
const inkAtReload = await evaluate(`strokes.size`);
await call('Page.navigate', { url: pageUrl });
check('reloading restores the replies and the drawings', await until(`document.querySelectorAll('.note:not(.hint)').length === 4 && document.querySelectorAll('iframe.drawing').length === 2`));
check('and the ink', await until(`typeof strokes !== 'undefined' && strokes.size === ${inkAtReload}`));
await sleep(800);
await shot('5-reloaded');

// Conversations flyout + settings dialog.
await evaluate(`document.getElementById('conversations').click()`);
await sleep(300);
await shot('6-flyout');
await evaluate(`document.getElementById('newConversation').click()`);
check('a new conversation shows the hint again', await until(`document.querySelectorAll('.note:not(.hint)').length === 0 && document.querySelector('.note.hint') !== null`));
// Type a note with the real keyboard path: the bar opens, text goes in, Enter sends.
await evaluate(`document.getElementById('keyboard').click()`);
check('the typing bar opens with the box focused', await evaluate(`!document.getElementById('typebar').hidden && document.activeElement === document.getElementById('typeInput')`));
await call('Input.insertText', { text: 'What is the capital of France?' });
await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
check('Enter sends it and empties the box', await until(`document.getElementById('typeInput').value === ''`));
check('the typed note appears in the user\'s ink', await until(`[...document.querySelectorAll('.note.typed')].some(n => n.textContent === 'What is the capital of France?')`));
check('the hint is gone and the typed note is on the first rule', await evaluate(`!document.querySelector('.note.hint') && parseFloat(document.querySelector('.note.typed').style.top) === 123`));
check('and Claude answers below it', await until(`[...document.querySelectorAll('.note:not(.hint):not(.typed)')].some(n => n.textContent.includes('Here is a sketch'))`, 20000));
await sleep(800);
await shot('6b-typed');

// ----- a timeline: drag an item to another day and time, drag its end, and ask -----
await evaluate(`window.firstDrawing = () => [...notes.values()].flatMap(n => [...n.drawings.values()]).find(d => d.kind === 'layout')`);
await until(`state && !state.busy`, 20000); // the last answer is still being written until then
if (await evaluate(`document.getElementById('typebar').hidden`)) await evaluate(`document.getElementById('keyboard').click()`);
await evaluate(`document.getElementById('typeInput').focus()`);
await call('Input.insertText', { text: 'show my day timeline' });
await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
check('a timeline arrives: two days, each a grid of hours, as tall as its range', await until(`(() => { const d = window.firstDrawing(); const doc = d && frameDoc(d); return !!doc && doc.querySelectorAll('.tl').length === 2 && Math.round(doc.querySelector('.tl').getBoundingClientRect().height) === 640; })()`, 20000));
await sleep(1200);
check('the standup sits at nine, as tall as its hour, and the hours are written once', await evaluate(`(() => { const doc = frameDoc(firstDrawing()), tl = doc.querySelector('.tl'), e = doc.getElementById('e1').getBoundingClientRect(), t = tl.getBoundingClientRect(); return Math.round(e.top - t.top) === 64 && Math.round(e.height) === 62 && doc.querySelectorAll('.tl-label').length === 11 && doc.querySelector('.tl-label').textContent === '08:00'; })()`));
check('overlapping items share the width side by side, and the hours line up between the days', await evaluate(`(() => { const doc = frameDoc(firstDrawing()), a = doc.getElementById('e2').getBoundingClientRect(), b = doc.getElementById('e3').getBoundingClientRect(), t = doc.querySelectorAll('.tl'); return a.right <= b.left + 1 && Math.abs(a.width - b.width) < 2 && a.top < b.bottom && b.top < a.bottom && Math.abs(t[0].getBoundingClientRect().top - t[1].getBoundingClientRect().top) < 1 && Math.abs(t[0].getBoundingClientRect().height - t[1].getBoundingClientRect().height) < 1; })()`));
await shot('4f-timeline');
let [tcx, tcy] = await onScreen('#e1');
await mouse('mousePressed', tcx, tcy, 'pen'); await mouse('mouseReleased', tcx, tcy, 'pen');
check('tapping an item selects it, offering its end but no turning', await until(`selection && selection.els[0].id === 'e1'`) && await evaluate(`getComputedStyle(selEl.querySelector('.rotate')).display === 'none' && getComputedStyle(selEl.querySelector('.resize')).display !== 'none'`));
// To eleven on Friday: where its top edge lands is what counts, so the pointer moves by as much as the edge has to.
const [, fTop, , fBottom] = await rectOnScreen('#fri .tl'), [, eTop] = await rectOnScreen('#e1'), [fl, , fr] = await rectOnScreen('#fri .tl');
const pxHour = (fBottom - fTop) / 10, dyTarget = fTop + 3 * pxHour - eTop;
await mouse('mousePressed', tcx, tcy);
for (let i = 1; i <= 10; i++) { await mouse('mouseMoved', tcx + (((fl + fr) / 2 - tcx) * i) / 10, tcy + (dyTarget * i) / 10); await sleep(15); }
check('while dragging, a line shows the time it would start at', await evaluate(`(() => { const m = frameDoc(firstDrawing()).getElementById('dropmark'); return !!m && m.firstChild.textContent === '11:00' && !m.firstChild.hidden; })()`));
await shot('4g-timeline-dragging');
await mouse('mouseReleased', (fl + fr) / 2, tcy + dyTarget);
check('dropping it on Friday at eleven moves it there, keeping its hour, in the layout the server sends back', await until(`(() => { const doc = new DOMParser().parseFromString(firstDrawing().doc, 'text/html'); const e = doc.querySelector('#fri #e1'); return !!e && e.dataset.start === '660' && e.dataset.end === '720'; })()`, 8000));
check('the move makes the page askable', await until(`!document.getElementById('ask').disabled`, 5000));
check('the moved item is still selected', await until(`selection && selection.els.length === 1 && selection.els[0].id === 'e1' && inTimeline(selection.els[0])`, 8000));
await sleep(500);
const knob = JSON.parse(await evaluate(`(() => { const f = selectionFrame(), r = inkCanvas.getBoundingClientRect(); return JSON.stringify([r.left + f.cx, r.top + f.bottom - scroller.scrollTop]); })()`));
await mouse('mousePressed', knob[0], knob[1]);
for (let i = 1; i <= 8; i++) { await mouse('mouseMoved', knob[0], knob[1] + (2 * pxHour * i) / 8); await sleep(15); }
check('dragging the end knob down two hours stretches the item as it goes', await evaluate(`Math.round(frameDoc(firstDrawing()).getElementById('e1').getBoundingClientRect().height) === ${Math.round(3 * pxHour - 2)}`));
await mouse('mouseReleased', knob[0], knob[1] + 2 * pxHour);
check('and sets its end to two o\'clock', await until(`(() => { const doc = new DOMParser().parseFromString(firstDrawing().doc, 'text/html'); const e = doc.querySelector('#fri #e1'); return !!e && e.dataset.start === '660' && e.dataset.end === '840'; })()`, 8000));
await key('Escape', 'Escape', 27);
await evaluate(`document.getElementById('ask').click()`);
check('asking tells the model what changed, in times', await until(`[...document.querySelectorAll('.note')].some(n => n.textContent === 'Rescheduled.')`, 20000));
const newest = fs.readdirSync(outDir).filter(f => /^request-\d+\.json$/.test(f)).map(f => [+f.match(/\d+/)[0], f]).sort((x, y) => y[0] - x[0]).map(x => x[1]);
const dayAsk = newest.map(f => JSON.parse(fs.readFileSync(`${outDir}/${f}`, 'utf8')).input.flatMap(m => (m.content || []).map(c => c.text || '')).join('\n')).find(t => t.includes('Layout "day":')) || '';
check('the request lists the move and the new end, as times', dayAsk.includes('- e1 "Standup": moved from #thu 09:00–10:00 to #fri 11:00–14:00') && !dayAsk.includes('e2 "Dentist"'));
check('and the layout the model sends back replaces the day where it is', await until(`document.querySelectorAll('iframe.drawing').length === 2 && firstDrawing().doc.includes('Updated by the model.')`, 8000));
await shot('4h-timeline-replaced');
// Connected apps: the dialog opens, the registry can be searched, and an app added by address shows up.
await evaluate(`document.getElementById('appsButton').click()`);
check('the connected apps dialog opens, empty, with Nextcloud ready to connect', await until(`document.getElementById('apps').open && document.getElementById('appList').textContent.includes('No apps yet') && document.querySelector('#nextcloud input') !== null`));
await evaluate(`document.getElementById('registryQuery').value = 'todo'; document.getElementById('registrySearch').click()`);
check('registry results list a usable server and count the others', await until(`document.querySelectorAll('#registryResults .hit').length === 1 && document.getElementById('registryNote').textContent.includes('1 more can\\'t be used')`));
await sleep(300);
await shot('6c-apps');
check('the Google Calendar section asks for the user\'s own client, shows the redirect address, and offers to let the model change events', await evaluate(`(() => { const g = document.getElementById('google'); const inputs = [...g.querySelectorAll('input')]; return g.textContent.includes('Redirect address') && g.querySelector('code').textContent === location.origin + '/oauth/callback' && inputs.some(i => i.placeholder.startsWith('Client ID')) && inputs.some(i => i.type === 'password') && inputs.some(i => i.type === 'checkbox' && !i.checked) && [...g.querySelectorAll('button')].some(b => b.textContent === 'Connect'); })()`));
check('the checkbox sits beside its words, which are plain', await evaluate(`(() => { const l = document.querySelector('#google label'), c = getComputedStyle(l); return c.display === 'flex' && c.fontWeight === '400'; })()`));
await evaluate(`document.getElementById('google').scrollIntoView({ block: 'center' })`);
await sleep(200);
await shot('6d-google-section');
check('connecting without a client ID says what to paste, and opens nothing', await (async () => { await evaluate(`[...document.querySelectorAll('#google button')].find(b => b.textContent === 'Connect').click()`); return await until(`document.getElementById('appsMessage').textContent.includes('client ID')`, 5000); })());
check('the old MCP sign-in client section is still there, without a Google preset', await evaluate(`!!document.getElementById('appClient') && ![...document.getElementById('appPreset').options].some(o => /google/i.test(o.text))`));
await evaluate(`document.getElementById('appName').value = 'Local Notes'; document.getElementById('appUrl').value = 'http://127.0.0.1:18081/open'; document.getElementById('appAdd').click()`);
check('an app added by address appears as needing no sign-in', await until(`document.getElementById('appList').textContent.includes('local-notes') && document.getElementById('appList').textContent.includes('No sign-in needed')`));
await evaluate(`document.getElementById('appsClose').click()`);
check('the dialog closes', await until(`!document.getElementById('apps').open`));
await evaluate(`document.getElementById('settingsButton').click()`);


check('the settings dialog opens with the models', await until(`document.getElementById('settings').open && document.getElementById('modelSelect').options.length === 2`));
check('the picker shows this conversation\'s model', await evaluate(`document.getElementById('modelSelect').value === conversationState.model`));
await sleep(300);
await shot('7-settings');
await evaluate(`(() => { document.getElementById('modelSelect').value = 'gpt-6.1-terra'; document.getElementById('settingsSave').click(); })()`);
check('choosing a model there changes this conversation\'s', await until(`!document.getElementById('settings').open && conversationState.model === 'gpt-6.1-terra'`));
console.log('console:', JSON.stringify(logs.slice(0, 10)));
check('no script errors', !logs.some(l => l.startsWith('EXCEPTION')));
ws.close();
process.exit(results.every(Boolean) ? 0 : 1);
