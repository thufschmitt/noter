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
const mouse = (type, x, y) => call('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: 1, pointerType: 'mouse' });
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
check('the pause ends in a reply', await until(`[...document.querySelectorAll('.note')].some(n => !n.classList.contains('hint') && n.textContent.includes('Claude answering'))`, 20000));
await sleep(800);
check('the reply sits below the ink on a rule line', await evaluate(`(() => { const n = [...document.querySelectorAll('.note')].find(n => !n.classList.contains('hint')); return parseFloat(n.style.top) - 3 === 280 || parseFloat(n.style.top) - 3 >= 240; })()`));
check('the reply is back to idle (Ask disabled, label Ask)', await until(`document.getElementById('ask').disabled && document.getElementById('askLabel').textContent === 'Ask'`));
await shot('3-answered');

// Annotate the reply: write over it.
const replyTop = await evaluate(`parseFloat([...document.querySelectorAll('.note')].find(n => !n.classList.contains('hint')).style.top)`);
await stroke([[ox + 90, replyTop + 20], [ox + 400, replyTop + 24], [ox + 500, replyTop + 20]]);
await until(`!document.getElementById('ask').disabled`);
await evaluate(`document.getElementById('ask').click()`);
check('a drawing arrives with the second reply', await until(`document.querySelectorAll('img.drawing').length === 1`, 20000));
await sleep(1500);
check('the drawing is loaded and sized', await evaluate(`(() => { const i = document.querySelector('img.drawing'); return i.complete && i.naturalWidth > 0 && parseFloat(i.style.width) > 100; })()`));
await shot('4-drawing');

// Reload: the conversation comes back.
await call('Page.navigate', { url: pageUrl });
check('reloading restores the replies and the drawing', await until(`document.querySelectorAll('.note:not(.hint)').length === 2 && document.querySelectorAll('img.drawing').length === 1`));
check('and the ink', await until(`typeof strokes !== 'undefined' && strokes.size === 4`));
await sleep(800);
await shot('5-reloaded');

// Conversations flyout + settings dialog.
await evaluate(`document.getElementById('conversations').click()`);
await sleep(300);
await shot('6-flyout');
await evaluate(`document.getElementById('newConversation').click()`);
check('a new conversation shows the hint again', await until(`document.querySelectorAll('.note:not(.hint)').length === 0 && document.querySelector('.note.hint') !== null`));
await evaluate(`document.getElementById('settingsButton').click()`);
check('the settings dialog opens with the models', await until(`document.getElementById('settings').open && document.getElementById('modelSelect').options.length === 3`));
await sleep(300);
await shot('7-settings');
console.log('console:', JSON.stringify(logs.slice(0, 10)));
check('no script errors', !logs.some(l => l.startsWith('EXCEPTION')));
ws.close();
process.exit(results.every(Boolean) ? 0 : 1);
