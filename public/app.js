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
const currentTemp      = $('currentTemp');
const currentSetTemp   = $('currentSetTemp');
const currentPumpSpeed = $('currentPumpSpeed');

// ─── 持久化 ──────────────────────────────────────────────────────────────────
const STORAGE_KEY = 'knownDevices';
const DEVICE_PREFERENCES_API = '/api/preferences/devices';
const API_TIMEOUT_MS = 10_000;

function sanitizeKnownDevices(devices) {
  const result = [];
  const seen = new Set();

  for (const entry of Array.isArray(devices) ? devices : []) {
    const deviceId = entry?.deviceId?.trim();
    if (!deviceId || seen.has(deviceId)) continue;
    seen.add(deviceId);
    result.push({ deviceId });
  }

  return result;
}

function loadKnownDevices() {
  try {
    return sanitizeKnownDevices(JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]'));
  } catch {
    return [];
  }
}

function saveKnownDevicesToStorage() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(knownDevices));
}

async function loadKnownDevicesFromServer() {
  try {
    const json = await requestJson(DEVICE_PREFERENCES_API);
    return sanitizeKnownDevices(json?.devices);
  } catch {
    return [];
  }
}

async function persistKnownDevicesToServer() {
  try {
    await requestJson(DEVICE_PREFERENCES_API, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ devices: knownDevices }),
    });
  } catch (err) {
    console.warn('保存设备 ID 备份失败', err);
  }
}

function saveKnownDevices() {
  knownDevices = sanitizeKnownDevices(knownDevices);
  saveKnownDevicesToStorage();
  void persistKnownDevicesToServer();
}

async function hydrateKnownDevices() {
  const localDevices = loadKnownDevices();
  const serverDevices = await loadKnownDevicesFromServer();
  knownDevices = sanitizeKnownDevices([...localDevices, ...serverDevices]);
  saveKnownDevicesToStorage();

  if (knownDevices.length !== localDevices.length || knownDevices.length !== serverDevices.length) {
    await persistKnownDevicesToServer();
  }
}

// ─── 状态 ────────────────────────────────────────────────────────────────────
let knownDevices = [];
let autoRefreshTimer = null;

// ─── UI 工具 ─────────────────────────────────────────────────────────────────
function setStatus(text, isError = false) {
  statusOutput.textContent = text;
  statusOutput.dataset.state = isError ? 'error' : 'ok';
  statusOutput.scrollTop = 0;
}

function setConnectionUI(connected, error) {
  const label = connected ? '已连接' : (error ? `断开 (${error})` : '未连接');
  connectionStatus.textContent = `MQTT：${label}`;
  connectionDot.dataset.state = connected ? 'connected' : 'disconnected';
}

function getSelectedDeviceId() {
  return deviceSelect.value || null;
}

function setControlsDisabled(disabled) {
  [temperatureInput, setTempBtn, pumpSpeedInput, setPumpSpeedBtn].forEach((element) => {
    element.disabled = disabled;
  });
}

function updateSliderFill(value) {
  const min = Number(pumpSpeedInput.min) || 0;
  const max = Number(pumpSpeedInput.max) || 10;
  const percent = max > min ? ((value - min) / (max - min)) * 100 : 0;
  pumpSpeedInput.style.background = `linear-gradient(90deg, var(--primary) ${percent}%, #cbd5e1 ${percent}%)`;
}

function updateMetricValue(element, nextValue) {
  const nextText = String(nextValue);
  if (element.textContent === nextText) return;

  element.textContent = nextText;
  element.classList.remove('value-updated');
  void element.offsetWidth;
  element.classList.add('value-updated');
}

function applyOptimisticMetricPatch(patch) {
  const previous = {
    setTemp: currentSetTemp.textContent,
    pumpSpeed: currentPumpSpeed.textContent,
  };

  if (Object.prototype.hasOwnProperty.call(patch, 'set_temp')) {
    updateMetricValue(currentSetTemp, patch.set_temp);
  }

  if (Object.prototype.hasOwnProperty.call(patch, 'wind_speed_set')) {
    updateMetricValue(currentPumpSpeed, patch.wind_speed_set);
  }

  return () => {
    if (Object.prototype.hasOwnProperty.call(patch, 'set_temp')) {
      updateMetricValue(currentSetTemp, previous.setTemp);
    }

    if (Object.prototype.hasOwnProperty.call(patch, 'wind_speed_set')) {
      updateMetricValue(currentPumpSpeed, previous.pumpSpeed);
    }
  };
}

