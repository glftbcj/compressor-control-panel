using System.Text.Json;

namespace Hvacr.App;

public sealed class KnownDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HvacrAppOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KnownDeviceStore(HvacrAppOptions options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<KnownDeviceRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<KnownDeviceRecord>> SaveAsync(IEnumerable<KnownDeviceRecord>? devices, CancellationToken cancellationToken = default)
    {
        var sanitized = Sanitize(devices).ToArray();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            var tempPath = _options.KnownDevicesPath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new KnownDevicesPayload { Devices = sanitized },
                    JsonOptions,
                    cancellationToken);
            }

            File.Move(tempPath, _options.KnownDevicesPath, true);
            return sanitized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<KnownDeviceRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.KnownDevicesPath))
        {
            return Array.Empty<KnownDeviceRecord>();
        }

        try
        {
            await using var stream = File.OpenRead(_options.KnownDevicesPath);
            var payload = await JsonSerializer.DeserializeAsync<KnownDevicesPayload>(stream, JsonOptions, cancellationToken);
            return Sanitize(payload?.Devices).ToArray();
        }
        catch
        {
            return Array.Empty<KnownDeviceRecord>();
        }
    }

    private static IEnumerable<KnownDeviceRecord> Sanitize(IEnumerable<KnownDeviceRecord>? devices)
    {
        if (devices is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            var deviceId = device?.DeviceId?.Trim();
            if (string.IsNullOrWhiteSpace(deviceId) || !seen.Add(deviceId))
            {
                continue;
            }

            yield return new KnownDeviceRecord(deviceId);
        }
    }
}
