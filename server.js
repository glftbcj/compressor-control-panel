const express = require('express');
const mqtt = require('mqtt');
const path = require('path');

// ─── 配置 ────────────────────────────────────────────────────────────────────
const MQTT_BROKER  = process.env.MQTT_BROKER  || 'mqtt://www.cndq.xyz:1883';
const MQTT_USER    = process.env.MQTT_USER    || 'cndq_bxkt';
const MQTT_PASS    = process.env.MQTT_PASS    || '08210012Abc';
const CMD_SUFFIX   = process.env.CMD_SUFFIX   || '/app';
const SUB_TOPIC    = process.env.SUB_TOPIC    || '+/bxkt/#';
const PORT         = Number(process.env.PORT) || 3000;

// ─── Express ─────────────────────────────────────────────────────────────────
const app = express();
app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

// ─── 设备存储 ─────────────────────────────────────────────────────────────────
const devices = new Map();
let mqttState = { connected: false, lastError: null };

setInterval(() => {
  const cutoff = Date.now() - 30 * 60_000;
  for (const [id, d] of devices) if (d.lastSeen < cutoff) devices.delete(id);
}, 5 * 60_000);

function deviceList() {
  return [...devices.values()].map(({ deviceId, lastSeen, topic, payload }) => ({
    deviceId, lastSeen, topic, payload,
  }));
}

function buildPayload(action, value) {
  switch (action) {
    case 'get_data':       return { get_data: 1 };
    case 'start':          return { power: true };
    case 'stop':           return { power: false };
    case 'setTemperature': return { set_temp: Number(value) };
    case 'setWindSpeed':   return { wind_speed_set: Number(value) };
    default:               return { [action]: value ?? 1 };
  }
}

// ─── MQTT ────────────────────────────────────────────────────────────────────
const client = mqtt.connect(MQTT_BROKER, {
  username: MQTT_USER,
  password: MQTT_PASS,
  reconnectPeriod: 5000,
  connectTimeout: 15000,
});

client.on('connect', () => {
  console.log('[MQTT] Connected:', MQTT_BROKER);
  mqttState = { connected: true, lastError: null };
  client.subscribe(SUB_TOPIC, { qos: 1 }, (err, granted) => {
    if (err) {
      console.error('[MQTT] Subscribe error:', err.message);
      mqttState = { connected: false, lastError: err.message };
    } else {
      console.log('[MQTT] Subscribed:', granted.map(g => g.topic).join(', '));
    }
  });
});

client.on('reconnect', () => console.log('[MQTT] Reconnecting...'));
client.on('close',     () => { mqttState = { connected: false, lastError: 'Connection closed' }; });
client.on('offline',   () => { mqttState = { connected: false, lastError: 'Client offline' }; });
client.on('error', (err) => {
  console.error('[MQTT] Error:', err.message);
  mqttState = { connected: false, lastError: err.message };
});

client.on('message', (topic, msg) => {
  if (topic.endsWith(CMD_SUFFIX)) return;
  let payload;
  try { payload = JSON.parse(msg.toString()); } catch { payload = msg.toString(); }
  const deviceId = topic.split('/')[0] || 'unknown';
  devices.set(deviceId, { deviceId, topic, payload, lastSeen: Date.now() });
});

// ─── 路由 ────────────────────────────────────────────────────────────────────
app.get('/api/health',  (_, res) => res.json({ ok: true, uptime: process.uptime() }));
app.get('/api/status',  (_, res) => res.json({ ...mqttState, devices: deviceList() }));
app.get('/api/devices', (_, res) => res.json({ connected: mqttState.connected, devices: deviceList() }));

app.post('/api/control', (req, res) => {
  const { deviceId, action, value } = req.body;
  if (!deviceId) return res.status(400).json({ error: 'Missing deviceId' });
  if (!action)   return res.status(400).json({ error: 'Missing action' });

  const payload = buildPayload(action, value);
  const topic   = `${deviceId}${CMD_SUFFIX}`;

  client.publish(topic, JSON.stringify(payload), { qos: 1 }, (err) => {
    if (err) return res.status(500).json({ error: 'Publish failed' });

    const dev = devices.get(deviceId);
    if (dev?.payload && typeof dev.payload === 'object') {
      if (action === 'setTemperature') dev.payload = { ...dev.payload, set_temp: Number(value) };
      if (action === 'setWindSpeed')   dev.payload = { ...dev.payload, wind_speed_set: Number(value) };
    }

    res.json({ success: true, sent: { topic, payload } });
  });
});

app.get(/^(?!\/api\/).*$/, (_, res) => res.sendFile(path.join(__dirname, 'public', 'index.html')));

// ─── 启动 ────────────────────────────────────────────────────────────────────
app.listen(PORT, () => console.log(`[Server] http://localhost:${PORT}`));