function syncInputs(payload) {
  if (!payload || typeof payload !== 'object') return;

  const temperature = Number(payload.set_temp);
  if (Number.isFinite(temperature)) {
    temperatureInput.value = temperature;
    temperatureInput.disabled = false;
    setTempBtn.disabled = false;
  }

  const speed = Number(payload.wind_speed_set);
  if (Number.isFinite(speed)) {
    pumpSpeedInput.value = speed;
    pumpSpeedValue.textContent = speed;
    pumpSpeedInput.disabled = false;
    setPumpSpeedBtn.disabled = false;
    updateSliderFill(speed);
  }
}

function updateMonitorDisplay(payload) {
  if (!payload || typeof payload !== 'object') return;

  const sjTemp = Number(payload.sj_temp);
  if (Number.isFinite(sjTemp)) updateMetricValue(currentTemp, sjTemp.toFixed(2));

  const setTemp = Number(payload.set_temp);
  if (Number.isFinite(setTemp)) updateMetricValue(currentSetTemp, setTemp);

  const pumpSpeed = Number(payload.wind_speed_set);
  if (Number.isFinite(pumpSpeed)) updateMetricValue(currentPumpSpeed, pumpSpeed);
}

function updateDeviceList() {
  const previous = deviceSelect.value;
  deviceSelect.innerHTML = '';

  if (!knownDevices.length) {
    deviceSelect.add(new Option('请选择设备', ''));
    deviceCount.textContent = '设备数量：0';
    return;
  }

  knownDevices.forEach(({ deviceId }) => deviceSelect.add(new Option(deviceId, deviceId)));
  deviceSelect.value = knownDevices.some((entry) => entry.deviceId === previous)
    ? previous
    : knownDevices[0].deviceId;
  deviceCount.textContent = `设备数量：${knownDevices.length}`;
}

// ─── Loading 包装 ────────────────────────────────────────────────────────────
function withLoading(button, action) {
  return async (...args) => {
    const label = button.textContent;
    const wasDisabled = button.disabled;
    button.disabled = true;
    button.textContent = '请稍候…';

    try {
      await action(...args);
    } finally {
      button.textContent = label;
      button.disabled = wasDisabled;
    }
  };
}

// ─── API ─────────────────────────────────────────────────────────────────────
async function requestJson(url, options = {}) {
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), API_TIMEOUT_MS);

  try {
    const response = await fetch(url, {
      ...options,
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        ...(options.headers || {}),
      },
    });

    const contentType = response.headers.get('content-type') || '';
    const json = contentType.includes('application/json') ? await response.json() : null;

    if (!response.ok) {
      throw new Error(json?.error || `请求失败 (${response.status})`);
    }

    return json;
  } catch (err) {
    if (err?.name === 'AbortError') {
      throw new Error('请求超时');
    }

    throw err;
  } finally {
    clearTimeout(timeoutId);
  }
}

async function fetchDeviceList() {
  try {
    const json = await requestJson('/api/devices');
    setConnectionUI(json.connected);
    updateDeviceList();
    return json;
  } catch (err) {
    setConnectionUI(false, err.message);
    setStatus(`获取设备列表失败: ${err.message}`, true);
    updateDeviceList();
    return null;
  }
}

async function sendCommand(action, value = null, deviceId = null, showStatus = true) {
  const id = deviceId || getSelectedDeviceId();
  if (!id) {
    setStatus('请先选择或添加一个设备。', true);
    return null;
  }

  try {
    const json = await requestJson('/api/control', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId: id, action, value }),
    });

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
  if (!deviceId) {
    setStatus('请先选择或添加一个设备。', true);
    return;
  }

  if (!silent) setStatus('正在请求设备状态…');
  await sendCommand('get_data', null, deviceId, false);
  await new Promise((resolve) => setTimeout(resolve, 600));

  try {
    const json = await requestJson('/api/status');
    setConnectionUI(json.connected, json.lastError);

    const device = json.devices?.find((entry) => entry.deviceId === deviceId);
    if (!device) {
      setStatus('设备未返回数据，请确认设备已上线。', true);
      return;
    }

    setStatus(JSON.stringify(device, null, 2));
    updateMonitorDisplay(device.payload);
    if (!silent) syncInputs(device.payload);
  } catch (err) {
    setStatus(`刷新失败: ${err.message}`, true);
  }
}

