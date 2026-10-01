using System.Text.Json.Nodes;

namespace Hvacr.App;

/// <summary>Publishing never edits confirmed telemetry.</summary>
public sealed class DeviceRegistry(HvacrAppOptions options, TimeProvider clock, StatusUpdates? updates = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private long _nextRevision;

    public void Upsert(string deviceId, string topic, JsonObject payload, DateTimeOffset seenAt, bool retained = false)
    {
        if (!DeviceIdentity.IsValid(deviceId) || topic != $"{deviceId}/bxkt/esp") return;
        lock (_sync)
        {
            if (!_devices.TryGetValue(deviceId, out var device))
            {
                if (_devices.Count >= DeviceIdentity.MaxDevices) return;
                _devices[deviceId] = device = new(deviceId, topic);
            }
            if (retained && device.HasLiveReport) return;
            device.Revision = ++_nextRevision;
            device.LastSeen = seenAt;
            device.Retained = retained;
            device.HasLiveReport |= !retained;
            foreach (var field in payload)
            {
                device.Payload[field.Key] = field.Value?.DeepClone();
                if (!retained) device.Evidence[field.Key] = new(field.Value?.DeepClone(), seenAt, device.Revision);
            }
        }
        updates?.Notify();
    }

    public FieldEvidence? Evidence(string deviceId, string field)
    {
        lock (_sync)
            return _devices.TryGetValue(deviceId, out var device) && device.Evidence.TryGetValue(field, out var evidence)
                ? evidence with { Value = evidence.Value?.DeepClone() } : null;
    }

    public IReadOnlyList<DeviceSnapshot> SnapshotList()
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            return _devices.Values.OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => new DeviceSnapshot(d.Id, d.Topic, d.LastSeen.ToUnixTimeMilliseconds(),
                    (JsonObject)d.Payload.DeepClone(), d.Revision, d.Retained,
                    !d.HasLiveReport || now - d.LastSeen > options.TelemetryMaxAge,
                    d.Evidence.ToDictionary(f => f.Key, f => f.Value.SeenAt.ToUnixTimeMilliseconds())))
                .ToArray();
        }
    }

    public void InvalidateEvidence()
    {
        lock (_sync)
            foreach (var device in _devices.Values)
            {
                device.Evidence.Clear();
                device.HasLiveReport = false;
            }
        updates?.Notify();
    }

    public void KeepOnly(IEnumerable<string> ids)
    {
        var known = ids.ToHashSet(StringComparer.Ordinal);
        lock (_sync)
            foreach (var id in _devices.Keys.Where(id => !known.Contains(id)).ToArray()) _devices.Remove(id);
        updates?.Notify();
    }

    public void PruneExpired(DateTimeOffset cutoff)
    {
        lock (_sync)
            foreach (var id in _devices.Values.Where(d => d.LastSeen < cutoff).Select(d => d.Id).ToArray()) _devices.Remove(id);
    }

    private sealed class DeviceState(string id, string topic)
    {
        public string Id { get; } = id;
        public string Topic { get; } = topic;
        public JsonObject Payload { get; } = new();
        public Dictionary<string, FieldEvidence> Evidence { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastSeen { get; set; }
        public long Revision { get; set; }
        public bool Retained { get; set; }
        public bool HasLiveReport { get; set; }
    }
}

