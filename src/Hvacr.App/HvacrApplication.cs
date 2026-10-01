using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hvacr.App;

public static class HvacrApplication
{
    public static async Task<HvacrHost> StartAsync(HvacrAppOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(HvacrApplication).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 32_768);
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.MaxDepth = 32;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StatusUpdates>();
        builder.Services.AddSingleton<RuntimeState>();
        builder.Services.AddSingleton<LocalApiGuard>();
        builder.Services.AddSingleton<DeviceRegistry>();
        builder.Services.AddSingleton<KnownDeviceStore>();
        builder.Services.AddSingleton<AppearanceStore>();
        builder.Services.AddSingleton<MqttConnectionState>();
        builder.Services.AddSingleton<ControlModeState>();
        builder.Services.AddSingleton<CommandCoordinator>();
        builder.Services.AddSingleton<IFileProvider>(_ => new ManifestEmbeddedFileProvider(typeof(HvacrApplication).Assembly, "public"));
        if (options.Simulation)
        {
            builder.Services.AddSingleton<SimulationTransport>();
            builder.Services.AddSingleton<ICommandTransport>(sp => sp.GetRequiredService<SimulationTransport>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationTransport>());
        }
        else
        {
            builder.Services.AddSingleton<MqttBridgeService>();
            builder.Services.AddSingleton<ICommandTransport>(sp => sp.GetRequiredService<MqttBridgeService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBridgeService>());
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (!await context.RequestServices.GetRequiredService<LocalApiGuard>().ValidateAsync(context)) return;
            try { await next(context); }
            catch (ArgumentException) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "设备列表格式无效：最多 100 台，ID 只允许 1–64 位字母、数字、下划线或短横线。" });
            }
            catch (IOException) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "本地配置读写失败，原文件已保留。请检查数据目录。" });
            }
            catch (UnauthorizedAccessException) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "无法访问本地数据目录，请检查目录权限。" });
            }
        });
        MapApi(app, options);
        MapStaticFiles(app);
        try { await app.StartAsync(cancellationToken); }
        catch { await app.DisposeAsync(); throw; }
        return new HvacrHost(app, options);
    }

    private static void MapApi(WebApplication app, HvacrAppOptions options)
    {
        app.MapGet("/api/config", (LocalApiGuard guard, ControlModeState mode) => Results.Json(new
        {
            title = options.AppTitle,
            sessionToken = guard.SessionToken,
            simulation = options.Simulation,
            readOnly = mode.ReadOnly,
            telemetryMaxAgeMs = options.TelemetryMaxAge.TotalMilliseconds,
            confirmationTimeoutMs = options.ConfirmationTimeout.TotalMilliseconds,
            cloudAuthorizationVerified = false
        }));
        app.MapGet("/api/health", (RuntimeState runtime) => Results.Json(new { ok = true, uptime = runtime.UptimeSeconds }));
        app.MapPut("/api/mode", (ControlModeRequest request, ControlModeState mode) =>
        {
            if (request.ReadOnly is null)
                return Results.BadRequest(new { error = "需要指定 readOnly。" });
            if (request.ReadOnly == false && !request.ConfirmEnable)
                return Results.BadRequest(new { error = "启用控制需要明确确认。" });
            mode.SetReadOnly(request.ReadOnly.Value);
            return Results.Ok(new { readOnly = mode.ReadOnly });
        });
        object Status(DeviceRegistry registry, MqttConnectionState connection, CommandCoordinator commands, ControlModeState mode, StatusUpdates updates)
        {
            var status = connection.Snapshot();
            var devices = registry.SnapshotList();
            return new
            {
                stateRevision = updates.Version,
                connected = status.Connected,
                readOnly = mode.ReadOnly,
                lastError = status.LastError,
                serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                devices,
                commands = devices.Select(d => new { deviceId = d.DeviceId, command = commands.Snapshot(d.DeviceId),
                    setpoints = commands.SetpointsSnapshot(d.DeviceId) })
            };
        }
        app.MapGet("/api/status", (DeviceRegistry registry, MqttConnectionState connection, CommandCoordinator commands, ControlModeState mode, StatusUpdates updates) =>
            Results.Json(Status(registry, connection, commands, mode, updates)));
        app.MapGet("/api/events", async (HttpContext context, DeviceRegistry registry, MqttConnectionState connection,
            CommandCoordinator commands, ControlModeState mode, StatusUpdates updates) =>
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var version = updates.Version;
                    var json = JsonSerializer.Serialize(Status(registry, connection, commands, mode, updates), jsonOptions);
                    await context.Response.WriteAsync("data: " + json + "\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await updates.WaitAsync(version, context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        app.MapGet("/api/devices", (DeviceRegistry registry, MqttConnectionState connection) =>
            Results.Json(new { connected = connection.Snapshot().Connected, devices = registry.SnapshotList() }));
        app.MapGet("/api/preferences/devices", async (KnownDeviceStore store, CancellationToken ct) =>
            Results.Json(new KnownDevicesPayload { Devices = await store.LoadAsync(ct) }));
        app.MapGet("/api/preferences/appearance", async (AppearanceStore store, CancellationToken ct) =>
        {
            var appearance = await store.LoadAsync(ct);
            return Results.Json(new { appearance.Theme, appearance.Accent, saved = store.Exists });
        });
        app.MapPut("/api/preferences/appearance", async (AppearanceSettings appearance, AppearanceStore store, CancellationToken ct) =>
        {
            try { return Results.Json(await store.SaveAsync(appearance, ct)); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "请选择有效的显示模式，并输入 # 开头的六位主颜色。" }); }
        });
        app.MapPut("/api/preferences/devices", async (KnownDevicesPayload payload, KnownDeviceStore store,
            DeviceRegistry registry, CancellationToken ct) =>
        {
            var devices = await store.SaveAsync(payload.Devices, ct);
            registry.KeepOnly(devices.Select(d => d.DeviceId));
            if (!options.Simulation)
            {
                // Preferences persist even if the broker is offline; background sync will retry.
                try { await app.Services.GetRequiredService<MqttBridgeService>().SyncSubscriptionsAsync(ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            return Results.Json(new KnownDevicesPayload { Devices = devices });
        });
        app.MapPost("/api/control", async (ControlCommandRequest request, CommandCoordinator commands, CancellationToken ct) =>
        {
            var result = await commands.ExecuteAsync(request, ct);
            return Results.Json(new { result.Success, result.State, result.Message, result.CommandId,
                error = result.Success ? null : result.Message }, statusCode: result.HttpStatus);
        });
        app.MapMethods("/api/{**path}", [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete],
            () => Results.NotFound(new { error = "接口不存在。" }));
    }

    private static void MapStaticFiles(WebApplication app)
    {
        app.MapGet("/{**path}", (string? path, IFileProvider provider) =>
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? "index.html" : path.TrimStart('/');
            var file = provider.GetFileInfo(normalized);
            if (!file.Exists || file.IsDirectory) return Results.NotFound();
            var contentType = Path.GetExtension(normalized).ToLowerInvariant() switch
            {
                ".css" => "text/css; charset=utf-8", ".js" => "application/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8", ".svg" => "image/svg+xml",
                ".png" => "image/png", ".ico" => "image/x-icon", _ => "text/html; charset=utf-8"
            };
            return Results.File(file.CreateReadStream(), contentType);
        });
    }
}

public sealed class HvacrHost(WebApplication application, HvacrAppOptions options) : IAsyncDisposable
{
    private int _disposed;
    public HvacrAppOptions Options { get; } = options;
    public Uri BaseAddress => new(Options.IsLoopback ? Options.LoopbackUrl : Options.ListenUrl);
    public Task WaitForShutdownAsync(CancellationToken ct = default) => application.WaitForShutdownAsync(ct);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await application.StopAsync(timeout.Token); }
        finally { await application.DisposeAsync(); }
    }
}

