using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hvacr.App;

public interface ICommandTransport
{
    bool Connected { get; }
    Task PublishAsync(string deviceId, JsonObject payload, CancellationToken cancellationToken);
}

/// <summary>All writable paths pass through this guard, including HTTP and simulation.</summary>
public sealed class CommandCoordinator(HvacrAppOptions options, DeviceRegistry registry,
    KnownDeviceStore store, ICommandTransport transport, TimeProvider clock, ControlModeState? mode = null, StatusUpdates? updates = null)
{
    private readonly ConcurrentDictionary<string, DeviceCommands> _devices = new(StringComparer.Ordinal);
    private readonly ControlModeState _mode = mode ?? new(options);

    public async Task<ControlResult> ExecuteAsync(ControlCommandRequest request, CancellationToken cancellationToken = default)
    {
        var id = request.DeviceId?.Trim();
        var action = request.Action;
        if (!DeviceIdentity.IsValid(id)) return Reject(400, "invalidDevice", "设备 ID 格式无效。");
        if (action is not ("get_data" or "start" or "stop" or "setTemperature" or "setWindSpeed"))
            return Reject(400, "invalidAction", "不支持此控制动作。");
        if (!(await store.LoadAsync(cancellationToken)).Any(d => d.DeviceId == id))
            return Reject(403, "unknownDevice", "先保存设备，再查询或控制。");
        if (!transport.Connected) return Reject(503, "disconnected", "通信未连接，未发送指令。");

        var state = _devices.GetOrAdd(id!, _ => new());
        if (action == "get_data") return await QueryAsync(id!, state, cancellationToken);
        if (!await state.Gate.WaitAsync(0, cancellationToken))
            return Reject(409, "busy", "该设备正在处理指令，请稍候。");
        try
        {
            var now = clock.GetUtcNow();
            if (_mode.ReadOnly) return Reject(403, "readOnly", "当前为只读模式，请在面板中启用控制。");
            if (request.CommandId is not { } commandId || commandId == Guid.Empty)
                return Reject(400, "missingCommandId", "控制指令需要唯一 commandId。");
            var signature = $"{action}|{request.Value?.GetRawText()}|{request.ExpectedValue?.GetRawText()}|{request.ConfirmPower}";
            if (state.History.TryGetValue(commandId, out var history))
                return history.Signature == signature ? history.Result
                    : Reject(409, "duplicateId", "此 commandId 已用于其他指令。");

            RefreshPending(id!, state, now);
            if (state.Pending is { State: "pending" or "uncertain" or "timedOut" })
                return Reject(409, "pending", "上一条指令尚未得到设备回读确认，请刷新状态。");

            var field = action switch { "setTemperature" => "set_temp", "setWindSpeed" => "wind_speed_set", _ => "power" };
            var evidence = registry.Evidence(id!, field);
            if (evidence is null || now - evidence.SeenAt > options.TelemetryMaxAge || evidence.SeenAt > now)
                return Reject(409, "stale", "当前值缺少新鲜设备回读，请先刷新状态。");

            JsonNode target;
            if (action is "start" or "stop")
            {
                if (!request.ConfirmPower) return Reject(400, "confirmationRequired", "启停需要明确确认。");
                if (!TryBool(evidence.Value, out var current) || !ReadBool(request.ExpectedValue, out var expected) || expected != current)
                    return Reject(409, "changed", "运行状态已变化，请刷新并重新确认。");
                var next = action == "start";
                if (next == current) return Reject(409, "unchanged", "设备已经处于该运行状态。");
                target = JsonValue.Create(next)!;
            }
            else
            {
                if (!ReadNumber(request.Value, out var next) || !ReadNumber(request.ExpectedValue, out var expected))
                    return Reject(400, "invalidValue", "value 和 expectedValue 必须为有限 JSON 数字。");
                if (!TryNumber(evidence.Value, out var current) || expected != current)
                    return Reject(409, "changed", "设备设定已变化，请刷新并重新调整。");
                var min = action == "setTemperature" ? -20 : 1;
                var max = action == "setTemperature" ? 50 : 10;
                if (current < min || current > max || next < min || next > max
                    || (action == "setWindSpeed" && (decimal.Truncate(next) != next || decimal.Truncate(current) != current)))
                    return Reject(400, "outOfRange", "温度范围为 −20–50℃；水泵只允许 1–10 的整数挡位。");
                if (next == current)
                    return Reject(409, "unchanged", "目标与设备当前设定相同，无需发送。");
                target = JsonValue.Create(next)!;
            }

            // Record BEFORE publish: a transport timeout does not prove the device received nothing.
            var pending = new PendingCommand(commandId, action!, field, target.DeepClone(), evidence.Revision, now);
            state.Pending = pending;
            if (action is "setTemperature" or "setWindSpeed") state.Setpoints[field] = pending;
            var result = new ControlResult(true, "pending", "指令已发送，等待设备回读。", 202, commandId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            try
            {
                await transport.PublishAsync(id!, new JsonObject { [field] = target }, timeout.Token);
                // Let the immediate post-write read bypass the regular query coalescing window.
                Volatile.Write(ref state.LastQueryMilliseconds, long.MinValue);
            }
            catch
            {
                pending.State = "uncertain";
                result = new(false, "uncertain", "发送结果不确定，请刷新设备回读；不要重复发送。", 504, commandId);
            }
            state.History[commandId] = (signature, result);
            while (state.History.Count > 64) state.History.Remove(state.History.Keys.First());
            return result;
        }
        finally
        {
            state.Gate.Release();
            updates?.Notify();
        }
    }

    private async Task<ControlResult> QueryAsync(string id, DeviceCommands state, CancellationToken ct)
    {
        if (!await state.QueryGate.WaitAsync(0, ct)) return new(true, "throttled", "状态查询已合并。");
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var lastQuery = Volatile.Read(ref state.LastQueryMilliseconds);
            if (lastQuery != long.MinValue && now - lastQuery < 1000) return new(true, "throttled", "状态查询已合并。");
            Volatile.Write(ref state.LastQueryMilliseconds, now);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            try
            {
                await transport.PublishAsync(id, new JsonObject { ["get_data"] = 1 }, timeout.Token);
                return new(true, "requested", "已请求设备上报。");
            }
            catch (OperationCanceledException) { return Reject(504, "timeout", "查询超时，请稍后刷新。"); }
            catch { return Reject(503, "disconnected", "查询发送失败。"); }
        }
        finally { state.QueryGate.Release(); }
    }

    public CommandSnapshot? Snapshot(string id)
    {
        if (!_devices.TryGetValue(id, out var state)) return null;
        // Never race pending changes while a publish is in flight.
        if (!state.Gate.Wait(0)) return null;
        try
        {
            RefreshPending(id, state, clock.GetUtcNow());
            var p = state.Pending;
            return p is null ? null : new(p.Id, p.Action, p.Target.DeepClone(), p.State,
                p.SentAt.ToUnixTimeMilliseconds(), p.State switch
                {
                    "confirmed" => "设备已回报目标设定。",
                    "notConfirmed" => "设备回报与目标不同，请检查设备状态。",
                    "timedOut" => "确认超时，等待新的设备回读；不会自动重发。",
                    "uncertain" => "发送结果不确定，等待设备回读。",
                    _ => "等待设备回读确认。"
                });
        }
        finally { state.Gate.Release(); }
    }

    private void RefreshPending(string id, DeviceCommands state, DateTimeOffset now)
    {
        var p = state.Pending;
        if (p is null || p.State is "confirmed" or "notConfirmed") return;
        var evidence = registry.Evidence(id, p.Field);
        if (evidence is not null && evidence.Revision > p.BaselineRevision && evidence.SeenAt >= p.SentAt
            && now - evidence.SeenAt <= options.TelemetryMaxAge && ValuesEqual(evidence.Value, p.Target))
            p.State = "confirmed";
        else if (now - p.SentAt > options.ConfirmationTimeout)
            p.State = evidence is not null && evidence.Revision > p.BaselineRevision
                && evidence.SeenAt > p.SentAt + options.ConfirmationTimeout
                && now - evidence.SeenAt <= options.TelemetryMaxAge ? "notConfirmed" : "timedOut";
    }

    // Preserve local intent separately from raw telemetry. A later, conflicting cloud
    // report is still exposed; it must not silently replace the user's applied target.
    public IReadOnlyList<SetpointSnapshot>? SetpointsSnapshot(string id)
    {
        if (!_devices.TryGetValue(id, out var state)) return [];
        if (!state.Gate.Wait(0)) return null;
        try
        {
            RefreshPending(id, state, clock.GetUtcNow());
            return state.Setpoints.Values.Select(p => new SetpointSnapshot(p.Field, p.Target.DeepClone(),
                p.State, p.SentAt.ToUnixTimeMilliseconds())).ToArray();
        }
        finally { state.Gate.Release(); }
    }

    public static bool TryNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        var text = value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString()
            : value.TryGetValue<string>(out var numericText) ? numericText : null;
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
    public static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is JsonValue json && json.TryGetValue<bool>(out value)) return true;
        if (!TryNumber(node, out var number) || number is not (0 or 1)) return false;
        value = number == 1;
        return true;
    }
    private static bool ValuesEqual(JsonNode? a, JsonNode b) =>
        TryNumber(a, out var x) && TryNumber(b, out var y) ? x == y :
        TryBool(a, out var p) && TryBool(b, out var q) && p == q;
    private static bool ReadNumber(JsonElement? value, out decimal number)
    {
        number = 0;
        return value is { ValueKind: JsonValueKind.Number } element && element.TryGetDecimal(out number);
    }
    private static bool ReadBool(JsonElement? value, out bool boolean)
    {
        boolean = value is { ValueKind: JsonValueKind.True };
        return value is { ValueKind: JsonValueKind.True or JsonValueKind.False };
    }
    private static ControlResult Reject(int code, string state, string message) => new(false, state, message, code);
    private sealed class DeviceCommands
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim QueryGate { get; } = new(1, 1);
        public Dictionary<Guid, (string Signature, ControlResult Result)> History { get; } = new();
        public long LastQueryMilliseconds = long.MinValue;
        public PendingCommand? Pending { get; set; }
        public Dictionary<string, PendingCommand> Setpoints { get; } = new(StringComparer.Ordinal);
    }
    private sealed class PendingCommand(Guid id, string action, string field, JsonNode target, long revision, DateTimeOffset sentAt)
    {
        public Guid Id { get; } = id;
        public string Action { get; } = action;
        public string Field { get; } = field;
        public JsonNode Target { get; } = target;
        public long BaselineRevision { get; } = revision;
        public DateTimeOffset SentAt { get; } = sentAt;
        public string State { get; set; } = "pending";
    }
}

