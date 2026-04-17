using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Hvacr.App;

public sealed class MqttBridgeService : BackgroundService
{
    private readonly HvacrAppOptions _options;
    private readonly DeviceRegistry _registry;
    private readonly MqttConnectionState _connectionState;
    private readonly ILogger<MqttBridgeService> _logger;
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public MqttBridgeService(
        HvacrAppOptions options,
        DeviceRegistry registry,
        MqttConnectionState connectionState,
        ILogger<MqttBridgeService> logger)
    {
        _options = options;
        _registry = registry;
        _connectionState = connectionState;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ConnectedAsync += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public MqttStatusSnapshot Snapshot() => _connectionState.Snapshot();

    public async Task<PublishCommandResult> PublishAsync(
        string deviceId,
        string action,
        JsonElement? value,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(action, value);
        var topic = $"{deviceId}{_options.CommandSuffix}";

        await EnsureConnectedAsync(cancellationToken);
        if (!_client.IsConnected)
        {
            return new PublishCommandResult
            {
                Success = false,
                Topic = topic,
                Payload = payload,
                Error = _connectionState.Snapshot().LastError ?? "MQTT 未连接"
            };
        }

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload.ToJsonString())
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _client.PublishAsync(message, cancellationToken);
            _registry.ApplyOptimisticUpdate(deviceId, action, payload);

            return new PublishCommandResult
            {
                Success = true,
                Topic = topic,
                Payload = payload
            };
        }
        catch (Exception ex)
        {
            _connectionState.SetDisconnected(ex.Message);
            _logger.LogWarning(ex, "Publish failed for {DeviceId}", deviceId);
            return new PublishCommandResult
            {
                Success = false,
                Topic = topic,
                Payload = payload,
                Error = "Publish failed"
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanupAt = DateTimeOffset.UtcNow + _options.CleanupInterval;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await EnsureConnectedAsync(stoppingToken);

                var now = DateTimeOffset.UtcNow;
                if (now >= nextCleanupAt)
                {
                    _registry.PruneExpired(now - _options.DeviceTtl);
                    nextCleanupAt = now + _options.CleanupInterval;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_client.IsConnected)
            {
                try
                {
                    await _client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_client.IsConnected)
            {
                return;
            }

            var options = BuildClientOptions();
            await _client.ConnectAsync(options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _connectionState.SetDisconnected(ex.Message);
            _logger.LogWarning(ex, "MQTT connect failed");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _logger.LogInformation("MQTT connected: {Broker}", _options.MqttBroker);
        _connectionState.SetConnected();

        return _client.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic(_options.SubscriptionTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var reason = args.Exception?.Message ?? args.ReasonString ?? "Connection closed";
        _connectionState.SetDisconnected(reason);
        _logger.LogWarning(args.Exception, "MQTT disconnected: {Reason}", reason);
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic ?? string.Empty;
        if (topic.EndsWith(_options.CommandSuffix, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var payloadText = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;
        var deviceId = topic.Split('/', 2, StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Task.CompletedTask;
        }

        if (TryParseJson(payloadText, out var jsonPayload))
        {
            _registry.Upsert(deviceId, topic, jsonPayload, null, DateTimeOffset.UtcNow);
        }
        else
        {
            _registry.Upsert(deviceId, topic, null, payloadText, DateTimeOffset.UtcNow);
        }

        return Task.CompletedTask;
    }

    private MqttClientOptions BuildClientOptions()
    {
        var brokerUri = new Uri(_options.MqttBroker);
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(brokerUri.Host, brokerUri.Port > 0 ? brokerUri.Port : 1883)
            .WithTimeout(_options.RequestTimeout)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(_options.MqttUser))
        {
            builder.WithCredentials(_options.MqttUser, _options.MqttPass);
        }

        return builder.Build();
    }

    private static bool TryParseJson(string payloadText, out JsonNode? jsonPayload)
    {
        try
        {
            jsonPayload = JsonNode.Parse(payloadText);
            return true;
        }
        catch
        {
            jsonPayload = null;
            return false;
        }
    }

    private static JsonObject BuildPayload(string action, JsonElement? value)
    {
        return action switch
        {
            "get_data" => new JsonObject { ["get_data"] = 1 },
            "start" => new JsonObject { ["power"] = true },
            "stop" => new JsonObject { ["power"] = false },
            "setTemperature" => new JsonObject { ["set_temp"] = ReadNumber(value) },
            "setWindSpeed" => new JsonObject { ["wind_speed_set"] = ReadNumber(value) },
            _ => new JsonObject { [action] = ConvertElement(value) ?? JsonValue.Create(1) }
        };
    }

    private static JsonNode? ConvertElement(JsonElement? element)
    {
        if (!element.HasValue)
        {
            return null;
        }

        return JsonNode.Parse(element.Value.GetRawText());
    }

    private static JsonNode ReadNumber(JsonElement? element)
    {
        if (!element.HasValue)
        {
            return JsonValue.Create(0)!;
        }

        var value = element.Value;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return JsonValue.Create(number)!;
        }

        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return JsonValue.Create(parsed)!;
        }

        return JsonValue.Create(0)!;
    }
}
