# 压缩机局域网控制面板

基于 Node.js + MQTT 的冷水机/压缩机本地网页控制系统，局域网运行，无需公网。

## 快速开始

```bash
npm install
npm start
```

浏览器打开 `http://localhost:3000`

开发模式（自动重启）：`npm run dev`

## 目录

```
server.js           Express 后端 + MQTT
public/
  index.html        页面
  styles.css        样式
  app.js            前端逻辑
```

## 功能

| 功能 | 说明 |
|------|------|
| 设备管理 | 手动输入设备ID 添加/删除，localStorage 持久化 |
| 启动 / 停止 | 下发 `power: true / false` |
| 设定温度 | -20 ~ 50℃，步进 1℃，回车或点击发送 |
| 水泵挡位 | 滑块 0–10 档，实时进度条 |
| 自动刷新 | 每 15s 静默拉取设备状态，不打断操作 |
| 连接指示 | 绿点/红点实时显示 MQTT 连接状态 |
| 乐观更新 | 设温/设挡位后立即更新本地数据 |

## MQTT 协议

**设备上报** `{deviceId}/bxkt/esp`

| 字段 | 类型 | 说明 |
|------|------|------|
| power | bool | 运行状态 |
| set_temp | number | 设定温度 ℃ |
| sj_temp | number | 实际温度 ℃ |
| ln_temp | number | 液管温度 ℃ |
| zf_temp | number | 蒸发温度 ℃ |
| wind_speed_set | number | 水泵挡位 0–10 |
| pump_switch | bool | 水泵开关 |
| voltage | number | 电压 V |
| run_fz | number | 运行频率 Hz |
| fault_codes | bool/string | 故障码 |
| version | number | 固件版本 |

**控制指令** `{deviceId}/app`

| action | payload | 说明 |
|--------|---------|------|
| get_data | `{"get_data":1}` | 请求上报状态 |
| start | `{"power":true}` | 启动 |
| stop | `{"power":false}` | 停止 |
| setTemperature | `{"set_temp":N}` | 设温 -20~50℃ |
| setWindSpeed | `{"wind_speed_set":N}` | 设挡位 0–10 |

## API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /api/devices | 设备列表 + 连接状态 |
| GET | /api/status | 设备列表 + 连接状态 + lastError |
| GET | /api/health | 健康检查 |
| POST | /api/control | 下发控制指令 |

```jsonc
// POST /api/control
{ "deviceId": "s6a34e3eccnd", "action": "setTemperature", "value": -5 }
```

## 环境变量

| 变量 | 默认值 |
|------|--------|
| MQTT_BROKER | mqtt://www.cndq.xyz:1883 |
| MQTT_USER | cndq_bxkt |
| MQTT_PASS | 08210012Abc |
| CMD_SUFFIX | /app |
| SUB_TOPIC | +/bxkt/# |
| PORT | 3000 |
