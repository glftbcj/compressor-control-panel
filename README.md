# 压缩机控制面板

面向冷水机 / 压缩机设备的本地控制面板，当前实现基于 .NET 10、MQTT 和 WebView2。

项目现在同时提供两种运行方式：

- Web 版：启动本地 HTTP 服务后，用浏览器访问。
- Windows 桌面版：原生 WinForms 宿主承载 WebView2，发布为单一 exe，运行时只依赖 WebView2 Runtime，不依赖 Edge 浏览器程序本身。

## 当前实现特性

- MQTT 双向通信，控制指令通过 `{deviceId}/app` 下发。
- 页面仍保持现有交互逻辑和 5 秒自动刷新节奏。
- 设备 ID 继续由用户手动输入，但现在会双重持久化：
  - 浏览器 localStorage
  - `%APPDATA%\HVACR\devices.json`
- 桌面端使用 WebView2 Runtime，用户数据目录保存在 `%APPDATA%\HVACR\webview2\`。
- Windows 桌面版以单一 exe 形式分发；GitHub 仓库不公开 `desktop` 目录，桌面端请从 GitHub Releases 下载 `hvacr.exe`。

## 公开仓库结构

```text
src/
  Hvacr.App/        共享后端逻辑（HTTP API、MQTT、静态资源、设备持久化）
  Hvacr.Server/     浏览器模式入口
public/
  index.html        页面结构
  styles.css        样式
  app.js            前端逻辑（UI 保持原交互，内部实现已加固）
```

说明：

- GitHub 公开仓库主要包含 Web 端和共享后端源码。
- Windows 桌面版在 GitHub 上不公开 `desktop` 目录源码，只在 Releases 提供编译后的 `hvacr.exe`。

## 运行方式

### 浏览器端怎么使用

前提条件：

- 源码运行需要已安装 .NET 10 SDK。
- 当前默认 MQTT Broker 为 `mqtt://www.cndq.xyz:1883`，需要本机能访问外网 MQTT 环境。
- 需要提前知道有效设备 ID，例如 ``。
- 只想使用浏览器端时，不要求安装 Node.js；`npm start` 只是对 `dotnet run` 的一层包装。

启动方式：

```bash
dotnet run --project src/Hvacr.Server/Hvacr.Server.csproj
```

或使用根脚本：

```bash
npm start
```

默认访问地址：

```text
http://127.0.0.1:3000
```

同一局域网内的其他设备也可以直接访问：

```text
http://本机局域网IP:3000
```

例如当前这台机器的局域网 IPv4 是 `192.168.50.152`，那么同一局域网里的其他设备应访问：

```text
http://192.168.50.152:3000
```

浏览器端使用步骤：

1. 在项目根目录启动本地服务。
2. 用浏览器打开 `http://127.0.0.1:3000`。
3. 查看页面顶部 MQTT 状态。如果 Broker 可达，会显示“MQTT：已连接”；如果 Broker 不可达，页面仍能打开，但会显示断开状态。
4. 在“设备ID”输入框中手动输入设备 ID，然后点击“添加设备”。
5. 添加后页面会立即请求设备数据，之后继续保持 5 秒自动刷新；也可以手动点击“刷新状态”。
6. 设备 ID 会同时保存到浏览器 localStorage 和 `%APPDATA%\HVACR\devices.json`，下次启动浏览器端或桌面端时会自动恢复。
7. 如果准备让手机、平板或另一台电脑访问本机控制面板，确保它们和这台电脑在同一局域网，并允许 Windows 防火墙放行 TCP 3000 端口。

开发模式：

```bash
npm run dev
```

浏览器端运行中的常见现象：

- 页面可以打开但顶部显示“MQTT：断开 ...”：本地 HTTP 服务正常，但 MQTT Broker 不可达，控制和状态刷新会失败。
- 页面显示“设备未返回数据”：设备 ID 已保存，但设备当前没有上报或尚未上线。
- 重启后设备 ID 仍能自动恢复：这是 localStorage 与 `%APPDATA%\HVACR\devices.json` 双持久化生效的预期行为。

### Windows 桌面版

要求：

- Windows
- 已安装 WebView2 Runtime
- 不要求安装或保留 Edge 浏览器程序

获取方式：

- 进入 GitHub Releases 下载 `hvacr.exe`

如果你在本地私有开发环境中保留了 `desktop` 目录，则仍可从源码调试桌面宿主：

```bash
npm run desktop
```

### 浏览器端与桌面端区别

| 模式 | 启动方式 | 运行时依赖 | 适用场景 |
|------|----------|------------|----------|
| 浏览器端 | `dotnet run` 或 `npm start` 后手动打开浏览器 | .NET 10 SDK、浏览器、可访问 MQTT Broker | 开发调试、局域网或本机操作 |
| Windows 桌面版 | 从 GitHub Releases 下载并运行 `hvacr.exe`；若本地私有环境保留 `desktop` 目录，也可使用 `npm run desktop` | WebView2 Runtime、可访问 MQTT Broker | 分发给 Windows 用户、获得更原生的窗口体验 |

## 构建与打包

