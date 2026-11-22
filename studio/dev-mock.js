// Design harness only: stands in for the WebView2 host so studio.js can run in a plain browser.
(function () {
  'use strict';
  if (window.chrome && window.chrome.webview) return;
  const empty = /(^|[#&])empty=1/.test(location.hash);
  const base = { opacity: 15, blur: 0, dim: 0, tint: 0, tintColor: '#8C66D1', focusX: 50, focusY: 50 };
  const make = (name, look, text, accents, hasAvatar) => ({
    name, look: Object.assign({}, base, look), hasAvatar: hasAvatar !== false, stamp: '1', broken: false,
    text: Object.assign({ title: name.toUpperCase(), subtitle: '', accent: accents[0], logs: [], font: '', frame: '', lines: [] }, text), accents
  });
  let themes = empty ? [] : [
    make('Sunset harbor', { opacity: 18 }, { subtitle: 'Evening at the pier', accent: '#E9B44C', style: 'terminal', font: 'mono', logs: ['HARBOR CONTROL \u00B7 LINK OPEN', 'tide tables loaded ......... ok', 'lanterns lit ......... ok', 'all quiet on the water'] }, ['#E9B44C', '#EF5B4C', '#7FD3E6']),
    make('Night train', { opacity: 18, blur: 24, dim: 52 }, { accent: '#7FD3E6' }, ['#7FD3E6', '#B49CFF']),
    make('Paper cranes', { opacity: 14, blur: 13 }, { subtitle: 'Folded by hand', accent: '#D980A7' }, ['#D980A7', '#8FD694', '#E9B44C']),
    make('Glass garden', { blur: 13, tint: 100, tintColor: '#3C9C8F' }, { title: 'GLASS GARDEN', subtitle: 'Open until dusk', accent: '#8FD694' }, []),
    make('Low tide', { opacity: 22, dim: 30 }, { accent: '#B49CFF' }, ['#B49CFF'])
  ];
  const apps = [
    { id: 'devin', name: 'Devin', theme: 'Sunset harbor', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'editor', helperExists: true, helperRunning: true, autostart: true, status: 'on', statusWhen: new Date(Date.now() - 120000).toISOString() },
    { id: 'antigravity', name: 'Antigravity', theme: 'Glass garden', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'editor', helperExists: true, helperRunning: false, autostart: true },
    { id: 'codex', name: 'Codex', theme: 'Glass garden', splash: false, speed: 2, style: 'short', once: false, installed: true, preview: 'chat', helperExists: false, helperRunning: false, autostart: false },
    { id: 'claude', name: 'Claude', theme: 'Paper cranes', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'chat', helperExists: true, helperRunning: true, autostart: false, status: 'parked', problem: 'kept failing; it stays off until its settings change', statusWhen: new Date(Date.now() - 3600000).toISOString() },
    { id: 'vscode', name: 'VS Code', theme: '', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'editor', helperExists: true, helperRunning: true, autostart: true },
    { id: 'cursor', name: 'Cursor', theme: '', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'editor', helperExists: true, helperRunning: true, autostart: false },
    { id: 'kiro', name: 'Kiro', theme: '', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'editor', helperExists: true, helperRunning: true, autostart: false },
    { id: 'windsurf', name: 'Windsurf', theme: '', splash: true, speed: 1, style: 'full', once: false, installed: false, preview: 'editor', helperExists: true, helperRunning: false, autostart: false },
    { id: 'vscodium', name: 'VSCodium', theme: '', splash: true, speed: 1, style: 'full', once: false, installed: false, preview: 'editor', helperExists: true, helperRunning: false, autostart: false },
    { id: 'obsidian', name: 'Obsidian', theme: '', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: 'editor', helperExists: true, helperRunning: false, autostart: false }
  ];
  const listeners = [];
  const looks = ['Studio Glow', 'Reader'];
  const prefs = { favorites: [], sort: 'name', welcome: true, lang: '' };
  const savedLooks = { 'Studio Glow': { opacity: 18, blur: 25, dim: 10, tint: 0, tintColor: '#8C66D1', focusX: 50, focusY: 50, clear: 0, clearSide: '', glow: 55, vignette: 0, sat: 110, fade: 0, fit: 'cover' }, 'Reader': { opacity: 30, blur: 10, dim: 30, tint: 0, tintColor: '#8C66D1', focusX: 50, focusY: 50, clear: 80, clearSide: 'left', glow: 0, vignette: 20, sat: 85, fade: 0, fit: 'cover' } };
  const state = () => JSON.parse(JSON.stringify({ themes, apps, looks, screen: { w: 2560, h: 1440 }, prefs, version: '3.0.0.0' }));
  function handle(m) {
    const find = n => themes.find(t => t.name === n);
    switch (m.cmd) {
      case 'state': return state();
      case 'saveTheme': { const t = find(m.name); t.look = m.look; t.text = m.text; return { look: m.look, text: m.text }; }
      case 'applyTheme': apps.forEach(a => { if (m.app === 'all' || m.app === a.id) a.theme = m.name; }); return JSON.parse(JSON.stringify(apps));
      case 'createTheme': return { cancelled: true };
      case 'renameTheme': { const t = find(m.name); if (find(m.newName)) throw new Error(`A theme called "${m.newName}" already exists.`); apps.forEach(a => { if (a.theme === t.name) a.theme = m.newName; }); t.name = m.newName; const s = state(); s.renamed = m.newName; return s; }
      case 'deleteTheme': themes = themes.filter(t => t.name !== m.name); apps.forEach(a => { if (a.theme === m.name) a.theme = ''; }); return state();
      case 'setApp': { const a = apps.find(x => x.id === m.app); if ('splash' in m) a.splash = m.splash; if ('speed' in m) a.speed = m.speed; if ('style' in m) a.style = m.style; if ('once' in m) a.once = m.once; if ('rotate' in m) a.rotate = m.rotate; if ('rotateFavs' in m) a.rotateFavs = m.rotateFavs; if ('followWindows' in m) a.followWindows = m.followWindows; if ('themeLight' in m) a.themeLight = m.themeLight; if ('themeNight' in m) a.themeNight = m.themeNight; if ('nightStart' in m) a.nightStart = m.nightStart; if ('nightUntil' in m) a.nightUntil = m.nightUntil; if ('nightDim' in m) a.nightDim = m.nightDim; if ('extraDim' in m) a.extraDim = m.extraDim; if ('hideFullscreen' in m) a.hideFullscreen = m.hideFullscreen; return JSON.parse(JSON.stringify(apps)); }
      case 'setAutostart': apps.find(x => x.id === m.app).autostart = m.on; return JSON.parse(JSON.stringify(apps));
      case 'startHelper': apps.find(x => x.id === m.app).helperRunning = true; return JSON.parse(JSON.stringify(apps));
      case 'saveLookPreset': { savedLooks[m.name] = m.look; if (!looks.includes(m.name)) looks.push(m.name); return state(); }
      case 'deleteLookPreset': { const i = looks.indexOf(m.name); if (i >= 0) looks.splice(i, 1); delete savedLooks[m.name]; return state(); }
      case 'applyLookPreset': { if (!savedLooks[m.name]) throw new Error(`The look "${m.name}" no longer exists.`); return { look: savedLooks[m.name] }; }
      case 'pickAppExe': return m.path ? { path: m.path, name: 'Obsidian' } : { path: 'C:\\Apps\\Obsidian\\Obsidian.exe', name: 'Obsidian' };
      case 'probeApp': return { path: m.path, exists: true, covered: apps.some(a => a.exe && a.exe.toLowerCase() === (m.path || '').toLowerCase()), running: true, windowClass: 'Chrome_WidgetWin_1' };
      case 'addCustomApp': {
        const id = (m.name || 'app').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'app';
        apps.push({ id, name: m.name, theme: '', splash: true, speed: 1, style: 'full', once: false, installed: true, preview: m.preview || 'editor', helperExists: true, helperRunning: true, autostart: false, custom: true, exe: m.path });
        const s = state(); s.added = id; return s;
      }
      case 'removeCustomApp': { const i = apps.findIndex(a => a.id === m.app && a.custom); if (i >= 0) apps.splice(i, 1); return JSON.parse(JSON.stringify(apps)); }
      case 'useDesktopWallpaper': { const t = make('Desktop wallpaper', {}, {}, ['#8C66D1'], false); themes.push(t); const s = state(); s.created = t.name; return s; }
      case 'pasteImage': { const t = make('Pasted image', {}, {}, ['#8C66D1'], false); themes.push(t); const s = state(); s.created = t.name; return s; }
      case 'importFolder': {
        const added = [make('Harbour light', {}, {}, ['#E9B44C'], false), make('Ember', {}, {}, ['#EF5B4C'], false)];
        themes.push(...added); const s = state(); s.created = added[0].name; s.count = added.length; return s;
      }
      case 'cropAvatar': { find(m.name).hasAvatar = true; return state(); }
      case 'setPrefs': { prefs.favorites = String(m.favorites || '').split('|').filter(Boolean); prefs.sort = m.sort || 'name'; if ('welcome' in m) prefs.welcome = m.welcome !== 'off'; if ('lang' in m) prefs.lang = m.lang || ''; return { favorites: prefs.favorites, sort: prefs.sort, welcome: prefs.welcome, lang: prefs.lang }; }
      case 'starterPreview': return { starters: ['Aurora fields', 'Paper dusk', 'Night garden', 'Sakura rain', 'Deep sea', 'Sunset'].map(name => ({ name, jpeg: '/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wgARCAABAAEDASIAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQBAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhADEAAAAT//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAqf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/ASP/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/ASP/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/Al//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/IV//2gAMAwEAAgADAAAAEP/EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQMBAT8QH//EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQIBAT8QH//EABQQAQAAAAAAAAAAAAAAAAAAABD/2gAIAQEAAT8QH//Z' })) };
      case 'seedStarters': { const made = []; for (const name of ['Aurora fields', 'Paper dusk', 'Night garden', 'Sakura rain', 'Deep sea', 'Sunset']) { if (find(name)) continue; themes.push(make(name, {}, {}, ['#8C66D1'], false)); made.push(name); } const s = state(); s.seeded = made; return s; }
      case 'createGradient': {
        const t = make('Gradient', {}, {}, ['#8C66D1'], false); t.gradient = `${m.from}|${m.to}|${m.degrees}`;
        themes.push(t); const s = state(); s.created = t.name; return s;
      }
      case 'editGradient': { const t = find(m.name); t.gradient = `${m.from}|${m.to}|${m.degrees}`; t.stamp = String(+t.stamp + 1); return state(); }
      case 'checkReadability': return { luma: 40, bright: false };
      case 'copyReport': return { copied: true, text: 'Animated Launchers report\n(mock)' };
      case 'openLogFolder': return { opened: true, path: 'C:\\Mock\\Logs' };
      case 'restartHelpers': apps.forEach(a => { a.helperRunning = true; }); return JSON.parse(JSON.stringify(apps));
      case 'checkUpdates': return { version: '9.9.9', current: '3.0.0.0', url: 'https://github.com/Synaap/Codex-Animated/releases/tag/v9.9.9', updateAvailable: true };
      case 'getUpdate': return { opened: true };
      default: return { cancelled: true };
    }
  }
  window.chrome = {
    webview: {
      addEventListener(type, fn) { if (type === 'message') listeners.push(fn); },
      postMessage(message) {
        setTimeout(() => {
          let reply;
          try { reply = { id: message.id, ok: true, data: handle(message) }; } catch (e) { reply = { id: message.id, ok: false, error: e.message }; }
          listeners.forEach(fn => fn({ data: reply }));
        }, 15);
      }
    }
  };
  window.__imgUrl = (kind, p) => kind === 'avatar' ? '../web/assets/avatar.jpg' : '../web/assets/artwork.jpg';
  window.__splashUrl = () => null;
})();
