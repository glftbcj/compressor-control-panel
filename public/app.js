'use strict';

const $ = (id) => document.getElementById(id);
const controls = [...document.querySelectorAll('[data-control]')];
const STORAGE_KEY = 'knownDevices';
const SELECTED_KEY = 'hvacrSelectedDevice';
const MIGRATED_KEY = 'hvacrDevicesMigratedV2';
const POLL_MS = 1_000;
let config = null;
let accessKey = '';
let knownDevices = [];
let selectedId = '';
let generation = 0;
let connected = false;
let statusAt = 0;
let serverTime = Date.now();
let serverTimeAt = performance.now();
let latestRevision = -1;
let streamAbort = null;
let streamRetryTimer = null;
let streamReady = false;
const queryRequests = new Map();
const queryTimes = new Map();
let refreshPromise = null;
let queuedQuery = false;
let pollTimer = null;
let draft = null;
let sending = false;
let modeChanging = false;
let temperatureDirty = false;
let pumpDirty = false;
let toastTimer = null;
const snapshots = new Map();
const commands = new Map();
const setpoints = new Map();
const samples = new Map();
const submitted = new Set();
const notices = new Map();
const waterAges = new Map();

function readStorage(key, fallback = null) {
  try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
}
function writeStorage(key, value) {
  try { localStorage.setItem(key, value); } catch { /* Server preferences remain authoritative. */ }
}
function validId(id) { return typeof id === 'string' && /^[A-Za-z0-9_-]{1,64}$/.test(id); }
function number(value) {
  if (value === null || value === undefined || typeof value === 'boolean' || (typeof value === 'string' && !value.trim())) return null;
  const result = Number(value);
  return Number.isFinite(result) ? result : null;
}
function boolean(value) {
  if (value === true || value === 1 || value === '1') return true;
  if (value === false || value === 0 || value === '0') return false;
  return null;
}
function format(value, digits = 1) {
  const n = number(value);
  return n === null ? '—' : n.toLocaleString('zh-CN', { maximumFractionDigits: digits, minimumFractionDigits: digits });
}
function serverNow() { return serverTime + performance.now() - serverTimeAt; }
function fieldFresh(device, field) {
  const time = device?.fieldSeenAt?.[field];
  const age = serverNow() - time;
  return Number.isFinite(time) && age >= -1000 && age <= (config?.telemetryMaxAgeMs ?? 30_000);
}
function showToast(message, isError = false) {
  $('toast').textContent = message;
  $('toast').dataset.state = isError ? 'error' : 'ok';
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, isError ? 7_000 : 4_000);
}
function openDialog(id) { if (!$(id).open) $(id).showModal(); }
function apiHeaders(extra = {}) {
  return { ...(accessKey ? { Authorization: 'Bearer ' + accessKey } : {}),
    ...(config?.sessionToken ? { 'X-Hvacr-Session': config.sessionToken } : {}), ...extra };
}

