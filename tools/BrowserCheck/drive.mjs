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
// A drawing's part, where it is on the screen (scrolled into view first).
const onScreen = async (selector) => JSON.parse(await evaluate(`(() => {
  const d = [...notes.values()].flatMap(n => [...n.drawings.values()])[0], root = svgRoot(d), el = root.querySelector(${JSON.stringify(selector)});
  const b = boxOf(root, el), [px, py] = rootToPage(d, b[0] + b[2] / 2, b[1] + b[3] / 2);
  scroller.scrollTop = Math.max(0, d.top - 80);
  const r = inkCanvas.getBoundingClientRect();
  return JSON.stringify([r.left + px, r.top + py - scroller.scrollTop]);
})()`));
const knob = async (which) => JSON.parse(await evaluate(`(() => { const f = selectionFrame(), r = inkCanvas.getBoundingClientRect(); const p = ${which === 'delete' ? '[f.right, f.top]' : '[f.cx, f.top - 30]'}; return JSON.stringify([r.left + p[0], r.top + p[1] - scroller.scrollTop]); })()`));
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
check('a drawing arrives with the second reply', await until(`document.querySelectorAll('iframe.drawing').length === 1`, 20000));
await sleep(1500);
check('the drawing is rendered and sized to its content', await evaluate(`(() => { const f = document.querySelector('iframe.drawing'); const d = f.contentDocument; return parseFloat(f.style.width) > 300 && parseFloat(f.style.height) > 200 && d.querySelectorAll('[data-edit]').length === 2 && d.querySelector('svg text').getBBox().width > 10; })()`));
check('nothing in the drawing ran, and it runs nothing', await evaluate(`window.hacked === undefined && !document.querySelector('iframe.drawing').contentDocument.querySelector('script') && document.querySelector('iframe.drawing').getAttribute('sandbox') === 'allow-same-origin'`));
check('the drawing is in the ink colour, on a clear background', await evaluate(`(() => { const d = document.querySelector('iframe.drawing').contentDocument; return getComputedStyle(d.body).color === 'rgb(44, 79, 196)' && getComputedStyle(d.body).backgroundColor === 'rgba(0, 0, 0, 0)'; })()`));
check('the words above the block are only the words', await evaluate(`[...document.querySelectorAll('.note')].some(n => n.textContent === 'Here is your week, with a sketch.')`));
await shot('4-block');

