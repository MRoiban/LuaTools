/* Source-owned Steam UI. Runs only in the injector's isolated JavaScript world.
 * CSS is supplied as __LuaToolsCss before this script. Closed shadow roots keep
 * Steam styles and page scripts out of controls; isTrusted guards bridge actions.
 * No page-world globals, remote markup, fetches, or Millennium dependencies. */
(function () {
  'use strict';
  const clean = (value, max = 240) => String(value == null ? '' : value).replace(/[\u0000-\u001f\u007f]/g, ' ').slice(0, max);
  const pageId = (kind, path) => { const m = new RegExp(`^/${kind}/(\\d+)(?:/|$)`).exec(path); return m && /^[1-9]\d{0,9}$/.test(m[1]) && Number(m[1]) <= 4294967295 ? Number(m[1]) : null; };
  const appId = path => pageId('app', path);
  const dlcPageId = path => pageId('dlc', path);
  // "Add all DLC": owned ids come from Steam's own ds_owned markers and only ever SKIP a DLC.
  const ownedIds = nodes => Array.from(new Set(Array.from(nodes || []).flatMap(n => String(n.getAttribute && n.getAttribute('data-ds-appid') || '').split(','))
    .map(v => v.trim()).filter(v => /^[1-9]\d{0,9}$/.test(v) && Number(v) <= 4294967295).map(Number))).slice(0, 400);
  function batchCounts(data) {
    const items = (Array.isArray(data && data.items) ? data.items : []).filter(i => i && typeof i === 'object').slice(0, 1000);
    const count = state => items.filter(i => i.state === state).length;
    return { total: items.length, ready: count('ready'), download: items.filter(i => i.state === 'ready' && i.download === true).length,
      owned: count('owned'), added: count('added'), unavailable: count('unavailable') + count('not-dlc'), installed: count('installed'),
      failed: count('failed'), limit: count('limit'), done: Math.max(0, Math.floor(Number(data && data.done) || 0)) };
  }
  const sourceRows = data => (Array.isArray(data.sources) ? data.sources : []).filter(s => s && typeof s === 'object').slice(0, 32).map(s => ({
    name: clean(s.name, 128), displayName: clean(s.displayName || s.name, 128), status: clean(s.status, 40),
    canDownload: s.canDownload === true, locked: s.locked === true, needsKey: s.needsKey === true,
    stats: clean(s.stats, 64), downloading: s.downloading === true, indeterminate: s.indeterminate === true,
    progress: Math.max(0, Math.min(100, Math.floor(Number(s.progress) || 0)))
  }));
  function classify(data) {
    if (data.cancelled) return 'cancelled';
    if (data.installStatus || data.installed) return 'added';
    if (data.error) return !data.fastFetch && sourceRows(data).length ? 'failed' : 'error';
    if (sourceRows(data).some(s => s.downloading)) return 'downloading';
    if (data.sourcesLoaded && !data.fastFetch) return 'pick';
    return 'checking';
  }
  // Dictionaries are keyed by original English copy; optional locale maps are
  // selected from Steam's document language. Backend names/errors stay verbatim.
  function translator(dictionaries, language = 'en') {
    const lang = clean(language, 32).replace(/_/g, '-').toLowerCase();
    const source = dictionaries && typeof dictionaries === 'object' ? dictionaries : {};
    const tag = lang.split('-')[0] === 'fr' ? 'fr' : 'en';
    const dict = source[tag] || {};
    return (key, values = {}) => {
      const value = Object.prototype.hasOwnProperty.call(dict, key) && typeof dict[key] === 'string' ? clean(dict[key], 1000) : key;
      return value.replace(/\{(\w+)\}/g, (match, name) => Object.prototype.hasOwnProperty.call(values, name) ? clean(values[name]) : match);
    };
  }
  // Pure helpers are exported only in Node, for source-owned behavior tests.
  if (typeof module === 'object' && module.exports) { module.exports = { clean, appId, dlcPageId, ownedIds, batchCounts, sourceRows, classify, translator }; return; }
  if (window.__LuaToolsInjected) return;
  if (location.hostname !== 'store.steampowered.com') return;
  window.__LuaToolsInjected = true;
  const css = typeof window.__LuaToolsCss === 'string' ? window.__LuaToolsCss : '';
  const t = translator(window.__LuaToolsStrings, document.documentElement.lang || 'en');
  let context = null, nav = null, overlay = null, menu = null, modal = null, url = '', scanPending = false, batch = null;
  let loaded = [], menuGeneration = 0;
  // An app restart injects a fresh isolated world while this one keeps running. The newest injection
  // stamps the shared DOM; an older instance that sees a newer stamp removes its hosts and goes quiet,
  // so the page never shows duplicate panels backed by a dead bridge.
  const generation = Math.random().toString(36).slice(2);
  document.documentElement.setAttribute('data-luatools-gen', generation);
  for (const stale of Array.from(document.querySelectorAll('[data-luatools]'))) stale.remove();
  const superseded = () => document.documentElement.getAttribute('data-luatools-gen') !== generation;
  const el = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; return n; };
  function moon(panel = false) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('aria-hidden', 'true');
    if (panel) svg.setAttribute('class', 'lt-mark');
    svg.setAttribute('width', panel ? '20' : '15'); svg.setAttribute('height', panel ? '20' : '15');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('fill', 'currentColor'); path.setAttribute('d', 'M14.5 2.5a9.5 9.5 0 1 0 7 15.9A8 8 0 0 1 14.5 2.5Z'); svg.append(path); return svg;
  }
  function root(parent, type) {
    const host = el('div'); host.dataset.luatools = type;
    const shadow = host.attachShadow({ mode: 'closed' });
    shadow.append(el('style', '', css)); parent.append(host);
    return { host, shadow };
  }
  function clear(shadow) { for (const child of Array.from(shadow.children)) if (child.tagName !== 'STYLE') child.remove(); }
  function button(text, action, kind = 'blue', small = false) {
    const b = el('button', `lt-btn lt-${kind}${small ? ' lt-small' : ''} Focusable`);
    b.type = 'button'; b.append(el('span', '', t(text)));
    b.addEventListener('click', event => { if (event.isTrusted && !b.disabled) action(event); }); return b;
  }
  async function call(method, args = {}) {
    if (!window.LuaToolsBridge || typeof window.LuaToolsBridge.call !== 'function') throw Error('LuaTools is unavailable.');
    const result = await window.LuaToolsBridge.call(method, args);
    if (!result || typeof result !== 'object') throw Error('LuaTools is unavailable.');
    if (result.success === false) throw Error(clean(result.error || 'LuaTools could not complete the request.', 1000));
    return result;
  }
  const current = c => context === c && c.host.isConnected;
  function stop(c) { c.generation++; clearTimeout(c.timer); c.timer = null; }
  function fail(c, error) {
    if (!current(c)) return;
    stop(c); c.busy = false; c.reading = false; c.error = clean(error && error.message || error, 1000);
    c.state = /disabled in Settings|\b403\b/i.test(c.error) ? 'disabled' : 'offline'; render(c);
  }
  async function read(c) {
    stop(c); c.busy = false; c.reading = true; const gen = c.generation;
    try {
      const result = await call('HasLuaToolsForApp', { appid: c.id });
      if (!current(c) || gen !== c.generation) return;
      c.reading = false; c.exists = result.exists === true; c.state = c.exists ? 'installed' : 'idle'; render(c);
    } catch (e) { if (gen === c.generation) fail(c, e); }
  }
  async function start(c) {
    if (!current(c) || c.busy || ['checking', 'downloading', 'pick'].includes(c.state)) return;
    stop(c); const gen = c.generation; c.busy = true; c.state = 'checking'; c.data = {}; c.error = ''; render(c);
    try {
      await call('StartLuaToolsAdd', { appid: c.id, name: c.name });
      if (!current(c) || gen !== c.generation) return;
      c.busy = false; poll(c, gen);
    } catch (e) { if (gen === c.generation) { c.busy = false; fail(c, e); } }
  }
  async function poll(c, gen) {
    if (!current(c) || gen !== c.generation) return;
    try {
      const data = await call('GetLuaToolsAddStatus', { appid: c.id });
      if (!current(c) || gen !== c.generation) return;
      c.data = data; c.state = classify(data); c.error = clean(data.error, 1000);
      if (c.state === 'added') c.exists = true;
      render(c);
      if (['checking', 'downloading', 'pick'].includes(c.state)) c.timer = setTimeout(() => poll(c, gen), 350);
    } catch (e) { if (gen === c.generation) fail(c, e); }
  }
  async function pick(c, source) {
    if (!current(c) || c.busy || c.data.fastFetch || !source.canDownload) return;
    stop(c); const gen = c.generation; c.busy = true;
    c.state = 'downloading'; c.data = { sources: [{ ...source, downloading: true, indeterminate: true }] }; render(c);
    try {
      await call('PickLuaToolsAddSource', { appid: c.id, source: source.name });
      if (!current(c) || gen !== c.generation) return;
      c.busy = false; poll(c, gen);
    } catch (e) { if (gen === c.generation) { c.busy = false; fail(c, e); } }
  }
  async function cancel(c) {
    if (!current(c)) return;
    stop(c); const gen = c.generation; c.busy = true; c.state = 'cancelled'; render(c);
    try { await call('CancelAddViaLuaTools', { appid: c.id }); }
    catch (e) { if (gen === c.generation) fail(c, e); }
    finally { if (current(c) && gen === c.generation) { c.busy = false; render(c); } }
  }
  function denuvo(container) { if (Array.from(document.querySelectorAll('.DRM_notice')).some(n => /denuvo/i.test(n.textContent))) container.append(el('span', 'lt-tag lt-warn', 'Denuvo')); }
  function render(c) {
    if (!current(c)) return;
    const active = c.shadow.activeElement || (c.flowBody && overlay.shadow.activeElement);
    const focusText = active && active.tagName === 'BUTTON' ? active.textContent : null;
    clear(c.shadow);
    if (c.fallback && !c.expanded) { c.shadow.append(button(c.exists ? 'LuaTools' : 'Add via LuaTools', () => { c.expanded = true; flowModal(c); })); return; }
    const area = el('section', c.state === 'installed' ? 'lt-owned' : 'lt-panel');
    area.setAttribute('aria-label', 'LuaTools');
    if (c.state === 'installed') {
      area.append(el('span', 'lt-check', '✓'));
      const tx = el('div', 'lt-owned-text', t('{game} is added via LuaTools', { game: c.name })); denuvo(tx); area.append(tx);
      const acts = el('div', 'lt-acts');
      acts.append(button('Fixes', () => simple('OpenFix', { appid: c.id }), 'blue', true), button('Remove', () => remove(c), 'blue', true), button('Restart Steam', restart, 'blue', true)); area.append(acts);
    } else {
      const state = c.state, data = c.data || {}, sources = sourceRows(data), downloading = sources.find(s => s.downloading);
      const addTitle = t('Add {game} via LuaTools', { game: c.name });
      const titles = { idle: addTitle, checking: addTitle, pick: t('Choose a source for {game}', { game: c.name }), downloading: t('Adding {game}', { game: c.name }), added: t('{game} added', { game: c.name }), failed: addTitle, error: addTitle, cancelled: t('Cancelled'), removed: t('{game} removed', { game: c.name }), offline: t('LuaTools unavailable'), disabled: t('LuaTools disabled') };
      area.append(el('h2', '', titles[state] || 'LuaTools'), moon(true));
      const sub = el('div', `lt-sub${['failed','error','offline','disabled'].includes(state) ? ' lt-error' : ''}`);
      const copy = { idle: t('Not added'), checking: t('Checking availability…'), pick: '', downloading: downloading ? t('Downloading from {source}…', { source: downloading.displayName }) : t('Starting download…'), added: clean(data.installStatus, 1000) || t('Added via LuaTools'), cancelled: '', removed: t('Restart Steam to apply changes') };
      sub.append(document.createTextNode(c.error || copy[state] || '')); denuvo(sub); area.append(sub);
      if (['pick', 'failed', 'error'].includes(state) && sources.length) {
        const list = el('div', 'lt-sources');
        for (const s of sources) {
          const locked = s.needsKey && s.locked, available = s.status === 'available';
          const row = el('div', `lt-src ${s.downloading ? 'lt-busy' : locked ? 'lt-locked' : available ? 'lt-ok' : 'lt-none'}`);
          row.append(el('span', 'lt-name', s.displayName));
          if (s.stats && (available || locked)) row.append(el('span', 'lt-use', s.stats));
          row.append(el('span', 'lt-status', `● ${t(s.downloading ? 'Downloading' : locked ? s.stats ? 'Limit reached' : 'Needs key' : available ? 'Available' : 'Not found')}`));
          const act = el('span', 'lt-source-action');
          if (locked) act.append(button('Add key', () => simple('OpenSettings'), 'link', true));
          else if (s.canDownload && !data.fastFetch && state !== 'error') {
            act.append(button('Download', () => pick(c, s), 'green', true));
            row.addEventListener('click', e => { if (e.isTrusted && !e.target.closest('button')) pick(c, s); });
          }
          row.append(act); list.append(row);
        } area.append(list);
      }
      if (state === 'downloading' || state === 'checking') {
        const p = el('div', 'lt-progress'), bar = el('div', `lt-bar${!downloading || downloading.indeterminate ? ' lt-indeterminate' : ''}`), fill = el('i');
        if (downloading && !downloading.indeterminate) fill.style.width = `${downloading.progress}%`;
        bar.setAttribute('role', 'progressbar'); bar.setAttribute('aria-label', 'Download progress');
        if (downloading && !downloading.indeterminate) { bar.setAttribute('aria-valuenow', downloading.progress); bar.setAttribute('aria-valuemin', '0'); bar.setAttribute('aria-valuemax', '100'); }
        bar.append(fill); p.append(bar, el('div', 'lt-percent', downloading && !downloading.indeterminate ? `${downloading.progress}%` : '…')); area.append(p);
      }
      const actions = el('div', 'lt-action');
      if (['idle','removed','cancelled'].includes(state)) actions.append(button('Add via LuaTools', () => start(c), 'green'));
      if (['checking','downloading','pick','failed'].includes(state)) actions.append(button('Cancel', () => cancel(c), 'grey'));
      if (['added','removed'].includes(state)) actions.append(button('Restart Steam', restart, 'green'));
      if (['failed','error'].includes(state)) {
        if (sources.some(s => s.locked && s.needsKey)) actions.append(button('Open Settings', () => simple('OpenSettings')));
        actions.append(button('Try Again', () => start(c), 'green'), button('Close', () => { stop(c); c.state = c.exists ? 'installed' : 'idle'; c.error = ''; render(c); }, 'grey'));
      }
      if (state === 'offline') actions.append(button('Retry', () => read(c), 'green'));
      for (const b of actions.children) b.disabled = c.busy;
      if (actions.childNodes.length) area.append(actions);
    }
    if (c.flowBody && modal && c.expanded) c.flowBody.replaceChildren(area); else c.shadow.append(area);
    if (focusText) Array.from(area.querySelectorAll('button')).find(b => b.textContent === focusText)?.focus();
  }
  const batchCurrent = b => batch === b && b.host.isConnected;
  async function batchPoll(b, gen) {
    if (!batchCurrent(b) || gen !== b.gen) return;
    try {
      const data = await call('GetDlcBatchStatus', { appid: b.id });
      if (!batchCurrent(b) || gen !== b.gen) return;
      b.data = data; b.state = ['checking', 'ready', 'applying', 'done', 'failed'].includes(data.phase) ? data.phase : 'idle';
      b.error = clean(data.error, 1000); renderBatch(b);
      if (['checking', 'applying'].includes(b.state)) b.timer = setTimeout(() => batchPoll(b, gen), 400);
    } catch (e) { if (gen === b.gen) { b.state = 'failed'; b.error = clean(e.message, 1000); renderBatch(b); } }
  }
  async function batchCall(b, method, args, next) {
    if (!batchCurrent(b) || b.busy) return;
    clearTimeout(b.timer); const gen = ++b.gen; b.busy = true; b.error = ''; b.state = next; renderBatch(b);
    try { await call(method, { appid: b.id, ...args }); b.busy = false; batchPoll(b, gen); }
    catch (e) { if (gen === b.gen) { b.busy = false; b.state = 'failed'; b.error = clean(e.message, 1000); renderBatch(b); } }
  }
  const batchCheck = b => batchCall(b, 'CheckDlcBatch', { owned: ownedIds(document.querySelectorAll('[data-ds-appid].ds_owned')) }, 'checking');
  const batchApply = b => batchCall(b, 'ApplyDlcBatch', {}, 'applying');
  async function batchCancel(b) {
    clearTimeout(b.timer); b.gen++;
    try { await call('CancelDlcBatch', { appid: b.id }); } catch (_) { /* The panel resets either way. */ }
    if (batchCurrent(b)) { b.state = 'idle'; b.busy = false; renderBatch(b); }
  }
  function renderBatch(b) {
    if (!batchCurrent(b)) return;
    clear(b.shadow);
    const n = batchCounts(b.data), area = el('section', 'lt-panel lt-batch'); area.setAttribute('aria-label', 'LuaTools DLC');
    area.append(el('h2', '', t('Add all DLC via LuaTools')), moon(true));
    const sub = el('div', `lt-sub${b.state === 'failed' ? ' lt-error' : ''}`);
    const lines = [];
    if (b.state === 'idle') lines.push(t('Checks every DLC with lua.tools, then adds the ones you don\'t own.'));
    if (b.state === 'checking') lines.push(n.total ? t('Checking DLC… {done}/{total}', { done: n.done, total: n.total }) : t('Finding DLC…'));
    if (b.state === 'ready') {
      lines.push(t('{count} DLC can be added', { count: n.ready }) + (n.download ? ' · ' + t('{count} use a lua.tools download', { count: n.download }) : ''));
      lines.push(t('{owned} owned · {added} already added · {unavailable} unavailable', n));
    }
    if (b.state === 'applying') lines.push(t('Adding DLC… {done}/{total}', { done: n.done, total: n.ready + n.installed + n.failed + n.limit }));
    if (b.state === 'done') {
      lines.push(t('Added {count} DLC. Restart Steam to apply.', { count: n.installed }));
      if (n.failed || n.limit) lines.push(t('{failed} failed · {limit} over the daily download limit', n));
    }
    if (b.state === 'failed') lines.push(b.error || t('LuaTools could not complete the request.'));
    sub.append(document.createTextNode(lines.join(' · '))); area.append(sub);
    if (['checking', 'applying'].includes(b.state)) {
      const total = b.state === 'checking' ? n.total : n.ready + n.installed + n.failed + n.limit;
      const bar = el('div', `lt-bar${total ? '' : ' lt-indeterminate'}`), fill = el('i'), p = el('div', 'lt-progress');
      if (total) fill.style.width = `${Math.min(100, Math.round(100 * n.done / total))}%`;
      bar.setAttribute('role', 'progressbar'); bar.setAttribute('aria-label', 'DLC progress'); bar.append(fill); p.append(bar); area.append(p);
    }
    const actions = el('div', 'lt-action');
    if (b.state === 'idle') actions.append(button('Check DLC', () => batchCheck(b), 'green'));
    if (b.state === 'ready') {
      const add = button(t('Add {count} DLC', { count: n.ready }), () => batchApply(b), 'green'); add.disabled = !n.ready; actions.append(add);
      actions.append(button('Cancel', () => batchCancel(b), 'grey'));
    }
    if (['checking', 'applying'].includes(b.state)) actions.append(button('Cancel', () => batchCancel(b), 'grey'));
    if (b.state === 'done') actions.append(button('Restart Steam', restart, 'green'), button('Check again', () => batchCheck(b), 'grey'));
    if (b.state === 'failed') actions.append(button('Try Again', () => batchCheck(b), 'green'));
    for (const x of actions.children) if (b.busy) x.disabled = true;
    if (actions.childNodes.length) area.append(actions);
    b.shadow.append(area);
  }
  function scanBatch() {
    // Base game page: next to its DLC section. "Browse all DLC" page: under the header.
    const id = appId(location.pathname) || dlcPageId(location.pathname);
    const section = appId(location.pathname) ? document.querySelector('#gameAreaDLCSection') : null;
    const header = dlcPageId(location.pathname) ? document.querySelector('#curator_header_area_ctn_id') : null;
    if (batch && (!batch.host.isConnected || batch.id !== id)) { clearTimeout(batch.timer); batch.host.remove(); batch = null; }
    if (batch || !id || !(section || header)) return;
    const host = el('div'); host.dataset.luatools = 'batch';
    const shadow = host.attachShadow({ mode: 'closed' }); shadow.append(el('style', '', css));
    if (section) section.parentElement.insertBefore(host, section); else header.append(host);
    batch = { host, shadow, id, state: 'idle', data: {}, gen: 0, timer: null, busy: false, error: '' };
    renderBatch(batch); batchPoll(batch, batch.gen); // resumes a batch already running for this game
  }
  function closeMenu(focus = true) { menuGeneration++; if (menu) menu.remove(); menu = null; if (nav) { nav.button.setAttribute('aria-expanded', 'false'); if (focus) nav.button.focus(); } }
  function closeModal() {
    if (!modal) return; const old = modal; modal = null; old.scrim.remove();
    if (context && context.expanded) { stop(context); context.busy = false; context.expanded = false; context.flowBody = null; render(context); }
    if (old.focus && old.focus.isConnected) old.focus.focus();
  }
  function focusables(node) { return Array.from(node.querySelectorAll('button:not(:disabled),a[href],[tabindex="0"]')).filter(n => !n.hidden); }
  function showModal(title, bodyText = '') {
    closeMenu(false); closeModal();
    const scrim = el('div', 'lt-scrim'), card = el('div', 'lt-modal');
    card.setAttribute('role', 'dialog'); card.setAttribute('aria-modal', 'true'); card.setAttribute('aria-label', t(title));
    const body = el('div', 'lt-body', t(bodyText)), footer = el('div', 'lt-footer');
    const x = button('×', closeModal, 'close'); x.setAttribute('aria-label', 'Close');
    card.append(el('h3', '', t(title)), x, body, footer); scrim.append(card);
    const focus = overlay.shadow.activeElement || (nav && nav.shadow.activeElement) || (context && context.shadow.activeElement) || document.activeElement;
    modal = { scrim, card, body, footer, focus }; overlay.shadow.append(scrim);
    scrim.addEventListener('click', e => { if (e.isTrusted && e.target === scrim) closeModal(); });
    card.addEventListener('keydown', e => {
      if (!e.isTrusted) return;
      if (e.key === 'Escape' || e.key === 'GoBack') { e.preventDefault(); e.stopPropagation(); closeModal(); }
      if (e.key === 'Tab') { const a = focusables(card), index = a.indexOf(overlay.shadow.activeElement); if (a.length && ((e.shiftKey && index <= 0) || (!e.shiftKey && index === a.length - 1))) { e.preventDefault(); a[e.shiftKey ? a.length - 1 : 0].focus(); } }
    });
    queueMicrotask(() => { if (modal && modal.card === card) (focusables(card)[0] || card).focus(); }); return modal;
  }
  function flowModal(c) {
    const m = showModal('LuaTools'); c.expanded = true; c.flowBody = m.body; render(c);
    if (c.reading) read(c);
    else if (['checking', 'pick', 'downloading'].includes(c.state)) { stop(c); c.busy = false; poll(c, c.generation); }
  }
  async function simple(method, args) {
    try { await call(method, args); } catch (e) { const m = showModal('LuaTools', clean(e.message, 1000)); m.footer.append(button('OK', closeModal, 'grey')); }
  }
  function restart() {
    const m = showModal('Restart Steam?', 'Restart Steam now?');
    m.footer.append(button('Cancel', closeModal, 'grey'), button('Restart', async e => {
      const control = e.currentTarget; control.disabled = true;
      try { await call('RestartSteam'); if (modal === m) closeModal(); }
      catch (error) { if (modal === m) { m.body.textContent = clean(error.message, 1000); control.disabled = false; } }
    }, 'green'));
  }
  function remove(c) {
    const m = showModal(t('Remove {game}?', { game: c.name }), 'Remove via LuaTools?');
    m.footer.append(button('Cancel', closeModal, 'grey'), button('Remove', async e => {
      const control = e.currentTarget; control.disabled = true;
      try { await call('DeleteLuaToolsForApp', { appid: c.id }); if (modal === m) closeModal(); if (current(c)) { stop(c); c.exists = false; c.state = 'removed'; c.error = ''; render(c); } }
      catch (error) { if (modal === m) { m.body.textContent = clean(error.message, 1000); control.disabled = false; } }
    }));
  }
  async function updates() {
    const m = showModal('Checking for updates', 'Checking…');
    try { await call('CheckForUpdatesNow'); if (modal === m) m.body.textContent = t('Update check requested.'); }
    catch (e) { if (modal === m) m.body.textContent = clean(e.message, 1000); }
    if (modal === m) m.footer.append(button('OK', closeModal, 'green'));
  }
  function normalizeApps(data) { return (Array.isArray(data.apps) ? data.apps : []).slice(0, 500).filter(a => a && appId(`/app/${a.appid}/`) !== null).map(a => ({ id: Number(a.appid), name: clean(a.name || a.appid) })); }
  function loadedModal() {
    const m = showModal('Added since last restart'), list = el('div', 'lt-list');
    for (const app of loaded) {
      const row = el('div', 'lt-list-item'); row.append(el('span', 'lt-name', app.name), button('Install', () => { location.href = `steam://install/${app.id}`; }, 'blue', true), button('SteamDB', () => simple('OpenExternalUrl', { url: `https://steamdb.info/app/${app.id}/` }), 'link', true)); list.append(row);
    }
    m.body.append(list); m.footer.append(button('Dismiss', async e => {
      const control = e.currentTarget; control.disabled = true;
      try { await call('DismissLoadedApps'); loaded = []; if (modal === m) closeModal(); }
      catch (error) { if (modal === m) { m.body.append(el('p', 'lt-error', clean(error.message, 1000))); control.disabled = false; } }
    }, 'grey'), button('Restart Steam', restart, 'green'));
  }
  async function openMenu() {
    if (menu) { closeMenu(); return; }
    const generation = ++menuGeneration;
    const node = el('div', 'lt-menu'); node.setAttribute('role', 'menu'); node.append(el('div', 'lt-menu-heading', 'LuaTools'));
    const item = (text, action) => { const b = button(text, () => { closeMenu(false); action(); }, 'menu-item'); b.setAttribute('role', 'menuitem'); node.append(b); return b; };
    const c = context;
    if (c) { item(t('Fixes for {game}…', { game: c.name }), () => simple('OpenFix', { appid: c.id })); if (c.exists) item(t('Remove {game}…', { game: c.name }), () => remove(c)); }
    const added = item(t('Added since last restart ({count})', { count: loaded.length }), loadedModal);
    node.append(el('hr')); item('Restart Steam…', restart); item('Check for updates', updates); item('Settings…', () => simple('OpenSettings')); item('Discord', () => simple('OpenExternalUrl', { url: 'https://discord.gg/luatools' }));
    menu = node; overlay.shadow.append(node); nav.button.setAttribute('aria-expanded', 'true');
    const rect = nav.button.getBoundingClientRect(); node.style.top = `${Math.max(8, Math.min(rect.bottom + 4, innerHeight - 260))}px`; node.style.right = `${Math.max(8, innerWidth - rect.right)}px`;
    node.addEventListener('keydown', e => {
      if (!e.isTrusted) return;
      const a = focusables(node), i = a.indexOf(overlay.shadow.activeElement);
      if (['Escape','GoBack','Tab'].includes(e.key)) { e.preventDefault(); closeMenu(); }
      if (['ArrowDown','ArrowUp','Home','End'].includes(e.key)) { e.preventDefault(); a[e.key === 'Home' ? 0 : e.key === 'End' ? a.length - 1 : (i + (e.key === 'ArrowDown' ? 1 : -1) + a.length) % a.length].focus(); }
    });
    focusables(node)[0].focus();
    try { const data = await call('ReadLoadedApps'); if (menu === node && generation === menuGeneration) { loaded = normalizeApps(data); added.firstChild.textContent = t('Added since last restart ({count})', { count: loaded.length }); } }
    catch (_) { /* The menu remains usable when the optional count is unavailable. */ }
  }
  function retire() {
    closeMenu(false); closeModal();
    if (context) { stop(context); context.host.remove(); context = null; }
    if (batch) { clearTimeout(batch.timer); batch.gen++; batch.host.remove(); batch = null; }
    for (const r of [nav, overlay]) if (r) r.host.remove();
    nav = null; overlay = null;
  }
  function scan() {
    scanPending = false;
    if (!document.body) return;
    if (superseded()) { retire(); return; }
    if (!overlay || !overlay.host.isConnected) { closeModal(); closeMenu(false); overlay = root(document.body, 'overlay'); }
    if (url !== location.href) { url = location.href; closeMenu(false); closeModal(); if (context) { stop(context); context.host.remove(); context = null; } if (batch) { clearTimeout(batch.timer); batch.host.remove(); batch = null; } }
    if (!nav || !nav.host.isConnected) {
      const anchor = document.querySelector('._1wn1lBlAzl3HMRqS1llwie') || document.querySelector('#store_nav_search_term')?.parentElement || document.querySelector('#store_nav_area, .store_nav');
      if (anchor) {
        nav = root(anchor, 'nav'); nav.button = button('', openMenu, 'icon'); nav.button.replaceChildren(moon()); nav.button.setAttribute('aria-label', 'LuaTools menu'); nav.button.setAttribute('aria-haspopup', 'menu'); nav.button.setAttribute('aria-expanded', 'false'); nav.shadow.append(nav.button); window.__LuaToolsReady = true;
        const n = nav; call('GetIconDataUrl').then(data => { if (nav === n && /^data:image\/(?:png|jpeg|webp|x-icon);base64,[A-Za-z0-9+/=]+$/.test(data.dataUrl || '') && data.dataUrl.length < 200000) { const img = el('img'); img.src = data.dataUrl; img.alt = ''; n.button.replaceChildren(img); } }).catch(() => {});
      }
    }
    const id = appId(location.pathname);
    const purchase = id && document.querySelector('#game_area_purchase');
    // Steam can render the header before the purchase area, or replace that area
    // without navigating. Move our existing host so state and in-flight work survive.
    if (context && purchase && (context.fallback || context.host.parentElement !== purchase)) {
      if (context.host.parentElement !== purchase) purchase.prepend(context.host);
      // Leave an open fallback dialog alone until the user closes it.
      if (!context.expanded) {
        const wasFallback = context.fallback;
        context.fallback = false;
        render(context);
        if (wasFallback && !context.reading && !context.busy && !context.timer
            && ['checking', 'pick', 'downloading'].includes(context.state)) poll(context, context.generation);
      }
    }
    if (context && !context.host.isConnected) { stop(context); context = null; }
    scanBatch();
    if (id && !context) {
      const fallback = document.querySelector('.apphub_OtherSiteInfo') || document.querySelector('#queueBtnFollow')?.parentElement;
      if (purchase || fallback) {
        const target = purchase || fallback, r = root(target, 'panel'); if (purchase) target.prepend(r.host);
        context = { ...r, id, name: clean(document.querySelector('.apphub_AppName')?.textContent || document.title.replace(/\s+on Steam\s*$/i, '').trim() || `App ${id}`), state: 'checking', generation: 0, timer: null, exists: false, data: {}, error: '', busy: false, fallback: !purchase, expanded: false };
        render(context); read(context);
      }
    }
  }
  function scheduleScan() { if (!scanPending) { scanPending = true; queueMicrotask(scan); } }
  // No page-world history monkey patch is needed: mutations plus URL checks cover
  // Steam SPA routing, including routes which do not immediately mutate the DOM.
  new MutationObserver(scheduleScan).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('popstate', scheduleScan);
  window.addEventListener('pagehide', () => { if (context) stop(context); closeMenu(false); closeModal(); });
  document.addEventListener('pointerdown', e => { if (e.isTrusted && menu && !e.composedPath().includes(overlay.host) && !e.composedPath().includes(nav.host)) closeMenu(false); }, true);
  document.addEventListener('focusin', e => { if (modal && !e.composedPath().includes(overlay.host)) focusables(modal.card)[0]?.focus(); }, true);
  setInterval(() => { if (superseded()) { if (nav || overlay || context || batch) retire(); return; } if (url !== location.href || !context || !nav || (context && (!context.host.isConnected || context.fallback)) || (nav && !nav.host.isConnected) || (batch && !batch.host.isConnected)) scheduleScan(); }, 750);
  scan();
})();
