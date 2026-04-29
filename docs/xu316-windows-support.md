# XMOS XU316 USB 音频设备 Windows 驱动支持说明

## 简介

XMOS XU316 是一颗广泛用于 USB 音频接口的 SoC，实现了 **USB Audio Class 2.0（UAC2）** 协议。本文档说明在 Windows 系统下使用基于 XU316 的 USB 音频设备时的驱动选项、安装方式和常见问题。

---

## Windows 驱动选项一览

### 选项 A：Windows 内置 UAC2 类驱动（推荐首选）

自 **Windows 10 版本 1703（Creators Update）** 起，微软已内置 USB Audio Class 2.0 驱动，无需单独安装任何驱动。

| 功能 | 支持情况 |
|------|---------|
| 立体声 / 多声道播放 | ✅ 支持 |
| 录音（输入通道） | ✅ 支持 |
| 高采样率（96kHz、192kHz） | ✅ 支持（取决于设备固件） |
| ASIO 低延迟 | ❌ 不支持（需要 ASIO4ALL 或 OEM 驱动） |
| 设备品牌化控制面板 | ❌ 不支持 |
| DSD / DoP | ⚠️ 取决于 Windows 版本和设备枚举方式 |

**如何验证内置驱动是否已生效：**

1. 将基于 XU316 的 USB 音频设备插入 Windows 10/11 电脑的 USB 端口。
2. 打开「设备管理器」（`Win + X` → 设备管理器）。
3. 在「声音、视频和游戏控制器」下应能看到该设备（通常显示设备厂商命名或 "USB Audio Device"）。
4. 如果看到该设备且无感叹号/错误图标，则内置 UAC2 驱动已正常工作。
5. 在「设置 → 系统 → 声音」中，该设备应出现在播放和录制设备列表中。

**Windows 版本要求：**

- Windows 10 1703 或更高版本：✅ 原生 UAC2 支持
- Windows 10 1607 及更早：⚠️ 仅支持 UAC1（48kHz/16-bit）
- Windows 7 / 8.1：❌ 不支持 UAC2，需要第三方驱动

---

### 选项 B：Thesycon TUSBAUDIO（商业 ASIO 驱动）