async function requestJson(url, options = {}) {
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), 12_000);
  try {
    const response = await fetch(url, {
      ...options, signal: abort.signal, cache: 'no-store', credentials: 'same-origin',
      headers: apiHeaders({ Accept: 'application/json', ...(options.headers || {}) })
    });
    const json = (response.headers.get('content-type') || '').includes('application/json') ? await response.json() : null;
    if (!response.ok) {
      const error = new Error(json?.error || json?.message || '请求失败 (' + response.status + ')');
      error.status = response.status;
      error.state = json?.state;
      error.commandId = json?.commandId;
      throw error;
    }
    if (json === null) throw new Error('服务返回格式无效，请重新打开面板。');
    return json;
  } catch (error) {
    if (error.name === 'AbortError') throw new Error('请求超时；控制指令不会自动重发，请刷新回读。');
    throw error;
  } finally { clearTimeout(timer); }
}
function postControl(body) {
  return requestJson('/api/control', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
}
async function savePreferences(next) {
  const response = await requestJson('/api/preferences/devices', {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ devices: next })
  });
  knownDevices = response.devices;
  writeStorage(STORAGE_KEY, JSON.stringify(knownDevices));
  renderDevices();
}
function renderDevices() {
  $('deviceSelect').replaceChildren();
  if (!knownDevices.length) $('deviceSelect').add(new Option('尚未添加设备', ''));
  for (const item of knownDevices) $('deviceSelect').add(new Option(item.deviceId, item.deviceId));
  $('deviceSelect').value = knownDevices.some(d => d.deviceId === selectedId) ? selectedId : (knownDevices[0]?.deviceId || '');
  $('deviceCount').textContent = String(knownDevices.length);
  $('deleteDeviceBtn').disabled = !knownDevices.length || sending;
}
function selectDevice(id) {
  selectedId = id;
  generation++;
  draft = null;
  temperatureDirty = false;
  pumpDirty = false;
  if ($('confirmDialog').open) $('confirmDialog').close();
  writeStorage(SELECTED_KEY, id);
  $('deviceSelect').value = id;
  renderDevice();
  void refresh({ query: true });
}
function setConnection(value, error = '') {
  connected = Boolean(value);
  $('connectionDot').dataset.state = connected ? 'connected' : 'disconnected';
  $('connectionLabel').textContent = connected ? '通信已连接' : '通信未连接';
  $('connectionNotice').hidden = connected || !error;
  $('connectionNotice').textContent = error;
}
function recordSamples(devices) {
  for (const device of devices) {
    const temperature = number(device.payload?.sj_temp);
    const at = device.fieldSeenAt?.sj_temp;
    if (temperature === null || !at || device.retained) continue;
    const series = samples.get(device.deviceId) || [];
    if (!series.length || series[series.length - 1].at !== at) series.push({ at, value: temperature });
    while (series.length && series[0].at < serverNow() - 20 * 60_000) series.shift();
    if (series.length > 1500) series.splice(0, series.length - 1500);
    samples.set(device.deviceId, series);
  }
}
function renderChart() {
  const series = (samples.get(selectedId) || []).filter(p => p.at >= serverNow() - 20 * 60_000);
  $('chartEmpty').hidden = series.length > 0;
  $('temperatureChart').toggleAttribute('hidden', series.length === 0);
  $('chartSampleCount').textContent = series.length + ' 个采样';
  const time = (at) => new Date(at).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false });
  $('chartStart').textContent = series.length ? time(series[0].at) : '—';
  $('chartLatest').textContent = series.length ? time(series[series.length - 1].at) : '—';
  if (!series.length) return;
  const low = Math.floor(Math.min(...series.map(p => p.value)) * 2) / 2 - .5;
  const high = Math.ceil(Math.max(...series.map(p => p.value)) * 2) / 2 + .5;
  const begin = series[0].at;
  const end = Math.max(begin + 60_000, series[series.length - 1].at);
  const points = series.map(p => ({ x: 48 + (p.at - begin) / (end - begin) * 536, y: 185 - (p.value - low) / (high - low) * 165 }));
  const path = points.map((p, i) => (i ? 'L' : 'M') + p.x.toFixed(2) + ' ' + p.y.toFixed(2)).join(' ');
  $('chartLine').setAttribute('d', path);
  $('chartArea').setAttribute('d', path + ' L' + points[points.length - 1].x.toFixed(2) + ' 185 L48 185 Z');
  $('chartEnd').setAttribute('cx', points[points.length - 1].x);
  $('chartEnd').setAttribute('cy', points[points.length - 1].y);
  $('chartMax').textContent = high.toFixed(1) + '°';
  $('chartMid').textContent = ((low + high) / 2).toFixed(1) + '°';
  $('chartMin').textContent = low.toFixed(1) + '°';
}
function currentCommand() { return commands.get(selectedId); }
function appliedTarget(field) { return setpoints.get(selectedId)?.find(setting => setting.field === field); }
function displayedSetting(field) {
  return number(appliedTarget(field)?.target ?? snapshots.get(selectedId)?.payload?.[field]);
}
function renderSetpoint(field, labelId, unit) {
  const target = appliedTarget(field);
  const device = snapshots.get(selectedId);
  const reported = number(device?.payload?.[field]);
  const waiting = target && ['pending', 'uncertain', 'timedOut'].includes(target.state);
  const conflict = target && !waiting && reported !== null && reported !== number(target.target);
  const label = $(labelId);
  label.dataset.pending = String(Boolean(waiting));
  label.dataset.conflict = String(Boolean(conflict));
  if (!target) label.textContent = '云端回报值';
  else if (waiting) label.textContent = '本机目标 · 等待回读';
  else if (conflict) label.textContent = '云端回报 ' + format(reported, field === 'set_temp' ? 1 : 0) + unit + ' · 与本机目标不同';
  else if (!fieldFresh(device, field)) label.textContent = '本机目标 · 当前回读已过期';
  else label.textContent = target.state === 'confirmed' ? '本机目标 · 已收到一致回报' : '本机目标 · 尚未确认';
}
function renderMode() {
  $('modeBadge').textContent = !config ? '连接中' : config.readOnly ? '只读监控' : '控制已启用';
  $('modeBadge').dataset.state = config?.readOnly ? 'warning' : 'ok';
  $('modeToggleBtn').textContent = config?.readOnly ? '启用控制' : '切回只读';
  $('modeToggleBtn').disabled = !config || sending || modeChanging;
  $('modeDescription').textContent = config?.readOnly ? '当前仅查看状态，点击右侧即可启用控制。' : '设置目标值，点击应用即可调整。';
}
function renderControls() {
  const device = snapshots.get(selectedId);
  const payload = device?.payload;
  const command = currentCommand();
  const pending = ['pending', 'uncertain', 'timedOut'].includes(command?.state);
  const recentStatus = statusAt && Date.now() - statusAt < 15_000;
  const available = config && connected && recentStatus && !config.readOnly && !sending && !pending;
  const temp = number(payload?.set_temp);
  const pump = number(payload?.wind_speed_set);
  const tempReady = available && fieldFresh(device, 'set_temp') && temp !== null && temp >= -20 && temp <= 50;
  const pumpReady = available && fieldFresh(device, 'wind_speed_set') && pump !== null && Number.isInteger(pump) && pump >= 1 && pump <= 10;
  const shownTemp = displayedSetting('set_temp');
  const shownPump = displayedSetting('wind_speed_set');
  if (!temperatureDirty) $('temperatureInput').value = shownTemp === null ? '' : shownTemp;
  if (!pumpDirty) $('pumpSlider').value = shownPump === null ? 5 : shownPump;
  const targetTemp = number($('temperatureInput').value);
  const targetPump = number($('pumpSlider').value);
  const validTemp = targetTemp !== null && targetTemp >= -20 && targetTemp <= 50;
  $('temperatureInput').disabled = !tempReady;
  $('tempDownBtn').disabled = !tempReady || !validTemp || targetTemp - 1 < -20;
  $('tempUpBtn').disabled = !tempReady || !validTemp || targetTemp + 1 > 50;
  $('applyTempBtn').disabled = !tempReady || !validTemp || targetTemp === temp;
  $('pumpSlider').disabled = !pumpReady;
  $('pumpTarget').textContent = pump === null && !pumpDirty ? '—' : String(targetPump);
  $('pumpSlider').setAttribute('aria-valuetext', targetPump + ' 挡');
  $('applyPumpBtn').disabled = !pumpReady || targetPump === pump || !Number.isInteger(targetPump) || targetPump < 1 || targetPump > 10;
  $('temperatureHelp').textContent = temperatureDirty && !validTemp ? '请输入 −20–50℃ 内的有效温度。' : temperatureDirty && targetTemp !== temp ? '待应用 ' + targetTemp + '℃ · 点击应用即可发送' : '输入目标温度 · −20–50℃';
  $('temperatureHelp').dataset.error = String(temperatureDirty && !validTemp);
  $('pumpHelp').textContent = pumpDirty && targetPump !== pump ? '待应用 ' + targetPump + ' 挡 · 点击应用即可发送' : '拖动选择目标挡位，点击应用即可发送。';
  const powerReady = available && fieldFresh(device, 'power');
  $('startBtn').disabled = !powerReady || boolean(payload?.power) !== false;
  $('stopBtn').disabled = !powerReady || boolean(payload?.power) !== true;
  $('temperatureBaseline').textContent = '云端回报 ' + format(temp, 1) + ' ℃';
  $('pumpBaseline').textContent = '云端回报 ' + format(pump, 0) + ' 挡';
  let hint = '设备回读后即可继续调整，无固定等待间隔。';
  if (!selectedId) hint = '请先添加或选择设备。';
  else if (!config || !connected || !recentStatus) hint = '通信未就绪，控制暂不可用。';
  else if (config.readOnly) hint = '点击上方「启用控制」即可调整。';
  else if (sending) hint = '正在发送，等待设备回读。';
  else if (pending) hint = command.message;
  else if (!tempReady && !pumpReady) hint = '当前设定缺少新鲜回读，请刷新状态。';
  $('controlHint').textContent = hint;
  $('deleteDeviceBtn').disabled = sending || !selectedId;
  const tempCommand = command?.action === 'setTemperature' && pending;
  renderSetpoint('set_temp', 'temperatureReadback', '℃');
  renderSetpoint('wind_speed_set', 'pumpReadback', ' 挡');
  $('applyTempBtn').textContent = sending && tempCommand ? '发送中' : tempCommand ? '回读中' : '应用';
  renderMode();
}
function renderDevice() {
  const device = snapshots.get(selectedId);
  const payload = device?.payload;
  $('selectedDeviceName').textContent = selectedId || '选择你的设备';
  $('currentTemp').textContent = format(payload?.sj_temp, 1);
  $('currentSetTemp').textContent = format(displayedSetting('set_temp'), 1);
  $('currentPumpSpeed').textContent = format(displayedSetting('wind_speed_set'), 0);
  const power = boolean(payload?.power);
  const pump = boolean(payload?.pump_switch);
  $('powerState').textContent = power === null ? '状态未知' : power ? '压缩机运行中' : '压缩机已停止';
  $('pumpState').textContent = pump === null ? '运行状态未知' : pump ? '水泵运行中' : '水泵已关闭';
  for (const [id, field, precision] of [['liquidTemp', 'ln_temp', 1], ['evaporatorTemp', 'zf_temp', 1], ['voltage', 'voltage', 0], ['frequency', 'run_fz', 0]]) {
    $(id).textContent = format(payload?.[field], precision);
    $(id).title = fieldFresh(device, field) ? '新鲜设备回读' : '尚无数据或数据已过期';
  }
  const fault = payload?.fault_codes;
  const noFault = fault === false || fault === 0 || fault === '0' || fault === '';
  $('faultState').textContent = fault === undefined || fault === null ? '故障状态未知' : noFault ? '未报告故障' : '故障：' + String(fault);
  $('faultState').dataset.state = fault === undefined || fault === null ? 'waiting' : noFault ? 'ok' : 'error';
  $('statusOutput').textContent = device ? JSON.stringify(device, null, 2) : '尚未收到设备数据。';
  renderFreshness();
  renderControls();
  renderChart();
}
function renderFreshness() {
  const device = snapshots.get(selectedId);
  const age = device ? Math.max(0, Math.floor((serverNow() - device.lastSeen) / 1000)) : null;
  const fresh = device && !device.stale && !device.retained && age * 1000 <= (config?.telemetryMaxAgeMs || 30_000) && connected;
  $('freshnessBadge').textContent = !device ? '等待回报' : device.retained ? '缓存回报' : fresh ? '实时回读' : '数据已过期';
  $('freshnessBadge').dataset.state = !device ? 'waiting' : fresh ? 'live' : 'stale';
  const waterAt = device?.fieldSeenAt?.sj_temp;
  let waterAge = null;
  if (waterAt) {
    const now = performance.now();
    const previous = waterAges.get(selectedId);
    let ageMs = Math.max(0, serverNow() - waterAt);
    if (previous?.at === waterAt) ageMs = Math.max(ageMs, previous.ageMs + now - previous.measuredAt);
    waterAges.set(selectedId, { at: waterAt, ageMs, measuredAt: now });
    waterAge = Math.floor(ageMs / 1000);
  }
  const waterFresh = connected && fieldFresh(device, 'sj_temp');
  $('waterReadingLabel').textContent = waterAge === null ? '等待水温回读' : waterFresh ? '设备实时回读' : '水温回读已过期';
  $('currentTemp').dataset.stale = !waterFresh;
  $('lastSeenLabel').textContent = waterAge === null ? '尚无实时回报' : waterAge === 0 ? '刚刚更新' : waterAge < 60 ? waterAge + ' 秒前更新' : Math.floor(waterAge / 60) + ' 分钟前更新';
  $('lastSeenLabel').title = waterAge === null ? '等待新的水温回报' : '距水温回报 ' + waterAge + ' 秒';
  $('currentSetTemp').dataset.stale = !fieldFresh(device, 'set_temp');
  $('currentPumpSpeed').dataset.stale = !fieldFresh(device, 'wind_speed_set');
  $('sidebarDeviceState').textContent = !device ? '等待设备回报' : fresh ? '已连接 · 回报正常' : '历史读数 · 等待新回报';
}