如果你只是 GitHub 仓库使用者，而不是本地私有构建环境维护者，那么桌面端不需要自行构建，直接从 GitHub Releases 下载 `hvacr.exe` 即可。

### 构建整个解决方案

```bash
npm run build
```

### 生成单文件桌面 exe（仅适用于本地保留 `desktop` 目录的构建环境）

```bash
cmd /c desktop\build.bat
```

构建结果：

- 最终分发文件：`desktop\hvacr.exe`
- 中间发布文件：`desktop\dist\Hvacr.Desktop.exe`

当前验证结果：

- 发布脚本已成功生成单一 exe。
- 当前 `desktop/dist` 目录只包含一个 exe 文件。
- 当前 `desktop\hvacr.exe` 体积约为 64.3 MB。

## MQTT 协议

### 设备上报主题

```text
{deviceId}/bxkt/esp
```

当前已验证的设备上报字段包括：

| 字段 | 类型 | 说明 |
|------|------|------|
| power | bool | 压缩机运行状态 |
| sj_temp | number | 实际温度 |
| set_temp | number | 设定温度 |
| wind_speed_set | number | 水泵挡位 |
| pump_switch | bool | 水泵开关 |
| ln_temp | number | 液管温度 |
| zf_temp | number | 蒸发温度 |
| voltage | number | 电压 |
| run_fz | number | 运行频率 |
| fault_codes | bool/string | 故障码 |
| version | number | 固件版本 |

### 控制下发主题

```text
{deviceId}/app
```

### 当前控制动作映射

| action | payload |
|--------|---------|
| get_data | `{"get_data":1}` |
| start | `{"power":true}` |
| stop | `{"power":false}` |
| setTemperature | `{"set_temp":N}` |
| setWindSpeed | `{"wind_speed_set":N}` |

## HTTP API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/health` | 健康检查 |
| GET | `/api/devices` | 返回连接状态和设备列表 |
| GET | `/api/status` | 返回连接状态、lastError 和设备列表 |
| POST | `/api/control` | 发送控制指令 |
| GET | `/api/preferences/devices` | 读取设备 ID 持久化列表 |
| PUT | `/api/preferences/devices` | 写入设备 ID 持久化列表 |

示例：

```json
{
  "deviceId": "",
  "action": "setTemperature",
  "value": 20
}
```

## 设备 ID 保存机制

设备 ID 仍然是手动输入，不会擅自写死到 UI 中。

当前保存路径分两层：

1. 浏览器或 WebView2 的 localStorage
2. `%APPDATA%\HVACR\devices.json`

当前已验证：

- `` 已能通过 `/api/preferences/devices` 正常读写。
- `%APPDATA%\HVACR\devices.json` 已成功落盘并可读取。

## 已完成的运行验证

当前这轮代码已完成以下验证：

- 解决方案可成功编译。
- Web 服务可启动并监听 `http://127.0.0.1:3000`。
- 页面、[public/app.js](public/app.js)、[public/styles.css](public/styles.css) 均返回 200。
- `/api/health`、`/api/preferences/devices`、`/api/control` 已顺序验证通过。
- `get_data` 指令已成功下发到设备 ``。
- 真实设备状态回读显示：
  - `power = true`
  - `set_temp = 20`

这表示当前默认测试设备 `` 在本次验证时确实处于开启状态，且设定温度位于 20–24℃ 范围内。

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| HOST | 浏览器端默认 `0.0.0.0`，桌面端固定 `127.0.0.1` | 浏览器端监听地址；设为 `0.0.0.0` 时可被局域网其他设备访问 |
| MQTT_BROKER | `mqtt://www.cndq.xyz:1883` | MQTT 地址 |
| MQTT_USER | `cndq_bxkt` | MQTT 用户名 |
| MQTT_PASS | `08210012Abc` | MQTT 密码 |
| CMD_SUFFIX | `/app` | 控制主题后缀 |
| SUB_TOPIC | `+/bxkt/#` | 订阅主题 |
| PORT | `3000` | 本地 HTTP 端口 |
| HVACR_DATA_DIR | `%APPDATA%\HVACR` | 本地数据目录 |

## 已知限制

- 浏览器端不是纯静态页面，必须先启动本地 HTTP 服务，然后再访问 `http://127.0.0.1:3000`。
- 如果局域网其他设备仍然访问不到 `http://本机局域网IP:3000`，通常不是页面本身问题，而是 Windows 防火墙、路由隔离或不同网段导致的网络阻断。
- GitHub 仓库公开内容不包含 `desktop` 目录；桌面端成品通过 Releases 分发，而不是通过仓库文件树直接下载。
- WebView2 NuGet 包当前会引入一个 `WindowsBase` 版本冲突警告；当前不影响编译、发布和运行，但仍属于构建期噪音。
- 单文件 exe 只消除了分发层面的多文件输出；运行时仍依赖目标机器已安装 WebView2 Runtime。
- 当前已经通过真实遥测确认 `` 处于开启状态且 `set_temp=20`，但更长时间的温控稳定性验证仍建议在现场继续观察。
- 如果 MQTT Broker 不可达，应用仍会启动，但控制与状态刷新会进入断开状态提示。
