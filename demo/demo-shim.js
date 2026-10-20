// K631 Live demo: stands in for K631Live.exe and the keyboard, so the real K631 Live page runs in any
// browser. It answers the same API (/api/lighting, /api/music, /api/layout) and sends the same live
// events (status, key presses, music frames) as the real program; nothing here touches a device.
(() => {
  const listeners = new Set();

  class DemoEventSource {
    constructor(url){
      this.url = url;
      this.handlers = {};
      this.onmessage = null;
      this.onerror = null;
      listeners.add(this);
      setTimeout(() => this.emit('status', { k631: true, all: false }), 60);
    }
    addEventListener(type, fn){ (this.handlers[type] = this.handlers[type] || []).push(fn); }
    removeEventListener(){}
    close(){ listeners.delete(this); }
    emit(type, data){
      const event = { data: JSON.stringify(data) };
      if (type === 'message'){ if (this.onmessage) this.onmessage(event); }
      else (this.handlers[type] || []).forEach(fn => fn(event));
    }
  }
  const broadcast = (type, data) => listeners.forEach(l => l.emit(type, data));
  window.EventSource = DemoEventSource;

  // ---- Simulated keyboard lighting (the K631's mode block, as K631Live.exe reports it) ----
  const PALETTE = ['#ff0000', '#0000ff', '#00ff00', '#ffff00', '#ff00ff', '#00ffff', '#ffffff'];
  const COLOR_EFFECTS = [1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 18, 19, 20];
  const kb = { mode: 18, pairs: {}, colourful: {}, colors: {} };
  for (let m = 1; m <= 20; m++){
    kb.pairs[m] = { speed: 3, brightness: 3 };
    kb.colourful[m] = true;
    kb.colors[m] = PALETTE.slice();
  }

  function describe(){
    const s = { ok: true, mode: kb.mode };
    if (kb.mode <= 20){
      Object.assign(s, kb.pairs[kb.mode]);
      s.colourful = kb.colourful[kb.mode];
      s.palette = kb.colors[kb.mode].slice();
      s.color = s.palette[0];
    }
    return s;
  }

  function setLighting(a){
    const fail = error => ({ ok: false, error });
    const mode = Number(a.mode);
    if (!(mode >= 1 && mode <= 21)) return fail('Effect must be 1-21.');
    if (a.brightness != null && !(a.brightness >= 0 && a.brightness <= 4)) return fail('Brightness must be 0-4.');
    if (a.speed != null && !(a.speed >= 1 && a.speed <= 4)) return fail('Speed must be 1-4.');
    if (a.color != null && !COLOR_EFFECTS.includes(mode)) return fail('This effect has no colour setting.');
    const before = JSON.stringify(describe());
    kb.mode = mode;
    if (mode <= 20){
      if (a.brightness != null) kb.pairs[mode].brightness = a.brightness;
      if (a.speed != null) kb.pairs[mode].speed = a.speed;
      if (a.color === 'colourful') kb.colourful[mode] = true;
      else if (a.color){ kb.colourful[mode] = false; kb.colors[mode][0] = a.color.toLowerCase(); }
    }
    const after = describe();
    return Object.assign(after, { changed: before === JSON.stringify(after) ? [] : ['demo'] });
  }

  // ---- Simulated music: a 120 BPM groove, rendered with the same five styles as K631Live.exe ----
  const music = { on: false, style: 'wave', keys: true, timer: 0, start: 0 };
  const musicState = () => ({ on: music.on, style: music.style, keys: music.keys, keysError: null });

  const renderer = (() => {
    const COLUMNS = 15, GRID_ROWS = 6, KEY_ROWS = 5;
    const shown = new Float64Array(16);
    let bloomHue = 0, pulseHue = 0, bassAverage = 0;
    const zoneHue = c => 120 + c * 300 / (COLUMNS - 1);
    const average = (from, to) => { let s = 0; for (let b = from; b <= to; b++) s += shown[b]; return s / (to - from + 1); };
    function set(rgb, column, row, hue, intensity){
      intensity = Math.max(0, Math.min(1, intensity));
      hue = ((hue % 360) + 360) % 360;
      const x = 1 - Math.abs(hue / 60 % 2 - 1);
      const [r, g, b] = hue < 60 ? [1, x, 0] : hue < 120 ? [x, 1, 0] : hue < 180 ? [0, 1, x]
        : hue < 240 ? [0, x, 1] : hue < 300 ? [x, 0, 1] : [1, 0, x];
      const i = (column * GRID_ROWS + row) * 3;
      rgb[i] = Math.round(r * 255 * intensity);
      rgb[i + 1] = Math.round(g * 255 * intensity);
      rgb[i + 2] = Math.round(b * 255 * intensity);
    }
    return (style, levels) => {
      for (let b = 0; b < 16; b++) shown[b] = levels[b] > shown[b] ? levels[b] : shown[b] * 0.82 + levels[b] * 0.18;
      const rgb = new Uint8Array(COLUMNS * GRID_ROWS * 3);
      const volume = average(0, 15), bass = average(0, 2);
      if (style === 'bars'){
        for (let c = 0; c < COLUMNS; c++)
          for (let row = 0; row < KEY_ROWS; row++)
            if (shown[c] >= (KEY_ROWS - row) / KEY_ROWS - 0.1) set(rgb, c, row, zoneHue(c), 1);
      } else if (style === 'bloom'){
        bloomHue = (bloomHue + 1.5) % 360;
        const radius = average(0, 7) * 9;
        for (let c = 0; c < COLUMNS; c++)
          for (let row = 0; row < KEY_ROWS; row++){
            const d = Math.sqrt((c - 7) ** 2 + (row - 2) ** 2 * 2.56);
            if (d <= radius) set(rgb, c, row, bloomHue + d * 25, 1 - 0.6 * d / (radius + 0.5));
          }
      } else if (style === 'stream'){
        const reach = volume * COLUMNS * 1.3;
        for (let c = 0; c < COLUMNS && c < reach; c++)
          for (let row = 0; row < KEY_ROWS; row++) set(rgb, c, row, 120 + c * 8, Math.min(1, reach - c));
      } else if (style === 'pulse'){
        if (bass > bassAverage * 1.35 && bass > 0.35) pulseHue = (pulseHue + 55) % 360;
        bassAverage = bassAverage * 0.95 + bass * 0.05;
        for (let c = 0; c < COLUMNS; c++)
          for (let row = 0; row < KEY_ROWS; row++) set(rgb, c, row, pulseHue, 0.12 + 0.88 * bass);
      } else {
        for (let c = 0; c < COLUMNS; c++)
          if (shown[c] > 0.05) set(rgb, c, KEY_ROWS - 1 - Math.round(Math.min(1, shown[c]) * (KEY_ROWS - 1)), zoneHue(c), 1);
      }
      return rgb;
    };
  })();

  // Band levels for a made-up song: kick on every beat, a bass line, a moving melody, hi-hats off the beat.
  function songLevels(t){
    const beat = t * 2, phase = beat % 1, bar = Math.floor(beat / 4) % 4;
    const kick = Math.exp(-phase * 7), hat = Math.exp(-((beat + 0.5) % 1) * 12);
    const levels = [];
    for (let b = 0; b < 16; b++){
      let v;
      if (b < 3) v = 0.35 + 0.65 * kick;
      else if (b < 6) v = 0.3 + 0.35 * (0.5 + 0.5 * Math.sin(t * 2.1 + b)) + 0.2 * kick;
      else if (b < 11) v = 0.2 + 0.5 * Math.max(0, Math.sin(t * 3.3 - b * 0.7 + bar)) ** 2;
      else v = 0.08 + 0.6 * hat * (1 - (b - 11) * 0.12);
      levels.push(Math.max(0, Math.min(1, v * (0.85 + 0.15 * Math.sin(t * 13 + b * 2.7)))));
    }
    return levels;
  }

  function tick(){
    const levels = songLevels((performance.now() - music.start) / 1000);
    const frame = renderer(music.style, levels);
    let hex = '';
    for (const v of frame) hex += v.toString(16).padStart(2, '0');
    broadcast('audio', { b: levels.map(v => Math.round(v * 100)), k: music.keys, f: hex });
  }

  function setMusic(a){
    if (a.style != null){
      if (!['wave', 'bars', 'bloom', 'stream', 'pulse'].includes(a.style)) return Object.assign(musicState(), { ok: false, error: 'Unknown music style.' });
      music.style = a.style;
    }
    if (a.keys != null) music.keys = !!a.keys;
    if (a.on != null){
      if (a.on && !music.on){ music.on = true; music.start = performance.now(); music.timer = setInterval(tick, 33); }
      else if (!a.on && music.on){ music.on = false; clearInterval(music.timer); }
    }
    broadcast('music', musicState());
    return Object.assign(musicState(), { ok: true });
  }

  // ---- The API K631Live.exe serves ----
  let savedLayout = null;
  const json = obj => new Response(JSON.stringify(obj), { headers: { 'Content-Type': 'application/json' } });
  const realFetch = window.fetch.bind(window);
  window.fetch = async (input, init = {}) => {
    const url = typeof input === 'string' ? input : input.url;
    const path = new URL(url, location.href).pathname;
    if (!/\/api\/(lighting|music|layout)/.test(path)) return realFetch(input, init);
    const method = (init.method || 'GET').toUpperCase();
    let body = null;
    if (init.body && !path.endsWith('/klc')){ try { body = JSON.parse(init.body); } catch { body = null; } }
    await new Promise(r => setTimeout(r, 90));   // about as long as a real keyboard write takes
    if (path.endsWith('/api/lighting')) return json(method === 'GET' ? describe() : setLighting(body || {}));
    if (path.endsWith('/api/music')) return json(method === 'GET' ? Object.assign(musicState(), { ok: true }) : setMusic(body || {}));
    if (path.endsWith('/api/layout/klc')) return json({ ok: true, path: 'K631Live\\export\\K631-custom.klc' });
    if (method === 'GET') return json(savedLayout);
    savedLayout = body;
    return json({ ok: true });
  };

  // ---- Simulated typing, used by the guided tour ----
  const keyEvent = (type, code) => broadcast('message', { type, code, scan: 0, vk: 0, k631: true });
  window.demoKeyboard = {
    press(code, holdMs = 140){
      keyEvent('down', code);
      return new Promise(r => setTimeout(() => { keyEvent('up', code); r(); }, holdMs));
    }
  };
})();