function applyStatus(response) {
  if (Number.isFinite(response.stateRevision) && response.stateRevision < latestRevision) return;
  if (response.stateRevision === latestRevision && response.serverTime < serverTime) return;
  latestRevision = response.stateRevision ?? latestRevision;
  serverTime = response.serverTime;
  serverTimeAt = performance.now();
  statusAt = Date.now();
  if (config && typeof response.readOnly === 'boolean') config.readOnly = response.readOnly;
  const wasConnected = connected;
  setConnection(response.connected, response.lastError);
  snapshots.clear();
  for (const device of response.devices || []) snapshots.set(device.deviceId, device);
  recordSamples(response.devices || []);
  for (const entry of response.commands || []) {
    const local = commands.get(entry.deviceId);
    if (local && ['pending', 'uncertain', 'timedOut'].includes(local.state)
        && (!entry.command || (local.commandId !== entry.command.commandId && entry.command.sentAt <= local.sentAt))) continue;
    if (Array.isArray(entry.setpoints)) setpoints.set(entry.deviceId, entry.setpoints);
    if (!entry.command) continue;
    commands.set(entry.deviceId, entry.command);
    const signature = entry.command.commandId + '/' + entry.command.state;
    if (submitted.has(entry.command.commandId) && notices.get(entry.deviceId) !== signature) {
      notices.set(entry.deviceId, signature);
      if (['confirmed', 'notConfirmed', 'timedOut'].includes(entry.command.state)) {
        showToast(entry.command.message, entry.command.state !== 'confirmed');
        if (entry.deviceId === selectedId && entry.command.state === 'confirmed') {
          if (entry.command.action === 'setTemperature') temperatureDirty = false;
          if (entry.command.action === 'setWindSpeed') pumpDirty = false;
        }
      }
    }
  }
  renderDevice();
  if (!wasConnected && connected) void queryDevice({ force: true });
}
async function queryDevice({ force = false, manual = false } = {}) {
  const id = selectedId;
  if (!config || !id) return;
  if (queryRequests.has(id)) {
    if (!force) return queryRequests.get(id);
    await queryRequests.get(id);
    if (id !== selectedId || !config) return;
  }
  const now = performance.now();
  if (!force && now - (queryTimes.get(id) ?? -Infinity) < POLL_MS) return;
  queryTimes.set(id, now);
  const request = postControl({ deviceId: id, action: 'get_data' }).catch(error => {
    if (error.status === 401 || (error.status === 403 && !error.state)) {
      config = null; stopStream();
      if (error.status === 401) openDialog('loginDialog');
    }
    if (manual) showToast(error.message, true);
  }).finally(() => queryRequests.delete(id));
  queryRequests.set(id, request);
  return request;
}
function stopStream() {
  clearTimeout(streamRetryTimer);
  streamAbort?.abort();
  streamAbort = null;
  streamReady = false;
}
async function startStream() {
  if (!config || document.hidden || streamAbort) return;
  const abort = new AbortController();
  streamAbort = abort;
  try {
    const response = await fetch('/api/events', { headers: apiHeaders({ Accept: 'text/event-stream' }), signal: abort.signal, cache: 'no-store', credentials: 'same-origin' });
    if (response.status === 401) { config = null; openDialog('loginDialog'); }
    if (!response.ok || !response.headers.get('content-type')?.startsWith('text/event-stream')) throw new Error('实时连接不可用');
    streamReady = true;
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    while (!abort.signal.aborted) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let end;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const event = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        if (event.startsWith('data: ')) applyStatus(JSON.parse(event.slice(6)));
      }
      if (buffer.length > 2_000_000) throw new Error('实时消息过大');
    }
  } catch (error) {
    if (error.name !== 'AbortError') streamReady = false; // Regular status reads remain available.
  } finally {
    if (streamAbort === abort) {
      streamAbort = null;
      streamReady = false;
      if (config && !document.hidden) streamRetryTimer = setTimeout(() => { void startStream(); }, POLL_MS);
    }
  }
}
async function refresh({ query = false, manual = false } = {}) {
  if (!config) return;
  if (refreshPromise) { queuedQuery ||= query; return refreshPromise; }
  const id = selectedId;
  const version = generation;
  if (query) void queryDevice({ force: manual, manual });
  $('refreshBtn').disabled = true;
  refreshPromise = (async () => {
    try {
      const response = await requestJson('/api/status');
      applyStatus(response);
    } catch (error) {
      setConnection(false, error.message);
      renderControls();
      renderFreshness();
      if (error.status === 401) { config = null; openDialog('loginDialog'); }
      if (manual) showToast(error.message, true);
    }
  })();
  try { await refreshPromise; }
  finally {
    refreshPromise = null;
    $('refreshBtn').disabled = false;
    if (queuedQuery || version !== generation) {
      queuedQuery = false;
      void refresh({ query: true });
    }
  }
}
function schedulePoll() {
  clearTimeout(pollTimer);
  if (!document.hidden) pollTimer = setTimeout(async () => {
    if (!config && !$('loginDialog').open) { await initialize(); }
    else {
      void queryDevice();
      if (!streamReady) await refresh();
    }
    schedulePoll();
  }, POLL_MS);
}
function prepareAdjustment(action, target) {
  if (!config || config.readOnly || sending) return;
  const device = snapshots.get(selectedId);
  const field = action === 'setTemperature' ? 'set_temp' : action === 'setWindSpeed' ? 'wind_speed_set' : 'power';
  if (!device || !fieldFresh(device, field)) { showToast('请先刷新状态。', true); return; }
  const power = ['start', 'stop'].includes(action);
  const current = power ? boolean(device.payload[field]) : number(device.payload[field]);
  const next = power ? undefined : number(target);
  if (!power && (current === null || next === null || next === current ||
      (action === 'setTemperature' ? next < -20 || next > 50 : !Number.isInteger(next) || next < 1 || next > 10))) {
    showToast('请输入有效且不同于当前设定的目标值。', true); return;
  }
  draft = { deviceId: selectedId, action, expectedValue: current, value: next, commandId: crypto.randomUUID(), confirmPower: power };
  if (!power) { const command = draft; draft = null; void sendCommand(command); return; }
  $('confirmTitle').textContent = power ? '确认' + (action === 'stop' ? '停止压缩机' : '启动压缩机') : '确认调整';
  $('confirmDevice').textContent = '设备 · ' + selectedId;
  $('confirmChange').textContent = power ? (current ? '运行中 → 停止' : '已停止 → 启动') : current + ' → ' + next + (field === 'set_temp' ? ' ℃' : ' 挡');
  $('confirmDescription').textContent = power ? '启停会影响散热，请确认运行状态。' : '发送后立即查询设备回读，回读后可继续调整。';
  $('powerConfirmWrap').hidden = !power;
  $('powerConfirmCheck').checked = false;
  $('confirmSendBtn').disabled = power;
  openDialog('confirmDialog');
}