// ----- editing the board: tap to select, drag to move, turn, delete, lasso -----
await evaluate(`window.firstDrawing = () => [...notes.values()].flatMap(n => [...n.drawings.values()])[0]`);
await sleep(1200); // a tap right after writing is part of the writing; wait it out
const strokesBefore = await evaluate(`strokes.size`);
let [cx, cy] = await onScreen('#card-1');
await mouse('mousePressed', cx, cy, 'pen'); await mouse('mouseReleased', cx, cy, 'pen');
check('tapping a part with the pen selects it, with no dot of ink', await until(`selection && selection.els.length === 1 && selection.els[0].id === 'card-1' && !document.getElementById('selection').hidden`) && await evaluate(`strokes.size`) === strokesBefore);
await shot('4b-selected');
await mouse('mousePressed', cx, cy); for (let i = 1; i <= 10; i++) { await mouse('mouseMoved', cx + i * 15, cy + i * 4); await sleep(10); } await mouse('mouseReleased', cx + 150, cy + 40);
check('dragging inside the frame moves the part', await evaluate(`/^translate\\(/.test(svgRoot(firstDrawing()).querySelector('#card-1').getAttribute('transform'))`));
check('the move makes the page askable, though nothing was written', await until(`!document.getElementById('ask').disabled`, 5000));
let [rx, ry] = await knob('rotate');
const frame = JSON.parse(await evaluate(`JSON.stringify(selectionFrame())`));
const [fcx, fcy] = await onScreen('#card-1');
await mouse('mousePressed', rx, ry); for (let i = 1; i <= 8; i++) { const a = -Math.PI / 2 + (i * 5 * Math.PI) / 180, R = Math.hypot(rx - fcx, ry - fcy); await mouse('mouseMoved', fcx + R * Math.cos(a), fcy + R * Math.sin(a)); await sleep(10); } await mouse('mouseReleased', rx, ry);
check('dragging the knob turns the part', await evaluate(`svgRoot(firstDrawing()).querySelector('#card-1').getAttribute('transform').startsWith('matrix(')`));
await shot('4c-moved-and-turned');
const [dx, dy] = await knob('delete');
await mouse('mousePressed', dx, dy); await mouse('mouseReleased', dx, dy);
check('the cross deletes the part', await until(`!svgRoot(firstDrawing()).querySelector('#card-1') && !selection`));
await evaluate(`document.getElementById('undo').click()`);
check('undo brings the part back (the server shows the drawing again)', await until(`(() => { const r = svgRoot(firstDrawing()); return r && r.querySelector('#card-1') && r.querySelector('#card-1').getAttribute('transform').startsWith('matrix('); })()`, 8000));
await sleep(500);
// The lasso: the pen's barrel button held, circling both cards.
const [c1x, c1y] = await onScreen('#card-1'), [c2x, c2y] = await onScreen('#card-2');
const loop = [[Math.min(c1x, c2x) - 150, Math.min(c1y, c2y) - 90], [Math.max(c1x, c2x) + 150, Math.min(c1y, c2y) - 90], [Math.max(c1x, c2x) + 150, Math.max(c1y, c2y) + 90], [Math.min(c1x, c2x) - 150, Math.max(c1y, c2y) + 90]];
await barrel('mousePressed', ...loop[0]);
for (let i = 1; i <= 4; i++) for (let k = 1; k <= 5; k++) { const a = loop[i - 1], b = loop[i % 4]; await barrel('mouseMoved', a[0] + (b[0] - a[0]) * k / 5, a[1] + (b[1] - a[1]) * k / 5); await sleep(8); }
await barrel('mouseReleased', ...loop[0]);
check('circling with the barrel button selects what is inside', await until(`selection && selection.els.length === 2`));
await shot('4d-lasso');
await key('Escape', 'Escape', 27);
check('Escape puts the selection away', await until(`!selection && document.getElementById('selection').hidden`));
// Writing, then a dot at once beside it, is writing: the dot is not a tap on the part under it.
[cx, cy] = await onScreen('#card-2');
const inkBefore = await evaluate(`strokes.size`);
await stroke([[cx - 40, cy - 6], [cx - 20, cy + 4], [cx, cy - 4]]);
await mouse('mousePressed', cx + 14, cy, 'pen'); await mouse('mouseReleased', cx + 14, cy, 'pen');
check('a dot right after other ink stays ink', await until(`strokes.size === ${inkBefore + 2}`) && await evaluate(`!selection`));
await sleep(1200);
await mouse('mousePressed', cx, cy - 50, 'pen'); await mouse('mouseReleased', cx, cy - 50, 'pen');
await evaluate(`clearSelection()`);
await until(`!document.getElementById('ask').disabled`);
// Ask: what was moved and turned goes to the model with the writing over it; the drawing it sends back replaces this one.
await evaluate(`document.getElementById('ask').click()`);
check('the model answers the edits', await until(`[...document.querySelectorAll('.note')].some(n => n.textContent === 'Moved it.')`, 20000));
const sentEdits = fs.existsSync(`${outDir}/request-3.json`) ? fs.readFileSync(`${outDir}/request-3.json`, 'utf8') : '';
check('the request told it what changed, and carried the drawing as it is now', sentEdits.includes('I changed your drawings by hand') && /card-1: moved by/.test(sentEdits) && sentEdits.includes('turned') && sentEdits.includes('Its source now:') && !sentEdits.includes('card-2: deleted'));
check('and the drawing was replaced where it was, by the one with the same id', await until(`document.querySelectorAll('iframe.drawing').length === 1 && !svgRoot(firstDrawing()).querySelector('#card-1').hasAttribute('transform') && svgRoot(firstDrawing()).querySelector('#card-1 rect').getAttribute('x') === '220'`, 8000));
await shot('4e-replaced');
check('the picture sent with the edits showed the drawing', fs.readdirSync(outDir).some(f => f.startsWith('sent-3-')));

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
check('and Claude answers below it', await until(`[...document.querySelectorAll('.note:not(.hint):not(.typed)')].some(n => n.textContent.includes('with a sketch'))`, 20000));
await sleep(800);
await shot('6b-typed');
// Connected apps: the dialog opens, the registry can be searched, and an app added by address shows up.
await evaluate(`document.getElementById('appsButton').click()`);
check('the connected apps dialog opens, empty, with Nextcloud ready to connect', await until(`document.getElementById('apps').open && document.getElementById('appList').textContent.includes('No apps yet') && document.querySelector('#nextcloud input') !== null`));
await evaluate(`document.getElementById('registryQuery').value = 'todo'; document.getElementById('registrySearch').click()`);
check('registry results list a usable server and count the others', await until(`document.querySelectorAll('#registryResults .hit').length === 1 && document.getElementById('registryNote').textContent.includes('1 more can\\'t be used')`));
await sleep(300);
await shot('6c-apps');
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
