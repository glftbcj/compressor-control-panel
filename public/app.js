// ─── DOM ─────────────────────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
const deviceSelect     = $('deviceSelect');
const addDeviceBtn     = $('addDeviceBtn');
const deviceIdInput    = $('deviceIdInput');
const deleteDeviceBtn  = $('deleteDeviceBtn');
const startBtn         = $('startBtn');
const stopBtn          = $('stopBtn');
const setTempBtn       = $('setTempBtn');
const setPumpSpeedBtn  = $('setPumpSpeedBtn');
const refreshBtn       = $('refreshBtn');
const temperatureInput = $('temperatureInput');
const pumpSpeedInput   = $('pumpSpeedInput');
const pumpSpeedValue   = $('pumpSpeedValue');
const connectionStatus = $('connectionStatus');
const connectionDot    = $('connectionDot');
const deviceCount      = $('deviceCount');
const statusOutput     = $('statusOutput');

// ─── 持久化 ──────────────────────────────────────────────────────────────────
const STORAGE_KEY = 'knownDevices';

function loadKnownDevices() {
  try {
    const arr = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
    return Array.isArray(arr) ? arr.filter(e => e?.deviceId) : [];
  } catch { return []; }
}

function saveKnownDevices() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(knownDevices));
}

// ─── 状态 ────────────────────────────────────────────────────────────────────
let knownDevices     = loadKnownDevices();
let autoRefreshTimer = null;

// ─── UI 工具 ─────────────────────────────────────────────────────────────────
function setStatus(text, isError = false) {
  statusOutput.textContent   = text;
  statusOutput.dataset.state = isError ? 'error' : 'ok';
}

function setConnectionUI(connected, error) {
  const label = connected ? '已连接' : (error ? `断开 (${error})` : '未连接');
  connectionStatus.textContent = `MQTT：${label}`;
  connectionDot.dataset.state  = connected ? 'connected' : 'disconnected';
}

function getSelectedDeviceId() {
  return deviceSelect.value || null;
}

function setControlsDisabled(disabled) {
  [temperatureInput, setTempBtn, pumpSpeedInput, setPumpSpeedBtn].forEach(el => el.disabled = disabled);
}

function updateSliderFill(val) {
  const min = +pumpSpeedInput.min || 0;
  const max = +pumpSpeedInput.max || 10;
  const pct = max > min ? ((val - min) / (max - min)) * 100 : 0;
  pumpSpeedInput.style.background = `linear-gradient(90deg,var(--primary) ${pct}%,#1e293b ${pct}%)`;
}

function syncInputs(payload) {
  if (!payload || typeof payload !== 'object') return;

  const temp = Number(payload.set_temp);
  if (Number.isFinite(temp)) {
    temperatureInput.value    = temp;
    temperatureInput.disabled = false;
    setTempBtn.disabled       = false;
  }

  const speed = Number(payload.wind_speed_set);
  if (Number.isFinite(speed)) {
    pumpSpeedInput.value       = speed;
    pumpSpeedValue.textContent = speed;
    pumpSpeedInput.disabled    = false;
    setPumpSpeedBtn.disabled   = false;
    updateSliderFill(speed);
  }
}

function updateDeviceList() {
  const prev = deviceSelect.value;
  deviceSelect.innerHTML = '';

  if (!knownDevices.length) {
    deviceSelect.add(new Option('请选择设备', ''));
    deviceCount.textContent = '设备数量：0';
    return;
  }

  knownDevices.forEach(({ deviceId }) => deviceSelect.add(new Option(deviceId, deviceId)));
  deviceSelect.value      = knownDevices.some(e => e.deviceId === prev) ? prev : knownDevices[0].deviceId;
  deviceCount.textContent = `设备数量：${knownDevices.length}`;
}

// ─── Loading 包装 ────────────────────────────────────────────────────────────
function withLoading(btn, fn) {
  return async (...args) => {
    const label = btn.textContent;
    btn.disabled = true;
    btn.textContent = '请稍候…';
    try { await fn(...args); }
    finally { btn.textContent = label; btn.disabled = false; }
  };
}

// ─── API ─────────────────────────────────────────────────────────────────────
async function fetchDeviceList() {
  try {
    const json = await (await fetch('/api/devices')).json();
    setConnectionUI(json.connected);
    updateDeviceList();
  } catch (err) {
    setConnectionUI(false, err.message);
    setStatus(`获取设备列表失败: ${err.message}`, true);
    updateDeviceList();
  }
}

async function sendCommand(action, value = null, deviceId = null, showStatus = true) {
  const id = deviceId || getSelectedDeviceId();
  if (!id) { setStatus('请先选择或添加一个设备。', true); return null; }

  try {
    const res  = await fetch('/api/control', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId: id, action, value }),
    });
    const json = await res.json();
    if (!res.ok) throw new Error(json.error || '命令发送失败');
    if (showStatus) setStatus(`已发送 ${action} → ${id}`);
    return json;
  } catch (err) {
    setStatus(`命令失败: ${err.message}`, true);
    return null;
  }
}