async function initialize() {
  stopStream();
  try {
    config = await requestJson('/api/config');
    latestRevision = -1;
    document.title = config.title;
    renderMode();
    $('simulationBanner').hidden = !config.simulation;
    const response = await requestJson('/api/preferences/devices');
    knownDevices = response.devices || [];
    // One-time migration of v1 data; deletion in the server list cannot resurrect an old list.
    if (!config.simulation && !readStorage(MIGRATED_KEY) && !knownDevices.length) {
      let legacy = [];
      try { legacy = JSON.parse(readStorage(STORAGE_KEY, '[]')); } catch { /* Ignore malformed browser data. */ }
      if (Array.isArray(legacy)) {
        const ids = [...new Set(legacy.map(d => d?.deviceId).filter(validId))].slice(0, 100);
        if (ids.length) await savePreferences(ids.map(deviceId => ({ deviceId })));
      }
    }
    writeStorage(MIGRATED_KEY, 'true');
    writeStorage(STORAGE_KEY, JSON.stringify(knownDevices));
    const old = readStorage(SELECTED_KEY);
    selectedId = knownDevices.some(d => d.deviceId === old) ? old : knownDevices[0]?.deviceId || '';
    generation++;
    renderDevices();
    renderDevice();
    await refresh({ query: true });
    void startStream();
    void window.CoolingAppearance.connect(requestJson, showToast);
  } catch (error) {
    config = null;
    setConnection(false, error.message);
    renderControls();
    if (error.status === 401) openDialog('loginDialog');
  }
}
$('deviceSelect').addEventListener('change', () => selectDevice($('deviceSelect').value));
$('openAddBtn').addEventListener('click', () => { $('addError').textContent = ''; openDialog('addDialog'); });
$('openSecurityBtn').addEventListener('click', () => openDialog('securityDialog'));
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => $(button.dataset.close).close()));
$('confirmDialog').addEventListener('close', () => { draft = null; });
$('powerConfirmCheck').addEventListener('change', () => { $('confirmSendBtn').disabled = !$('powerConfirmCheck').checked; });
function stepTemperature(delta) {
  const current = number($('temperatureInput').value);
  if (current === null) return;
  const decimals = Math.min(15, (String(current).split('.')[1] || '').length);
  $('temperatureInput').value = Number((current + delta).toFixed(decimals));
  temperatureDirty = true;
  renderControls();
}
$('tempDownBtn').addEventListener('click', () => stepTemperature(-1));
$('tempUpBtn').addEventListener('click', () => stepTemperature(1));
$('temperatureInput').addEventListener('input', () => { temperatureDirty = true; renderControls(); });
$('temperatureInput').addEventListener('keydown', event => {
  if (event.key === 'Enter') { event.preventDefault(); if (!$('applyTempBtn').disabled) $('applyTempBtn').click(); }
});
$('pumpSlider').addEventListener('input', () => { pumpDirty = true; renderControls(); });
$('applyTempBtn').addEventListener('click', () => prepareAdjustment('setTemperature', $('temperatureInput').value));
$('applyPumpBtn').addEventListener('click', () => prepareAdjustment('setWindSpeed', $('pumpSlider').value));
$('startBtn').addEventListener('click', () => prepareAdjustment('start'));
$('stopBtn').addEventListener('click', () => prepareAdjustment('stop'));
$('refreshBtn').addEventListener('click', () => { void refresh({ query: true, manual: true }); });
async function setMode(readOnly) {
  if (!config || modeChanging || sending) return;
  modeChanging = true;
  renderMode();
  try {
    const response = await requestJson('/api/mode', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ readOnly, confirmEnable: !readOnly })
    });
    if (config) config.readOnly = response.readOnly;
    if (readOnly) {
      temperatureDirty = false; pumpDirty = false;
      if ($('confirmDialog').open) $('confirmDialog').close();
    }
    showToast(readOnly ? '已切回只读监控。' : '控制已启用，可以设置目标值。');
    await refresh();
  } catch (error) { showToast(error.message, true); }
  finally { modeChanging = false; renderControls(); }
}
$('modeToggleBtn').addEventListener('click', () => {
  if (config) void setMode(!config.readOnly);
});
async function sendCommand(command) {
  if (sending || command.deviceId !== selectedId) return;
  const previous = commands.get(command.deviceId);
  submitted.add(command.commandId);
  sending = true;
  commands.set(command.deviceId, { ...command, sentAt: serverNow(), state: 'pending', target: command.value,
    message: '正在发送，随后立即查询设备回读。' });
  $('confirmSendBtn').disabled = true;
  renderControls();
  try {
    const result = await postControl(command);
    if (commands.get(command.deviceId)?.state !== 'confirmed') commands.set(command.deviceId, {
      ...command, sentAt: serverNow(), state: result.state,
      message: result.message, target: command.value
    });
    if (commands.get(command.deviceId)?.state !== 'confirmed') showToast(result.message);
  } catch (error) {
    // A lost HTTP response may follow a successful publish. Hold controls until status is re-read.
    if (!error.status || error.state === 'uncertain') commands.set(command.deviceId, {
      ...command, sentAt: serverNow(), state: 'uncertain', message: '发送结果不确定，请刷新回读。'
    });
    else if (commands.get(command.deviceId)?.commandId === command.commandId) {
      if (previous) commands.set(command.deviceId, previous); else commands.delete(command.deviceId);
    }
    showToast(error.message, true);
  } finally {
    sending = false;
    if ($('confirmDialog').open) $('confirmDialog').close();
    renderControls();
    void queryDevice({ force: true });
    void refresh();
  }
}
$('confirmForm').addEventListener('submit', event => {
  event.preventDefault();
  if (!draft || sending || draft.deviceId !== selectedId || (draft.confirmPower && !$('powerConfirmCheck').checked)) return;
  void sendCommand({ ...draft });
});