// ─── 自动刷新 ─────────────────────────────────────────────────────────────────
const AUTO_REFRESH_MS = 5_000;

function startAutoRefresh() {
  stopAutoRefresh();
  autoRefreshTimer = setInterval(() => {
    if (getSelectedDeviceId()) {
      void refreshStatus(true);
    } else {
      void fetchDeviceList();
    }
  }, AUTO_REFRESH_MS);
}

function stopAutoRefresh() {
  if (autoRefreshTimer) {
    clearInterval(autoRefreshTimer);
    autoRefreshTimer = null;
  }
}

// ─── 设备管理 ─────────────────────────────────────────────────────────────────
function addKnownDevice(id) {
  if (!id || knownDevices.some((entry) => entry.deviceId === id)) return false;
  knownDevices.push({ deviceId: id });
  saveKnownDevices();
  return true;
}

async function addDeviceAndSync(id) {
  addKnownDevice(id);
  updateDeviceList();
  deviceSelect.value = id;
  setStatus(`已添加 ${id}，正在查询状态…`);
  await sendCommand('get_data', null, id, false);
  await new Promise((resolve) => setTimeout(resolve, 500));
  await refreshStatus();
}

function deleteSelectedDevice() {
  const id = getSelectedDeviceId();
  if (!id) {
    setStatus('请选择一个设备后再删除。', true);
    return;
  }

  if (!confirm(`确定要删除设备 "${id}" 吗？`)) return;

  knownDevices = knownDevices.filter((entry) => entry.deviceId !== id);
  saveKnownDevices();
  updateDeviceList();
  setStatus(`已删除 ${id}`);
  setControlsDisabled(true);
}

// ─── 事件 ────────────────────────────────────────────────────────────────────
deleteDeviceBtn.addEventListener('click', deleteSelectedDevice);

addDeviceBtn.addEventListener('click', withLoading(addDeviceBtn, async () => {
  const id = deviceIdInput.value.trim();
  if (!id) {
    setStatus('请输入设备ID后再添加。', true);
    return;
  }

  await addDeviceAndSync(id);
  deviceIdInput.value = '';
}));

startBtn.addEventListener('click', withLoading(startBtn, () => sendCommand('start')));
stopBtn.addEventListener('click', withLoading(stopBtn, () => sendCommand('stop')));

setTempBtn.addEventListener('click', withLoading(setTempBtn, async () => {
  const value = Number(temperatureInput.value);
  if (!Number.isFinite(value) || value < -20 || value > 50) {
    setStatus('温度须在 -20 ~ 50 ℃ 范围内', true);
    return null;
  }

  const rollback = applyOptimisticMetricPatch({ set_temp: value });
  const result = await sendCommand('setTemperature', value);
  if (!result) rollback();
  return result;
}));

setPumpSpeedBtn.addEventListener('click', withLoading(setPumpSpeedBtn, async () => {
  const value = Number(pumpSpeedInput.value);
  const rollback = applyOptimisticMetricPatch({ wind_speed_set: value });
  const result = await sendCommand('setWindSpeed', value);
  if (!result) rollback();
  return result;
}));

refreshBtn.addEventListener('click', withLoading(refreshBtn, () => refreshStatus()));

pumpSpeedInput.addEventListener('input', () => {
  pumpSpeedValue.textContent = pumpSpeedInput.value;
  updateSliderFill(Number(pumpSpeedInput.value));
});

deviceIdInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') addDeviceBtn.click();
});

temperatureInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') setTempBtn.click();
});

deviceSelect.addEventListener('change', () => {
  setControlsDisabled(true);
  if (getSelectedDeviceId()) void refreshStatus();
});

// ─── 初始化 ──────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', async () => {
  setControlsDisabled(true);
  temperatureInput.value = '';
  pumpSpeedInput.value = '';
  pumpSpeedValue.textContent = '0';
  updateSliderFill(0);

  await hydrateKnownDevices();
  updateDeviceList();
  await fetchDeviceList();

  if (knownDevices.length) {
    knownDevices.forEach(({ deviceId }) => {
      void sendCommand('get_data', null, deviceId, false);
    });
    await new Promise((resolve) => setTimeout(resolve, 300));
    await refreshStatus();
  }

  startAutoRefresh();
});