// silent = true → 后台静默刷新（不显示"正在请求"）
async function refreshStatus(silent = false) {
  const deviceId = getSelectedDeviceId();
  if (!deviceId) { setStatus('请先选择或添加一个设备。', true); return; }

  if (!silent) setStatus('正在请求设备状态…');
  await sendCommand('get_data', null, deviceId, false);
  await new Promise(r => setTimeout(r, 600));

  try {
    const json = await (await fetch('/api/status')).json();
    setConnectionUI(json.connected, json.lastError);

    const dev = json.devices?.find(d => d.deviceId === deviceId);
    if (!dev) { setStatus('设备未返回数据，请确认设备已上线。', true); return; }

    setStatus(JSON.stringify(dev, null, 2));
    syncInputs(dev.payload);
  } catch (err) {
    setStatus(`刷新失败: ${err.message}`, true);
  }
}

// ─── 自动刷新 ─────────────────────────────────────────────────────────────────
const AUTO_REFRESH_MS = 15_000;

function startAutoRefresh() {
  stopAutoRefresh();
  autoRefreshTimer = setInterval(() => {
    getSelectedDeviceId() ? refreshStatus(true) : fetchDeviceList();
  }, AUTO_REFRESH_MS);
}

function stopAutoRefresh() {
  if (autoRefreshTimer) { clearInterval(autoRefreshTimer); autoRefreshTimer = null; }
}

// ─── 设备管理 ─────────────────────────────────────────────────────────────────
function addKnownDevice(id) {
  if (!id || knownDevices.some(e => e.deviceId === id)) return;
  knownDevices.push({ deviceId: id });
  saveKnownDevices();
}

async function addDeviceAndSync(id) {
  addKnownDevice(id);
  updateDeviceList();
  deviceSelect.value = id;
  setStatus(`已添加 ${id}，正在查询状态…`);
  await sendCommand('get_data', null, id, false);
  await new Promise(r => setTimeout(r, 500));
  await refreshStatus();
}

function deleteSelectedDevice() {
  const id = getSelectedDeviceId();
  if (!id) { setStatus('请选择一个设备后再删除。', true); return; }
  if (!confirm(`确定要删除设备 "${id}" 吗？`)) return;
  knownDevices = knownDevices.filter(e => e.deviceId !== id);
  saveKnownDevices();
  updateDeviceList();
  setStatus(`已删除 ${id}`);
  setControlsDisabled(true);
}

// ─── 事件 ────────────────────────────────────────────────────────────────────
deleteDeviceBtn.addEventListener('click', deleteSelectedDevice);

addDeviceBtn.addEventListener('click', withLoading(addDeviceBtn, async () => {
  const id = deviceIdInput.value.trim();
  if (!id) { setStatus('请输入设备ID后再添加。', true); return; }
  await addDeviceAndSync(id);
  deviceIdInput.value = '';
}));

startBtn.addEventListener('click', withLoading(startBtn, () => sendCommand('start')));
stopBtn.addEventListener('click',  withLoading(stopBtn,  () => sendCommand('stop')));

setTempBtn.addEventListener('click', withLoading(setTempBtn, () => {
  const v = Number(temperatureInput.value);
  if (!Number.isFinite(v) || v < -20 || v > 50) {
    setStatus('温度须在 -20 ~ 50 ℃ 范围内', true);
    return;
  }
  return sendCommand('setTemperature', v);
}));

setPumpSpeedBtn.addEventListener('click', withLoading(setPumpSpeedBtn, () =>
  sendCommand('setWindSpeed', Number(pumpSpeedInput.value))
));

refreshBtn.addEventListener('click', withLoading(refreshBtn, () => refreshStatus()));

pumpSpeedInput.addEventListener('input', () => {
  pumpSpeedValue.textContent = pumpSpeedInput.value;
  updateSliderFill(+pumpSpeedInput.value);
});

deviceIdInput.addEventListener('keydown',    e => { if (e.key === 'Enter') addDeviceBtn.click(); });
temperatureInput.addEventListener('keydown', e => { if (e.key === 'Enter') setTempBtn.click(); });

deviceSelect.addEventListener('change', () => {
  setControlsDisabled(true);
  if (getSelectedDeviceId()) refreshStatus();
});

// ─── 初始化 ──────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', async () => {
  setControlsDisabled(true);
  temperatureInput.value     = '';
  pumpSpeedInput.value       = '';
  pumpSpeedValue.textContent = '0';
  updateSliderFill(0);

  await fetchDeviceList();

  if (knownDevices.length) {
    knownDevices.forEach(({ deviceId }) => sendCommand('get_data', null, deviceId, false));
    await new Promise(r => setTimeout(r, 300));
    await refreshStatus();
  }

  startAutoRefresh();
});