$('addDeviceForm').addEventListener('submit', async event => {
  event.preventDefault();
  const id = $('deviceIdInput').value.trim();
  if (!validId(id)) { $('addError').textContent = '请输入有效设备 ID。'; return; }
  $('addDeviceBtn').disabled = true;
  try {
    if (!knownDevices.some(d => d.deviceId === id)) await savePreferences([...knownDevices, { deviceId: id }]);
    $('addDialog').close();
    $('deviceIdInput').value = '';
    selectDevice(id);
    showToast('设备已保存，正在查询状态。');
  } catch (error) { $('addError').textContent = error.message; }
  finally { $('addDeviceBtn').disabled = false; }
});
$('deleteDeviceBtn').addEventListener('click', async () => {
  if (!selectedId || sending || !confirm('移除设备 ' + selectedId + '？这只删除面板记录，不会关闭设备。')) return;
  const id = selectedId;
  $('deleteDeviceBtn').disabled = true;
  try {
    await savePreferences(knownDevices.filter(d => d.deviceId !== id));
    snapshots.delete(id); commands.delete(id); setpoints.delete(id); samples.delete(id);
    selectDevice(knownDevices[0]?.deviceId || '');
    showToast('已移除设备记录。');
  } catch (error) { showToast(error.message, true); renderControls(); }
});
$('loginForm').addEventListener('submit', async event => {
  event.preventDefault();
  accessKey = $('accessKeyInput').value;
  const button = event.currentTarget.querySelector('button');
  button.disabled = true;
  try {
    config = await requestJson('/api/config');
    $('loginDialog').close();
    $('accessKeyInput').value = '';
    $('loginError').textContent = '';
    await initialize();
    schedulePoll();
  } catch (error) {
    accessKey = '';
    $('loginError').textContent = error.message;
  } finally { button.disabled = false; }
});
$('loginDialog').addEventListener('cancel', event => event.preventDefault());
document.addEventListener('visibilitychange', () => {
  clearTimeout(pollTimer);
  $('pollingLabel').textContent = document.hidden ? '窗口隐藏 · 状态查询已暂停' : '实时回读 · 每秒查询状态';
  if (document.hidden) stopStream();
  else { void refresh({ query: true }); void startStream(); schedulePoll(); }
});
setInterval(() => { renderFreshness(); renderControls(); }, 1000);
void initialize().then(schedulePoll);
