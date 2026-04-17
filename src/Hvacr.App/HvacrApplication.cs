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
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.WebViewUserDataDirectory);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(HvacrApplication).Assembly.GetName().Name
        });

        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            jsonOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new RuntimeState());
        builder.Services.AddSingleton<DeviceRegistry>();
        builder.Services.AddSingleton<KnownDeviceStore>();
        builder.Services.AddSingleton<MqttConnectionState>();
        builder.Services.AddSingleton<IFileProvider>(_ => new ManifestEmbeddedFileProvider(typeof(HvacrApplication).Assembly, "public"));
        builder.Services.AddSingleton<MqttBridgeService>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MqttBridgeService>());

        var app = builder.Build();
        MapApi(app);
        MapStaticFiles(app);
        await app.StartAsync(cancellationToken);

        return new HvacrHost(app, options);
    }

    private static void MapApi(WebApplication app)
    {
        app.MapGet("/api/health", (RuntimeState runtimeState) =>
            Results.Json(new { ok = true, uptime = runtimeState.UptimeSeconds }));

        app.MapGet("/api/status", (DeviceRegistry registry, MqttConnectionState connectionState) =>
        {
            var status = connectionState.Snapshot();
            return Results.Json(new
            {
                connected = status.Connected,
                lastError = status.LastError,
                devices = registry.SnapshotList()
            });
        });

        app.MapGet("/api/devices", (DeviceRegistry registry, MqttConnectionState connectionState) =>
        {
            var status = connectionState.Snapshot();
            return Results.Json(new
            {
                connected = status.Connected,
                devices = registry.SnapshotList()
            });
        });

        app.MapGet("/api/preferences/devices", async (KnownDeviceStore store, CancellationToken cancellationToken) =>
        {
            var devices = await store.LoadAsync(cancellationToken);
            return Results.Json(new KnownDevicesPayload { Devices = devices });
        });

        app.MapPut("/api/preferences/devices", async (KnownDevicesPayload payload, KnownDeviceStore store, CancellationToken cancellationToken) =>
        {
            var devices = await store.SaveAsync(payload.Devices, cancellationToken);
            return Results.Json(new KnownDevicesPayload { Devices = devices });
        });

        app.MapPost("/api/control", async (ControlCommandRequest request, MqttBridgeService mqttBridge, CancellationToken cancellationToken) =>
        {
            var deviceId = request.DeviceId?.Trim();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Results.BadRequest(new { error = "Missing deviceId" });
            }

            var action = request.Action?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                return Results.BadRequest(new { error = "Missing action" });
            }

            if (!IsSupportedRequest(action, request.Value, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            var result = await mqttBridge.PublishAsync(deviceId, action, request.Value, cancellationToken);
            if (!result.Success)
            {
                return Results.Json(new { error = result.Error ?? "Publish failed" }, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new
            {
                success = true,
                sent = new
                {
                    topic = result.Topic,
                    payload = result.Payload
                }
            });
        });
    }

    private static void MapStaticFiles(WebApplication app)
    {
        app.MapMethods("/{**path}", new[] { HttpMethods.Get }, async (string? path, IFileProvider fileProvider, HttpContext context) =>
        {
            var normalizedPath = string.IsNullOrWhiteSpace(path) ? "index.html" : path.TrimStart('/');
            var fileInfo = fileProvider.GetFileInfo(normalizedPath);

            if (!fileInfo.Exists)
            {
                if (Path.HasExtension(normalizedPath))
                {
                    return Results.NotFound();
                }

                fileInfo = fileProvider.GetFileInfo("index.html");
            }

            if (!fileInfo.Exists)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            return Results.File(fileInfo.CreateReadStream(), GetContentType(normalizedPath));
        });
    }

    private static bool IsSupportedRequest(string action, JsonElement? value, out string? error)
    {
        error = null;
        if (action is "setTemperature")
        {
            if (!TryReadNumber(value, out var number) || number < -20 || number > 50)
            {
                error = "Temperature out of range";
                return false;
            }
        }
        else if (action is "setWindSpeed")
        {
            if (!TryReadNumber(value, out var number) || number < 0 || number > 10)
            {
                error = "Wind speed out of range";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadNumber(JsonElement? value, out decimal number)
    {
        number = 0;
        if (!value.HasValue)
        {
            return false;
        }

        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDecimal(out number))
        {
            return true;
        }

        return value.Value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.Value.GetString(), out number);
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => "text/html; charset=utf-8"
        };
    }
}

public sealed class HvacrHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    public HvacrHost(WebApplication application, HvacrAppOptions options)
    {
        _application = application;
        Options = options;
    }

    public HvacrAppOptions Options { get; }
    public Uri BaseAddress => new(Options.ListenUrl);

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        return _application.WaitForShutdownAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}
