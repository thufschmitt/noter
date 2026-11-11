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
await sleep(2600);
check('nothing is asked by itself', await evaluate(`![...document.querySelectorAll('.note')].some(n => !n.classList.contains('hint') && !n.classList.contains('typed'))`));
await evaluate(`document.getElementById('ask').click()`);
check('Ask gets a reply', await until(`[...document.querySelectorAll('.note')].some(n => !n.classList.contains('hint') && n.textContent.includes('Claude answering'))`, 20000));
await sleep(800);
check('the reply sits below the ink on a rule line', await evaluate(`(() => { const n = [...document.querySelectorAll('.note')].find(n => !n.classList.contains('hint')); return parseFloat(n.style.top) - 3 === 280 || parseFloat(n.style.top) - 3 >= 240; })()`));
check('the reply is back to idle (Ask disabled, label Ask)', await until(`document.getElementById('ask').disabled && document.getElementById('askLabel').textContent === 'Ask'`));
await shot('3-answered');

// Annotate the reply: write over it.
const replyTop = await evaluate(`parseFloat([...document.querySelectorAll('.note')].find(n => !n.classList.contains('hint')).style.top)`);
await stroke([[ox + 90, replyTop + 20], [ox + 400, replyTop + 24], [ox + 500, replyTop + 20]]);
await until(`!document.getElementById('ask').disabled`);
await evaluate(`document.getElementById('ask').click()`);
check('a block arrives with the second reply', await until(`document.querySelectorAll('iframe.drawing').length === 1`, 20000));
await sleep(1500);
check('the block is rendered and sized to its content', await evaluate(`(() => { const f = document.querySelector('iframe.drawing'); const d = f.contentDocument; return parseFloat(f.style.width) > 300 && parseFloat(f.style.height) > 200 && d.querySelectorAll('td').length === 2 && d.querySelector('svg text').getBBox().width > 10; })()`));
check('nothing in the block ran, and it runs nothing', await evaluate(`window.hacked === undefined && !document.querySelector('iframe.drawing').contentDocument.querySelector('script') && document.querySelector('iframe.drawing').getAttribute('sandbox') === 'allow-same-origin'`));
check('the block is in the ink colour, on a clear background', await evaluate(`(() => { const d = document.querySelector('iframe.drawing').contentDocument; return getComputedStyle(d.body).color === 'rgb(44, 79, 196)' && getComputedStyle(d.body).backgroundColor === 'rgba(0, 0, 0, 0)'; })()`));
check('the words above the block are only the words', await evaluate(`[...document.querySelectorAll('.note')].some(n => n.textContent === 'Here is your week, with a sketch.')`));
await shot('4-block');

// Annotate the block: the picture sent to Claude shows it.
const block = await evaluate(`(() => { const f = document.querySelector('iframe.drawing'); return { top: parseFloat(f.style.top), h: parseFloat(f.style.height) }; })()`);
await stroke([[ox + 120, block.top + 30], [ox + 200, block.top + block.h / 2], [ox + 260, block.top + 40]]);
await until(`!document.getElementById('ask').disabled`);
await evaluate(`document.getElementById('ask').click()`);
check('annotating the block gets a reply', await until(`document.querySelectorAll('iframe.drawing').length === 2`, 20000));
await sleep(800);

// Reload: the conversation comes back.
await call('Page.navigate', { url: pageUrl });
check('reloading restores the replies and the blocks', await until(`document.querySelectorAll('.note:not(.hint)').length === 3 && document.querySelectorAll('iframe.drawing').length === 2`));
check('and the ink', await until(`typeof strokes !== 'undefined' && strokes.size === 5`));
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


check('the settings dialog opens with the models', await until(`document.getElementById('settings').open && document.getElementById('modelSelect').options.length === 3`));
check('the picker shows this conversation\'s model', await evaluate(`document.getElementById('modelSelect').value === conversationState.model`));
await sleep(300);
await shot('7-settings');
await evaluate(`(() => { document.getElementById('modelSelect').value = 'claude-sonnet-5-5'; document.getElementById('settingsSave').click(); })()`);
check('choosing a model there changes this conversation\'s', await until(`!document.getElementById('settings').open && conversationState.model === 'claude-sonnet-5-5'`));
console.log('console:', JSON.stringify(logs.slice(0, 10)));
check('no script errors', !logs.some(l => l.startsWith('EXCEPTION')));
ws.close();
process.exit(results.every(Boolean) ? 0 : 1);
