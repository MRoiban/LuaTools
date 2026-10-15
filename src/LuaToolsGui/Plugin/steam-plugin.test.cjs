// Run: node --test src/LuaToolsGui/Plugin/steam-plugin.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { clean, appId, sourceRows, classify, translator } = require('./steam-plugin.js');

test('app IDs, remote names and progress are bounded', () => {
  assert.equal(appId('/app/1145360/Hades/'), 1145360);
  for (const path of ['/app/0/', '/app/1evil', '/app/-1', '/app/11111111111', '/app/4294967296/', '/sub/1']) assert.equal(appId(path), null);
  assert.equal(clean('<script>\n' + 'a'.repeat(300)).length, 240);
  assert.equal(sourceRows({ sources: Array.from({ length: 50 }, () => ({ progress: 120, name: 'x'.repeat(500) })) }).length, 32);
  assert.equal(sourceRows({ sources: [{ progress: -10 }, { progress: 12.9 }, { progress: Infinity }] })[1].progress, 12);
  assert.equal(sourceRows({ sources: [{ name: 'x'.repeat(500) }] })[0].name.length, 128);
});
test('manual, FastFetch, failure, checking and installed status mapping', () => {
  assert.equal(classify({}), 'checking');
  assert.equal(classify({ sourcesLoaded: true, fastFetch: true }), 'checking');
  assert.equal(classify({ sourcesLoaded: true }), 'pick');
  assert.equal(classify({ sources: [{ downloading: true }] }), 'downloading');
  assert.equal(classify({ error: 'limit', sources: [{ needsKey: true, locked: true }] }), 'failed');
  assert.equal(classify({ error: 'missing', fastFetch: true, sources: [{}] }), 'error');
  assert.equal(classify({ installStatus: 'Done' }), 'added');
  assert.equal(classify({ installed: true }), 'added');
  assert.equal(classify({ cancelled: true, error: 'cancelled' }), 'cancelled');
  assert.equal(sourceRows({ sources: [null, false, 5] }).length, 0);
});
test('optional authored dictionaries follow page language with English fallback', () => {
  const t = translator({ fr: { 'Add {game} via LuaTools': 'Ajouter {game} via LuaTools', Cancel: 'Annuler' } }, 'fr-BE');
  assert.equal(t('Add {game} via LuaTools', { game: '<Game>' }), 'Ajouter <Game> via LuaTools');
  assert.equal(t('Cancel'), 'Annuler'); assert.equal(t('Not added'), 'Not added');
  assert.equal(translator(undefined)('Cancel'), 'Cancel');
});

