// XMOS XU316 USB Audio — Windows Setup Bootstrapper
// This tool is a DIAGNOSTIC / GUIDANCE utility only.
// It does NOT install any driver. It does NOT bundle any third-party driver.
// See docs/xu316-windows-support.md for the full explanation.

using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Xu316Setup;

internal static class Program
{
    // XMOS USB Vendor ID (official)
    private const int XmosVendorId = 0x20B1;

    // Well-known XMOS XU316-based product PIDs (non-exhaustive reference list)
    private static readonly Dictionary<int, string> KnownXmosPids = new()
    {
        [0x0014] = "XMOS XK-AUDIO-316-MC-AB (reference board)",
        [0x8010] = "XMOS USB Audio (generic UAC2)",
        [0x8030] = "XMOS USB Audio 2.0 (generic)",
    };

    [SupportedOSPlatform("windows")]
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        PrintBanner();

        var checkOnly = args.Contains("--check") || args.Contains("-c");

        PrintSection("1. Windows 版本检查");
        var winOk = CheckWindowsVersion();

        PrintSection("2. 已连接的 XMOS USB 音频设备");
        var devices = ScanXmosUsbDevices();
        PrintDevices(devices);

        PrintSection("3. 诊断建议");
        PrintRecommendations(winOk, devices);

        PrintSection("4. 官方资料链接");
        PrintLinks();

        if (!checkOnly)
        {
            Console.WriteLine();
            Console.WriteLine("按任意键退出...");
            Console.ReadKey(intercept: true);
        }

