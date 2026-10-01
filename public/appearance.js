'use strict';
(() => {
  const key = 'hvacrAppearanceV1';
  const defaults = { theme: 'system', accent: '#147c6e' };
  let settings = { ...defaults };
  let legacy = false;
  let request = null;
  let notify = () => {};
  let revision = 0;
  let savedRevision = 0;
  let saveTimer = null;
  let saving = null;
  try {
    const saved = JSON.parse(localStorage.getItem(key) || 'null');
    if (['light', 'dark', 'system'].includes(saved?.theme) && /^#[0-9a-f]{6}$/i.test(saved?.accent)) {
      settings = { theme: saved.theme, accent: saved.accent.toLowerCase() }; legacy = true;
    }
  } catch { /* Use a usable theme when browser storage is unavailable. */ }
  const media = matchMedia('(prefers-color-scheme: dark)');
  const rgb = hex => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
  const hex = values => '#' + values.map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
  const mix = (a, b, weight) => hex(rgb(a).map((v, i) => v * (1 - weight) + rgb(b)[i] * weight));
  const luminance = color => rgb(color).map(v => {
    v /= 255; return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
  }).reduce((sum, v, i) => sum + v * [.2126, .7152, .0722][i], 0);
  const contrast = (a, b) => (Math.max(luminance(a), luminance(b)) + .05) / (Math.min(luminance(a), luminance(b)) + .05);
  function apply(persist = false) {
    const dark = settings.theme === 'dark' || (settings.theme === 'system' && media.matches);
    const surface = dark ? '#1a222a' : '#ffffff';
    const ink = dark ? '#e8edf1' : '#243239';
    let accentInk = settings.accent;
    for (let step = 1; contrast(accentInk, surface) < 4.5 && step <= 20; step++)
      accentInk = mix(settings.accent, ink, step / 20);
    const style = document.documentElement.style;
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    document.documentElement.dataset.themePreference = settings.theme;
    style.setProperty('--accent', settings.accent);
    style.setProperty('--accent-ink', accentInk);
    const onAccent = contrast(settings.accent, '#ffffff') >= contrast(settings.accent, '#142028') ? '#ffffff' : '#142028';
    style.setProperty('--accent-hover', mix(settings.accent, onAccent === '#ffffff' ? '#000000' : '#ffffff', .12));
    style.setProperty('--on-accent', onAccent);
    style.setProperty('--accent-soft', mix(surface, settings.accent, dark ? .14 : .09));
    style.setProperty('--hero-bg', mix('#15272e', settings.accent, .24));
    style.setProperty('--hero-glow', mix('#2a4248', settings.accent, .28));
    if (persist) try { localStorage.setItem(key, JSON.stringify(settings)); } catch { }
  }
  apply();
  media.addEventListener('change', () => { if (settings.theme === 'system') apply(); });
  function status(message, error = false) {
    const label = document.getElementById('appearanceSaveStatus');
    if (label) { label.textContent = message; label.dataset.error = String(error); }
  }
  function changed() {
    revision++; apply(true);
    status('正在保存到本地…');
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => { void save().catch(saveError); }, 180);
  }
  function saveError(error) {
    status('保存失败，请点击完成重试。', true);
    notify('外观设置未保存：' + error.message, true);
  }
  async function save() {
    clearTimeout(saveTimer);
    if (!request) throw new Error('本地服务尚未连接。');
    if (saving) return saving;
    saving = (async () => {
      while (savedRevision < revision) {
        const version = revision;
        const next = { ...settings };
        await request('/api/preferences/appearance', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(next)
        });
        savedRevision = version;
      }
      status('已保存到设备 ID 所在的数据目录。');
    })();
    try { await saving; } finally { saving = null; }
  }
  window.CoolingAppearance = {
    async connect(apiRequest, showToast) {
      request = apiRequest; notify = showToast;
      try {
        const saved = await request('/api/preferences/appearance');
        if (saved.saved) settings = { theme: saved.theme, accent: saved.accent };
        else if (legacy) { revision++; await save(); }
        else settings = { ...defaults };
        apply(true); sync();
        status(saved.saved || legacy ? '已保存到设备 ID 所在的数据目录。' : '调整后自动保存到设备 ID 所在的数据目录。');
      } catch (error) { savedRevision = -1; saveError(error); }
      finally { document.getElementById('appearanceBtn').disabled = false; }
    }
  };
  function sync() {
    const select = document.getElementById('themeSelect');
    if (!select) return;
    select.value = settings.theme;
    document.getElementById('accentColor').value = settings.accent;
    document.getElementById('accentHex').value = settings.accent;
    document.getElementById('accentHex').setCustomValidity('');
    document.querySelectorAll('[data-accent]').forEach(button =>
      button.setAttribute('aria-pressed', String(button.dataset.accent === settings.accent)));
  }
  function setColor(value) {
    if (!/^#[0-9a-f]{6}$/i.test(value)) return false;
    settings.accent = value.toLowerCase(); changed(); sync(); return true;
  }
  document.addEventListener('DOMContentLoaded', () => {
    sync();
    document.getElementById('appearanceBtn').addEventListener('click', () => document.getElementById('appearanceDialog').showModal());
    document.getElementById('themeSelect').addEventListener('change', event => {
      settings.theme = event.target.value; changed();
    });
    document.getElementById('accentColor').addEventListener('input', event => setColor(event.target.value));
    document.getElementById('accentHex').addEventListener('input', event => {
      const input = event.target;
      if (!setColor(input.value)) input.setCustomValidity('请输入 # 开头的六位颜色，例如 #4e79df。');
    });
    document.getElementById('accentHex').addEventListener('change', event => event.target.reportValidity());
    document.querySelectorAll('[data-accent]').forEach(button => {
      button.style.setProperty('--swatch', button.dataset.accent);
      button.addEventListener('click', () => setColor(button.dataset.accent));
    });
    document.getElementById('resetAppearanceBtn').addEventListener('click', () => {
      settings = { ...defaults }; changed(); sync();
    });
    const dialog = document.getElementById('appearanceDialog');
    const finish = document.getElementById('saveAppearanceBtn');
    async function closeSaved() {
      if (!document.getElementById('accentHex').reportValidity()) return;
      finish.disabled = true;
      try { await save(); dialog.close(); }
      catch (error) { saveError(error); }
      finally { finish.disabled = false; }
    }
    finish.addEventListener('click', () => { void closeSaved(); });
    dialog.addEventListener('cancel', event => { event.preventDefault(); void closeSaved(); });
  });
})();
