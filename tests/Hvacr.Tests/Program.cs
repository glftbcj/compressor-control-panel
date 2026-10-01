using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hvacr.App;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;
var cases = new List<(string Name, Func<Task> Run)>();
void Case(string name, Func<Fixture, Task> run) => cases.Add((name, async () => { await using var f = await Fixture.Create(); await run(f); }));
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void State(string expected, ControlResult result) => Equal(expected, result.State);

Case("One degree accepted; confirmed telemetry unchanged until report", async f =>
{
    State("pending", await f.Send("setTemperature", 24, 23));
    Equal(23m, f.Number("set_temp")); Equal(1, f.Transport.Sent.Count);
    f.Report(new() { ["set_temp"] = 24 });
    Equal("confirmed", f.Commands.Snapshot(Fixture.Id)!.State);
    Equal(24m, f.Number("set_temp"));
});
Case("Temperature rejects empty values, strings, unchanged and out of range values", async f =>
{
    State("unchanged", await f.Send("setTemperature", 23, 23));
    State("invalidValue", await f.Send("setTemperature", "24", 23));
    State("invalidValue", await f.Send("setTemperature", null, 23));
    State("outOfRange", await f.Send("setTemperature", 51, 23));
    Equal(0, f.Transport.Sent.Count);
});
Case("Conflicting cloud reports do not replace local setpoints or cause another publish", async f =>
{
    State("pending", await f.Send("setTemperature", 10, 23));
    f.Report(new() { ["set_temp"] = 10 });
    Equal("confirmed", f.Commands.Snapshot(Fixture.Id)!.State);
    f.Clock.Advance(TimeSpan.FromSeconds(2));
    f.Report(new() { ["set_temp"] = 14 });
    var target = f.Commands.SetpointsSnapshot(Fixture.Id)!.Single();
    Equal(10m, target.Target.GetValue<decimal>());
    Equal("confirmed", target.State);
    Equal(14m, f.Number("set_temp"));
    Equal(1, f.Transport.Sent.Count);
    State("pending", await f.Send("setWindSpeed", 6, 5));
    f.Report(new() { ["wind_speed_set"] = 6 });
    Equal("confirmed", f.Commands.Snapshot(Fixture.Id)!.State);
    Equal(10m, f.Commands.SetpointsSnapshot(Fixture.Id)!.Single(p => p.Field == "set_temp").Target.GetValue<decimal>());
    State("changed", await f.Send("setTemperature", 11, 10));
    Equal(2, f.Transport.Sent.Count);
    State("pending", await f.Send("setTemperature", 11, 14));
    Equal(11m, f.Commands.SetpointsSnapshot(Fixture.Id)!.Single(p => p.Field == "set_temp").Target.GetValue<decimal>());
});
Case("Normal temperature control accepts a direct target without a one degree limit", async f =>
{
    State("pending", await f.Send("setTemperature", 18.5, 23));
    Equal(18.5m, f.Transport.Sent[0]["set_temp"]!.GetValue<decimal>());
});
Case("Pump accepts direct integer targets and never zero or fractions", async f =>
{
    State("outOfRange", await f.Send("setWindSpeed", 5.5, 5));
    f.Report(new() { ["wind_speed_set"] = 1 });
    State("outOfRange", await f.Send("setWindSpeed", 0, 1));
    State("pending", await f.Send("setWindSpeed", 10, 1));
    Equal(10m, f.Transport.Sent[0]["wind_speed_set"]!.GetValue<decimal>());
});
Case("Stale or absent target field blocks writes", async f =>
{
    f.Clock.Advance(TimeSpan.FromSeconds(31));
    State("stale", await f.Send("setTemperature", 24, 23));
    f.Report(new() { ["sj_temp"] = 22 });
    State("stale", await f.Send("setTemperature", 24, 23));
});
Case("Expected baseline prevents changing a setting that moved elsewhere", async f =>
{
    f.Report(new() { ["set_temp"] = 22 });
    State("changed", await f.Send("setTemperature", 24, 23));
    Equal(0, f.Transport.Sent.Count);
});
Case("Unsupported actions and topic injection rejected", async f =>
{
    State("invalidAction", await f.Send("pump_switch", false, null));
    State("invalidAction", await f.Send("factoryReset", 1, null));
    State("invalidDevice", await f.Commands.ExecuteAsync(new() { DeviceId = "+/app", Action = "get_data" }));
    State("unknownDevice", await f.Commands.ExecuteAsync(new() { DeviceId = "other", Action = "get_data" }));
    Equal(0, f.Transport.Sent.Count);
});
Case("Read only and disconnected transports never publish", async f =>
{
    var readOnly = new CommandCoordinator(f.Options with { ReadOnly = true }, f.Registry, f.Store, f.Transport, f.Clock);
    State("readOnly", await readOnly.ExecuteAsync(f.Request("setTemperature", 24, 23)));
    f.Transport.Connected = false;
    State("disconnected", await f.Send("setTemperature", 24, 23));
    Equal(0, f.Transport.Sent.Count);
});
Case("Power requires explicit confirmation and fresh boolean baseline", async f =>
{
    State("confirmationRequired", await f.Send("stop", null, true));
    var request = f.Request("stop", null, false); request.ConfirmPower = true;
    State("changed", await f.Commands.ExecuteAsync(request));
    request = f.Request("stop", null, true); request.ConfirmPower = true;
    State("pending", await f.Commands.ExecuteAsync(request));
    Equal(true, f.Registry.SnapshotList()[0].Payload["power"]!.GetValue<bool>());
});
Case("Duplicate command id does not resend; reuse with another target rejected", async f =>
{
    var request = f.Request("setTemperature", 24, 23);
    State("pending", await f.Commands.ExecuteAsync(request));
    State("pending", await f.Commands.ExecuteAsync(request));
    request.Value = JsonSerializer.SerializeToElement(22);
    State("duplicateId", await f.Commands.ExecuteAsync(request));
    Equal(1, f.Transport.Sent.Count);
});
Case("A command requires an idempotency id", async f =>
{
    var request = f.Request("setTemperature", 24, 23); request.CommandId = null;
    State("missingCommandId", await f.Commands.ExecuteAsync(request));
});
Case("Pending command blocks another control and unrelated telemetry cannot confirm", async f =>
{
    State("pending", await f.Send("setTemperature", 24, 23));
    f.Report(new() { ["sj_temp"] = 24, ["wind_speed_set"] = 6 });
    Equal("pending", f.Commands.Snapshot(Fixture.Id)!.State);
    var result = await f.Send("setWindSpeed", 7, 6);
    Equal(false, result.Success); State("pending", result);
    Equal(1, f.Transport.Sent.Count);
});
Case("Confirmed command allows the next adjustment immediately", async f =>
{
    await f.Send("setTemperature", 24, 23); f.Report(new() { ["set_temp"] = 24 });
    State("pending", await f.Send("setTemperature", 23, 24));
});
Case("Confirmed power change has no fixed adjustment interval", async f =>
{
    var request = f.Request("stop", null, true); request.ConfirmPower = true;
    await f.Commands.ExecuteAsync(request); f.Report(new() { ["power"] = false });
    State("pending", await f.Send("setTemperature", 24, 23));
});
Case("Transport failure remains uncertain, duplicate is not retried", async f =>
{
    f.Transport.Fail = true;
    var request = f.Request("setTemperature", 24, 23);
    State("uncertain", await f.Commands.ExecuteAsync(request));
    State("uncertain", await f.Commands.ExecuteAsync(request));
    Equal(1, f.Transport.Sent.Count);
    f.Transport.Fail = false;
    State("pending", await f.Send("setWindSpeed", 6, 5));
    f.Report(new() { ["set_temp"] = 24 });
    Equal("confirmed", f.Commands.Snapshot(Fixture.Id)!.State);
});
Case("Timeout stays locked until a new relevant report resolves uncertainty", async f =>
{
    await f.Send("setTemperature", 24, 23);
    f.Clock.Advance(TimeSpan.FromSeconds(21));
    Equal("timedOut", f.Commands.Snapshot(Fixture.Id)!.State);
    State("pending", await f.Send("setWindSpeed", 6, 5));
    f.Report(new() { ["set_temp"] = 23 });
    Equal("notConfirmed", f.Commands.Snapshot(Fixture.Id)!.State);
    State("pending", await f.Send("setTemperature", 22, 23));
});
Case("Retained reports and reconnect invalidation never authorize writes", async f =>
{
    f.Registry.InvalidateEvidence();
    f.Report(new() { ["set_temp"] = 24 }, retained: true);
    State("stale", await f.Send("setTemperature", 25, 24));
    f.Report(new() { ["set_temp"] = 23 });
    State("pending", await f.Send("setTemperature", 24, 23));
});
Case("Retained data cannot overwrite a live reading or confirm a command", async f =>
{
    await f.Send("setTemperature", 24, 23);
    f.Report(new() { ["set_temp"] = 24 }, retained: true);
    Equal(23m, f.Number("set_temp"));
    Equal("pending", f.Commands.Snapshot(Fixture.Id)!.State);
});
Case("Merging partial reports preserves values without refreshing their evidence", async f =>
{
    var before = f.Registry.Evidence(Fixture.Id, "set_temp")!;
    f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Report(new() { ["sj_temp"] = 22 });
    Equal(23m, f.Number("set_temp"));
    Equal(before.SeenAt, f.Registry.Evidence(Fixture.Id, "set_temp")!.SeenAt);
    await Task.CompletedTask;
});
Case("Snapshots are independent of caller changes and command topics ignored", async f =>
{
    f.Registry.SnapshotList()[0].Payload["set_temp"] = 50;
    f.Registry.Upsert(Fixture.Id, $"{Fixture.Id}/app", new() { ["set_temp"] = 0 }, f.Clock.GetUtcNow());
    Equal(23m, f.Number("set_temp"));
    await Task.CompletedTask;
});
Case("Concurrent writes serialize per device", async f =>
{
    f.Transport.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = f.Send("setTemperature", 24, 23);
    State("busy", await f.Send("setWindSpeed", 6, 5));
    f.Transport.Wait.SetResult(); await first;
    Equal(1, f.Transport.Sent.Count);
});
Case("Query coalescing does not write controls", async f =>
{
    State("requested", await f.Send("get_data", null, null));
    State("throttled", await f.Send("get_data", null, null));
    Equal(1, f.Transport.Sent.Count);
    Equal(1, f.Transport.Sent[0]["get_data"]!.GetValue<int>());
});
Case("Immediate post-write query bypasses query coalescing", async f =>
{
    State("requested", await f.Send("get_data", null, null));
    State("pending", await f.Send("setTemperature", 24, 23));
    State("requested", await f.Send("get_data", null, null));
    Equal(3, f.Transport.Sent.Count);
});
Case("Status queries remain available while a write is in flight", async f =>
{
    f.Transport.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var write = f.Send("setTemperature", 24, 23);
    var query = f.Send("get_data", null, null);
    Equal(2, f.Transport.Sent.Count);
    f.Transport.Wait.SetResult();
    State("pending", await write);
    State("requested", await query);
});
Case("Atomic preferences round trip and reject malformed ids", async f =>
{
    await f.Store.SaveAsync([new(Fixture.Id), new(Fixture.Id), new("second-device")]);
    Equal(2, (await f.Store.LoadAsync()).Count);
    var disk = new KnownDeviceStore(f.Options, NullLogger<KnownDeviceStore>.Instance);
    Equal(2, (await disk.LoadAsync()).Count);
    try { await f.Store.SaveAsync([new("../escape")]); throw new Exception("Invalid ID accepted"); }
    catch (ArgumentException) { }
    Equal(2, (await disk.LoadAsync()).Count);
});
Case("Corrupt preferences preserved and reported", async f =>
{
    await File.WriteAllTextAsync(f.Options.KnownDevicesPath, "{broken");
    var disk = new KnownDeviceStore(f.Options, NullLogger<KnownDeviceStore>.Instance);
    try { await disk.LoadAsync(); throw new Exception("Corrupt list accepted"); }
    catch (InvalidDataException) { }
    Equal("{broken", await File.ReadAllTextAsync(f.Options.KnownDevicesPath));
});
Case("Telemetry numeric strings use invariant culture", async f =>
{
    var previous = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
        f.Report(new() { ["set_temp"] = "23.5" });
        State("pending", await f.Send("setTemperature", 24.5, 23.5));
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
});
Case("Appearance persists beside devices across new store instances and invalid writes preserve files", async f =>
{
    var store = new AppearanceStore(f.Options);
    Equal(AppearanceSettings.Default, await store.LoadAsync());
    var settingsFile = Path.Combine(f.Options.DataDirectory, "settings.json");
    await File.WriteAllTextAsync(settingsFile, "private settings sentinel");
    var devices = await File.ReadAllTextAsync(f.Options.KnownDevicesPath);
    await store.SaveAsync(new("dark", "#8A5CCF"));
    Equal(new AppearanceSettings("dark", "#8a5ccf"), await new AppearanceStore(f.Options).LoadAsync());
    Equal(Path.GetDirectoryName(f.Options.KnownDevicesPath), Path.GetDirectoryName(f.Options.AppearancePath));
    var saved = await File.ReadAllTextAsync(f.Options.AppearancePath);
    foreach (var invalid in new AppearanceSettings[] { new("other", "#147c6e"), new("dark", "red"), new("dark", null!) })
    {
        try { await store.SaveAsync(invalid); throw new Exception("Invalid appearance accepted"); }
        catch (ArgumentException) { }
        Equal(saved, await File.ReadAllTextAsync(f.Options.AppearancePath));
    }
    Equal(devices, await File.ReadAllTextAsync(f.Options.KnownDevicesPath));
    Equal("private settings sentinel", await File.ReadAllTextAsync(settingsFile));
    await store.SaveAsync(new("light", "#4e79df"));
    Equal(saved, await File.ReadAllTextAsync(f.Options.AppearancePath + ".bak"));
    await File.WriteAllTextAsync(f.Options.AppearancePath, "{broken");
    try { await new AppearanceStore(f.Options).LoadAsync(); throw new Exception("Corrupt appearance accepted"); }
    catch (InvalidDataException) { }
    Equal("{broken", await File.ReadAllTextAsync(f.Options.AppearancePath));
});
cases.Add(("LAN binding requires a strong access key", () =>
{
    try { (new HvacrAppOptions { Host = "0.0.0.0" }).Validate(); throw new Exception("Unprotected LAN accepted"); }
    catch (InvalidDataException) { }
    return Task.CompletedTask;
}));
Case("Packaged defaults bootstrap settings without overwriting saved data or explicit configuration", async f =>
{
    string[] keys = ["HVACR_DATA_DIR", "HVACR_SETTINGS", "MQTT_BROKER", "MQTT_USER", "MQTT_PASS",
        "HVACR_ACCESS_TOKEN", "HVACR_READ_ONLY", "HVACR_SIMULATION", "HOST", "PORT", "APP_TITLE"];
    var original = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    const string defaults = """{"mqttBroker":"mqtt://127.0.0.1:1883","mqttUser":"offline-user","mqttPass":"offline-password","readOnly":true}""";
    var path = Path.Combine(f.Options.DataDirectory, "settings.json");
    var devices = await File.ReadAllTextAsync(f.Options.KnownDevicesPath);
    try
    {
        foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
        Environment.SetEnvironmentVariable("HVACR_DATA_DIR", f.Options.DataDirectory);
        try { HvacrAppOptions.FromEnvironment(fallbackSettingsJson: "{broken"); throw new Exception("Malformed defaults accepted"); }
        catch (JsonException) { }
        Equal(false, File.Exists(path));
        var loaded = HvacrAppOptions.FromEnvironment(fallbackSettingsJson: defaults);
        Equal("offline-user", loaded.MqttUser); Equal("offline-password", loaded.MqttPass);
        Equal(true, loaded.ReadOnly); Equal(defaults, await File.ReadAllTextAsync(path));
        Equal(devices, await File.ReadAllTextAsync(f.Options.KnownDevicesPath));
        const string custom = """{"mqttBroker":"mqtt://127.0.0.1:1884","mqttUser":"saved-user","mqttPass":"saved-password","readOnly":false}""";
        await File.WriteAllTextAsync(path, custom);
        loaded = HvacrAppOptions.FromEnvironment(fallbackSettingsJson: defaults);
        Equal("saved-user", loaded.MqttUser); Equal(false, loaded.ReadOnly);
        Equal(custom, await File.ReadAllTextAsync(path));
        Environment.SetEnvironmentVariable("MQTT_USER", "environment-user");
        Equal("environment-user", HvacrAppOptions.FromEnvironment(fallbackSettingsJson: defaults).MqttUser);
        Equal(custom, await File.ReadAllTextAsync(path));
        Environment.SetEnvironmentVariable("MQTT_USER", null);
        var explicitPath = Path.Combine(f.Options.DataDirectory, "custom.json");
        await File.WriteAllTextAsync(explicitPath, defaults);
        Environment.SetEnvironmentVariable("HVACR_SETTINGS", explicitPath);
        Equal("offline-user", HvacrAppOptions.FromEnvironment(fallbackSettingsJson: defaults).MqttUser);
        Environment.SetEnvironmentVariable("HVACR_SETTINGS", explicitPath + ".missing");
        Equal("", HvacrAppOptions.FromEnvironment(fallbackSettingsJson: defaults).MqttUser);
        Equal(custom, await File.ReadAllTextAsync(path));
        Equal(0, Directory.GetFiles(f.Options.DataDirectory, "settings.json.*.tmp").Length);
    }
    finally { foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value); }
});
Case("First-run connection setup validates and atomically saves only connection settings", async f =>
{
    var path = Path.Combine(f.Options.DataDirectory, "settings.json");
    var devices = Path.Combine(f.Options.DataDirectory, "devices.json");
    await File.WriteAllTextAsync(devices, "device-record-sentinel");
    var setup = f.Options with { MqttBroker = "mqtt://offline.invalid:1883", MqttUser = "offline-user", MqttPass = "offline-password" };
    ConnectionSettingsStore.Save(setup);
    var original = await File.ReadAllTextAsync(path);
    try { ConnectionSettingsStore.Save(setup with { MqttBroker = "https://invalid.example" }); throw new Exception("Invalid broker accepted"); }
    catch (InvalidDataException) { }
    try { ConnectionSettingsStore.Save(setup with { MqttPass = "" }); throw new Exception("Empty credentials accepted"); }
    catch (InvalidDataException) { }
    Equal(original, await File.ReadAllTextAsync(path));
    ConnectionSettingsStore.Save(setup with { ReadOnly = false });
    Equal(original, await File.ReadAllTextAsync(path + ".bak"));
    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    Equal("offline-user", saved.RootElement.GetProperty("mqttUser").GetString());
    Equal(false, saved.RootElement.GetProperty("readOnly").GetBoolean());
    Equal("device-record-sentinel", await File.ReadAllTextAsync(devices));
    Equal(0, Directory.GetFiles(f.Options.DataDirectory, "settings.json.*.tmp").Length);
});
cases.Add(("HTTP integration: offline simulation, security, control and persisted preferences", async () =>
{
    var directory = Fixture.DirectoryPath();
    var options = new HvacrAppOptions { Simulation = true, ReadOnly = false, Port = HvacrAppOptions.FindAvailablePort(43100), DataDirectory = directory };
    await using var host = await HvacrApplication.StartAsync(options);
    using var client = new HttpClient { BaseAddress = host.BaseAddress };
    using var config = JsonDocument.Parse(await client.GetStringAsync("/api/config"));
    var session = config.RootElement.GetProperty("sessionToken").GetString()!;
    Equal(true, config.RootElement.GetProperty("simulation").GetBoolean());
    Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/preferences/appearance", new { theme = "dark", accent = "#4e79df" })).StatusCode);
    Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/control", new { deviceId = "demo-compressor01", action = "get_data" })).StatusCode);
    client.DefaultRequestHeaders.Add("X-Hvacr-Session", session);
    client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
    Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/status")).StatusCode);
    client.DefaultRequestHeaders.Remove("Origin");
    Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/preferences/appearance", new { theme = "dark", accent = "#4e79df" })).StatusCode);
    Equal(new AppearanceSettings("dark", "#4e79df"), await new AppearanceStore(options).LoadAsync());
    Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/preferences/appearance", new { theme = "dark", accent = "invalid" })).StatusCode);
    using var appearance = JsonDocument.Parse(await client.GetStringAsync("/api/preferences/appearance"));
    Equal("dark", appearance.RootElement.GetProperty("theme").GetString());
    Equal(true, appearance.RootElement.GetProperty("saved").GetBoolean());
    client.DefaultRequestHeaders.Host = "evil.example";
    Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/config")).StatusCode);
    client.DefaultRequestHeaders.Host = null;
    Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/unknown")).StatusCode);
    Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/control", new { deviceId = "demo-compressor01", action = "pump_switch", value = false })).StatusCode);
    var response = await client.PostAsJsonAsync("/api/control", new { deviceId = "demo-compressor01", action = "setTemperature", value = 24, expectedValue = 23, commandId = Guid.NewGuid() });
    Equal(HttpStatusCode.Accepted, response.StatusCode);
    using var status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
    Equal(24, status.RootElement.GetProperty("devices")[0].GetProperty("payload").GetProperty("set_temp").GetInt32());
    Equal("confirmed", status.RootElement.GetProperty("commands")[0].GetProperty("command").GetProperty("state").GetString());
    Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/preferences/devices", new { devices = new[] { new { deviceId = "#" } } })).StatusCode);
    Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/control", new StringContent("hello"))).StatusCode);
    Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/control", new StringContent("{broken", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
}));
cases.Add(("Appearance survives a full host restart on a different port", async () =>
{
    var options = new HvacrAppOptions { Simulation = true,
        Port = HvacrAppOptions.FindAvailablePort(43104), DataDirectory = Fixture.DirectoryPath() };
    await using (var first = await HvacrApplication.StartAsync(options))
    {
        using var client = new HttpClient { BaseAddress = first.BaseAddress };
        using var config = JsonDocument.Parse(await client.GetStringAsync("/api/config"));
        client.DefaultRequestHeaders.Add("X-Hvacr-Session", config.RootElement.GetProperty("sessionToken").GetString());
        Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/preferences/appearance", new { theme = "dark", accent = "#8a5ccf" })).StatusCode);
    }
    await using var second = await HvacrApplication.StartAsync(options with { Port = HvacrAppOptions.FindAvailablePort(43105) });
    using var restarted = new HttpClient { BaseAddress = second.BaseAddress };
    using var appearance = JsonDocument.Parse(await restarted.GetStringAsync("/api/preferences/appearance"));
    Equal("dark", appearance.RootElement.GetProperty("theme").GetString());
    Equal("#8a5ccf", appearance.RootElement.GetProperty("accent").GetString());
    Equal(true, appearance.RootElement.GetProperty("saved").GetBoolean());
}));
cases.Add(("HTTP bearer authentication protects reads and writes", async () =>
{
    var options = new HvacrAppOptions { Simulation = true, ReadOnly = true, AccessToken = new('a', 48),
        Port = HvacrAppOptions.FindAvailablePort(43101), DataDirectory = Fixture.DirectoryPath() };
    await using var host = await HvacrApplication.StartAsync(options);
    using var client = new HttpClient { BaseAddress = host.BaseAddress };
    Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
    Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/events")).StatusCode);
    Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/preferences/appearance")).StatusCode);
    client.DefaultRequestHeaders.Authorization = new("Bearer", options.AccessToken);
    using var config = JsonDocument.Parse(await client.GetStringAsync("/api/config"));
    client.DefaultRequestHeaders.Add("X-Hvacr-Session", config.RootElement.GetProperty("sessionToken").GetString());
    Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/control", new { deviceId = "demo-compressor01", action = "stop", expectedValue = true, confirmPower = true, commandId = Guid.NewGuid() })).StatusCode);
}));

cases.Add(("HTTP control mode requires session and confirmation; switching does not send device commands", async () =>
{
    var options = new HvacrAppOptions { Simulation = true, ReadOnly = true,
        Port = HvacrAppOptions.FindAvailablePort(43102), DataDirectory = Fixture.DirectoryPath() };
    await using var host = await HvacrApplication.StartAsync(options);
    using var client = new HttpClient { BaseAddress = host.BaseAddress };
    Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/mode", new { readOnly = false, confirmEnable = true })).StatusCode);
    using var config = JsonDocument.Parse(await client.GetStringAsync("/api/config"));
    Equal(true, config.RootElement.GetProperty("readOnly").GetBoolean());
    client.DefaultRequestHeaders.Add("X-Hvacr-Session", config.RootElement.GetProperty("sessionToken").GetString());
    Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/mode", new { })).StatusCode);
    Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/mode", new { readOnly = false })).StatusCode);
    Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/mode", new { readOnly = false, confirmEnable = true })).StatusCode);
    using var status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
    Equal(false, status.RootElement.GetProperty("readOnly").GetBoolean());
    Equal(false, status.RootElement.GetProperty("commands")[0].TryGetProperty("command", out _));
    var command = new { deviceId = "demo-compressor01", action = "setTemperature", value = 10, expectedValue = 23, commandId = Guid.NewGuid() };
    Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/control", command)).StatusCode);
    Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/mode", new { readOnly = true })).StatusCode);
    Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/control", new { deviceId = "demo-compressor01", action = "setWindSpeed", value = 6, expectedValue = 5, commandId = Guid.NewGuid() })).StatusCode);
}));

cases.Add(("HTTP real-time stream delivers confirmed telemetry within one second", async () =>
{
    var options = new HvacrAppOptions { Simulation = true, ReadOnly = false,
        Port = HvacrAppOptions.FindAvailablePort(43103), DataDirectory = Fixture.DirectoryPath() };
    await using var host = await HvacrApplication.StartAsync(options);
    using var client = new HttpClient { BaseAddress = host.BaseAddress };
    using var config = JsonDocument.Parse(await client.GetStringAsync("/api/config"));
    client.DefaultRequestHeaders.Add("X-Hvacr-Session", config.RootElement.GetProperty("sessionToken").GetString());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    using var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
    using var initial = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))![6..]);
    Equal(23, initial.RootElement.GetProperty("devices")[0].GetProperty("payload").GetProperty("set_temp").GetInt32());
    var timer = System.Diagnostics.Stopwatch.StartNew();
    Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/control", new { deviceId = "demo-compressor01",
        action = "setTemperature", value = 24, expectedValue = 23, commandId = Guid.NewGuid() }, timeout.Token)).StatusCode);
    while (true)
    {
        var line = await reader.ReadLineAsync(timeout.Token);
        if (line?.StartsWith("data: ") != true) continue;
        using var update = JsonDocument.Parse(line[6..]);
        if (update.RootElement.GetProperty("commands")[0].TryGetProperty("command", out var command)
            && command.GetProperty("state").GetString() == "confirmed")
        {
            Equal(24, update.RootElement.GetProperty("devices")[0].GetProperty("payload").GetProperty("set_temp").GetInt32());
            if (timer.ElapsedMilliseconds >= 1000) throw new Exception($"Slow stream: {timer.ElapsedMilliseconds} ms");
            break;
        }
    }
}));

foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"RESULT {cases.Count - failures}/{cases.Count} passed");
return failures == 0 ? 0 : 1;

sealed class Fixture : IAsyncDisposable
{
    public const string Id = "test-device";
    public HvacrAppOptions Options { get; }
    public ManualClock Clock { get; } = new();
    public FakeTransport Transport { get; } = new();
    public DeviceRegistry Registry { get; }
    public KnownDeviceStore Store { get; }
    public CommandCoordinator Commands { get; }
    private Fixture()
    {
        Options = new() { ReadOnly = false, DataDirectory = DirectoryPath() };
        Registry = new(Options, Clock);
        Store = new(Options, NullLogger<KnownDeviceStore>.Instance);
        Commands = new(Options, Registry, Store, Transport, Clock);
    }
    public static string DirectoryPath() => Path.Combine(Directory.GetCurrentDirectory(), ".test-data", Guid.NewGuid().ToString("N"));
    public static async Task<Fixture> Create()
    {
        var f = new Fixture();
        await f.Store.SaveAsync([new(Id)]);
        f.Report(new() { ["set_temp"] = 23, ["wind_speed_set"] = 5, ["power"] = true });
        return f;
    }
    public void Report(JsonObject payload, bool retained = false) => Registry.Upsert(Id, $"{Id}/bxkt/esp", payload, Clock.GetUtcNow(), retained);
    public decimal Number(string field)
    {
        if (!CommandCoordinator.TryNumber(Registry.SnapshotList()[0].Payload[field], out var value)) throw new Exception("Missing number");
        return value;
    }
    public ControlCommandRequest Request(string action, object? value, object? expected) => new()
    {
        DeviceId = Id, Action = action, Value = JsonSerializer.SerializeToElement(value),
        ExpectedValue = JsonSerializer.SerializeToElement(expected), CommandId = Guid.NewGuid()
    };
    public Task<ControlResult> Send(string action, object? value, object? expected) => Commands.ExecuteAsync(Request(action, value, expected));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan amount) => _now += amount;
}
sealed class FakeTransport : ICommandTransport
{
    public bool Connected { get; set; } = true;
    public bool Fail { get; set; }
    public TaskCompletionSource? Wait { get; set; }
    public List<JsonObject> Sent { get; } = [];
    public async Task PublishAsync(string deviceId, JsonObject payload, CancellationToken ct)
    {
        Sent.Add((JsonObject)payload.DeepClone());
        if (Wait is not null) await Wait.Task.WaitAsync(ct);
        if (Fail) throw new IOException("Injected transport fault");
    }
}

