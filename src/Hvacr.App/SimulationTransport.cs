using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

namespace Hvacr.App;

/// <summary>Entirely offline: no MQTT client is constructed in simulation mode.</summary>
public sealed class SimulationTransport(DeviceRegistry registry, KnownDeviceStore store,
    MqttConnectionState connectionState, TimeProvider clock) : BackgroundService, ICommandTransport
{
    private readonly object _sync = new();
    private readonly Dictionary<string, JsonObject> _payloads = new(StringComparer.Ordinal);
    public bool Connected => connectionState.Snapshot().Connected;

    public async Task PublishAsync(string deviceId, JsonObject payload, CancellationToken cancellationToken)
    {
        await Task.Delay(180, cancellationToken);
        lock (_sync)
        {
            var report = GetPayload(deviceId);
            foreach (var field in payload)
                if (field.Key != "get_data") report[field.Key] = field.Value?.DeepClone();
            Report(deviceId, report);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if ((await store.LoadAsync(stoppingToken)).Count == 0)
            await store.SaveAsync([new("demo-compressor01")], stoppingToken);
        connectionState.SetConnected();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var devices = await store.LoadAsync(stoppingToken);
                lock (_sync)
                {
                    foreach (var id in _payloads.Keys.Except(devices.Select(d => d.DeviceId)).ToArray()) _payloads.Remove(id);
                    foreach (var device in devices)
                    {
                        var report = GetPayload(device.DeviceId);
                        report["sj_temp"] = 22.6 + Math.Sin(clock.GetUtcNow().ToUnixTimeSeconds() / 15.0) * 0.15;
                        Report(device.DeviceId, report);
                    }
                    registry.KeepOnly(devices.Select(d => d.DeviceId));
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { connectionState.SetDisconnected("模拟已停止。"); }
    }

    private JsonObject GetPayload(string id)
    {
        if (!_payloads.TryGetValue(id, out var payload))
            _payloads[id] = payload = new JsonObject
            {
                ["power"] = true, ["sj_temp"] = 22.6, ["set_temp"] = 23,
                ["wind_speed_set"] = 5, ["pump_switch"] = true, ["ln_temp"] = 31.2,
                ["zf_temp"] = 14.8, ["voltage"] = 220, ["run_fz"] = 35, ["fault_codes"] = false,
                ["version"] = 1.0
            };
        return payload;
    }
    private void Report(string id, JsonObject payload) => registry.Upsert(id, $"{id}/bxkt/esp", payload, clock.GetUtcNow());
}

