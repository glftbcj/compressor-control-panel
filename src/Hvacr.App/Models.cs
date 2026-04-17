using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hvacr.App;

public sealed class ControlCommandRequest
{
    public string? DeviceId { get; set; }
    public string? Action { get; set; }
    public JsonElement? Value { get; set; }
}

public sealed class KnownDevicesPayload
{
    public IReadOnlyList<KnownDeviceRecord> Devices { get; set; } = Array.Empty<KnownDeviceRecord>();
}

public sealed record KnownDeviceRecord(string DeviceId);

public sealed class DeviceSnapshot
{
    public required string DeviceId { get; init; }
    public required string Topic { get; init; }
    public required long LastSeen { get; init; }
    public object? Payload { get; init; }
}

public sealed class PublishCommandResult
{
    public required bool Success { get; init; }
    public required string Topic { get; init; }
    public required JsonNode Payload { get; init; }
    public string? Error { get; init; }
}

public sealed class MqttStatusSnapshot
{
    public required bool Connected { get; init; }
    public string? LastError { get; init; }
}