// Deliberately small DOM harness: exercise the actual script and bridge lifecycle
// without installing a browser or dependencies. Visual/CEF behavior needs Steam QA.
class Element {
  constructor(tag, doc) { this.tagName = tag.toUpperCase(); this.doc = doc; this.children = []; this.parentElement = null; this.dataset = {}; this.style = {}; this.events = {}; this.attrs = {}; this._text = ''; this.disabled = false; this.hidden = false; }
  append(...nodes) { for (const n of nodes) { n.remove(); n.parentElement = this; this.children.push(n); } }
  prepend(n) { n.remove(); n.parentElement = this; this.children.unshift(n); }
  replaceChildren(...nodes) { for (const n of [...this.children]) n.remove(); this._text = ''; this.append(...nodes); }
  remove() { if (this.parentElement) { const a = this.parentElement.children; a.splice(a.indexOf(this), 1); this.parentElement = null; } }
  attachShadow() { const n = new Element('shadow', this.doc); n.host = this; this.doc.shadows.push(n); return n; }
  set textContent(s) { for (const n of [...this.children]) n.remove(); this._text = String(s); }
  get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
  get firstChild() { return this.children[0]; }
  get childNodes() { return this.children; }
  get isConnected() { return this === this.doc.body || !!(this.host ? this.host.isConnected : this.parentElement && this.parentElement.isConnected); }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  addEventListener(k, f) { (this.events[k] ||= []).push(f); }
  focus() { let p = this; while (p.parentElement) p = p.parentElement; p.activeElement = this; }
  getBoundingClientRect() { return { bottom: 35, right: 900 }; }
  querySelectorAll(selector) {
    const all = []; const walk = n => { for (const c of n.children) { all.push(c); walk(c); } }; walk(this);
    return all.filter(n => selector === 'button' ? n.tagName === 'BUTTON' : n.tagName === 'BUTTON' && !n.disabled);
  }
  closest(tag) { let n = this; while (n) { if (n.tagName === tag.toUpperCase()) return n; n = n.parentElement; } return null; }
}
const tick = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
function harness(respond, fallback = false) {
  const document = { shadows: [], createElement(tag) { return new Element(tag, this); }, createTextNode(text) { const n = new Element('text', this); n.textContent = text; return n; }, addEventListener() {} };
  document.createElementNS = (_, tag) => document.createElement(tag);
  document.body = new Element('body', document); document.documentElement = document.body;
  let purchase = new Element('main', document), anchor = new Element('nav', document), name = new Element('h1', document); name.textContent = '<Unsafe> Game'; document.body.append(purchase, anchor, name);
  document.querySelector = s => s === '#game_area_purchase' ? fallback ? null : purchase : s === '.apphub_OtherSiteInfo' ? fallback ? purchase : null : s === '._1wn1lBlAzl3HMRqS1llwie' ? anchor : s === '.apphub_AppName' ? name : null;
  document.querySelectorAll = () => [];
  const calls = [], timers = new Map(); let nextTimer = 1, interval, mutation;
  const world = { document, location: { hostname: 'store.steampowered.com', href: 'https://store.steampowered.com/app/123/', pathname: '/app/123/' }, innerWidth: 1000, innerHeight: 800, sessionStorage: { getItem: () => '1' }, MutationObserver: class { constructor(callback) { mutation = callback; } observe() {} }, queueMicrotask, console,
    setTimeout(fn) { const id = nextTimer++; timers.set(id, fn); return id; }, clearTimeout(id) { timers.delete(id); }, setInterval(fn) { interval = fn; }, addEventListener() {},
    LuaToolsBridge: { async call(method, args) { if ('appid' in args) assert.ok(Number.isInteger(args.appid) && args.appid > 0 && args.appid <= 4294967295, 'bridge appid is a positive uint'); calls.push({ method, args }); return respond(method, args); } }, __LuaToolsCss: '' };
  world.window = world; vm.runInNewContext(fs.readFileSync(require.resolve('./steam-plugin.js'), 'utf8'), world);
  const buttons = () => document.shadows.flatMap(s => s.querySelectorAll('button'));
  return { world, calls, timers, get purchase() { return purchase; }, buttons, async showPurchase(notify = true) { purchase = new Element('main', document); document.body.append(purchase); fallback = false; if (notify) mutation(); else interval(); await tick(); }, async replacePurchase() { purchase.remove(); purchase = new Element('main', document); document.body.append(purchase); mutation(); await tick(); }, async rescan() { mutation(); await tick(); }, async click(text, trusted = true) { const b = buttons().find(b => b.isConnected && b.textContent === text); assert.ok(b, `button ${text} exists`); for (const fn of b.events.click || []) fn({ isTrusted: trusted, currentTarget: b, target: b }); await tick(); }, async poll() { const [id, fn] = timers.entries().next().value; timers.delete(id); fn(); await tick(); }, navigate() { world.location.href = 'https://store.steampowered.com/'; world.location.pathname = '/'; interval(); } };
}
test('actual script rejects synthetic actions, downloads manually and cancels without stale poll', async () => {
  let pending; let status = { sourcesLoaded: true, sources: [{ name: 'source', displayName: 'Source', canDownload: true, status: 'available' }] };
  const h = harness(method => method === 'HasLuaToolsForApp' ? { exists: false } : method === 'GetLuaToolsAddStatus' ? status : { success: true });
  await tick(); assert.equal(h.world.__LuaToolsReady, true);
  await h.click('Add via LuaTools', false); assert.equal(h.calls.some(c => c.method === 'StartLuaToolsAdd'), false);
  await h.click('Add via LuaTools'); assert.equal(h.timers.size, 1);
  assert.equal(h.calls.find(c => c.method === 'StartLuaToolsAdd').args.name, '<Unsafe> Game');
  await h.click('Download'); assert.equal(h.calls.filter(c => c.method === 'PickLuaToolsAddSource').length, 1);
  status = { sources: [{ downloading: true, progress: 40 }] }; await h.poll();
  h.world.LuaToolsBridge.call = async method => { h.calls.push({ method }); if (method === 'GetLuaToolsAddStatus') return new Promise(r => pending = r); return { success: true }; };
  await h.poll(); await h.click('Cancel'); assert.equal(h.timers.size, 0);
  pending({ installed: true }); await tick(); assert.equal(h.timers.size, 0);
  assert.ok(h.buttons().some(b => b.textContent === 'Add via LuaTools' && b.isConnected));
});
test('actual script requires remove/restart confirmation and navigation stops polling', async () => {
  const h = harness(method => method === 'HasLuaToolsForApp' ? { exists: true } : { success: true }); await tick();
  await h.click('Remove'); assert.equal(h.calls.some(c => c.method === 'DeleteLuaToolsForApp'), false);
  await h.click('Cancel'); assert.equal(h.calls.some(c => c.method === 'DeleteLuaToolsForApp'), false);
  await h.click('Restart Steam'); assert.equal(h.calls.some(c => c.method === 'RestartSteam'), false);
  await h.click('Restart'); assert.equal(h.calls.filter(c => c.method === 'RestartSteam').length, 1);
  await h.click('Remove'); await h.click('Remove'); assert.equal(h.calls.filter(c => c.method === 'DeleteLuaToolsForApp').length, 1);
  await h.click('Add via LuaTools'); assert.equal(h.timers.size, 1);
  h.navigate(); await tick(); assert.equal(h.timers.size, 0);
});
test('cancel bridge failure leaves Retry enabled', async () => {
  const h = harness(method => { if (method === 'HasLuaToolsForApp') return { exists: false }; if (method === 'CancelAddViaLuaTools') throw Error('offline'); return { success: true }; });
  await tick(); await h.click('Add via LuaTools'); await h.click('Cancel');
  assert.equal(h.timers.size, 0);
  const retry = h.buttons().find(b => b.isConnected && b.textContent === 'Retry');
  assert.ok(retry); assert.equal(retry.disabled, false);
  await h.click('Retry'); await h.click('Add via LuaTools'); assert.equal(h.timers.size, 1);
});
test('fallback modal close stops polling and reopening resumes existing operation', async () => {
  const h = harness(method => method === 'HasLuaToolsForApp' ? { exists: false } : { success: true }, true);
  await tick(); await h.click('Add via LuaTools'); await h.click('Add via LuaTools');
  assert.equal(h.timers.size, 1); await h.click('×'); assert.equal(h.timers.size, 0);
  await h.click('Add via LuaTools'); assert.equal(h.timers.size, 1);
  assert.equal(h.calls.filter(c => c.method === 'StartLuaToolsAdd').length, 1);
});


