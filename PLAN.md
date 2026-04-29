# XU316 Windows 驱动可交付物 — 实施计划

## 1. 可行性与法律/许可限制

### 1.1 XMOS XU316 简介

XMOS XU316 是一颗高性能 USB 音频方案 SoC，被广泛应用于专业声卡、麦克风接口、录音棚接口等设备。XMOS 官方提供：

- **sw_usb_audio / XUA 参考固件**（BSD 风格许可，开源）：用于在 XU316 上实现 USB Audio Class 2.0 (UAC2) 功能。
- **硬件参考设计**（XK-AUDIO-316-MC-AB 开发板等）：包括原理图、BOM、用户手册。
- **公开文档**：USB Audio Software Design Guide、USB Audio User Guide。

以上均为 **公开可获取** 的材料，可作为本项目的参考依据。

### 1.2 Windows 驱动选项

| 选项 | 来源 | 是否可自由分发 | 适用场景 |
|------|------|---------------|---------|
| **Windows 内置 UAC2 驱动**（Windows 10 1703+ / Windows 11） | 微软 | ✅ 无需另行分发，OS 自带 | 普通立体声/多声道播放录音，不需要 ASIO |
| **Thesycon USB Audio Class 2 驱动**（TUSBAUDIO） | Thesycon GmbH（第三方商业授权） | ❌ 需要向 Thesycon 购买 OEM 授权，不可从公开材料免费分发 | ASIO、DPC 低延迟、品牌化安装包 |
| **自制开源 WinUSB/WASAPI 驱动** | 社区/自研 | ⚠️ 技术可行，但需要 WHQL 签名或测试签名，未签名驱动在 Windows 11 上默认被阻止 | 极客/开发用途 |
| **macOS/Linux** | 系统原生 | ✅ 无需额外驱动 | 非 Windows 平台 |

### 1.3 关键法律限制

> **重要说明**：本仓库 **不能** 合法地打包或再分发 Thesycon TUSBAUDIO 驱动，因为该驱动需要商业 OEM 授权。任何声称直接提供"XMOS公版驱动.exe"的第三方分发包，如果包含 Thesycon 组件，实质上均属于未经授权的重分发。

本项目采用的合规替代方案：

- 提供一个 **Windows 引导安装程序（Bootstrapper）**，其功能为：
  1. 检测 Windows 版本和已存在的 UAC2 驱动状态。
  2. 如果 Windows 内置驱动已可用，指引用户直接使用。
  3. 如果用户需要 ASIO / 专业音频，说明需要向 Thesycon 购买驱动，或联系 OEM 获取授权包。
  4. 提供指向 XMOS 官方文档和工具（如 DFU 升级工具）的链接。
- 提供完整的 **文档**，解释驱动选项和安装流程。
- 提供 **CI/CD 流水线**，将引导程序打包为单一 `.exe` 并附加到 GitHub Releases。

---

## 2. 实施步骤

### 步骤 1：文档

- [x] 创建 `docs/xu316-windows-support.md`：详细说明 Windows UAC2 驱动支持、Thesycon 选项、ASIO 配置、DFU 升级流程。

### 步骤 2：引导安装程序

- [x] 在 `xu316-driver/` 目录下创建一个 .NET 9 控制台应用（`Xu316Setup`）：
  - 检测 Windows 版本（需要 10 1703+ 以获得内置 UAC2）。
  - 检测已接入的 XMOS USB 音频设备（通过 WMI/SetupAPI）。
  - 打印诊断信息和下一步建议。
  - 打包为 `win-x64` 自包含单文件 `.exe`（约 10–15 MB），无需目标机器预装 .NET。

### 步骤 3：CI/CD 自动化

- [x] 创建 `.github/workflows/xu316-release.yml`：
  - 触发条件：`workflow_dispatch` 手动触发，以及推送格式为 `xu316/v*` 的 tag。
  - 构建步骤：`dotnet publish` 生成自包含单文件 `.exe`。
  - 发布步骤：将 `.exe` 作为 artifact 上传，并在 GitHub Release 中附加。

### 步骤 4：README 更新

- [x] 在根 `README.md` 中添加 XU316 相关章节，说明用途和限制。

---

## 3. 关于"建立新私有仓库"的说明

用户请求建立一个新的私有仓库。**当前可用的工具权限仅限于在本仓库（`glftbcj/compressor-control-panel`）内进行修改**，无法通过当前工具创建新仓库。建议：

1. 在 GitHub 上手动创建新私有仓库（例如 `glftbcj/xu316-windows-driver`）。
2. 将本仓库的 `xu316-driver/`、`docs/xu316-windows-support.md`、`.github/workflows/xu316-release.yml` 目录内容复制到新仓库。
3. 或者，直接在本仓库中将 XU316 相关内容作为独立模块维护（当前采用此方式）。

---

## 4. 预期产出物（本 PR 内）

| 文件 | 说明 |
|------|------|
| `PLAN.md` | 本文档 |
| `docs/xu316-windows-support.md` | Windows 驱动支持详细说明 |
| `xu316-driver/Xu316Setup/Xu316Setup.csproj` | .NET bootstrapper 项目文件 |
| `xu316-driver/Xu316Setup/Program.cs` | Bootstrapper 主程序 |
| `.github/workflows/xu316-release.yml` | CI/CD 发布流水线 |

---

## 5. 参考资料

- [XMOS USB Audio Driver Support](https://www.xmos.com/en/usb-audio-driver-support/)
- [XMOS USB Audio Software Design Guide](https://www.xmos.com/file/usb-audio-software-design-guide)
- [XMOS XK-AUDIO-316-MC-AB Hardware](https://www.xmos.com/xk-audio-316-mc-ab)
- [XMOS sw_usb_audio GitHub](https://github.com/xmos/sw_usb_audio)
- [Thesycon USB Audio Class 2 Driver](https://www.thesycon.de/eng/usb_audio.shtml)
- [Microsoft USB Audio 2.0 Driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/usb-2-0-audio-drivers)