        await Task.CompletedTask;
        return 0;
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║      XMOS XU316 USB Audio — Windows 驱动引导工具 v1.0.0      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("【免责声明】本工具为纯诊断/引导程序，不安装任何驱动，");
        Console.WriteLine("           不包含任何第三方驱动组件，不修改系统驱动相关配置。");
        Console.WriteLine();
    }

    private static void PrintSection(string title)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── {title} ──────────────────────────────────────");
        Console.ResetColor();
    }

    private static bool CheckWindowsVersion()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("× 当前系统不是 Windows，本工具仅支持 Windows 平台。");
            Console.ResetColor();
            return false;
        }

        var version = Environment.OSVersion.Version;
        Console.WriteLine($"  操作系统：{RuntimeInformation.OSDescription}");
        Console.WriteLine($"  内核版本：{version}");

        // Windows 10 1703 = build 15063; Windows 11 = build 22000+
        if (version.Major >= 10 && version.Build >= 15063)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ✓ 当前 Windows 版本支持内置 USB Audio Class 2.0 驱动（无需额外安装）。");
            Console.ResetColor();
            return true;
        }
        else if (version.Major >= 10)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  ⚠ Windows 10 版本低于 1703，UAC2 内置驱动不可用。");
            Console.WriteLine("    建议升级 Windows 10 至最新版本，或安装第三方驱动。");
            Console.ResetColor();
            return false;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  × Windows 版本过旧（< Windows 10），原生 UAC2 驱动不支持。");
            Console.WriteLine("    需要安装 OEM 提供的第三方驱动（如 Thesycon TUSBAUDIO）。");
            Console.ResetColor();
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<UsbAudioDevice> ScanXmosUsbDevices()
    {
        var results = new List<UsbAudioDevice>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"SELECT * FROM Win32_PnPEntity WHERE ClassGuid = '{4d36e96c-e325-11ce-bfc1-08002be10318}'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? string.Empty;
                var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                var status = obj["Status"]?.ToString() ?? string.Empty;
                var manufacturer = obj["Manufacturer"]?.ToString() ?? string.Empty;

                // Extract VID/PID from DeviceID string (e.g. USB\VID_20B1&PID_0014\...)
                var vid = ExtractHexSegment(deviceId, "VID_");
                var pid = ExtractHexSegment(deviceId, "PID_");

                var isXmos = vid == XmosVendorId;
                var knownName = (isXmos && KnownXmosPids.TryGetValue(pid, out var kn)) ? kn : null;

                results.Add(new UsbAudioDevice(
                    Name: name,
                    DeviceId: deviceId,
                    Status: status,
                    Manufacturer: manufacturer,
                    Vid: vid,
                    Pid: pid,
                    IsXmos: isXmos,
                    KnownProductName: knownName));
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  ⚠ 无法通过 WMI 枚举 USB 音频设备：{ex.Message}");
            Console.WriteLine("    （可能需要管理员权限，或当前系统不支持 WMI 查询）");
            Console.ResetColor();
        }

        return results;
    }

    private static void PrintDevices(List<UsbAudioDevice> devices)
    {
        if (devices.Count == 0)
        {
            Console.WriteLine("  （未检测到已安装的 USB 音频设备，或 WMI 查询受限）");
            return;
        }

        foreach (var d in devices)
        {
            if (d.IsXmos)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  ★ [XMOS] ");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  · [其他] ");
            }

            Console.ResetColor();
            Console.Write($"{d.Name}");

            if (d.Vid != 0 || d.Pid != 0)
                Console.Write($"  (VID=0x{d.Vid:X4} PID=0x{d.Pid:X4})");

            if (!string.IsNullOrEmpty(d.KnownProductName))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"  ← {d.KnownProductName}");
                Console.ResetColor();
            }

            Console.WriteLine($"  [{d.Status}]");

            if (d.IsXmos && !string.IsNullOrEmpty(d.Manufacturer))
                Console.WriteLine($"       制造商：{d.Manufacturer}");
        }
    }

    private static void PrintRecommendations(bool winOk, List<UsbAudioDevice> devices)
    {
        var xmosDevices = devices.Where(d => d.IsXmos).ToList();

        if (!winOk)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  ⚠ Windows 版本不支持内置 UAC2 驱动，建议：");
            Console.WriteLine("    1. 升级 Windows 10 至 1703 或更高版本（推荐）。");
            Console.WriteLine("    2. 联系设备厂商获取适配当前 Windows 版本的专用驱动包。");
            Console.ResetColor();
            return;
        }

        if (xmosDevices.Count == 0)
        {
            Console.WriteLine("  · 未检测到 XMOS USB 音频设备接入。");
            Console.WriteLine("    请将基于 XU316 的 USB 音频设备连接到电脑后重新运行本工具。");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ 检测到 {xmosDevices.Count} 个 XMOS USB 音频设备。");
        Console.ResetColor();

        var allOk = xmosDevices.All(d => string.Equals(d.Status, "OK", StringComparison.OrdinalIgnoreCase));
        if (allOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ✓ 所有 XMOS 设备状态正常（由 Windows 内置 UAC2 驱动管理）。");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  普通使用建议：");
            Console.WriteLine("    ・ 打开「设置 → 系统 → 声音」，将 XMOS 设备设为默认播放/录制设备。");
            Console.WriteLine("    ・ 如需 ASIO 低延迟，请联系设备厂商获取 ASIO 驱动包，");
            Console.WriteLine("      或临时使用 ASIO4ALL（https://www.asio4all.org）。");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  ⚠ 部分 XMOS 设备状态异常，可能存在驱动问题。建议：");
            Console.WriteLine("    1. 在设备管理器中右键 → 更新驱动程序 → 自动搜索。");
            Console.WriteLine("    2. 换用其他 USB 端口（建议 USB 3.0）。");
            Console.WriteLine("    3. 联系设备厂商获取专用驱动或固件更新。");
            Console.ResetColor();
        }
    }

    private static void PrintLinks()
    {
        var links = new[]
        {
            ("XMOS USB Audio 驱动支持说明", "https://www.xmos.com/en/usb-audio-driver-support/"),
            ("XMOS USB Audio 软件设计指南", "https://www.xmos.com/file/usb-audio-software-design-guide"),
            ("XMOS sw_usb_audio 参考固件（GitHub）", "https://github.com/xmos/sw_usb_audio"),
            ("Microsoft USB Audio 2.0 驱动文档", "https://learn.microsoft.com/zh-cn/windows-hardware/drivers/audio/usb-2-0-audio-drivers"),
            ("Thesycon TUSBAUDIO（商业 ASIO 驱动）", "https://www.thesycon.de/eng/usb_audio.shtml"),
            ("ASIO4ALL（免费 ASIO 包装器）", "https://www.asio4all.org"),
        };

        foreach (var (label, url) in links)
        {
            Console.Write($"  · {label}");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"\n    {url}");
            Console.ResetColor();
        }
    }

    private static int ExtractHexSegment(string source, string prefix)
    {
        var idx = source.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        var start = idx + prefix.Length;
        var end = start;
        while (end < source.Length && IsHexChar(source[end]))
            end++;

        if (end == start) return 0;

        return Convert.ToInt32(source[start..end], 16);
    }

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

internal record UsbAudioDevice(
    string Name,
    string DeviceId,
    string Status,
    string Manufacturer,
    int Vid,
    int Pid,
    bool IsXmos,
    string? KnownProductName);