test('late purchase section upgrades fallback without resetting state or duplicating hosts', async () => {
  const h = harness(method => method === 'HasLuaToolsForApp' ? { exists: true } : { success: true }, true);
  await tick();
  const host = h.purchase.children.find(n => n.dataset.luatools === 'panel');
  assert.ok(host); assert.equal(h.buttons().some(b => b.isConnected && b.textContent === 'Remove'), false);
  await h.showPurchase();
  assert.equal(h.purchase.children[0], host);
  assert.ok(h.buttons().some(b => b.isConnected && b.textContent === 'Remove'));
  await h.rescan(); await h.rescan();
  assert.equal(h.purchase.children.filter(n => n.dataset.luatools === 'panel').length, 1);
  assert.equal(h.calls.filter(c => c.method === 'HasLuaToolsForApp').length, 1);
});

test('periodic reconciliation upgrades late purchase even without a mutation notification', async () => {
  const h = harness(method => method === 'HasLuaToolsForApp' ? { exists: false } : { success: true }, true);
  await tick(); const host = h.purchase.children.find(n => n.dataset.luatools === 'panel');
  await h.showPurchase(false); assert.equal(h.purchase.children[0], host);
  assert.ok(h.world.document.shadows.some(s => s.host === host && s.children.some(n => n.className === 'lt-panel')));
});

test('purchase redraw preserves an active operation instead of resetting to idle', async () => {
  const h = harness(method => method === 'HasLuaToolsForApp' ? { exists: false } : { success: true });
  await tick(); await h.click('Add via LuaTools');
  const host = h.purchase.children.find(n => n.dataset.luatools === 'panel');
  await h.replacePurchase(); assert.equal(h.purchase.children[0], host);
  assert.ok(h.buttons().some(b => b.isConnected && b.textContent === 'Cancel'));
  assert.equal(h.calls.filter(c => c.method === 'HasLuaToolsForApp').length, 1);
  assert.equal(h.calls.filter(c => c.method === 'StartLuaToolsAdd').length, 1);
  assert.equal(h.timers.size, 1);
});

test('only French page languages select French; other languages use English', () => {
  const dictionaries = { en: { Cancel: 'Cancel' }, fr: { Cancel: 'Annuler' }, de: { Cancel: 'Abbrechen' } };
  assert.equal(translator(dictionaries, 'fr_CA')('Cancel'), 'Annuler');
  assert.equal(translator(dictionaries, 'de-DE')('Cancel'), 'Cancel');
  assert.equal(translator(dictionaries, 'en-US')('Cancel'), 'Cancel');
});
