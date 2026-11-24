(() => {
  'use strict';

  // ---------- helpers ----------
  const $ = (selector, root = document) => root.querySelector(selector);
  // UI language: the saved pref wins, else the browser language, else English.
  // Strings are keyed by their English source (gettext style); a missing
  // translation falls back to the key, so an untranslated string can never
  // render blank. `tf` interpolates {0}, {1}, … into the translated template.
  let lang = 'en', forcedLang = null;
  const _ = s => (window.STUDIO_I18N && window.STUDIO_I18N[lang] && window.STUDIO_I18N[lang][s]) || s;
  const _f = (s, ...args) => _(s).replace(/\{(\d+)\}/g, (m, i) => args[+i] != null ? String(args[+i]) : m);
  const setLang = code => {
    const c = (code || 'en').toLowerCase().split('-')[0];
    lang = window.STUDIO_I18N && window.STUDIO_I18N[c] ? c : 'en';
    document.documentElement.lang = lang;
  };
  const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
  const enc = encodeURIComponent;
  const glyph = code => { const i = document.createElement('i'); i.className = 'icon'; i.setAttribute('aria-hidden', 'true'); i.textContent = String.fromCodePoint(code); return i; };

  function h(tag, props, ...kids) {
    const el = document.createElement(tag);
    for (const key in props || {}) {
      const value = props[key];
      if (value == null || value === false) continue;
      if (key === 'class') el.className = value;
      else if (key === 'text') el.textContent = value;
      else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
      else if (value === true) el.setAttribute(key, '');
      else el.setAttribute(key, value);
    }
    for (const kid of kids.flat()) {
      if (kid == null || kid === false) continue;
      el.append(kid.nodeType ? kid : document.createTextNode(kid));
    }
    return el;
  }

  const hexToRgb = hex => { const m = /^#?([0-9a-f]{6})$/i.exec(hex || ''); return m ? [0, 2, 4].map(i => parseInt(m[1].substr(i, 2), 16)) : null; };
  function hue(hex) {
    const rgb = hexToRgb(hex); if (!rgb) return 0;
    const [r, g, b] = rgb.map(v => v / 255), max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    if (!d) return 0;
    let v = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
    v *= 60; return v < 0 ? v + 360 : v;
  }
  const hueGap = (a, b) => { const d = Math.abs(hue(a) - hue(b)); return Math.min(d, 360 - d); };
  function luminance(hex) {
    const rgb = hexToRgb(hex) || [0, 0, 0];
    const [r, g, b] = rgb.map(v => { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); });
    return .2126 * r + .7152 * g + .0722 * b;
  }
  const contrast = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };

  // ---------- bridge ----------
  const host = window.chrome && window.chrome.webview;
  let nextId = 1;
  const pending = new Map();
  if (host) host.addEventListener('message', event => {
    const m = event.data;
    if (m && m.event === 'imported' && m.state) {
      mergeState(m.state); S.sel = m.state.imported || S.sel; S.view = 'edit'; buildAll();
      setStatus(_('Theme imported'));
      return;
    }
    const waiting = m && pending.get(m.id);
    if (!waiting) return;
    pending.delete(m.id);
    if (m.ok) waiting.resolve(m.data); else waiting.reject(new Error(m.error || _('Something went wrong.')));
  });
  const call = (cmd, args) => new Promise((resolve, reject) => {
    if (!host) { reject(new Error(_('Theme Studio is not connected to its host.'))); return; }
    const id = nextId++;
    pending.set(id, { resolve, reject });
    host.postMessage(Object.assign({ id, cmd }, args || {}));
  });

  const query = params => Object.keys(params).map(k => `${k}=${enc(params[k])}`).join('&');
  const imgUrl = window.__imgUrl || ((kind, params) => `img/${kind}?${query(params)}`);
  const splashUrl = window.__splashUrl || ((name, stamp, still) =>
    `splash/${enc(name)}/${enc(stamp)}/index.html?${still ? `still=${still}&` : ''}app=devin&t=${Date.now()}`);

  // ---------- state ----------
  const DEFAULT_ACCENT = '#D980A7';
  const PRESETS = [['Original', 0, 0, 0], ['Soft', 18, 30, 0], ['Ghost', 24, 52, 0], ['Aura', 13, 0, 100], ['Light', 10, 0, 0, { fade: 65, sat: 85 }]];
  const TINTS = ['#8C66D1', '#B8475E', '#4C78D6', '#3C9C8F', '#C48A32', '#A8A8A8'];
  const ACCENTS = ['#D980A7', '#B49CFF', '#EF5B4C', '#E9B44C', '#7FD3E6', '#8FD694'];
  const S = { themes: [], apps: [], looks: [], screen: { w: 2560, h: 1440 }, sel: null, view: 'edit', stage: 'app', confirming: false, still: 3.4, playing: false, previewApp: null,
    prefs: { favorites: [], sort: 'name' }, search: '', compare: false, history: {} };
  let dirty = false, saving = false, saveAgain = false, saveTimer = 0, statusTimer = 0, splashTimer = 0;
  let ui = {};

  const cur = () => S.themes.find(t => t.name === S.sel) || null;
  const usedBy = name => S.apps.filter(a => a.theme === name);

  function parseHash() {
    const out = {};
    for (const part of location.hash.replace(/^#/, '').split('&')) {
      if (!part) continue;
      const i = part.indexOf('=');
      const key = decodeURIComponent(i < 0 ? part : part.slice(0, i));
      out[key] = i < 0 ? '' : decodeURIComponent(part.slice(i + 1));
    }
    return out;
  }

  function mergeState(state) {
    const local = cur();
    const keep = dirty && local ? { name: local.name, look: local.look, text: local.text } : null;
    S.themes = state.themes;
    S.apps = state.apps;
    S.looks = state.looks || S.looks || [];
    S.crosshairs = state.crosshairs || S.crosshairs || [];
    S.cursorPacks = state.cursorPacks || S.cursorPacks || [];
    S.screen = state.screen || S.screen;
    if (state.prefs) { S.prefs = state.prefs; setLang(forcedLang || S.prefs.lang || navigator.language); }
    if (state.version) S.version = state.version;
    if (!S.apps.find(a => a.id === S.previewApp)) { const first = S.apps.find(a => a.installed !== false) || S.apps[0]; S.previewApp = first ? first.id : null; }
    if (keep) { const t = S.themes.find(x => x.name === keep.name); if (t) { t.look = keep.look; t.text = keep.text; } }
    if (!S.themes.find(t => t.name === S.sel)) S.sel = defaultSelection();
  }

  function defaultSelection() {
    if (!S.themes.length) return null;
    let best = null, count = -1;
    for (const t of S.themes) { const n = usedBy(t.name).length; if (n > count) { best = t; count = n; } }
    return best.name;
  }

  function setStatus(text, error) {
    clearTimeout(statusTimer);
    if (ui.status) { ui.status.textContent = text || ''; ui.status.classList.toggle('error', !!error); }
    if (text && !error && ui.status) statusTimer = setTimeout(() => { if (ui.status) ui.status.textContent = ''; }, 1600);
  }

  // ---------- accent ----------
  function applyAccent() {
    const t = cur();
    const accent = t && /^#[0-9a-f]{6}$/i.test(t.text.accent) ? t.text.accent : DEFAULT_ACCENT;
    const root = document.documentElement.style;
    root.setProperty('--accent', accent);
    root.setProperty('--on-accent', contrast(accent, '#121318') >= contrast(accent, '#ffffff') ? '#121318' : '#ffffff');
  }

  // ---------- controls ----------
  function button(label, kind, onClick, iconCode) {
    return h('button', { type: 'button', class: `btn ${kind}`, onclick: onClick }, iconCode ? glyph(iconCode) : null, label);
  }

  function segmented(items, selected, onPick, fill) {
    const root = h('div', { class: `seg${fill ? ' fill' : ''}`, role: 'group' });
    const buttons = items.map(([value, label]) => h('button', { type: 'button', 'aria-pressed': String(value === selected), 'data-value': value, text: label }));
    const paint = value => buttons.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.value === String(value))));
    buttons.forEach((b, i) => {
      b.addEventListener('click', () => onPick(b.dataset.value));
      b.addEventListener('keydown', e => {
        if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
        const next = buttons[clamp(i + (e.key === 'ArrowRight' ? 1 : -1), 0, buttons.length - 1)];
        next.focus(); next.click(); e.preventDefault();
      });
      root.append(b);
    });
    root.paint = paint;
    return root;
  }

  function toggle(on, label, onChange) {
    const el = h('button', { type: 'button', class: 'switch', role: 'switch', 'aria-checked': String(!!on), 'aria-label': label });
    el.addEventListener('click', () => { const next = el.getAttribute('aria-checked') !== 'true'; el.setAttribute('aria-checked', String(next)); onChange(next); });
    return el;
  }

  // def = the value a double-click restores; clicking the number types it.
  function slider(label, min, max, value, format, onInput, def) {
    const valueEl = h('span', { class: 'value', text: format(value), title: _('Click to type a number') });
    const input = h('input', { class: 'slider', type: 'range', min, max, step: 1, 'aria-label': label });
    const fill = () => input.style.setProperty('--fill', `${(input.value - min) / (max - min) * 100}%`);
    input.value = value; fill();
    input.addEventListener('input', () => { fill(); valueEl.textContent = format(+input.value); onInput(+input.value); });
    if (def !== undefined) input.addEventListener('dblclick', () => {
      if (+input.value === def) return;
      input.value = def; fill(); valueEl.textContent = format(def); onInput(def);
    });
    valueEl.addEventListener('click', () => {
      if (valueEl.querySelector('input')) return;
      const typed = h('input', { class: 'val-edit', type: 'text', value: String(input.value), 'aria-label': _f('{0} value', label) });
      let finished = false;
      const finish = save => {
        if (finished) return;
        finished = true;
        let v = save ? parseInt(typed.value, 10) : NaN;
        if (isNaN(v)) { valueEl.textContent = format(+input.value); return; }
        v = Math.max(min, Math.min(max, v));
        input.value = v; fill(); valueEl.textContent = format(v); onInput(v);
      };
      typed.addEventListener('keydown', e => {
        if (e.key === 'Enter') finish(true);
        else if (e.key === 'Escape') finish(false);
        e.stopPropagation();
      });
      typed.addEventListener('blur', () => finish(true));
      valueEl.textContent = ''; valueEl.append(typed); typed.focus(); typed.select();
    });
    const row = h('div', { class: 'srow' }, h('div', { class: 'head' }, h('span', { class: 'label', text: label }), valueEl), input);
    row.set = v => { input.value = v; fill(); valueEl.textContent = format(v); };
    return row;
  }

  function customColor(label, value, selected, onInput, onChange) {
    const input = h('input', { type: 'color', 'aria-label': label, value: /^#[0-9a-f]{6}$/i.test(value) ? value.toLowerCase() : '#d980a7' });
    input.addEventListener('input', () => onInput(input.value.toUpperCase()));
    input.addEventListener('change', onChange);
    return h('span', { class: 'swatch custom', 'aria-pressed': String(selected) }, glyph(0xE790), input);
  }

  function swatchButton(hex, label, pressed, onClick) {
    return h('button', { type: 'button', class: 'swatch', style: `background:${hex}`, 'aria-label': `${label} ${hex}`, 'aria-pressed': String(pressed), onclick: onClick });
  }

  // ---------- rail ----------
  const isFav = name => S.prefs.favorites.indexOf(name) >= 0;
  function toggleFav(name, star) {
    const fav = !isFav(name);
    if (fav) S.prefs.favorites.push(name);
    else S.prefs.favorites = S.prefs.favorites.filter(n => n !== name);
    star.classList.toggle('on', fav);
    star.setAttribute('aria-pressed', String(fav));
    savePrefs();
    if (S.prefs.sort === 'favorites') renderRail();
  }

  // Sort and search are studio preferences — they persist, they are not theme data.
  function visibleThemes() {
    const q = S.search.trim().toLowerCase();
    let list = S.themes.filter(t => !q || t.name.toLowerCase().indexOf(q) >= 0);
    const byName = (a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
    if (S.prefs.sort === 'newest') list = list.slice().sort((a, b) => String(b.created || '').localeCompare(String(a.created || '')) || byName(a, b));
    else if (S.prefs.sort === 'favorites') list = list.slice().sort((a, b) => (isFav(b.name) - isFav(a.name)) || byName(a, b));
    else list = list.slice().sort(byName);
    return list;
  }

  let prefTimer = 0;
  function savePrefs() {
    clearTimeout(prefTimer);
    prefTimer = setTimeout(() => call('setPrefs', { favorites: S.prefs.favorites.join('|'), sort: S.prefs.sort }).catch(() => { }), 300);
  }

  function railTools() {
    const search = h('input', { class: 'input rail-search', type: 'search', placeholder: _('Search themes'), 'aria-label': _('Search themes'), value: S.search });
    search.addEventListener('input', () => { S.search = search.value; renderRail(); });
    ui.search = search;
    const sort = h('select', { class: 'select rail-sort', 'aria-label': _('Sort themes') });
    [['name', _('A-Z')], ['favorites', _('Favourites')], ['newest', _('Newest')]].forEach(([v, label]) => sort.append(h('option', { value: v, text: label })));
    sort.value = S.prefs.sort || 'name';
    sort.addEventListener('change', () => { S.prefs.sort = sort.value; savePrefs(); renderRail(); });
    ui.sort = sort;
    return h('div', { class: 'rail-tools' }, search, sort);
  }

  function renderRail() {
    if (ui.sort) ui.sort.value = S.prefs.sort || 'name';
    if (ui.search && document.activeElement !== ui.search) ui.search.value = S.search;
    const list = ui.list;
    const focused = document.activeElement && document.activeElement.closest && document.activeElement.closest('.row') ? document.activeElement.dataset.name : null;
    list.textContent = '';
    const visible = visibleThemes();
    if (!visible.length)
      list.append(h('p', { class: 'rail-empty', text: S.search.trim() ? _('No themes match the search.') : _('No themes yet.') }));
    for (const t of visible) {
      const using = usedBy(t.name);
      const thumbImg = h('img', { alt: '', src: imgUrl('wallpaper', { theme: t.name, w: 160, v: t.stamp }) });
      const thumb = h('span', { class: `thumb${t.broken ? ' broken' : ''}` }, thumbImg,
        t.gif ? h('i', { class: 'gif-badge', text: 'GIF' }) : t.apng ? h('i', { class: 'gif-badge', text: 'APNG' }) :
        t.slides ? h('i', { class: 'gif-badge', text: _('Slides') }) : null);
      thumbImg.addEventListener('error', () => thumb.classList.add('broken'));
      const fav = isFav(t.name);
      const star = h('button', {
        type: 'button', class: `fav${fav ? ' on' : ''}`, 'aria-label': _f(fav ? 'Unstar {0}' : 'Star {0}', t.name),
        'aria-pressed': String(fav), tabindex: '-1'
      }, glyph(0xE735));
      star.addEventListener('click', e => { e.stopPropagation(); toggleFav(t.name, star); });
      const row = h('button', {
        type: 'button', role: 'option', class: `row${t.name === S.sel && S.view === 'edit' ? ' sel' : ''}`,
        'aria-selected': String(t.name === S.sel && S.view === 'edit'), 'data-name': t.name,
        tabindex: t.name === S.sel ? '0' : '-1', onclick: () => selectTheme(t.name)
      }, thumb, h('span', { class: 'row-text' }, h('div', { class: 'row-name', text: t.name }),
        using.length ? h('div', { class: 'row-use', text: _f('In use: {0}', using.map(a => a.name).join(', ')) }) : null), star);
      row.addEventListener('keydown', e => {
        if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
        const rows = visibleThemes();
        const i = rows.findIndex(x => x.name === t.name);
        const next = rows[clamp(i + (e.key === 'ArrowDown' ? 1 : -1), 0, rows.length - 1)];
        if (next && next.name !== t.name) selectTheme(next.name, true);
        e.preventDefault();
      });
      list.append(row);
    }
    if (focused) { const row = [...list.children].find(r => r.dataset.name === focused); if (row) row.focus(); }
  }

  function selectTheme(name, keepFocus) {
    if (S.sel === name && S.view === 'edit') return;
    flushSave();
    S.sel = name; S.view = 'edit'; S.confirming = false; S.playing = false;
    // The "opened" snapshot: what this theme looked like when it was first
    // opened this session — the Reset button lands here.
    const t = cur();
    if (t) { const hst = hist(t.name); if (!hst.opened) { hst.opened = snap(t); if (!hst.lastCommitted) hst.lastCommitted = snap(t); } }
    hintKey = '';
    buildAll();
    if (t) refreshHint(t.name, t.look);
    if (keepFocus) { const row = [...ui.list.children].find(r => r.dataset.name === name); if (row) row.focus(); }
  }

  // ---------- undo ----------
  // One undo step = one save burst (the whole debounce window). Snapshots are
  // per theme, so they survive switching away and back.
  const snap = t => JSON.parse(JSON.stringify({ look: t.look, text: t.text }));
  const hist = name => S.history[name] || (S.history[name] = { undo: [], redo: [], opened: null, lastCommitted: null });

  function restore(t, shot, status) {
    t.look = JSON.parse(JSON.stringify(shot.look));
    t.text = JSON.parse(JSON.stringify(shot.text));
    dirty = true;
    buildAll();
    return doSave().then(() => setStatus(status));
  }

  function undoEdit() {
    const t = cur(); if (!t) return;
    const hst = hist(t.name);
    if (!hst.undo.length) { setStatus(_('Nothing to undo')); return; }
    flushSave().then(() => {
      const target = hst.undo.pop();
      hst.redo.push(snap(t));
      restore(t, target, _('Undone'));
    });
  }

  function redoEdit() {
    const t = cur(); if (!t) return;
    const hst = hist(t.name);
    if (!hst.redo.length) { setStatus(_('Nothing to redo')); return; }
    flushSave().then(() => {
      const target = hst.redo.pop();
      hst.undo.push(snap(t));
      restore(t, target, _('Redone'));
    });
  }

  function resetToOpened() {
    const t = cur(); if (!t) return;
    const hst = hist(t.name);
    if (!hst.opened) return;
    flushSave().then(() => {
      if (JSON.stringify(snap(t)) === JSON.stringify(hst.opened)) { setStatus(_('Already as it was')); return; }
      hst.undo.push(snap(t));
      restore(t, hst.opened, _('Back to how it was opened'));
    });
  }

  // ---------- canvas ----------
  function renderCanvasHead(t) {
    const error = h('div', { class: 'name-error' });
    const field = h('input', { class: 'name-field', value: t.name, maxlength: 60, 'aria-label': _('Theme name'), spellcheck: 'false' });
    let reverting = false;
    const commit = () => {
      if (reverting) return;
      const next = field.value.trim();
      if (next === t.name) { error.textContent = ''; return; }
      call('renameTheme', { name: t.name, newName: next }).then(state => {
        mergeState(state); S.sel = state.renamed; buildAll(); setStatus(_('Renamed'));
      }).catch(e => { error.textContent = _f('Couldn\'t rename. {0}', e.message); field.value = t.name; });
    };
    field.addEventListener('keydown', e => {
      if (e.key === 'Enter') { field.blur(); }
      else if (e.key === 'Escape') { reverting = true; field.value = t.name; error.textContent = ''; field.blur(); reverting = false; }
    });
    field.addEventListener('blur', commit);
    const tab = S.useOnTab || 'ai';
    const tabs = segmented([['ai', _('AI')], ['other', _('Work in progress')]], tab, v => { S.useOnTab = v; refreshChips(); });
    const chips = S.apps.filter(app => app.group === tab).map(app => {
      const on = app.theme === t.name;
      const chip = h('button', { type: 'button', class: `chip${on ? ' on' : ''}`, 'aria-pressed': String(on), onclick: () => on ? removeFrom(app.id, app.name) : applyTo(app.id, app.name) },
        on ? glyph(0xE73E) : null, app.name);
      return chip;
    });
    return h('div', { class: 'cv-head' }, h('div', { class: 'name-wrap' }, field, error),
      h('div', { class: 'use-on' }, h('span', { class: 'label', text: _('Use on') }), tabs, chips, button(_('All apps'), 'quiet', () => applyTo('all', 'all apps'))));
  }

  function applyTo(appId, label) {
    const t = cur(); if (!t) return;
    flushSave().then(() => call('applyTheme', { name: t.name, app: appId })).then(apps => {
      S.apps = apps; renderRail(); refreshChips();
      setStatus(appId === 'all' ? _('Applied to all apps') : _f('Applied to {0}', label));
    }).catch(e => setStatus(_f('Couldn\'t apply. {0}', e.message), true));
  }

  // Clicking a chip that's already on takes the theme off the app: the copied
  // files go and the app falls back to its bundled wallpaper.
  function removeFrom(appId, label) {
    const t = cur(); if (!t) return;
    flushSave().then(() => call('removeTheme', { name: t.name, app: appId })).then(apps => {
      S.apps = apps; renderRail(); refreshChips();
      setStatus(_f('Removed from {0}', label));
    }).catch(e => setStatus(_f('Couldn\'t apply. {0}', e.message), true));
  }

  function refreshChips() {
    const t = cur(); if (!t || !ui.head) return;
    const fresh = renderCanvasHead(t);
    const field = $('.name-field', ui.head);
    const editing = document.activeElement === field;
    if (editing) { const useOn = $('.use-on', fresh); $('.use-on', ui.head).replaceWith(useOn); }
    else ui.head.replaceWith(fresh);
    if (!editing) ui.head = fresh;
  }

  function buildCanvas(t) {
    const stageSwitch = segmented([['app', _('In the app')], ['splash', _('Startup animation')]], S.stage, v => setStage(v, true));
    ui.stageSwitch = stageSwitch;
    ui.head = renderCanvasHead(t);
    ui.status = h('div', { class: 'status', role: 'status', 'aria-live': 'polite' });
    ui.foot = h('div', { class: 'cv-foot' });
    buildStage(t);
    renderFoot();
    return h('main', { class: 'canvas' }, ui.head, h('div', { class: 'cv-tools' }, stageSwitch), ui.stageWrap, ui.foot);
  }

  function renderFoot() {
    const t = cur();
    ui.foot.textContent = '';
    ui.foot.append(ui.status);
    if (S.confirming) {
      ui.foot.append(h('div', { class: 'confirm' }, _('Delete this theme? It goes to the Recycle Bin.'),
        button(_('Delete'), 'secondary danger', () => deleteTheme(t.name)), button(_('Keep'), 'quiet', () => { S.confirming = false; renderFoot(); })));
    } else ui.foot.append(button(_('Delete theme'), 'quiet danger', () => { S.confirming = true; renderFoot(); }, 0xE74D));
  }

  function deleteTheme(name) {
    const index = S.themes.findIndex(t => t.name === name);
    S.confirming = false;
    call('deleteTheme', { name }).then(state => {
      dirty = false; mergeState(state);
      S.sel = state.themes.length ? state.themes[Math.min(index, state.themes.length - 1)].name : null;
      buildAll(); setStatus(_('Deleted'));
    }).catch(e => { renderFoot(); setStatus(_f('Couldn\'t delete. {0}', e.message), true); });
  }

  // ---------- stage ----------
  const MOCK_W = 1600;
  const CODE = [
    '<k>using</k> System;', '<k>using</k> System.Drawing;', '', '<k>internal static class</k> <t>ArtworkLook</t>', '{',
    '    <c>// Resolution-independent blur, then tint and dim.</c>',
    '    <k>internal static</k> <t>Bitmap</t> <m>Render</m>(<t>Bitmap</t> src, <t>Look</t> look)', '    {',
    '        <k>double</k> sigma = <m>Sigma</m>(look.Blur, Math.<m>Max</m>(src.Width, src.Height));',
    '        <k>bool</k> blur = sigma >= <n>0.3</n>;', '        <k>bool</k> color = look.Tint > <n>0</n> || look.Dim > <n>0</n>;',
    '        <k>if</k> (!blur &amp;&amp; !color) <k>return</k> src;', '        <k>return</k> <m>Colorize</m>(blur ? <m>Blur</m>(src, sigma) : src, look);', '    }', '}'
  ];
  const FILES = ['AppTarget.cs', 'ArtworkLook.cs', 'ArtworkOverlay.cs', 'ImageCodec.cs', 'Launcher.cs', 'SplashPage.cs', 'ThemeSettings.cs', 'ThemeStudio.cs'];

  function mockEditor() {
    const mock = h('div', { class: 'mock', 'aria-hidden': 'true' });
    mock.innerHTML =
      '<div class="activity"><i></i><i></i><i></i><i></i></div>' +
      '<div class="side"><div class="t">Explorer</div>' + FILES.map(f => `<div class="f${f === 'ArtworkLook.cs' ? ' on' : ''}">${f}</div>`).join('') + '</div>' +
      '<div class="tabs"><span class="tab">ArtworkLook.cs</span></div>' +
      '<div class="code">' + CODE.map((line, i) => `<span class="ln">${i + 1}</span>${line.replace(/<(k|t|c|n|s|m)>(.*?)<\/\1>/g, '<span class="$1">$2</span>')}`).join('\n') + '</div>' +
      '<div class="status-bar">main    0 problems    Ln 8, Col 5    UTF-8    C#</div>';
    return mock;
  }

  function mockChat(rawName) {
    const name = String(rawName).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const mock = h('div', { class: 'mock chat', 'aria-hidden': 'true' });
    const groups = [['Today', 'Fix the build', 'Summarize notes'], ['Yesterday', 'Trip plan', 'Draft a reply'], ['Previous 7 days', 'Regex help', 'Recipe ideas']];
    mock.innerHTML =
      '<div class="chat-side"><div class="t">' + name + '</div>' +
      groups.map(g => `<div class="g">${g[0]}</div>` + g.slice(1).map(c => `<div class="c">${c}</div>`).join('')).join('') +
      '</div>' +
      '<div class="chat-main"><div class="msg in">Can you summarize the pull request for me?</div>' +
      '<div class="msg out">Two commits: the renderer moved into its own class, and the settings panel gained an opacity slider.</div>' +
      '<div class="msg in">Nice. Write the release note.</div>' +
      `<div class="composer">Message ${name}…</div></div>`;
    return mock;
  }

  function previewApp() { return S.apps.find(a => a.id === S.previewApp) || S.apps[0]; }

  function mockApp() {
    const pa = previewApp();
    return pa && pa.preview === 'chat' ? mockChat(pa.name) : mockEditor();
  }

  function rebuildMock() {
    const fresh = mockApp();
    if (ui.mock && ui.stage) { ui.stage.replaceChild(fresh, ui.mock); ui.mock = fresh; ui.mock.hidden = S.stage === 'splash'; layoutStage(); }
  }

  function buildStage(t) {
    ui.mock = mockApp();
    ui.art = h('img', { class: 'art', alt: '' });
    ui.frame = null;
    ui.stage = h('div', { class: 'stage', tabindex: '0', 'aria-label': 'Preview. Drag or use the arrow keys to choose what stays in view.' }, ui.mock, ui.art);
    ui.bar = h('div', { class: 'stage-bar' });
    ui.stageWrap = h('div', { class: 'stage-wrap' }, ui.stage, ui.bar);
    wireDrag();
    ui.shown = ''; ui.inflight = false;
    updateArt(t, true);
    applyStageMode();
    if (window.ResizeObserver) { if (ui.ro) ui.ro.disconnect(); ui.ro = new ResizeObserver(layoutStage); ui.ro.observe(ui.stageWrap); }
  }

  function lookParams(t) { return { theme: t.name, blur: t.look.blur, dim: t.look.dim, tint: t.look.tint,
    tintColor: t.look.tintColor, sat: t.look.sat, fade: t.look.fade, v: t.stamp }; }

  // Preview-only approximations of the window-space effects. The helper folds
  // these into per-pixel alpha at present time; CSS gets close enough to judge.
  function updateArt(t, initial) {
    ui.art.style.opacity = S.compare ? '1' : String(t.look.opacity / 100);
    ui.art.style.objectPosition = S.compare ? '50% 50%' : `${t.look.focusX}% ${t.look.focusY}%`;
    ui.art.style.objectFit = S.compare ? 'cover' :
      t.look.fit === 'contain' || t.look.fit === 'pixel' ? 'contain' :
      t.look.fit === 'center' ? 'none' : 'cover';
    ui.art.style.imageRendering = !S.compare && t.look.fit === 'pixel' ? 'pixelated' : '';
    const masks = [];
    if (!S.compare) {
      if (t.look.clear > 0 && t.look.clearSide) {
        const keep = Math.max(0, Math.min(1, 1 - t.look.clear / 100));
        if (t.look.clearSide === 'left') masks.push(`linear-gradient(to right, rgba(0,0,0,${keep}) 0%, rgba(0,0,0,1) 60%)`);
        else if (t.look.clearSide === 'right') masks.push(`linear-gradient(to left, rgba(0,0,0,${keep}) 0%, rgba(0,0,0,1) 60%)`);
        else masks.push(`linear-gradient(to right, rgba(0,0,0,1) 0%, rgba(0,0,0,${keep}) 32%, rgba(0,0,0,${keep}) 68%, rgba(0,0,0,1) 100%)`);
      }
      if (t.look.vignette > 0)
        masks.push(`radial-gradient(ellipse at center, rgba(0,0,0,1) 55%, rgba(0,0,0,${1 - t.look.vignette / 100}) 100%)`);
    }
    ui.art.style.webkitMaskImage = masks.length ? masks.join(', ') : '';
    ui.art.style.maskImage = ui.art.style.webkitMaskImage;
    ui.art.style.webkitMaskComposite = masks.length > 1 ? 'source-in' : '';
    ui.art.style.maskComposite = masks.length > 1 ? 'intersect' : '';
    ui.art.style.mixBlendMode = S.compare || t.look.glow <= 0 ? '' : 'screen';
    // An animated theme previews the real gif; img/look is a still-only render,
    // so the look's blur/dim/saturation get a CSS approximation instead. Tint
    // stays unapproximated — it needs a colour pass no filter can fake well.
    const filt = [];
    const slideList = t.slideFiles || [];
    const slideMode = !S.compare && !(t.gif || t.apng) && slideList.length > 1;
    if (!S.compare && (t.gif || t.apng || slideMode)) {
      if (t.look.blur > 0) filt.push(`blur(${(t.look.blur * 0.15).toFixed(1)}px)`);
      const bright = 1 - (t.look.dim || 0) / 100 * 0.8 + (t.look.fade || 0) / 100 * 0.3;
      if (Math.abs(bright - 1) > 0.01) filt.push(`brightness(${bright.toFixed(2)})`);
      if (t.look.sat !== undefined && t.look.sat !== 100) filt.push(`saturate(${(t.look.sat / 100).toFixed(2)})`);
    }
    ui.art.style.filter = filt.join(' ');
    const wanted = S.compare ? imgUrl('wallpaper', { theme: t.name, w: 1920, v: t.stamp }) :
      (t.gif || t.apng) ? imgUrl('wallpaper', { theme: t.name, w: 1200, v: t.stamp }) :
      slideMode ? imgUrl('slide', { theme: t.name, n: (S.slideN || 0) % slideList.length, w: 1200, v: t.stamp }) :
      imgUrl('look', lookParams(t));
    ui.wanted = wanted;
    if (initial) { ui.shown = ''; }
    if (wanted === ui.shown) return;
    if (ui.inflight) return;
    loadLook();
  }

  function loadLook() {
    const url = ui.wanted;
    ui.inflight = true;
    const probe = new Image();
    const done = ok => {
      ui.inflight = false;
      ui.shown = url;
      if (ok) ui.art.src = url;
      ui.art.hidden = S.stage === 'splash' || !ok;
      layoutStage();
      if (ui.wanted !== ui.shown) loadLook(); else ui.artLoaded && ui.artLoaded();
    };
    probe.onload = () => done(true);
    probe.onerror = () => done(false);
    probe.src = url;
  }

  // Runs a bridge command and re-renders from the state it returns.
  function runState(p) {
    return p.then(state => { if (state && state.themes) { mergeState(state); buildAll(); } })
      .catch(e => setStatus(e && e.message ? e.message : String(e), true));
  }

  // Slideshow themes cycle their slides in the live preview.
  setInterval(() => {
    const t = cur();
    if (!t || S.view !== 'edit' || S.stage !== 'app' || S.compare) return;
    const list = t.slideFiles || [];
    if (list.length < 2 || t.gif || t.apng) return;
    S.slideN = ((S.slideN || 0) + 1) % list.length;
    updateArt(t);
  }, 1400);

  // The Slideshow section of the theme inspector: thumbnails of slides\ with
  // reorder/remove tools, plus the add-pictures button.
  function slidesGroup(t) {
    const files = t.slideFiles || [];
    const strip = h('div', { class: 'slide-strip' });
    files.forEach((f, i) => {
      strip.append(h('span', { class: 'slide-cell' },
        h('img', { alt: f, src: imgUrl('slide', { theme: t.name, n: i, w: 96, v: t.stamp }) }),
        h('span', { class: 'slide-tools' },
          i > 0 ? h('button', { type: 'button', class: 'mini', text: '←', 'aria-label': _f('Move {0} earlier', f),
            onclick: () => runState(call('moveSlide', { name: t.name, file: f, dir: 'up' })) }) : null,
          i < files.length - 1 ? h('button', { type: 'button', class: 'mini', text: '→', 'aria-label': _f('Move {0} later', f),
            onclick: () => runState(call('moveSlide', { name: t.name, file: f, dir: 'down' })) }) : null,
          h('button', { type: 'button', class: 'mini', text: '×', 'aria-label': _f('Remove {0}', f),
            onclick: () => runState(call('removeSlide', { name: t.name, file: f })) }))));
    });
    const group = h('section', { class: 'group', id: 'g-slides' }, h('h3', { text: _('Slideshow') }),
      h('div', { class: 'field' },
        files.length ? strip : h('p', { class: 'hint', text: _('No slides yet.') }),
        h('div', { class: 'btn-row' }, button(_('Add slides…'), 'secondary', () => runState(call('addSlides', { name: t.name })), 0xE8B9)),
        h('p', { class: 'hint', text: files.length > 1
          ? _f('{0} pictures cycle in order — the pace is each app’s slide seconds.', files.length)
          : _('Two or more pictures turn this theme into a slideshow.') })));
    group.addEventListener('focusin', () => setStage('app'));
    return group;
  }

  function layoutStage() {
    const wrap = ui.stageWrap; if (!wrap || !wrap.isConnected) return;
    const box = wrap.getBoundingClientRect();
    const aspect = S.stage === 'splash' ? 3 / 2 : S.screen.w / S.screen.h;
    const barH = ui.bar.offsetHeight + 10;
    const width = Math.max(100, Math.min(box.width, (box.height - barH) * aspect));
    const height = width / aspect;
    ui.bar.style.width = `${Math.round(width)}px`;
    ui.stage.style.width = `${Math.round(width)}px`;
    ui.stage.style.height = `${Math.round(height)}px`;
    const mockH = Math.round(MOCK_W * S.screen.h / S.screen.w);
    ui.mock.style.height = `${mockH}px`;
    ui.mock.style.transform = `scale(${width / MOCK_W})`;
    if (ui.frame) ui.frame.style.transform = `scale(${width / 1000})`;
    if (ui.placeholder) ui.placeholder.style.transform = `scale(${width / 1000})`;
    const draggable = S.stage === 'app' && overflowOf().x + overflowOf().y > 1;
    ui.stage.classList.toggle('draggable', draggable);
  }

  function overflowOf() {
    const w = ui.stage.clientWidth, hgt = ui.stage.clientHeight, nw = ui.art.naturalWidth, nh = ui.art.naturalHeight;
    if (!nw || !nh || !w) return { x: 0, y: 0 };
    const scale = Math.max(w / nw, hgt / nh);
    return { x: Math.max(0, nw * scale - w), y: Math.max(0, nh * scale - hgt) };
  }

  function wireDrag() {
    let drag = null;
    ui.stage.addEventListener('pointerdown', e => {
      if (S.stage !== 'app' || e.button !== 0) return;
      const o = overflowOf(); if (o.x + o.y <= 1) return;
      const t = cur();
      drag = { x: e.clientX, y: e.clientY, fx: t.look.focusX, fy: t.look.focusY, o };
      ui.stage.setPointerCapture(e.pointerId); ui.stage.classList.add('dragging');
    });
    ui.stage.addEventListener('pointermove', e => {
      if (!drag) return;
      const t = cur();
      if (drag.o.x > 1) t.look.focusX = clamp(Math.round(drag.fx - (e.clientX - drag.x) / drag.o.x * 100), 0, 100);
      if (drag.o.y > 1) t.look.focusY = clamp(Math.round(drag.fy - (e.clientY - drag.y) / drag.o.y * 100), 0, 100);
      onEdit('look');
    });
    const end = () => { if (drag) { drag = null; ui.stage.classList.remove('dragging'); } };
    ui.stage.addEventListener('pointerup', end);
    ui.stage.addEventListener('pointercancel', end);
    ui.stage.addEventListener('keydown', e => {
      const step = { ArrowLeft: [-2, 0], ArrowRight: [2, 0], ArrowUp: [0, -2], ArrowDown: [0, 2] }[e.key];
      if (!step || S.stage !== 'app') return;
      const o = overflowOf(), t = cur();
      if (o.x > 1) t.look.focusX = clamp(t.look.focusX + step[0], 0, 100);
      if (o.y > 1) t.look.focusY = clamp(t.look.focusY + step[1], 0, 100);
      onEdit('look'); e.preventDefault();
    });
  }

  function setStage(stage, user) {
    if (S.stage === stage && !S.playing) return;
    if (stage !== 'splash') S.playing = false;
    if (stage !== 'app') S.compare = false;
    S.stage = stage;
    if (ui.stageSwitch) ui.stageSwitch.paint(stage);
    applyStageMode();
  }

  function setStill(seconds) {
    if (S.still === seconds) return;
    S.still = seconds;
    if (S.stage === 'splash' && !S.playing) reloadFrame();
  }

  function applyStageMode() {
    const splash = S.stage === 'splash';
    ui.mock.hidden = splash;
    ui.art.hidden = splash || !ui.art.getAttribute('src');
    renderBar(splash);
    if (splash) {
      if (!ui.frame && !ui.placeholder) {
        const url = splashUrl(cur().name, cur().stamp, S.still);
        if (url) {
          ui.frame = h('iframe', { class: 'splash-frame still', title: _('Startup animation preview'), tabindex: '-1' });
          ui.stage.append(ui.frame);
          ui.frame.src = url;
          ui.frame.addEventListener('load', () => ui.frameLoaded && ui.frameLoaded());
        } else {
          ui.placeholder = h('div', { class: 'splash-placeholder', text: _f('Startup animation preview: {0}', cur().name) });
          ui.stage.append(ui.placeholder);
        }
      } else reloadFrame();
    }
    if (ui.frame) ui.frame.hidden = !splash;
    if (ui.placeholder) ui.placeholder.hidden = !splash;
    layoutStage();
  }

  function renderBar(splash) {
    ui.bar.textContent = '';
    if (splash) { ui.bar.append(button(_('Play'), 'primary', play, 0xE768), button(_('Play full size'), 'quiet', playFull, 0xE740)); return; }
    const pick = h('select', { class: 'select stage-app', 'aria-label': _('Preview as app') });
    for (const a of S.apps) pick.append(h('option', { value: a.id, text: a.installed === false ? _f('{0} (not installed)', a.name) : a.name }));
    pick.value = previewApp() ? previewApp().id : '';
    pick.addEventListener('change', () => { S.previewApp = pick.value; rebuildMock(); });
    const compareBtn = button(_('Compare'), 'quiet', () => { }, 0xE7B4);
    compareBtn.title = _('Hold to see the untouched picture (or hold C)');
    compareBtn.addEventListener('pointerdown', e => { e.preventDefault(); compare(true); });
    compareBtn.addEventListener('pointerup', () => compare(false));
    compareBtn.addEventListener('pointerleave', () => compare(false));
    compareBtn.addEventListener('keydown', e => { if (e.key === ' ' || e.key === 'Enter') { compare(true); e.preventDefault(); } });
    compareBtn.addEventListener('keyup', () => compare(false));
    ui.bar.append(h('p', { class: 'cv-help', text: _('Drag to choose what stays in view. Text is enlarged here; apps using this theme update as you edit.') }),
      compareBtn,
      h('span', { class: 'stage-app-wrap' }, h('span', { class: 'label', text: _('Preview in') }), pick));
  }

  // While held, show the untouched picture instead of the look.
  function compare(on) {
    if (S.compare === on) return;
    S.compare = on;
    if (ui.stage) ui.stage.classList.toggle('comparing', on);
    const t = cur();
    if (t && S.stage === 'app' && ui.art) { ui.shown = ''; updateArt(t); }
  }

  function reloadFrame() {
    const t = cur(); if (!t || !ui.frame) return;
    ui.frame.classList.toggle('still', !S.playing);
    ui.frame.src = splashUrl(t.name, t.stamp, S.playing ? 0 : S.still);
  }

  function scheduleFrame() {
    clearTimeout(splashTimer);
    if (!ui.frame) return;
    splashTimer = setTimeout(() => { if (S.stage === 'splash' && !S.playing) reloadFrame(); }, 300);
  }

  function play() {
    const t = cur(); if (!t) return;
    setStage('splash'); S.playing = true;
    if (ui.frame) reloadFrame();
  }

  window.addEventListener('message', event => {
    if (event.origin !== location.origin || !event.data || !event.data.splash) return;
    if (!S.playing) return;
    S.playing = false;
    if (ui.frame) reloadFrame();
  });

  // ---------- editing and saving ----------
  function onEdit(kind) {
    const t = cur(); if (!t) return;
    if (!dirty) {
      // First change of a burst: the last committed state is the undo target.
      const hst = hist(t.name);
      if (hst.lastCommitted) {
        hst.undo.push(hst.lastCommitted);
        if (hst.undo.length > 50) hst.undo.shift();
      }
      hst.redo = [];
    }
    dirty = true;
    updateArt(t);
    if (kind === 'look') refreshPresets();
    if (ui.centerBtn) ui.centerBtn.hidden = t.look.focusX === 50 && t.look.focusY === 50;
    if (ui.tintGroup) ui.tintGroup.hidden = t.look.tint <= 0;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(doSave, 200);
  }

  function flushSave() {
    clearTimeout(saveTimer);
    if (dirty && !saving) return doSave();
    return Promise.resolve();
  }

  function doSave() {
    const t = cur(); if (!t) return Promise.resolve();
    if (saving) { saveAgain = true; return Promise.resolve(); }
    saving = true;
    const name = t.name, look = Object.assign({}, t.look), text = Object.assign({}, t.text);
    dirty = false;
    return call('saveTheme', { name, look, text }).then(result => {
      const live = S.themes.find(x => x.name === name);
      if (live) {
        hist(name).lastCommitted = snap(live);
        if (result.text.accent && result.text.accent !== live.text.accent) { live.text.accent = result.text.accent; if (cur() === live) { applyAccent(); refreshAccent(); } }
      }
      setStatus(_('Saved'));
      scheduleFrame();
      refreshHint(name, look);
    }).catch(e => { dirty = true; setStatus(_f('Couldn\'t save. {0} It will retry with your next change.', e.message), true); })
      .then(() => { saving = false; if (saveAgain) { saveAgain = false; return doSave(); } });
  }

  // ---------- inspector ----------
  let hintKey = '';
  function refreshHint(name, look) {
    if (!ui.hint) return;
    const key = name + '|' + JSON.stringify(look);
    if (key === hintKey) return;
    hintKey = key;
    call('checkReadability', { name, blur: look.blur, dim: look.dim, tint: look.tint, tintColor: look.tintColor, sat: look.sat, fade: look.fade, clear: look.clear, clearSide: look.clearSide })
      .then(r => { if (ui.hint && hintKey === key) ui.hint.hidden = !r.bright; })
      .catch(() => { });
  }

  function refreshPresets() {
    const t = cur();
    const match = PRESETS.find(p => p[1] === t.look.blur && p[2] === t.look.dim && p[3] === t.look.tint);
    if (ui.presets) ui.presets.paint(match ? match[0] : '');
  }

  function refreshAccent() {
    if (!ui.accentBox) return;
    const fresh = accentField(cur());
    ui.accentBox.replaceWith(fresh);
    ui.accentBox = fresh;
  }

  function accentField(t) {
    const shown = [];
    for (const hex of [...(t.accents || []), ...ACCENTS]) {
      if (!shown.some(s => s.toLowerCase() === hex.toLowerCase() || hueGap(s, hex) < 20)) shown.push(hex);
    }
    const current = (t.text.accent || '').toUpperCase();
    const inList = shown.some(s => s.toUpperCase() === current);
    const list = h('div', { class: 'swatches' });
    const pick = hex => { t.text.accent = hex; applyAccent(); onEdit('text'); refreshAccent(); };
    for (const hex of shown) list.append(swatchButton(hex, _('Accent'), hex.toUpperCase() === current, () => pick(hex)));
    if (!inList && /^#[0-9a-f]{6}$/i.test(current)) list.append(swatchButton(current, _('Accent'), true, () => pick(current)));
    list.append(customColor(_('Custom accent'), current, false,
      hex => { t.text.accent = hex; applyAccent(); onEdit('text'); }, () => refreshAccent()));
    return h('div', { class: 'field' }, h('span', { class: 'label', text: _('Accent') }), list);
  }

  function tintField(t) {
    const list = h('div', { class: 'swatches' });
    const current = (t.look.tintColor || '').toUpperCase();
    const inList = TINTS.some(c => c.toUpperCase() === current);
    const pick = hex => { t.look.tintColor = hex.toUpperCase(); onEdit('look'); refreshTints(); };
    for (const hex of TINTS) list.append(swatchButton(hex, _('Tint color'), hex.toUpperCase() === current, () => pick(hex)));
    if (!inList && /^#[0-9a-f]{6}$/i.test(current)) list.append(swatchButton(current, _('Tint color'), true, () => pick(current)));
    list.append(customColor(_('Custom tint color'), current, false,
      hex => { t.look.tintColor = hex; onEdit('look'); }, () => refreshTints()));
    return h('div', { class: 'field' }, h('span', { class: 'label', text: _('Tint color') }), list);
  }

  function refreshTints() {
    const fresh = tintField(cur());
    ui.tintGroup.replaceWith(fresh); ui.tintGroup = fresh; fresh.hidden = cur().look.tint <= 0;
  }

  function buildInspector(t) {
    const look = t.look;
    ui.presets = segmented(PRESETS.map(p => [p[0], _(p[0])]), '', name => {
      const p = PRESETS.find(x => x[0] === name);
      look.blur = p[1]; look.dim = p[2]; look.tint = p[3];
      if (p[4]) Object.assign(look, p[4]); else { look.fade = 0; look.sat = 100; }
      onEdit('look');
      const aside = document.querySelector('.inspector');
      if (aside) aside.replaceWith(buildInspector(t));
    }, true);
    const sOpacity = slider(_('Visibility'), 4, 40, look.opacity, v => `${v}%`, v => { look.opacity = v; onEdit('look'); }, 15);
    const sBlur = slider(_('Blur'), 0, 100, look.blur, v => String(v), v => { look.blur = v; onEdit('look'); }, 0);
    const sDim = slider(_('Dim'), 0, 80, look.dim, v => String(v), v => { look.dim = v; onEdit('look'); }, 0);
    const sTint = slider(_('Tint'), 0, 100, look.tint, v => String(v), v => { look.tint = v; onEdit('look'); }, 0);
    ui.tintGroup = tintField(t); ui.tintGroup.hidden = look.tint <= 0;
    ui.centerBtn = button(_('Center image'), 'quiet', () => { look.focusX = 50; look.focusY = 50; onEdit('look'); }); ui.centerBtn.hidden = look.focusX === 50 && look.focusY === 50;
    const sSat = slider(_('Colour'), 0, 150, look.sat ?? 100, v => `${v}%`, v => { look.sat = v; onEdit('look'); }, 100);
    const sFade = slider(_('Fade to light'), 0, 100, look.fade ?? 0, v => `${v}%`, v => { look.fade = v; onEdit('look'); }, 0);
    const sVignette = slider(_('Vignette'), 0, 100, look.vignette ?? 0, v => `${v}%`, v => { look.vignette = v; onEdit('look'); }, 0);
    const sGlow = slider(_('Glow'), 0, 100, look.glow ?? 0, v => `${v}%`, v => { look.glow = v; onEdit('look'); }, 0);
    const sClear = slider(_('Clear zone'), 0, 100, look.clear ?? 0, v => `${v}%`, v => { look.clear = v; onEdit('look'); }, 0);
    const sDrift = slider(_('Drift'), 0, 100, look.drift ?? 0, v => `${v}%`, v => { look.drift = v; onEdit('look'); }, 0);
    const sidePick = h('select', { class: 'select', 'aria-label': _('Clear side') });
    [['', _('Off')], ['left', _('Left')], ['right', _('Right')], ['center', _('Middle')]].forEach(([v, label]) => sidePick.append(h('option', { value: v, text: label })));
    sidePick.value = look.clearSide || '';
    sidePick.addEventListener('change', () => { look.clearSide = sidePick.value; onEdit('look'); });
    const fitPick = h('select', { class: 'select', 'aria-label': _('Image fit') });
    [['cover', _('Fill window')], ['contain', _('Fit whole image')], ['center', _('Center')], ['pixel', _('Pixel art')]].forEach(([v, label]) => fitPick.append(h('option', { value: v, text: label })));
    fitPick.value = look.fit || 'cover';
    fitPick.addEventListener('change', () => { look.fit = fitPick.value; onEdit('look'); });
    const fine = h('details', { class: 'field' }, h('summary', { text: _('Fine tuning') }),
      sSat, sFade, sVignette, sGlow, sClear, sDrift,
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Clear side') }), sidePick),
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Image fit') }), fitPick),
      h('p', { class: 'hint', text: _('Fade for light apps, glow keeps only bright parts, the clear zone keeps text readable.') }));
    const lookPick = h('select', { class: 'select', 'aria-label': _('My looks') });
    lookPick.append(h('option', { value: '', text: _('My looks…') }));
    for (const n of S.looks) lookPick.append(h('option', { value: n, text: n }));
    lookPick.value = '';
    lookPick.addEventListener('change', () => {
      if (!lookPick.value) return;
      const n = lookPick.value;
      call('applyLookPreset', { name: n }).then(r => {
        Object.assign(look, r.look); onEdit('look');
        const aside = document.querySelector('.inspector');
        if (aside) aside.replaceWith(buildInspector(t));
      }).catch(e => setStatus(_f('Couldn\'t load the look. {0}', e.message), true));
      lookPick.value = '';
    });
    const lookName = h('input', { class: 'input', maxlength: 60, placeholder: _f('{0} look', t.name), 'aria-label': _('Name for this look') });
    const saveLookBtn = button(_('Save look'), 'secondary', () => {
      const name = lookName.value.trim() || `${t.name} look`;
      call('saveLookPreset', { name, look }).then(state => { mergeState(state); lookName.value = ''; setStatus(_f('Look saved as "{0}"', name)); }).catch(e => setStatus(_f('Couldn\'t save the look. {0}', e.message), true));
    });
    const myLooks = h('div', { class: 'field' }, h('span', { class: 'label', text: _('My looks') }),
      h('div', { class: 'btn-row' }, lookPick, lookName, saveLookBtn));
    ui.hint = h('p', { class: 'hint warn', text: _('The picture stays bright where app text sits — try Dim, Blur or a clear zone.'), hidden: true });
    const wallpaper = h('section', { class: 'group', id: 'g-wallpaper' }, h('h3', { text: _('Wallpaper') }),
      h('div', { class: 'field' }, ui.presets), sOpacity, sBlur, sDim, sTint, ui.tintGroup, ui.centerBtn, fine, myLooks, ui.hint,
      h('div', { class: 'field' }, button(_('Replace image…'), 'secondary', () => replaceImage('replaceWallpaper'), 0xE8B9)));
    wallpaper.addEventListener('focusin', () => setStage('app'));

    // A generated wallpaper keeps its recipe in theme.ini ("gradient"): offer the
    // maker again prefilled instead of the usual replace button alone.
    if (t.gradient) {
      const parts = t.gradient.split('|');
      wallpaper.append(h('div', { class: 'field' }, button(_('Edit gradient…'), 'secondary', () => gradientBox(parts))));
    }

    const title = h('input', { class: 'input', maxlength: 40, placeholder: t.name.toUpperCase(), value: t.text.title, 'aria-label': _('Title') });
    title.addEventListener('input', () => { t.text.title = title.value; onEdit('text'); });
    const subtitle = h('input', { class: 'input', maxlength: 60, placeholder: _('Optional'), value: t.text.subtitle, 'aria-label': _('Subtitle') });
    subtitle.addEventListener('input', () => { t.text.subtitle = subtitle.value; onEdit('text'); });
    subtitle.addEventListener('focus', () => setStill(9.5));
    subtitle.addEventListener('blur', () => setStill(3.4));
    ui.accentBox = accentField(t);
    const avatarThumb = t.hasAvatar ? h('img', { alt: '', src: imgUrl('avatar', { theme: t.name, v: t.stamp }) }) : null;
    const avatar = h('div', { class: 'field' }, h('span', { class: 'label', text: _('Avatar') }),
      h('div', { class: 'avatar-row' }, h('span', { class: 'avatar-slot' }, avatarThumb), button(_('Change…'), 'secondary', () => replaceImage('setAvatar')),
        button(_('Crop…'), 'secondary', () => startCrop(t)),
        t.hasAvatar ? button(_('Remove'), 'quiet', () => replaceImage('clearAvatar')) : null),
      h('p', { class: 'hint', text: _('Shown in the scanner when the animation starts.') }));
    const logs = h('textarea', { class: 'input', rows: 4, 'aria-label': _('Boot lines'), spellcheck: 'false' });
    logs.value = (t.text.logs || []).join('\n');
    logs.addEventListener('input', () => { t.text.logs = logs.value.split('\n').map(x => x.trim()).filter(Boolean).slice(0, 4); onEdit('text'); });
    const pool = h('textarea', { class: 'input', rows: 3, 'aria-label': _('Line pool'), spellcheck: 'false', placeholder: _('One line per row — a random one becomes the last boot line') });
    pool.value = (t.text.lines || []).join('\n');
    pool.addEventListener('input', () => { t.text.lines = pool.value.split('\n').map(x => x.trim()).filter(Boolean).slice(0, 16); onEdit('text'); });
    const boot = h('details', { class: 'field' }, h('summary', { text: _('Boot lines') }), logs,
      h('p', { class: 'hint', text: _('Up to four lines shown while the app starts. Leave empty for the standard ones. {app}, {theme}, {user}, {date}, {time} and {starts} fill in when it plays.') }),
      h('span', { class: 'label', text: _('Line pool') }), pool,
      h('p', { class: 'hint', text: _('When the pool has entries a random one becomes the last boot line each play.') }));
    const font = h('select', { class: 'select', 'aria-label': _('Title font') });
    for (const [value, name] of [['', _('Original')], ['segoe', _('Segoe UI')], ['mono', _('Console')], ['serif', _('Serif')],
      ['gothic', _('Gothic')], ['type', _('Typewriter')], ['impact', _('Impact')]])
      font.append(h('option', { value, text: name }));
    font.value = t.text.font || '';
    font.addEventListener('change', () => { t.text.font = font.value; onEdit('text'); });
    const frame = h('select', { class: 'select', 'aria-label': _('Avatar frame') });
    for (const [value, name] of [['', _('Square')], ['round', _('Rounded')], ['circle', _('Circle')], ['diamond', _('Diamond')], ['hex', _('Hexagon')]])
      frame.append(h('option', { value, text: name }));
    frame.value = t.text.frame || '';
    frame.addEventListener('change', () => { t.text.frame = frame.value; onEdit('text'); });
    const style = h('select', { class: 'select', 'aria-label': _('Animation style') });
    for (const [value, name] of [['', _('Follow the app')], ['full', _('Full')], ['short', _('Card')],
      ['terminal', _('Terminal')], ['manga', _('Manga')], ['minimal', _('Minimal')]])
      style.append(h('option', { value, text: name }));
    style.value = t.text.style || '';
    style.addEventListener('change', () => { t.text.style = style.value; onEdit('text'); });
    const chimePick = h('select', { class: 'select', 'aria-label': _('Built-in chime') });
    for (const [v, label] of [['bell', _('Bell')], ['blip', _('Blip')], ['whoosh', _('Whoosh')]])
      chimePick.append(h('option', { value: v, text: label }));
    const chime = h('div', { class: 'field' }, h('span', { class: 'label', text: _('Startup sound') }),
      h('div', { class: 'btn-row' },
        button(_('Pick .wav…'), 'secondary', () => runState(call('setChime', { name: t.name }))),
        chimePick, button(_('Use'), 'quiet', () => runState(call('useChime', { name: t.name, kind: chimePick.value }))),
        t.sound ? button(_('Play'), 'secondary', () => call('playChime', { name: t.name }).catch(e => setStatus(e.message, true))) : null,
        t.sound ? button(_('Remove'), 'quiet', () => runState(call('clearChime', { name: t.name }))) : null),
      h('p', { class: 'hint', text: t.sound ? _('A chime plays when the animation starts.') : _('Optional chime when the animation starts — pick a file or a built-in.') }));
    const startup = h('section', { class: 'group', id: 'g-startup' }, h('h3', { text: _('Startup animation') }),
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Style') }), style,
        h('p', { class: 'hint', text: _('The animation look this theme plays. Apps without their own style follow it.') })),
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Title') }), title),
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Title font') }), font),
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Subtitle') }), subtitle),
      ui.accentBox, avatar,
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Avatar frame') }), frame),
      chime, boot);
    startup.addEventListener('focusin', () => setStage('splash'));

    const credit = h('input', { class: 'input', maxlength: 80, placeholder: _('Optional — artist or source'), value: t.text.credit || '', 'aria-label': _('Credit') });
    credit.addEventListener('input', () => { t.text.credit = credit.value; onEdit('text'); });
    const sharing = h('section', { class: 'group', id: 'g-sharing' }, h('h3', { text: _('Sharing') }),
      h('div', { class: 'field' }, h('span', { class: 'label', text: _('Credit') }), credit,
        h('p', { class: 'hint', text: _('Travels with the theme file when it is exported.') })),
      h('div', { class: 'field btn-row' },
        button(_('Export…'), 'secondary', exportTheme, 0xEDE1),
        button(_('Duplicate'), 'secondary', duplicateTheme, 0xE8C8)));
    refreshPresets();
    const hst = hist(t.name);
    const undoBtn = button(_('Undo'), 'quiet', undoEdit, 0xE7A7);
    undoBtn.title = 'Undo (Ctrl+Z)'; undoBtn.disabled = !hst.undo.length;
    const redoBtn = button(_('Redo'), 'quiet', redoEdit, 0xE7A6);
    redoBtn.title = 'Redo (Ctrl+Y)'; redoBtn.disabled = !hst.redo.length;
    const resetBtn = button(_('Reset'), 'quiet', resetToOpened, 0xE777);
    resetBtn.title = 'Back to how the theme was when it was opened';
    const history = h('div', { class: 'history' }, undoBtn, redoBtn, resetBtn);
    return h('aside', { class: 'inspector' }, history, wallpaper, slidesGroup(t), startup, sharing);
  }

  function exportTheme() {
    const t = cur(); if (!t) return;
    flushSave().then(() => call('exportTheme', { name: t.name })).then(r => {
      if (!r.cancelled) setStatus(_('Exported'));
    }).catch(e => setStatus(_f('Couldn\'t export. {0}', e.message), true));
  }

  function duplicateTheme() {
    const t = cur(); if (!t) return;
    call('duplicateTheme', { name: t.name }).then(state => {
      mergeState(state); S.sel = state.created; S.view = 'edit'; buildAll(); setStatus(_('Duplicated'));
    }).catch(e => setStatus(_f('Couldn\'t duplicate. {0}', e.message), true));
  }

  function replaceImage(cmd) {
    const t = cur(); if (!t) return;
    flushSave().then(() => call(cmd, { name: t.name })).then(state => {
      if (state.cancelled) return;
      mergeState(state); buildAll(); setStatus(cmd === 'replaceWallpaper' ? _('Image replaced') : _('Saved'));
    }).catch(e => setStatus(_f('Couldn\'t change the image. {0}', e.message), true));
  }

  // ---------- avatar crop ----------
  // A square dragged straight off the wallpaper preview. Stage coords map back
  // to source pixels through the same object-fit/object-position math the <img>
  // uses, so the avatar is pixel-exact at every fit mode.
  function startCrop(t) {
    if (S.stage !== 'app') setStage('app');
    if (ui.crop || !ui.stage) return;
    const stage = ui.stage;

    // Content box of the rendered image inside the stage element (mirrors the
    // object-fit rules in updateArt).
    function srcRect() {
      const sw = stage.clientWidth, sh = stage.clientHeight;
      const nw = ui.art.naturalWidth, nh = ui.art.naturalHeight;
      if (!nw || !nh || !sw || !sh) return null;
      const fit = t.look.fit;
      const scale = fit === 'contain' || fit === 'pixel' ? Math.min(sw / nw, sh / nh)
        : fit === 'center' ? 1 : Math.max(sw / nw, sh / nh);
      const ox = (sw - nw * scale) * t.look.focusX / 100;
      const oy = (sh - nh * scale) * t.look.focusY / 100;
      // The slice of the image the user can actually see.
      const vx = Math.max(0, ox), vy = Math.max(0, oy);
      return { scale, ox, oy, nw, nh, sw, sh,
        vx, vy, vw: Math.min(sw, ox + nw * scale) - vx, vh: Math.min(sh, oy + nh * scale) - vy };
    }
    const r0 = srcRect();
    if (!r0 || r0.vw < 8 || r0.vh < 8) { setStatus(_('No wallpaper to crop from.'), true); return; }

    const c = { x: 0, y: 0, s: Math.round(Math.min(r0.vw, r0.vh) * 0.6) };
    c.x = Math.round(r0.vx + (r0.vw - c.s) / 2);
    c.y = Math.round(r0.vy + (r0.vh - c.s) / 2);

    const boxEl = h('div', { class: 'crop-box' }, h('span', { class: 'crop-handle' }));
    const overlay = h('div', { class: 'crop' }, boxEl);
    ui.crop = overlay;

    function paint() {
      boxEl.style.left = `${c.x}px`; boxEl.style.top = `${c.y}px`;
      boxEl.style.width = `${c.s}px`; boxEl.style.height = `${c.s}px`;
    }
    function clampBox() {
      const r = srcRect(); if (!r) return;
      c.s = Math.max(16, Math.min(c.s, r.vw, r.vh));
      c.x = Math.max(r.vx, Math.min(c.x, r.vx + r.vw - c.s));
      c.y = Math.max(r.vy, Math.min(c.y, r.vy + r.vh - c.s));
    }
    function stagePoint(e) {
      const b = stage.getBoundingClientRect();
      return { x: e.clientX - b.left, y: e.clientY - b.top };
    }

    let drag = null;
    boxEl.addEventListener('pointerdown', e => {
      if (e.button !== 0) return;
      e.stopPropagation(); e.preventDefault();
      const p = stagePoint(e);
      const sizing = e.target.classList.contains('crop-handle');
      drag = sizing ? { size: true, s: c.s, px: p.x, py: p.y } : { x: c.x - p.x, y: c.y - p.y };
      try { boxEl.setPointerCapture(e.pointerId); } catch (_) { }
    });
    boxEl.addEventListener('pointermove', e => {
      if (!drag) return;
      const p = stagePoint(e);
      if (drag.size) { c.s = Math.round(drag.s + Math.max(p.x - drag.px, p.y - drag.py)); }
      else { c.x = Math.round(drag.x + p.x); c.y = Math.round(drag.y + p.y); }
      clampBox(); paint();
    });
    const drop = () => { drag = null; };
    boxEl.addEventListener('pointerup', drop);
    boxEl.addEventListener('pointercancel', drop);
    overlay.addEventListener('pointerdown', e => e.stopPropagation());
    overlay.addEventListener('keydown', e => {
      const move = { ArrowLeft: [-4, 0], ArrowRight: [4, 0], ArrowUp: [0, -4], ArrowDown: [0, 4],
        '+': [0, 0, 8], '=': [0, 0, 8], '-': [0, 0, -8], '_': [0, 0, -8] }[e.key];
      if (e.key === 'Escape') { done(); return; }
      if (e.key === 'Enter') { use(); return; }
      if (!move) return;
      e.preventDefault(); e.stopPropagation();
      if (move[2]) c.s += move[2]; else { c.x += move[0]; c.y += move[1]; }
      clampBox(); paint();
    });

    function done() {
      overlay.remove();
      if (ui.crop === overlay) ui.crop = null;
      ui.bar.textContent = '';
    }
    function use() {
      const r = srcRect(); if (!r) { done(); return; }
      // Fractions of the source wallpaper — the preview can be scaled down.
      const args = { name: t.name,
        fx: Math.max(0, (c.x - r.ox) / (r.nw * r.scale)),
        fy: Math.max(0, (c.y - r.oy) / (r.nh * r.scale)),
        fsize: Math.max(0.01, c.s / (Math.min(r.nw, r.nh) * r.scale)) };
      call('cropAvatar', args).then(state => {
        mergeState(state); done(); buildAll(); setStatus(_('Avatar updated'));
      }).catch(e => { done(); setStatus(_f('Couldn\'t crop. {0}', e.message), true); });
    }

    ui.bar.textContent = '';
    ui.bar.append(
      h('span', { class: 'crop-hint', text: _('Drag the square to frame the avatar') }),
      button(_('Use'), 'primary', use),
      button(_('Cancel'), 'quiet', done));
    stage.append(overlay);
    paint();
    boxEl.tabIndex = -1; boxEl.focus();
  }

  function playFull() {
    const t = cur(); if (!t) return;
    flushSave().then(() => call('playSplash', { name: t.name, app: 'devin' })).catch(e => setStatus(_f('Couldn\'t play the animation. {0}', e.message), true));
  }

  // ---------- apps view ----------
  function buildApps() {
    const wrap = h('main', { class: 'apps' }, h('h1', { text: _('Apps and startup') }),
      h('p', { class: 'lead', text: _('The helper draws the wallpaper inside the app and plays the animation when the app starts.') }));
    for (const app of S.apps) {
      const notInstalled = app.installed === false;
      const state = notInstalled ? h('span', { class: 'app-state dim', text: _('Not installed') })
        : !app.helperExists ? h('span', { class: 'app-state', text: _('Helper not installed') })
        : app.helperRunning ? h('span', { class: 'app-state', text: _('Helper running') })
          : h('span', { class: 'app-state', text: _('Helper not running') });
      const line1 = h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: app.name }), state,
        app.helperExists && !app.helperRunning && !notInstalled ? button(_('Start helper'), 'secondary', () => startHelper(app.id)) : null);
      const select = h('select', { class: 'select', 'aria-label': _f('{0} theme', app.name) });
      select.append(h('option', { value: '', text: _('Custom') }));
      for (const t of S.themes) select.append(h('option', { value: t.name, text: t.name }));
      select.value = app.theme || '';
      select.addEventListener('change', () => {
        if (!select.value) { select.value = app.theme || ''; return; }
        call('applyTheme', { name: select.value, app: app.id }).then(apps => { S.apps = apps; setAppsStatus(_('Applied')); }).catch(e => setAppsStatus(_f('Couldn\'t apply. {0}', e.message), true));
      });
      const splash = toggle(app.splash, _f('Play animation on start for {0}', app.name), on =>
        call('setApp', { app: app.id, splash: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const speed = segmented([['1', _('Full')], ['2', _('Quick')], ['3', _('Turbo')]], String(app.speed), v =>
        call('setApp', { app: app.id, speed: +v }).then(apps => { S.apps = apps; speed.paint(v); }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const appStyle = h('select', { class: 'select', 'aria-label': _f('{0} animation style', app.name) });
      for (const [value, name] of [['full', _('Full')], ['short', _('Card')], ['terminal', _('Terminal')], ['manga', _('Manga')], ['minimal', _('Minimal')]])
        appStyle.append(h('option', { value, text: name }));
      appStyle.value = app.style || 'full';
      appStyle.addEventListener('change', () =>
        call('setApp', { app: app.id, style: appStyle.value }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const once = toggle(!!app.once, _f('Play only on the first start after sign-in for {0}', app.name), on =>
        call('setApp', { app: app.id, once: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const auto = toggle(app.autostart, _f('Start helper with Windows for {0}', app.name), on =>
        call('setAutostart', { app: app.id, on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const rotate = segmented([['off', _('Off')], ['start', _('On start')], ['15', _('15 min')], ['60', _('Hourly')]], app.rotate || 'off', v =>
        call('setApp', { app: app.id, rotate: v }).then(apps => { S.apps = apps; rotate.paint(v); }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const favsOnly = toggle(!!app.rotateFavs, _f('Rotate only through starred themes for {0}', app.name), on =>
        call('setApp', { app: app.id, rotateFavs: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const lightPick = h('select', { class: 'select', 'aria-label': _f('{0} light-mode theme', app.name), disabled: !app.followWindows });
      lightPick.append(h('option', { value: '', text: _('(same)') }));
      for (const t of S.themes) lightPick.append(h('option', { value: t.name, text: t.name }));
      lightPick.value = app.themeLight || '';
      lightPick.addEventListener('change', () =>
        call('setApp', { app: app.id, themeLight: lightPick.value }).then(apps => { S.apps = apps; setAppsStatus(_('Saved')); }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const follow = toggle(!!app.followWindows, _f('Follow Windows light and dark mode for {0}', app.name), on =>
        call('setApp', { app: app.id, followWindows: on }).then(apps => { S.apps = apps; lightPick.disabled = !on; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const nightPick = h('select', { class: 'select', 'aria-label': _f('{0} night theme', app.name), title: app.modeNight ? _('Night hours are active now') : '' });
      nightPick.append(h('option', { value: '', text: _('(same)') }));
      for (const t of S.themes) nightPick.append(h('option', { value: t.name, text: t.name }));
      nightPick.value = app.themeNight || '';
      nightPick.addEventListener('change', () =>
        call('setApp', { app: app.id, themeNight: nightPick.value }).then(apps => { S.apps = apps; setAppsStatus(_('Saved')); }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const hourSel = (val, label, key) => {
        const s = h('select', { class: 'select hour', 'aria-label': _f('{0} night hours {1}', app.name, label) });
        for (let i = 0; i < 24; i++) s.append(h('option', { value: String(i), text: `${i}:00` }));
        s.value = String(val);
        s.addEventListener('change', () =>
          call('setApp', { app: app.id, [key]: +s.value }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
        return s;
      };
      const nightHours = h('div', { class: 'hour-row' },
        hourSel(app.nightStart ?? 20, _('starts'), 'nightStart'), h('span', { class: 'hour-dash', text: _('to') }), hourSel(app.nightUntil ?? 7, _('ends'), 'nightUntil'));
      const dimSeg = (val, label, key) => {
        const seg = segmented([['0', _('Off')], ['10', _('+10')], ['20', _('+20')], ['40', _('+40')]], String(val ?? 0), v =>
          call('setApp', { app: app.id, [key]: +v }).then(apps => { S.apps = apps; seg.paint(v); }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
        seg.setAttribute('aria-label', _f('{0} for {1}', label, app.name));
        return seg;
      };
      const nightDim = dimSeg(app.nightDim, _('Extra artwork dim at night'), 'nightDim');
      const extraDim = dimSeg(app.extraDim, _('Extra artwork dim'), 'extraDim');
      const hideFs = toggle(app.hideFullscreen !== false, _f('Hide the artwork while {0} is full screen or presenting', app.name), on =>
        call('setApp', { app: app.id, hideFullscreen: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const gifSel = (val, key, opts, label, dflt) => {
        const s = h('select', { class: 'select', 'aria-label': _f('{0} for {1}', label, app.name) });
        for (const [v, text] of opts) s.append(h('option', { value: v, text }));
        s.value = String(val ?? dflt);
        s.addEventListener('change', () =>
          call('setApp', { app: app.id, [key]: +s.value }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
        return s;
      };
      const gifSpeed = gifSel(app.gifSpeed, 'gifSpeed', [['50', _('½×')], ['100', _('1×')], ['150', _('1½×')], ['200', _('2×')]], _('Wallpaper speed'), 100);
      const gifCap = gifSel(app.gifCap, 'gifCap', [['0', _('As authored')], ['10', _('10 fps')], ['15', _('15 fps')], ['20', _('20 fps')], ['30', _('30 fps')]], _('Wallpaper frame cap'), 0);
      const gifPause = toggle(app.gifPause === true, _('Freeze the animated wallpaper on its current frame'), on =>
        call('setApp', { app: app.id, gifPause: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const battSaver = toggle(app.battSaver === true, _('Pause the animation while running on battery'), on =>
        call('setApp', { app: app.id, battSaver: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const slideSec = gifSel(app.slideSec, 'slideSec', [['5', _('5 s')], ['15', _('15 s')], ['30', _('30 s')], ['60', _('1 min')], ['300', _('5 min')]], _('Slide seconds'), 30);
      const strSel = (val, key, opts, label) => {
        const s = h('select', { class: 'select', 'aria-label': _f('{0} for {1}', label, app.name) });
        for (const [v, text] of opts) s.append(h('option', { value: v, text }));
        s.value = String(val ?? '');
        s.addEventListener('change', () =>
          call('setApp', { app: app.id, [key]: s.value }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
        return s;
      };
      const pauseBg = toggle(app.pauseBg === true, _('Pause the animation while the app isn’t focused'), on =>
        call('setApp', { app: app.id, pauseBg: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true)));
      const crosshair = strSel(app.crosshair || 'off', 'crosshair',
        [['off', _('Off')]].concat((S.crosshairs || []).map(id => [id, crossLabel(id)])), _('Pointer'));
      const crossColor = strSel(app.crosshairColor || '', 'crosshairColor',
        [['', _('Theme accent')], ['#2ee678', _('Lime')], ['#35c7ff', _('Cyan')], ['#ff4fa3', _('Pink')], ['#ffb52e', _('Amber')], ['#ffffff', _('White')]], _('Pointer colour'));
      const ctl = (label, el) => h('div', { class: 'ctl' }, h('span', { class: 'label', text: label }), h('div', { class: 'ctl-row' }, el));
      const appInfo = app.custom ? h('div', { class: 'app-exe', text: app.exe }) : null;
      const statusLine = app.status ? h('div', { class: 'app-status' + (app.problem ? ' has-problem' : ''), text: appStatusText(app) }) : null;
      const removeBtn = app.custom ? button(_('Remove'), 'secondary', () => removeCustomApp(app.id)) : null;
      wrap.append(h('section', { class: 'app-block' + (notInstalled ? ' not-installed' : ''), 'data-app': app.id }, line1, appInfo, statusLine,
        h('div', { class: 'app-controls' }, ctl(_('Theme'), select), ctl(_('Play animation on start'), splash), ctl(_('Speed'), speed),
          ctl(_('Style'), appStyle), ctl(_('First start only'), once), ctl(_('Start helper with Windows'), auto),
          ctl(_('Rotate themes'), rotate), ctl(_('Starred themes only'), favsOnly), ctl(_('Follow Windows'), follow), ctl(_('Light theme'), lightPick),
          ctl(_('Night theme'), nightPick), ctl(_('Night hours'), nightHours), ctl(_('Night dim'), nightDim), ctl(_('Extra dim'), extraDim),
          ctl(_('Hide in full screen'), hideFs),
          ctl(_('Wallpaper speed'), gifSpeed), ctl(_('Frame cap'), gifCap), ctl(_('Freeze animation'), gifPause), ctl(_('On battery'), battSaver), ctl(_('Slide seconds'), slideSec),
          ctl(_('Unfocused'), pauseBg), ctl(_('Pointer'), crosshair), ctl(_('Pointer colour'), crossColor),
          ctl(_('Pointer all'), button(_('Change all'), 'quiet', () => {
            const style = app.crosshair || 'off', col = app.crosshairColor || '';
            let p = Promise.resolve();
            for (const a of S.apps || [])
              p = p.then(() => call('setApp', { app: a.id, crosshair: style, crosshairColor: col }).then(r => { S.apps = r; }));
            p.then(() => { buildAll(); setAppsStatus(_('Pointer saved')); })
             .catch(e => setAppsStatus(_f('Couldn\'t save. {0}', e.message), true));
          })),
          removeBtn ? h('div', { class: 'ctl' }, h('span', { class: 'label', text: _('Your app') }), h('div', { class: 'ctl-row' }, removeBtn)) : null)));
    }
    ui.appsStatus = h('div', { class: 'status', role: 'status', 'aria-live': 'polite' });
    wrap.append(h('section', { class: 'app-block' },
      h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: _('Add your own app') })),
      h('p', { class: 'lead', text: _('Pick any program\'s .exe file and it gets the same wallpaper and animation as the built-in apps. Works best with regular desktop windows.') }),
      h('div', { class: 'app-controls' },
        h('div', { class: 'ctl' },
          h('span', { class: 'label', text: _('Program') }),
          h('div', { class: 'ctl-row' }, button(_('Choose .exe…'), 'primary', addCustomApp))),
        presetCtl())));
    wrap.append(ui.appsStatus);
    return wrap;
  }

  // ---------- crosshair gallery ----------
  function crossLabel(id) {
    return ({ dot: _('Dot'), cross: _('Cross'), circle: _('Circle'), ring: _('Ring'),
      scope: _('Scope'), chevron: _('Chevron'), diamond: _('Diamond'), star: _('Star'),
      cat: _('Cat'), paw: _('Paw'), heart: _('Heart'), bow: _('Bow'), ghost: _('Ghost'),
      bunny: _('Bunny'), crown: _('Crown'), flower: _('Flower'),
      sparkle: _('Sparkle'), bolt: _('Bolt'), moon: _('Moon'), skull: _('Skull'),
      music: _('Music'), cherry: _('Cherry'), mushroom: _('Mushroom'), snowflake: _('Snowflake'),
      off: _('Off') })[id] || id;
  }

  // An inline-SVG preview of a crosshair style, mirroring CrosshairArt.Shapes.
  function crossSvg(id) {
    const NS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', '0 0 48 48');
    svg.setAttribute('class', 'cross-svg');
    svg.setAttribute('aria-hidden', 'true');
    const c = 24;
    const el = (tag, attrs) => { const n = document.createElementNS(NS, tag); for (const k in attrs) n.setAttribute(k, attrs[k]); svg.append(n); };
    const ln = (x1, y1, x2, y2) => el('line', { x1, y1, x2, y2 });
    const ring = r => el('circle', { cx: c, cy: c, r, fill: 'none' });
    const dot = (r, dy) => el('circle', { cx: c, cy: c + (dy || 0), r, fill: 'currentColor', stroke: 'none' });
    switch (id) {
      case 'dot': dot(3); ring(8); break;
      case 'cross': ln(c-11,c,c-4,c); ln(c+4,c,c+11,c); ln(c,c-11,c,c-4); ln(c,c+4,c,c+11); dot(1.5); break;
      case 'circle': ring(8); dot(2.5); break;
      case 'ring': ring(12); break;
      case 'scope': ring(10); ln(c-14,c,c-10,c); ln(c+10,c,c+14,c); ln(c,c-14,c,c-10); ln(c,c+10,c,c+14); dot(2); break;
      case 'chevron': ln(c-8,c-5,c,c+3); ln(c+8,c-5,c,c+3); dot(2,-7.5); break;
      case 'diamond': ln(c,c-9,c+9,c); ln(c+9,c,c,c+9); ln(c,c+9,c-9,c); ln(c-9,c,c,c-9); dot(2); break;
      case 'star': for (let i = 0; i < 8; i++) { const a = i * Math.PI / 4; ln(c+Math.cos(a)*4, c+Math.sin(a)*4, c+Math.cos(a)*9, c+Math.sin(a)*9); } dot(1.5); break;
      case 'cat': el('ellipse', { cx: c, cy: c + 0.5, rx: 10, ry: 8.5, fill: 'none' });
        el('path', { d: `M${c-8} ${c-4} L${c-8} ${c-13} L${c-3} ${c-9} M${c+8} ${c-4} L${c+8} ${c-13} L${c+3} ${c-9} M${c-1} ${c+1.5} L${c} ${c+2.5} L${c+1} ${c+1.5} M${c-12} ${c+1} L${c-8.5} ${c+0.5} M${c-12} ${c+3.5} L${c-8.5} ${c+3} M${c+12} ${c+1} L${c+8.5} ${c+0.5} M${c+12} ${c+3.5} L${c+8.5} ${c+3}`, fill: 'none' });
        el('circle', { cx: c - 3.75, cy: c - 0.25, r: 1.25, fill: 'currentColor', stroke: 'none' });
        el('circle', { cx: c + 3.75, cy: c - 0.25, r: 1.25, fill: 'currentColor', stroke: 'none' }); break;
      case 'paw': el('ellipse', { cx: c, cy: c + 6, rx: 8, ry: 5, fill: 'none' });
        el('circle', { cx: c - 7, cy: c - 4.5, r: 2.5, fill: 'none' });
        el('circle', { cx: c, cy: c - 6.5, r: 2.5, fill: 'none' });
        el('circle', { cx: c + 7, cy: c - 4.5, r: 2.5, fill: 'none' }); break;
      case 'heart': el('path', { d: `M${c-9} ${c-2.5} A4.5 4.5 0 0 1 ${c} ${c-2.5} A4.5 4.5 0 0 1 ${c+9} ${c-2.5} L${c} ${c+10} Z`, fill: 'none' }); break;
      case 'bow': el('path', { d: `M${c-3} ${c} L${c-11} ${c-6} L${c-11} ${c+6} Z M${c+3} ${c} L${c+11} ${c-6} L${c+11} ${c+6} Z`, fill: 'none' }); dot(2.5); break;
      case 'ghost': el('path', { d: `M${c-9} ${c+6} L${c-9} ${c-2} A9 8 0 0 1 ${c+9} ${c-2} L${c+9} ${c+6} L${c+6} ${c+9} L${c+3} ${c+6} L${c} ${c+9} L${c-3} ${c+6} L${c-6} ${c+9} Z`, fill: 'none' });
        el('circle', { cx: c - 3.5, cy: c - 2.5, r: 1.5, fill: 'currentColor', stroke: 'none' });
        el('circle', { cx: c + 3.5, cy: c - 2.5, r: 1.5, fill: 'currentColor', stroke: 'none' }); break;
      case 'bunny': el('ellipse', { cx: c - 4.75, cy: c - 7.5, rx: 2.75, ry: 6.5, fill: 'none' });
        el('ellipse', { cx: c + 4.75, cy: c - 7.5, rx: 2.75, ry: 6.5, fill: 'none' });
        el('ellipse', { cx: c, cy: c + 4.5, rx: 7, ry: 6.5, fill: 'none' }); dot(1.5, 4.5); break;
      case 'crown': el('path', { d: `M${c-10} ${c+5} L${c-10} ${c-4} L${c-5} ${c+1} L${c} ${c-7} L${c+5} ${c+1} L${c+10} ${c-4} L${c+10} ${c+5} Z`, fill: 'none' }); break;
      case 'flower': for (let i = 0; i < 5; i++) { const a = -Math.PI / 2 + i * 2 * Math.PI / 5;
        el('circle', { cx: c + Math.cos(a) * 7, cy: c + Math.sin(a) * 7, r: 4, fill: 'none' }); } dot(2.5); break;
      case 'sparkle': el('polygon', { points: `${c},${c-14} ${c+3},${c-3} ${c+14},${c} ${c+3},${c+3} ${c},${c+14} ${c-3},${c+3} ${c-14},${c} ${c-3},${c-3}`, fill: 'none' }); dot(1.5); break;
      case 'bolt': el('polygon', { points: `${c+3},${c-14} ${c-6},${c+2} ${c-1},${c+2} ${c-3},${c+14} ${c+6},${c-2} ${c+1},${c-2}`, fill: 'none' }); break;
      case 'moon': el('path', { d: `M${c-2.5} ${c-10.5} A9 11 0 0 0 ${c-2.5} ${c+10.5} A9 9.5 0 0 1 ${c-2.5} ${c-10.5} Z`, fill: 'none' }); break;
      case 'skull': el('ellipse', { cx: c, cy: c - 2.5, rx: 8, ry: 7.5, fill: 'none' });
        el('rect', { x: c - 5, y: c + 3, width: 10, height: 8, fill: 'none' });
        ln(c - 2, c + 3, c - 2, c + 11); ln(c + 2, c + 3, c + 2, c + 11);
        el('circle', { cx: c - 3.25, cy: c - 2.25, r: 1.75, fill: 'currentColor', stroke: 'none' });
        el('circle', { cx: c + 3.25, cy: c - 2.25, r: 1.75, fill: 'currentColor', stroke: 'none' }); break;
      case 'music': ln(c + 5, c - 12, c + 5, c + 6); ln(c + 5, c - 12, c + 10, c - 8);
        el('ellipse', { cx: c - 0.5, cy: c + 6, rx: 5.5, ry: 4, fill: 'none' });
        ln(c - 1, c - 8, c - 1, c + 4); ln(c - 1, c - 8, c + 5, c - 12); break;
      case 'cherry': el('circle', { cx: c - 5.5, cy: c + 6.5, r: 4.5, fill: 'none' });
        el('circle', { cx: c + 6.5, cy: c + 7.5, r: 4.5, fill: 'none' });
        ln(c - 5, c + 3, c + 4, c - 11); ln(c + 7, c + 4, c + 4, c - 11); ln(c + 4, c - 11, c + 10, c - 9); break;
      case 'mushroom': el('path', { d: `M${c-11} ${c-3} A11 8 0 0 1 ${c+11} ${c-3} Z`, fill: 'none' });
        el('path', { d: `M${c-5} ${c-3} L${c-4} ${c+10} L${c+4} ${c+10} L${c+5} ${c-3}`, fill: 'none' });
        el('circle', { cx: c - 3.5, cy: c - 7.5, r: 1.5, fill: 'currentColor', stroke: 'none' });
        el('circle', { cx: c + 3.5, cy: c - 6.5, r: 1.5, fill: 'currentColor', stroke: 'none' }); break;
      case 'snowflake': ln(c, c-12, c, c+12); ln(c-10, c-6, c+10, c+6); ln(c-10, c+6, c+10, c-6);
        ln(c-2.5, c-10, c, c-12); ln(c, c-12, c+2.5, c-10); ln(c-2.5, c+10, c, c+12); ln(c, c+12, c+2.5, c+10); break;
      default: dot(3); break;
    }
    return svg;
  }

  function applyCross(style, target) {
    const ids = target === 'all' ? (S.apps || []).map(a => a.id) : [target];
    let p = Promise.resolve();
    for (const appId of ids)
      p = p.then(() => {
        // The colour is only sent once it was picked here; otherwise each app keeps its own.
        const args = { app: appId, crosshair: style };
        if (S.crossColor !== undefined) args.crosshairColor = S.crossColor;
        return call('setApp', args).then(r => { S.apps = r; });
      });
    p.then(() => { buildAll(); setStatus(_('Pointer saved')); })
     .catch(e => setStatus(_f('Couldn\'t save. {0}', e.message), true));
  }

  function uploadCursor(pack) {
    call('addCursor', { pack }).then(r => {
      if (r && r.cancelled) return;
      S.cursorPacks = r || S.cursorPacks;
      buildAll(); setStatus(_('Pointer saved'));
    }).catch(e => setStatus(_f('Couldn\'t save. {0}', e.message), true));
  }

  function newPack() {
    const names = (S.cursorPacks || []).map(p => p.name);
    let pack = 'custom', n = 2;
    while (names.indexOf(pack) >= 0) { pack = 'custom-' + n; n++; }
    uploadCursor(pack);
  }

  function buildCross() {
    const apps = S.apps || [];
    const target = S.crossApp && apps.some(a => a.id === S.crossApp) ? S.crossApp : 'all';
    const packs = S.cursorPacks || [];
    const isOn = id => target === 'all'
      ? apps.length > 0 && apps.every(a => (a.crosshair || 'off') === id)
      : ((apps.find(a => a.id === target) || {}).crosshair || 'off') === id;
    const colorSel = h('select', { class: 'select', 'aria-label': _('Pointer colour') });
    for (const [v, text] of [['', _('Theme accent')], ['#2ee678', _('Lime')], ['#35c7ff', _('Cyan')],
      ['#ff4fa3', _('Pink')], ['#ffb52e', _('Amber')], ['#ffffff', _('White')]])
      colorSel.append(h('option', { value: v, text }));
    const shownApp = apps.find(a => a.id === target) || apps[0] || {};
    colorSel.value = S.crossColor !== undefined ? S.crossColor : (shownApp.crosshairColor || '');
    colorSel.addEventListener('change', () => {
      S.crossColor = colorSel.value;
      // Recolour whatever shape the selected apps already use.
      const ids = (target === 'all' ? apps : apps.filter(a => a.id === target)).filter(a => (a.crosshair || 'off') !== 'off');
      let p = Promise.resolve();
      for (const a of ids)
        p = p.then(() => call('setApp', { app: a.id, crosshairColor: S.crossColor }).then(r => { S.apps = r; }));
      p.then(() => ids.length && setStatus(_('Pointer saved')))
       .catch(e => setStatus(_f('Couldn\'t save. {0}', e.message), true));
    });
    const appSeg = segmented([['all', _('All apps')]].concat(apps.map(a => [a.id, a.name])), target,
      v => { S.crossApp = v; buildAll(); });
    const main = h('main', { class: 'apps cross-view' }, h('h1', { text: _('Mouse pointers') }),
      h('p', { class: 'lead', text: _('Pick a pointer for the cursor inside the app window.') }),
      h('div', { class: 'cross-bar' }, appSeg, colorSel,
        button(_('Windows default'), 'quiet', () => {
          call('resetCursorPack')
            .then(() => setStatus(_('Windows pointers restored')))
            .catch(e => setStatus(e.message || String(e), true));
        }, 0xE777)));

    const packImg = (pack, f, cls) => h('img', { class: cls || 'cross-img',
      src: `img/cursor?pack=${encodeURIComponent(pack)}&file=${encodeURIComponent(f)}`, alt: f, draggable: false });

    if (S.crossPack) {
      const pack = packs.find(p => p.name === S.crossPack) || { name: S.crossPack, files: [] };
      const grid = h('div', { class: 'cross-grid' });
      for (const f of pack.files) {
        const id = `img:${pack.name}/${f}`;
        const card = h('div', { class: 'cross-cell' },
          h('button', { type: 'button', class: `cross-card${isOn(id) ? ' sel' : ''}`,
            'aria-pressed': String(isOn(id)), onclick: () => applyCross(id, target) },
            packImg(pack.name, f), h('span', { class: 'cross-name', text: f })),
          h('button', { type: 'button', class: 'cross-del', text: '×',
            'aria-label': _f('Remove {0}', f),
            onclick: () => call('removeCursor', { pack: pack.name, file: f })
              .then(r => { S.cursorPacks = r || S.cursorPacks; buildAll(); })
              .catch(e => setStatus(e.message || String(e), true)) }));
        grid.append(card);
      }
      grid.append(h('button', { type: 'button', class: 'cross-card', onclick: () => uploadCursor(pack.name) },
        h('span', { class: 'cross-none', text: '+' }), h('span', { class: 'cross-name', text: _('Add pointer…') })));
      main.append(h('div', { class: 'cross-bar' },
        button('‹ ' + _('Back'), 'quiet', () => { S.crossPack = null; buildAll(); }, 0xE72B),
        h('h2', { class: 'cross-pack-name', text: pack.name }),
        button(_('Use on Windows'), 'accent', () => {
          call('applyCursorPack', { pack: pack.name })
            .then(() => setStatus(_('Windows pointers updated')))
            .catch(e => setStatus(e.message || String(e), true));
        }, 0xE8A7)), grid);
      return main;
    }

    const tabSeg = segmented([['packs', _('Packs')], ['shapes', _('Shapes')]],
      S.crossTab || 'packs', v => { S.crossTab = v; buildAll(); });
    main.append(h('div', { class: 'cross-tabs' }, tabSeg));
    if ((S.crossTab || 'packs') === 'shapes') {
      const grid = h('div', { class: 'cross-grid' });
      for (const id of ['off'].concat(S.crosshairs || []))
        grid.append(h('button', { type: 'button', class: `cross-card${isOn(id) ? ' sel' : ''}`,
          'aria-pressed': String(isOn(id)), onclick: () => applyCross(id, target) },
          id === 'off' ? h('span', { class: 'cross-none', text: '∅' }) : crossSvg(id),
          h('span', { class: 'cross-name', text: crossLabel(id) })));
      main.append(grid);
      return main;
    }
    const pgrid = h('div', { class: 'cross-grid' });
    for (const pack of packs) {
      const thumb = pack.files.indexOf('pointer') >= 0 ? 'pointer' : pack.files[0];
      const anyOn = pack.files.some(f => isOn(`img:${pack.name}/${f}`));
      pgrid.append(h('button', { type: 'button', class: `cross-card${anyOn ? ' sel' : ''}`,
        onclick: () => { S.crossPack = pack.name; buildAll(); } },
        packImg(pack.name, thumb), h('span', { class: 'cross-name', text: pack.name })));
    }
    pgrid.append(h('button', { type: 'button', class: 'cross-card', onclick: newPack },
      h('span', { class: 'cross-none', text: '+' }), h('span', { class: 'cross-name', text: _('New pack…') })));
    main.append(pgrid);
    return main;
  }

  // ---------- settings view ----------
  function buildSettings() {
    const wrap = h('main', { class: 'apps settings' }, h('h1', { text: _('Settings') }),
      h('p', { class: 'lead', text: _f('Animated Launchers {0} — helpers, logs and updates.', S.version || '') }));
    ui.settingsStatus = h('div', { class: 'status', role: 'status', 'aria-live': 'polite' });
    const note = h('p', { class: 'hint', text: _('Nothing contacts the internet until you click Check for updates.') });
    const getBtn = button(_('Download and install'), 'primary', null);
    getBtn.hidden = true;
    const checkBtn = button(_('Check for updates'), 'secondary', () => {
      checkBtn.disabled = true;
      setSettingsStatus(_('Checking GitHub…'));
      call('checkUpdates', window.__studio && window.__studio.updateUrl ? { url: window.__studio.updateUrl } : undefined).then(r => {
        checkBtn.disabled = false;
        if (r.updateAvailable) {
          setSettingsStatus(_f('Version {0} is available — you\'re on {1}.', r.version, r.current));
          getBtn.hidden = false;
          getBtn.textContent = r.zip ? _('Download and install') : _('Open download page');
          getBtn.onclick = () => {
            getBtn.disabled = true;
            setSettingsStatus(r.zip ? _('Downloading…') : _('Opening the release page…'));
            call('getUpdate', { url: r.url, zip: r.zip, sha: r.sha, version: r.version }).then(g => {
              getBtn.disabled = false;
              if (g.started) setSettingsStatus(_('Installer started — Theme Studio will close in a moment.'));
              else setSettingsStatus(_('Release page opened in your browser.'));
            }).catch(e => { getBtn.disabled = false; setSettingsStatus(_f('Couldn\'t get the update. {0}', e.message), true); });
          };
        } else {
          setSettingsStatus(_f('You\'re up to date ({0}).', r.current));
          getBtn.hidden = true;
        }
      }).catch(e => { checkBtn.disabled = false; setSettingsStatus(_f('Couldn\'t check. {0}', e.message), true); });
    });
    const langPick = h('select', { class: 'select', 'aria-label': _('Language') });
    [['', _('Same as Windows')], ['en', 'English'], ['fr', 'Français']].forEach(([v, label]) => langPick.append(h('option', { value: v, text: label })));
    langPick.value = S.prefs.lang || '';
    langPick.addEventListener('change', () => {
      forcedLang = null;
      S.prefs.lang = langPick.value;
      setLang(langPick.value || navigator.language);
      call('setPrefs', { lang: langPick.value }).then(p => { if (p) S.prefs = Object.assign({}, S.prefs, p); }).catch(() => { });
      buildAll();
    });
    const splashState = S.apps.length ? (S.apps.every(a => a.splash) ? 'on' : (S.apps.every(a => !a.splash) ? 'off' : '')) : 'off';
    const splashSeg = segmented([['on', _('On')], ['off', _('Off')]], splashState, v => {
      call('setSplashAll', { on: v === 'on' }).then(apps => { S.apps = apps; setSettingsStatus(v === 'on' ? _('Animation plays on every app.') : _('Animation off everywhere.')); buildAll(); })
        .catch(e => setSettingsStatus(_f('Couldn\'t save. {0}', e.message), true));
    });
    wrap.append(
      h('section', { class: 'app-block' },
        h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: _('Startup animation') })),
        h('div', { class: 'app-controls' }, h('div', { class: 'ctl' },
          h('span', { class: 'label', text: _('Play when apps start') }), splashSeg,
          h('p', { class: 'hint', text: _('Switches the animation for every app at once. Per-app switches live under Apps — custom apps stay off unless you turn them on.') })))),
      h('section', { class: 'app-block' },
        h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: _('Language') })),
        h('div', { class: 'app-controls' }, h('div', { class: 'ctl' },
          h('span', { class: 'label', text: _('Interface') }), langPick))),
      h('section', { class: 'app-block' },
        h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: _('Updates') })),
        h('div', { class: 'app-controls' }, h('div', { class: 'ctl' },
          h('span', { class: 'label', text: _('New versions') }), h('div', { class: 'ctl-row' }, checkBtn, getBtn), note))),
      h('section', { class: 'app-block' },
        h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: _('Helpers and logs') })),
        h('div', { class: 'app-controls' },
          h('div', { class: 'ctl' }, h('span', { class: 'label', text: _('Bug reports') }),
            h('div', { class: 'ctl-row' }, button(_('Copy report'), 'secondary', () =>
              call('copyReport').then(() => setSettingsStatus(_('Report copied — paste it into the bug report.')))
                .catch(e => setSettingsStatus(_f('Couldn\'t copy. {0}', e.message), true))),
              button(_('Open logs folder'), 'secondary', () =>
                call('openLogFolder').catch(e => setSettingsStatus(_f('Couldn\'t open it. {0}', e.message), true))))),
          h('div', { class: 'ctl' }, h('span', { class: 'label', text: _('Helpers') }),
            h('div', { class: 'ctl-row' }, button(_('Restart helpers'), 'secondary', restartHelpers))),
          h('p', { class: 'hint', text: _('Restarting picks up new settings and clears a stuck session. Artwork comes back in a second.') }))),
      ui.settingsStatus);
    return wrap;
  }

  function restartHelpers() {
    call('restartHelpers').then(apps => { S.apps = apps; setSettingsStatus(_('Helpers restarted.')); })
      .catch(e => setSettingsStatus(_f('Couldn\'t restart. {0}', e.message), true));
  }

  function setSettingsStatus(text, isError) {
    if (!ui.settingsStatus) return;
    ui.settingsStatus.textContent = text;
    ui.settingsStatus.classList.toggle('error', !!isError);
  }

  function appStatusText(app) {
    const map = { on: _('Wallpaper attached'), waiting: _('Watching for its window'), off: _('Wallpaper off'), parked: _('Parked after errors') };
    let text = map[app.status] || app.status;
    if (app.problem) text += ` — ${app.problem}`;
    const when = relTime(app.statusWhen);
    if (when) text += ` · ${when}`;
    return text;
  }

  function relTime(iso) {
    const then = Date.parse(iso || '');
    if (!then) return '';
    const mins = Math.max(0, Math.round((Date.now() - then) / 60000));
    if (mins < 1) return _('just now');
    if (mins < 60) return _f('{0} min ago', mins);
    const hrs = Math.round(mins / 60);
    return hrs < 48 ? _f('{0} h ago', hrs) : _f('{0} d ago', Math.round(hrs / 24));
  }

  function addCustomApp() {
    call('pickAppExe', window.__studio && window.__studio.testPath ? { path: window.__studio.testPath } : undefined).then(picked => {
      if (picked.cancelled) return;
      return call('probeApp', { path: picked.path }).then(report => {
        if (!report.exists) { setAppsStatus(_('That file was not found.'), true); return; }
        if (report.covered) { setAppsStatus(_('That app is already covered.'), true); return; }
        const notes = [picked.name];
        if (report.running && report.windowClass) notes.push(_f('window type: {0}', report.windowClass));
        else if (!report.running) notes.push('not running — start it to check the wallpaper fit');
        return call('addCustomApp', { name: picked.name, path: picked.path, preview: 'editor' }).then(state => {
          if (state.error) { setAppsStatus(_f('Couldn\'t add it. {0}', state.error), true); return; }
          mergeState(state); buildAll();
          setAppsStatus(_f('{0} added. It shows the wallpaper once the helper picks it up.', notes.join(' — ')));
        });
      });
    }).catch(e => setAppsStatus(_f('Couldn\'t add the app. {0}', e.message), true));
  }

  // "Common apps" chips: the bridge lists installed, not-yet-covered programs;
  // one click registers them without the file picker.
  function presetCtl() {
    const row = h('div', { class: 'ctl-row preset-chips' }, h('span', { class: 'hint', text: _('Checking installed programs…') }));
    call('presetApps').then(list => {
      row.textContent = '';
      if (!list || !list.length) { row.append(h('span', { class: 'hint', text: _('Nothing to suggest.') })); return; }
      for (const p of list) row.append(button(p.name, 'secondary', () => {
        call('addCustomApp', { name: p.name, path: p.path, preview: 'editor' }).then(state => {
          if (state.error) { setAppsStatus(_f('Couldn\'t add it. {0}', state.error), true); return; }
          mergeState(state); buildAll();
          setAppsStatus(_f('{0} added. It shows the wallpaper once the helper picks it up.', p.name));
        }).catch(e => setAppsStatus(_f('Couldn\'t add it. {0}', e.message), true));
      }));
    }).catch(() => { row.textContent = ''; });
    return h('div', { class: 'ctl' }, h('span', { class: 'label', text: _('Common apps') }), row);
  }

  function removeCustomApp(id) {
    call('removeCustomApp', { app: id }).then(apps => { S.apps = apps; buildAll(); setAppsStatus(_('Removed.')); })
      .catch(e => setAppsStatus(_f('Couldn\'t remove it. {0}', e.message), true));
  }

  function setAppsStatus(text, error) { if (ui.appsStatus) { ui.appsStatus.textContent = text; ui.appsStatus.classList.toggle('error', !!error); } }

  function startHelper(id) {
    call('startHelper', { app: id }).then(apps => { S.apps = apps; buildAll(); }).catch(e => setAppsStatus(_f('Couldn\'t start the helper. {0}', e.message), true));
  }

  // ---------- empty ----------
  function buildEmpty() {
    const main = h('main', { class: 'empty' }, h('h1', { text: _('Make your first theme') }),
      h('p', { text: _('Pick a wallpaper image. You can tune how it looks behind your editor and give it a title for the startup animation.') }),
      button(_('Choose image…'), 'primary', createTheme, 0xE710));
    if (!S.prefs || S.prefs.welcome === false) return main;
    const row = h('div', { class: 'starter-row' }, h('div', { class: 'starter-loading', text: _('Painting previews…') }));
    call('starterPreview').then(res => {
      row.textContent = '';
      for (const starter of res.starters || []) {
        row.append(h('figure', { class: 'starter' },
          h('img', { src: 'data:image/jpeg;base64,' + starter.jpeg, alt: starter.name }),
          h('figcaption', { text: starter.name })));
      }
    }).catch(() => { row.textContent = ''; });
    main.append(h('section', { class: 'welcome-card' },
      h('h2', { text: _('Start with a few ready-made themes') }),
      h('p', { class: 'hint', text: _('Six generated wallpapers — procedurally painted, so they are license-clean. Names already in your library are skipped.') }),
      row,
      h('div', { class: 'welcome-actions' },
        button(_('Add the starter themes'), 'primary', () => {
          call('seedStarters').then(state => {
            mergeState(state);
            S.sel = state.seeded && state.seeded.length ? state.seeded[0] : S.sel;
            S.view = 'edit';
            buildAll();
            setStatus(state.seeded && state.seeded.length ? _f('{0} starter theme{1} added', state.seeded.length, state.seeded.length === 1 ? '' : _('s')) : _('All starter themes were already in your library'));
          }).catch(e => setStatus(_f('Couldn\'t add the starter themes. {0}', e.message), true));
        }, 0x2713),
        button(_('No thanks'), 'secondary', () => {
          call('setPrefs', { sort: S.prefs && S.prefs.sort || 'name', favorites: (S.prefs && S.prefs.favorites || []).join('|'), welcome: 'off' })
            .then(p => { if (S.prefs) S.prefs = p; buildAll(); }).catch(() => {});
        }))));
    return main;
  }

  function createTheme() {
    flushSave().then(() => call('createTheme', window.__studio && window.__studio.testPath ? { path: window.__studio.testPath } : undefined)).then(state => {
      if (state.cancelled) return;
      mergeState(state); S.sel = state.created; S.view = 'edit'; buildAll(); setStatus(_('Theme created'));
    }).catch(e => setStatus(_f('Couldn\'t create the theme. {0}', e.message), true));
  }

  // The New dropdown: every way a theme can start, in one place.
  function newMenu() {
    const items = [
      [_('From image…'), () => createTheme()],
      [_('Paste image'), () => quickCreate('pasteImage', _('Nothing to paste — the clipboard has no image.'))],
      [_('Desktop wallpaper'), () => quickCreate('useDesktopWallpaper', _('No desktop wallpaper is set.'))],
      [_('From folder…'), () => call('importFolder', window.__studio && window.__studio.testPath ? { path: window.__studio.testPath } : undefined).then(state => {
        if (state.cancelled) return;
        mergeState(state); if (state.created) { S.sel = state.created; S.view = 'edit'; buildAll(); }
        setStatus(state.count ? _f('{0} theme{1} added', state.count, state.count === 1 ? '' : _('s')) : _('No images found in that folder'));
      }).catch(e => setStatus(_f('Couldn\'t add the folder. {0}', e.message), true))],
      [_('Gradient…'), () => gradientBox(null)]
    ];
    const menu = h('details', { class: 'new-menu' }, h('summary', { class: 'btn secondary' }, _('New')),
      h('div', { class: 'menu-card' }));
    const card = menu.lastElementChild;
    for (const [label, fn] of items) {
      const item = button(label, 'menu-item', () => { menu.open = false; fn(); });
      card.append(item);
    }
    // Any click outside or Escape folds it back.
    menu.addEventListener('toggle', () => {
      if (!menu.open) return;
      const close = e => { if (!menu.contains(e.target)) { menu.open = false; document.removeEventListener('pointerdown', close, true); } };
      document.addEventListener('pointerdown', close, true);
    });
    menu.addEventListener('keydown', e => { if (e.key === 'Escape') menu.open = false; });
    return menu;
  }

  function quickCreate(cmd, noneMessage) {
    flushSave().then(() => call(cmd)).then(state => {
      if (state.none) { setStatus(noneMessage, true); return; }
      mergeState(state); S.sel = state.created; S.view = 'edit'; buildAll(); setStatus(_('Theme created'));
    }).catch(e => setStatus(_f('Couldn\'t create the theme. {0}', e.message), true));
  }

  // Ctrl+V anywhere in the window is a shortcut for New → Paste image; an image
  // on the clipboard can't go into a text field anyway.
  window.addEventListener('paste', e => {
    const types = e.clipboardData ? e.clipboardData.types : null;
    if (!types || ![...types].includes('Files')) return;
    e.preventDefault();
    quickCreate('pasteImage', _('Nothing to paste — the clipboard has no image.'));
  });

  // ---------- keys ----------
  const SHORTCUTS = () => [
    ['Ctrl+Z', _('Undo the last change')],
    ['Ctrl+Y / Ctrl+Shift+Z', _('Redo')],
    ['C (hold)', _('See the untouched picture')],
    ['Ctrl+F or /', _('Find a theme')],
    ['Ctrl+Tab', _('Next theme')],
    ['Ctrl+S', _('Save now')],
    ['Ctrl+V', _('New theme from a copied image')],
    ['Ctrl+N', _('Open the New menu')],
    ['Ctrl+D', _('Duplicate this theme')],
    ['Ctrl+E', _('Export this theme')],
    ['? / F1', _('This list')]
  ];

  function keysCard() {
    if ($('.keys-card')) return;
    const rows = SHORTCUTS().map(([k, what]) => h('div', { class: 'key-row' }, h('kbd', { text: k }), h('span', { text: what })));
    const card = h('div', { class: 'keys-card', role: 'dialog', 'aria-label': _('Keyboard shortcuts'), tabindex: '-1' },
      h('h3', { text: _('Keys') }), h('div', { class: 'key-list' }, rows));
    document.body.append(card);
    card.focus();
    const gone = e => { if (!card.contains(e.target)) { card.remove(); document.removeEventListener('pointerdown', gone, true); } };
    document.addEventListener('pointerdown', gone, true);
    card.addEventListener('keydown', e => { if (e.key === 'Escape') card.remove(); });
    card.addEventListener('blur', e => { if (!card.contains(e.relatedTarget)) card.remove(); });
  }

  const inField = el => el && el.closest && el.closest('input, textarea, select, [contenteditable="true"]');

  window.addEventListener('keydown', e => {
    const ctrl = e.ctrlKey || e.metaKey;
    if (ctrl && e.key.toLowerCase() === 's') { e.preventDefault(); flushSave(); return; }
    if (inField(e.target)) return;
    if (ctrl && !e.shiftKey && e.key.toLowerCase() === 'z') { e.preventDefault(); undoEdit(); return; }
    if (ctrl && (e.key.toLowerCase() === 'y' || (e.shiftKey && e.key.toLowerCase() === 'z'))) { e.preventDefault(); redoEdit(); return; }
    if (ctrl && e.key.toLowerCase() === 'f') { e.preventDefault(); if (ui.search) { ui.search.focus(); ui.search.select(); } return; }
    if (ctrl && e.key.toLowerCase() === 'n') { e.preventDefault(); const m = $('.new-menu'); if (m) m.open = !m.open; return; }
    if (ctrl && e.key.toLowerCase() === 'd' && S.view === 'edit' && cur()) { e.preventDefault(); duplicateTheme(); return; }
    if (ctrl && e.key.toLowerCase() === 'e' && S.view === 'edit' && cur()) { e.preventDefault(); exportTheme(); return; }
    if (ctrl && e.key === 'Tab') {
      e.preventDefault();
      const rows = visibleThemes();
      if (rows.length > 1) {
        const i = Math.max(0, rows.findIndex(x => x.name === S.sel));
        selectTheme(rows[(i + (e.shiftKey ? rows.length - 1 : 1)) % rows.length].name, true);
      }
      return;
    }
    if (e.repeat) return;
    if (e.key === '/') { e.preventDefault(); if (ui.search) ui.search.focus(); return; }
    if (e.key.toLowerCase() === 'c' && !ctrl) compare(true);
    if (e.key === '?' || e.key === 'F1') { e.preventDefault(); keysCard(); }
  });
  window.addEventListener('keyup', e => { if (e.key.toLowerCase() === 'c' && !e.ctrlKey) compare(false); });
  window.addEventListener('blur', () => compare(false));

  // A small gradient maker: two colours, an angle, done. Reused for "Edit
  // gradient" on themes that carry one (initial = [hex, hex, degrees]).
  function gradientBox(initial) {
    const from = h('input', { class: 'input hex', maxlength: 7, value: initial ? initial[0] : '#2B2438', 'aria-label': _('First colour') });
    const to = h('input', { class: 'input hex', maxlength: 7, value: initial ? initial[1] : '#8C66D1', 'aria-label': _('Second colour') });
    const angle = h('input', { class: 'slider', type: 'range', min: 0, max: 359, value: initial ? initial[2] : 135, 'aria-label': _('Angle') });
    const angleVal = h('span', { class: 'val', text: `${angle.value}°` });
    angle.addEventListener('input', () => { angleVal.textContent = `${angle.value}°`; });
    const swatch = h('span', { class: 'grad-swatch' });
    // Stored degrees use the renderer's convention (0 = left to right, 90 = top to
    // bottom); CSS counts from the top, so the swatch adds 90 to match the wallpaper.
    const paint = () => { swatch.style.background = `linear-gradient(${(+angle.value + 90) % 360}deg, ${from.value}, ${to.value})`; };
    for (const el of [from, to, angle]) el.addEventListener('input', paint);
    paint();
    const t = cur();
    const make = button(initial ? _('Save gradient') : _('Make theme'), 'primary', () => {
      const cmd = initial ? 'editGradient' : 'createGradient';
      const args = { from: from.value, to: to.value, degrees: +angle.value };
      if (initial) args.name = t.name;
      call(cmd, args).then(state => {
        mergeState(state); if (state.created) { S.sel = state.created; S.view = 'edit'; }
        buildAll(); setStatus(initial ? _('Gradient updated') : _('Gradient theme created'));
      }).catch(e => setStatus(_f('Couldn\'t make the gradient. {0}', e.message), true));
    });
    const card = h('div', { class: 'grad-card' },
      h('div', { class: 'grad-row' }, from, to, swatch),
      h('div', { class: 'grad-row' }, angle, angleVal),
      h('div', { class: 'grad-row' }, make));
    document.body.append(card);
    card.querySelector('.hex').focus();
    const gone = e => { if (!card.contains(e.target)) { card.remove(); document.removeEventListener('pointerdown', gone, true); } };
    document.addEventListener('pointerdown', gone, true);
    card.addEventListener('keydown', e => { if (e.key === 'Escape') card.remove(); });
  }

  // ---------- files in and out ----------
  function importTheme() {
    flushSave().then(() => call('importTheme')).then(state => {
      if (state.cancelled) return;
      mergeState(state); S.sel = state.imported; S.view = 'edit'; buildAll(); setStatus(_('Theme imported'));
    }).catch(e => setStatus(_f('Couldn\'t import. {0}', e.message), true));
  }

  function backupLibrary() {
    call('backupLibrary').then(r => { if (!r.cancelled) setStatus(_('Backup written')); })
      .catch(e => setStatus(_f('Couldn\'t back up. {0}', e.message), true));
  }

  function restoreLibrary() {
    call('restoreLibrary').then(state => {
      if (state.cancelled) return;
      mergeState(state); buildAll(); setStatus(_f('Restored {0} themes', state.restored));
    }).catch(e => setStatus(_f('Couldn\'t restore. {0}', e.message), true));
  }

  function openThemesFolder() {
    call('openThemesFolder').catch(e => setStatus(_f('Couldn\'t open the folder. {0}', e.message), true));
  }

  // A dropped file's bytes are read here and sent to the host; the full path is
  // never exposed to the page.
  const DROP_LIMIT = 40 * 1024 * 1024;
  let dragDepth = 0;
  function fileBase64(file) {
    return file.arrayBuffer().then(buf => {
      const bytes = new Uint8Array(buf);
      let text = '';
      for (let i = 0; i < bytes.length; i += 0x8000)
        text += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
      return btoa(text);
    });
  }

  function dropped(file) {
    const name = file.name || 'dropped';
    const dot = name.lastIndexOf('.');
    const ext = dot < 0 ? '' : name.slice(dot).toLowerCase();
    if (file.size > DROP_LIMIT) {
      setStatus(_('That file is too big to drop. Use the buttons instead.'), true);
      return Promise.resolve(false);
    }
    const isPack = ext === '.altheme' || ext === '.albackup' || ext === '.zip';
    const isImage = ['.jpg', '.jpeg', '.png', '.bmp', '.gif'].includes(ext);
    if (!isPack && !isImage) {
      setStatus(_('Drop a theme file (.altheme / .albackup) or an image.'), true);
      return Promise.resolve(false);
    }
    setStatus(_('Reading ') + name + '…');
    return fileBase64(file).then(data => call(isPack ? 'importAnyData' : 'createThemeData', { fileName: name, data })).then(state => {
      mergeState(state);
      if (state.imported) { S.sel = state.imported; S.view = 'edit'; buildAll(); setStatus(_('Theme imported')); }
      else if (state.created) { S.sel = state.created; S.view = 'edit'; buildAll(); setStatus(_('Theme created')); }
      else if (state.restored != null) { buildAll(); setStatus(_f('Restored {0} themes', state.restored)); }
      return true;
    }).catch(e => { setStatus(_f('Couldn\'t use that file. {0}', e.message), true); return false; });
  }

  function wireDrop() {
    const veil = h('div', { class: 'drop-veil', 'aria-hidden': 'true' },
      h('div', { class: 'card' }, _('Drop to add'), h('div', { class: 'sub', text: _('a theme file (.altheme / .albackup) to import · an image to make a theme') })));
    document.body.append(veil);
    const wants = e => e.dataTransfer && [...e.dataTransfer.types].includes('Files');
    window.addEventListener('dragenter', e => { if (wants(e)) { dragDepth++; veil.classList.add('on'); } });
    window.addEventListener('dragleave', () => { dragDepth = Math.max(0, dragDepth - 1); if (!dragDepth) veil.classList.remove('on'); });
    window.addEventListener('dragover', e => { if (wants(e)) e.preventDefault(); });
    window.addEventListener('drop', e => {
      if (wants(e)) e.preventDefault();
      dragDepth = 0; veil.classList.remove('on');
      const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
      if (file) dropped(file);
    });
  }

  // ---------- build ----------
  let firstBuild = true;
  function buildAll() {
    const app = $('#app');
    app.tabIndex = -1;
    app.textContent = '';
    const t = cur();
    const empty = !S.themes.length;
    app.className = empty ? 'empty-view' : S.view !== 'edit' ? 'apps-view' : '';
    applyAccent();
    ui = { stage: null };
    ui.list = h('div', { class: 'rail-list', role: 'listbox', 'aria-label': _('Themes') });
    const appsBtn = button(_('Apps and startup'), 'quiet', () => { flushSave(); S.view = 'apps'; buildAll(); }, 0xE713);
    appsBtn.setAttribute('aria-current', String(S.view === 'apps'));
    const crossBtn = button(_('Mouse pointers'), 'quiet', () => { flushSave(); S.view = 'cross'; buildAll(); }, 0xE753);
    crossBtn.setAttribute('aria-current', String(S.view === 'cross'));
    const settingsBtn = button(_('Settings'), 'quiet', () => { flushSave(); S.view = 'settings'; buildAll(); }, 0xE713);
    settingsBtn.setAttribute('aria-current', String(S.view === 'settings'));
    app.append(h('nav', { class: 'rail' },
      h('div', { class: 'rail-head' }, h('h2', { text: _('Themes') }),
        h('div', { class: 'rail-actions' },
          newMenu(),
          button(_('Import…'), 'quiet', importTheme, 0xE8C4))),
      railTools(),
      ui.list, h('div', { class: 'rail-foot' }, appsBtn, crossBtn, settingsBtn,
        h('div', { class: 'foot-row' },
          button(_('Backup…'), 'quiet', backupLibrary, 0xE74E),
          button(_('Restore…'), 'quiet', restoreLibrary, 0xE895),
          button(_('Folder'), 'quiet', openThemesFolder, 0xE8DA),
          button(_('Keys'), 'quiet', keysCard, 0xE765)))));
    renderRail();
    if (empty && S.view === 'edit') { app.append(buildEmpty()); markReady(); return; }
    if (S.view === 'apps') { app.append(buildApps()); markReady(); return; }
    if (S.view === 'cross') { app.append(buildCross()); markReady(); return; }
    if (S.view === 'settings') { app.append(buildSettings()); markReady(); return; }
    app.append(buildCanvas(t), buildInspector(t));
    applyStageMode();
    layoutStage();
    markReady();
  }

  function markReady() {
    const root = document.documentElement;
    delete root.dataset.ready;
    const imgs = [...document.querySelectorAll('.thumb img, .stage .art, .avatar-slot img')].filter(i => i.getAttribute('src'));
    const loaded = imgs.map(img => img.complete ? Promise.resolve() : new Promise(r => { img.addEventListener('load', r, { once: true }); img.addEventListener('error', r, { once: true }); }));
    const waits = [Promise.all(loaded)];
    if (ui.stage && !ui.art.getAttribute('src') && !ui.art.hidden) waits.push(new Promise(r => { ui.artLoaded = r; }));
    if (S.stage === 'splash' && ui.frame) waits.push(new Promise(r => { ui.frameLoaded = () => setTimeout(r, 900); }));
    Promise.race([Promise.all(waits), new Promise(r => setTimeout(r, 12000))]).then(() => requestAnimationFrame(() => requestAnimationFrame(() => { root.dataset.ready = '1'; })));
  }

  // ---------- refresh on focus ----------
  window.addEventListener('focus', () => {
    call('state').then(state => {
      const editing = document.activeElement && document.activeElement.closest && document.activeElement.closest('.inspector, .name-field');
      mergeState(state);
      if (!ui.list) return;
      if (S.view !== 'edit' && !editing) { buildAll(); return; }
      renderRail();
      if (S.view === 'edit') refreshChips();
    }).catch(() => { });
  });

  // ---------- start ----------
  function start() {
    call('state').then(state => {
      mergeState(state);
      const hash = parseHash();
      if (hash.lang) { forcedLang = hash.lang; setLang(hash.lang); }
      if (hash.empty === '1') { S.themes = []; S.sel = null; }
      S.sel = hash.theme && S.themes.find(t => t.name === hash.theme) ? hash.theme : defaultSelection();
      if (hash.view === 'apps' || hash.view === 'settings') S.view = hash.view;
      if (hash.stage === 'splash') S.stage = 'splash';
      buildAll();
      if (firstBuild) { firstBuild = false; $('#app').focus({ preventScroll: true }); }
    }).catch(e => { $('#app').textContent = _f('Theme Studio couldn\'t load its themes. {0}', e.message); });
  }
  window.__studio = { call, state: S, select: selectTheme, edit: onEdit, flush: flushSave, play, setStage, dropped };
  wireDrop();
  start();
})();
