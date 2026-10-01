# 冷却控制台

用于压缩机 / 冷水机的本地控制台，使用 C#、.NET 10、MQTT 和 WebView2。支持 Windows 单文件桌面程序，也可从公开源码运行 Web 服务。

**[下载 Windows x64 单文件 exe](https://github.com/glftbcj/compressor-control-panel/releases/latest/download/hvacr.exe)** · [发布记录](https://github.com/glftbcj/compressor-control-panel/releases) · [使用说明](docs/使用说明.md) · [安全边界](docs/安全边界.md) · [验证记录](docs/验证记录.md)

## 桌面版

下载 `hvacr.exe` 后直接双击，无需安装 .NET SDK 或 Node.js。界面需要本机 Microsoft Edge WebView2 Runtime；缺少时程序会提示。exe 包含程序、.NET 运行时和前端，无需旁置 DLL、settings.json 或启动脚本。

公开版不包含任何个人连接凭据或设备 ID。首次运行填写 MQTT 服务器、账号、密码并保存，之后直接打开；也可选择离线预览。已有本机配置可直接复用，旧 exe 旁的配置会迁移到用户数据目录。连接信息、设备记录和外观统一保存到 `%APPDATA%\HVACR`，移动 exe 不影响它们。

默认只读。添加自己的设备 ID 后，打开软件立即查询所选设备状态；通信连接恢复后也立即查询。在「设备控制」点击「启用控制」即可调整，无需重启或第二个确认窗口。关闭软件不会停止设备。

## 2.0.0 的变化

- 简化为单页控制台，删除侧栏；设备选择、管理和外观设置集中在顶部。
- 浅色、暗色、跟随系统，自定义主颜色；外观持久化，与设备 ID 保存在同一目录。
- 温度可直接输入小数目标或使用 ± 按钮，水泵使用 1–10 挡滑块；点击应用直接发送，编辑和拖动不发送。
- 正常使用没有单次 1℃ / 一个挡位限制，也没有固定调整间隔。相关回读确认后可继续调整；压缩机启停保留确认。
- 打开即查询、发送后立即查询；通过本地实时流更新状态，保持每秒查询。应用目标立即显示，回读状态单独标明。
- 修复同一水温回报的「几秒前更新」倒退，以及后续不同云端回报覆盖本机设定的问题。
- 默认只读、仅本机访问、精确设备订阅、指令白名单、数值与基准校验、去重和回读确认。

本次程序运行中下发的温度 / 水泵目标会单独保留，刷新页面或调整另一项不会丢失。云端后来回报不同值时，目标和输入框保持原值，并显示具体差异；实际水温和原始回报继续更新，不会自动重新下发。重启程序后初始设定来自云端回报。

## 从源码运行

需要 .NET 10 SDK：

```powershell
dotnet run --project src/Hvacr.Server
```

默认访问 `http://127.0.0.1:3000`。从源码运行时，用 `settings.example.json` 的格式填写本地配置，或设置 MQTT 环境变量。公开源码不包含默认密码。

离线预览不构造 MQTT 客户端：

```powershell
$env:HVACR_SIMULATION = 'true'
$env:HVACR_READ_ONLY = 'false'
$env:HVACR_DATA_DIR = "$PWD\.test-data\demo"
dotnet run --project src/Hvacr.Server
```

桌面版也可运行 `hvacr.exe --simulate`，模拟数据独立保存于 `%APPDATA%\HVACR\demo`。`--control` 指定本次启动即启用控制；`--read-only` 指定只读。

## 配置和数据

环境变量优先，其次 `HVACR_SETTINGS` 指定的 JSON。桌面版默认优先使用数据目录的 settings.json，缺少时迁移旧 exe 旁文件；首次连接界面保存到数据目录。Web 服务保留 exe 旁配置优先的兼容行为。已有配置不会被自动覆盖。

```json
{
  "mqttBroker": "mqtt://your-broker:1883",
  "mqttUser": "your-user",
  "mqttPass": "your-password",
  "readOnly": true
}
```

| 文件 | 用途 |
|---|---|
| settings.json | 连接信息和启动模式 |
| devices.json | 已保存设备 ID 与名称 |
| appearance.json | 主题和主颜色 |
| webview2/ | 桌面浏览器用户数据 |

这些文件在用户数据目录自动生成，不需要与 exe 一起分发。配置没有加密，不应上传或分享自己的配置、设备记录和包含凭据的个人 exe。

| 环境变量 | 默认值 | 说明 |
|---|---|---|
| HOST | 127.0.0.1 | IP 或 localhost；局域网监听需访问密钥 |
| PORT | 3000 | 1–65535；桌面版自动选择本机端口 |
| HVACR_READ_ONLY | true | 启动模式；界面切换仅本次运行有效 |
| HVACR_SIMULATION | false | 离线模拟 |
| HVACR_DATA_DIR | %APPDATA%\HVACR | 配置、设备、外观和桌面用户数据目录 |
| HVACR_SETTINGS | 自动选择 | 本地配置文件路径 |
| HVACR_ACCESS_TOKEN | 空 | 局域网必需，至少 32 字符；配置后本机 API 也需认证 |
| MQTT_BROKER | mqtt://www.cndq.xyz:1883 | mqtt / mqtts；后者校验证书 |
| MQTT_USER / MQTT_PASS | 空 | MQTT 连接凭据 |

不支持 CMD_SUFFIX 或 SUB_TOPIC 覆盖，控制主题与设备订阅范围固定。

## 控制与权限

写入仅针对已保存设备。温度范围 −20–50℃，水泵为 1–10 的整数挡位。控制须带唯一 commandId 与 expectedValue，并使用相关字段最近 30 秒的非 retained 回读作为基准。其他字段更新不延长该字段的有效期。

发送成功后等待新回读，不修改原始遥测；重复指令去重，待确认或不确定时阻止继续控制，不自动重发。断线退避重连，控制不排队，只订阅已保存设备的精确主题。

本地 API 拒绝跨站请求和不合法 Host。写入要求会话令牌及 JSON；配置访问密钥时所有 API 需要 Bearer 认证。厂商共享认证和设备 ACL **没有通过客户端修复**；第三方仍可能绕过本应用直接向云端发布。协议没有可验证的设备签名或指令确认 ID，回读仅表示观察到目标值。详见 [安全边界](docs/安全边界.md)。

## 构建与验证

```powershell
dotnet build Hvacr.slnx -c Release
dotnet run --project tests/Hvacr.Tests -c Release
npm ci
npx playwright install chromium
npm run test:ui
```

Node.js 只用于浏览器测试。Windows 可使用 `scripts/verify.ps1 -Browser`；`HVACR_DOTNET` 指定 SDK，`HVACR_NUGET_SOURCE` 指定包源，`HVACR_BROWSER` 指定已安装的 Chrome / Chromium。

桌面宿主按项目约定保存在本地独立 Git 仓库，未包含在公开源码包中；公开解决方案可独立构建后端和测试。具有本地 desktop 源码时：

```powershell
# 公开发布包：明确排除所有私有内嵌配置
.\scripts\build-desktop.ps1 -PublicRelease
# 本地个人包：可内嵌自己的默认配置，仅供本人使用
.\scripts\build-desktop.ps1 -PrivateSettingsPath '.local\settings.json'
```

公开包输出到 `artifacts/github-release/hvacr.exe`，个人包到 `artifacts/release/hvacr.exe`。`-PublicRelease` 不读取个人配置，也不覆盖个人交付目录。Release 附件另提供 SHA256SUMS.txt。

## HTTP API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /api/config | 模式、阈值、会话令牌 |
| PUT | /api/mode | 本次运行只读 / 控制模式；启用须 confirmEnable=true |
| GET | /api/health | 进程健康 |
| GET | /api/status | 遥测、连接、指令回读和服务器时间 |
| GET | /api/events | 受相同访问保护的同源实时状态流 |
| GET | /api/devices | 兼容设备状态列表 |
| GET / PUT | /api/preferences/devices | 服务端权威设备记录 |
| GET / PUT | /api/preferences/appearance | 文件保存的主题与主颜色 |
| POST | /api/control | 查询或控制 |

先 GET /api/config 获取 sessionToken；所有 POST / PUT 带 X-Hvacr-Session。配置密钥时另带 Authorization: Bearer。允许动作：get_data、start、stop、setTemperature、setWindSpeed；启停须 confirmPower=true。

```json
{
  "deviceId": "your-device",
  "action": "setTemperature",
  "value": 24,
  "expectedValue": 23,
  "commandId": "099f90a8-b218-4cc7-ad32-883da77155bf"
}
```

202 表示已发送待回读；409 表示基准变化、过期、忙碌或上一条未确认；503 表示通信不可用；504 的 uncertain 表示发送结果不确定。不要自动重试 uncertain 写入，先查询状态。

## 目录

- src/Hvacr.App：访问保护、配置持久化、设备记录、通信、指令协调和内嵌 UI。
- src/Hvacr.Server：Web 入口。
- public：无外部字体、脚本和图片依赖的 UI。
- tests：后端、HTTP 集成和浏览器检查。
- docs：使用说明、安全边界和验证记录。
- desktop：忽略的本地私有宿主，独立 Git 保存。

自动验证使用离线模拟器或假传输。实机开发测试遵守每次最多 1℃ / 一个挡位的用户授权范围，不执行实机启停或更改设备 MQTT 配置。真实响应速度、云端 ACL 和长期散热表现不在离线验证范围内。
