using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hvacr.App;

public sealed class KnownDeviceStore(HvacrAppOptions options, Microsoft.Extensions.Logging.ILogger<KnownDeviceStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<KnownDeviceRecord>? _cache;

    public async Task<IReadOnlyList<KnownDeviceRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null) return _cache.ToArray();
            if (!File.Exists(options.KnownDevicesPath)) return _cache = [];
            await using var stream = File.OpenRead(options.KnownDevicesPath);
            var payload = await JsonSerializer.DeserializeAsync<KnownDevicesPayload>(stream, JsonOptions, cancellationToken);
            return _cache = Normalize(payload?.Devices);
        }
        catch (JsonException)
        {
            // Do not silently overwrite a corrupted list with an empty one.
            logger.LogError("设备列表 JSON 损坏，请备份并修复 devices.json。");
            throw new InvalidDataException("设备列表损坏，原文件已保留。请备份并修复 devices.json。");
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<KnownDeviceRecord>> SaveAsync(IEnumerable<KnownDeviceRecord>? devices, CancellationToken cancellationToken = default)
    {
        var sanitized = Normalize(devices);
        await _gate.WaitAsync(cancellationToken);
        var tempPath = options.KnownDevicesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new KnownDevicesPayload { Devices = sanitized }, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(options.KnownDevicesPath))
                File.Copy(options.KnownDevicesPath, options.KnownDevicesPath + ".bak", true);
            File.Move(tempPath, options.KnownDevicesPath, true);
            _cache = sanitized;
            return sanitized.ToArray();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            _gate.Release();
        }
    }

    private static IReadOnlyList<KnownDeviceRecord> Normalize(IEnumerable<KnownDeviceRecord>? devices)
    {
        if (devices is null) throw new ArgumentException("devices 必须为数组。");
        var result = new List<KnownDeviceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            var id = device?.DeviceId?.Trim();
            if (!DeviceIdentity.IsValid(id)) throw new ArgumentException("设备 ID 只允许 1–64 位英文字母、数字、下划线和短横线。");
            if (seen.Add(id!)) result.Add(new(id!));
            if (result.Count > DeviceIdentity.MaxDevices) throw new ArgumentException("最多保存 100 台设备。");
        }
        return result.ToArray();
    }
}

