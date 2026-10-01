using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hvacr.App;

public sealed class ControlCommandRequest
{
    public string? DeviceId { get; set; }
    public string? Action { get; set; }
    public JsonElement? Value { get; set; }
    public JsonElement? ExpectedValue { get; set; }
    public Guid? CommandId { get; set; }
    public bool ConfirmPower { get; set; }
}
public sealed class KnownDevicesPayload
{
    public IReadOnlyList<KnownDeviceRecord>? Devices { get; set; } = [];
}
public sealed record KnownDeviceRecord(string DeviceId);
public sealed record FieldEvidence(JsonNode? Value, DateTimeOffset SeenAt, long Revision);
public sealed record CommandSnapshot(Guid CommandId, string Action, JsonNode? Target,
    string State, long SentAt, string Message);
public sealed record SetpointSnapshot(string Field, JsonNode Target, string State, long SentAt);
public sealed record AppearanceSettings(string Theme, string Accent)
{
    public static AppearanceSettings Default { get; } = new("system", "#147c6e");
}
public sealed record DeviceSnapshot(string DeviceId, string Topic, long LastSeen,
    JsonObject Payload, long Revision, bool Retained, bool Stale,
    IReadOnlyDictionary<string, long> FieldSeenAt);
public sealed record ControlResult(bool Success, string State, string Message,
    int HttpStatus = 200, Guid? CommandId = null);
public sealed class MqttStatusSnapshot
{
    public required bool Connected { get; init; }
    public string? LastError { get; init; }
}
public static class DeviceIdentity
{
    public const int MaxDevices = 100;
    public static bool IsValid(string? id) => id is { Length: > 0 and <= 64 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

