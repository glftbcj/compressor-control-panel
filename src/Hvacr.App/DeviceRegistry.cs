using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hvacr.App;

public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    public void Upsert(string deviceId, string topic, JsonNode? jsonPayload, string? textPayload, DateTimeOffset seenAt)
    {
        var next = new DeviceState(deviceId, topic, CloneNode(jsonPayload), textPayload, seenAt);
        _devices.AddOrUpdate(deviceId, next, (_, _) => next);
    }

    public IReadOnlyList<DeviceSnapshot> SnapshotList()
    {
        return _devices.Values
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .Select(device => new DeviceSnapshot
            {
                DeviceId = device.DeviceId,
                Topic = device.Topic,
                LastSeen = device.LastSeen.ToUnixTimeMilliseconds(),
                Payload = device.ClonePayload()
            })
            .ToArray();
    }

    public void ApplyOptimisticUpdate(string deviceId, string action, JsonNode payload)
    {
        while (_devices.TryGetValue(deviceId, out var current))
        {
            if (current.JsonPayload is not JsonObject currentObject)
            {
                return;
            }

            var updatedPayload = currentObject.DeepClone() as JsonObject;
            if (updatedPayload is null)
            {
                return;
            }

            switch (action)
            {
                case "start":
                    updatedPayload["power"] = true;
                    break;
                case "stop":
                    updatedPayload["power"] = false;
                    break;
                case "setTemperature":
                    updatedPayload["set_temp"] = CloneNode(payload["set_temp"]);
                    break;
                case "setWindSpeed":
                    updatedPayload["wind_speed_set"] = CloneNode(payload["wind_speed_set"]);
                    break;
                default:
                    return;
            }

            var next = current with { JsonPayload = updatedPayload };
            if (_devices.TryUpdate(deviceId, next, current))
            {
                return;
            }
        }
    }

    public void PruneExpired(DateTimeOffset cutoff)
    {
        foreach (var device in _devices)
        {
            if (device.Value.LastSeen < cutoff)
            {
                _devices.TryRemove(device.Key, out _);
            }
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node?.DeepClone();
    }

    private sealed record DeviceState(
        string DeviceId,
        string Topic,
        JsonNode? JsonPayload,
        string? TextPayload,
        DateTimeOffset LastSeen)
    {
        public object? ClonePayload() => JsonPayload?.DeepClone() ?? TextPayload;
    }
}