[Thesycon GmbH](https://www.thesycon.de/eng/usb_audio.shtml) 提供专业级 Windows USB Audio Class 2 驱动，支持 ASIO、低延迟和品牌化安装包。

**重要限制：**

> Thesycon TUSBAUDIO 是 **商业授权软件**，需要设备制造商向 Thesycon 购买 OEM 授权后才能合法分发给终端用户。**本仓库不包含、不重分发 Thesycon 驱动组件。**

如果你的设备供应商基于 XU316 提供了品牌化驱动包（如 Focusrite、MOTU、SSL 等品牌的接口），请从 **设备厂商官方网站** 下载驱动，而不是搜索"XMOS 驱动"。

**如何获取 Thesycon 驱动（设备厂商渠道）：**

1. 前往设备品牌官方网站的支持/下载页面。
2. 按型号和操作系统版本下载驱动安装程序。
3. 以管理员身份运行安装程序，重启后即可使用 ASIO。

---

### 选项 C：ASIO4ALL（通用 ASIO 包装器）

[ASIO4ALL](https://www.asio4all.org/) 是一个免费的通用 ASIO 驱动包装器，可将 Windows WDM 驱动转为 ASIO 接口，适合在没有专用 ASIO 驱动时降低延迟。

**注意：** ASIO4ALL 基于 WDM/KS 层，不能完全发挥 XU316 低延迟能力，适合临时或低要求场景。

---

### 选项 D：WinUSB / 自定义驱动（开发者）

如果你是固件开发者，正在基于 XMOS XU316 开发自定义产品，可以参考：

- [XMOS sw_usb_audio](https://github.com/xmos/sw_usb_audio)：XU316 USB Audio 参考固件（BSD 许可，开源）。
- [XMOS XTC Tools](https://www.xmos.com/xtc-tools/)：用于编译和调试 XU316 固件。
- [XMOS USB Audio User Guide](https://www.xmos.com/file/usb-audio-software-design-guide)：详细设计指南。

对于需要自定义 Windows 驱动（非 UAC2 类驱动）的场景，参考微软文档：
- [开发 USB 设备驱动程序](https://learn.microsoft.com/zh-cn/windows-hardware/drivers/usbcon/)
- [Windows 驱动程序签名要求](https://learn.microsoft.com/zh-cn/windows-hardware/drivers/install/driver-signing)

---

## 设备固件升级（DFU）

XMOS 提供 **Device Firmware Upgrade（DFU）** 工具，可通过 USB 对 XU316 设备进行固件升级。

**工具获取：**
- [XMOS USB Audio DFU 工具](https://www.xmos.com/software/tools/)（登录后可下载）

**升级流程：**
1. 从设备厂商获取新版固件（`.bin` 格式）。
2. 使用 XMOS DFU 工具连接设备并执行升级。
3. 升级完成后设备自动重新枚举。

---

## 本项目引导程序（Xu316Setup.exe）

本仓库提供一个 Windows 引导安装程序 `Xu316Setup.exe`（在 GitHub Releases 中下载），其功能如下：

1. **检测 Windows 版本**，判断内置 UAC2 驱动是否可用。
2. **扫描已连接的 XMOS USB 音频设备**，显示 VID/PID 和驱动状态。
3. **输出诊断信息和建议**，指导用户进行下一步操作。
4. **提供官方资料链接**，方便快速跳转到 XMOS 官网。

> **免责声明：** `Xu316Setup.exe` 是一个 **纯诊断/引导工具**，不安装任何驱动程序。它不包含任何第三方驱动组件，不修改系统注册表驱动相关项，不需要管理员权限（扫描功能可能因权限不足而受限）。

---

## 常见问题（FAQ）

**Q: 插入设备后 Windows 没有识别？**
A: 先检查 USB 线缆和端口是否正常。确认 Windows 版本不低于 10 1703。尝试换一个 USB 3.0 端口（UAC2 在某些旧 USB 2.0 控制器上可能有兼容性问题）。

**Q: 识别了但没有声音？**
A: 在「设置 → 系统 → 声音」中，确认已将默认播放/录制设备切换为该 USB 音频设备。

**Q: 用 DAW 时延迟太高？**
A: Windows 内置 UAC2 驱动不支持 ASIO。需要 OEM 提供的 ASIO 驱动（基于 Thesycon），或临时使用 ASIO4ALL。

**Q: 采样率被限制在 48kHz？**
A: 右键点击系统托盘音频图标 → 声音 → 播放 → 选择设备 → 属性 → 高级，在「默认格式」中选择更高采样率。

**Q: XMOS 官方有没有直接提供 Windows 驱动下载？**
A: XMOS 不直接为终端用户提供统一的 Windows 驱动下载。终端产品驱动应向 **设备厂商** 获取。XMOS 提供的是给 OEM/ODM 用的参考设计和 SDK，不面向普通消费者直接分发驱动。参见 [XMOS USB Audio Driver Support](https://www.xmos.com/en/usb-audio-driver-support/)。

---

## 参考链接

- [XMOS USB Audio Driver Support](https://www.xmos.com/en/usb-audio-driver-support/)
- [XMOS XU316 产品页](https://www.xmos.com/xmos-products/)
- [XMOS sw_usb_audio（GitHub）](https://github.com/xmos/sw_usb_audio)
- [XMOS USB Audio Software Design Guide](https://www.xmos.com/file/usb-audio-software-design-guide)
- [Microsoft USB Audio 2.0 驱动文档](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/usb-2-0-audio-drivers)
- [Thesycon TUSBAUDIO](https://www.thesycon.de/eng/usb_audio.shtml)
- [ASIO4ALL](https://www.asio4all.org/)
