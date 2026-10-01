using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Hvacr.App;

public sealed class MqttBridgeService : BackgroundService, ICommandTransport
{
    private readonly HvacrAppOptions _options;
    private readonly DeviceRegistry _registry;
    private readonly KnownDeviceStore _store;
    private readonly MqttConnectionState _state;
    private readonly TimeProvider _clock;
    private readonly ILogger<MqttBridgeService> _logger;
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim _subscriptionsGate = new(1, 1);
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    public bool Connected => _client.IsConnected && _state.Snapshot().Connected;

    public MqttBridgeService(HvacrAppOptions options, DeviceRegistry registry, KnownDeviceStore store,
        MqttConnectionState state, TimeProvider clock, ILogger<MqttBridgeService> logger)
    {
        _options = options;
        _registry = registry;
        _store = store;
        _state = state;
        _clock = clock;
        _logger = logger;
        _client.ConnectedAsync += async _ =>
        {
            _registry.InvalidateEvidence();
            await _subscriptionsGate.WaitAsync();
            try { _subscribed.Clear(); }
            finally { _subscriptionsGate.Release(); }
            await SyncSubscriptionsAsync(CancellationToken.None);
            _state.SetConnected();
        };
        _client.DisconnectedAsync += _ =>
        {
            _state.SetDisconnected("MQTT 已断开，正在等待重连。");
            _registry.InvalidateEvidence();
            return Task.CompletedTask;
        };
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task PublishAsync(string deviceId, JsonObject payload, CancellationToken cancellationToken)
    {
        // Connecting belongs to the background loop. Control writes are never queued or replayed.
        if (!Connected) throw new InvalidOperationException("MQTT 未连接。");
        var result = await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"{deviceId}/app").WithPayload(payload.ToJsonString())
            .WithRetainFlag(false).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), cancellationToken);
        if (!result.IsSuccess) throw new IOException("MQTT 发布未成功。");
    }

    public async Task SyncSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        await _subscriptionsGate.WaitAsync(timeout.Token);
        try
        {
            if (!_client.IsConnected) return;
            var ids = (await _store.LoadAsync(timeout.Token)).Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _subscribed.Except(ids).ToArray())
            {
                await _client.UnsubscribeAsync($"{id}/bxkt/esp", timeout.Token);
                _subscribed.Remove(id);
            }
            foreach (var id in ids.Except(_subscribed).ToArray())
            {
                var result = await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic($"{id}/bxkt/esp")
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)).Build(), timeout.Token);
                if (result.Items.Any(item => (int)item.ResultCode > 2))
                    throw new IOException("MQTT 订阅被服务器拒绝。");
                _subscribed.Add(id);
            }
            _registry.KeepOnly(ids);
        }
        finally { _subscriptionsGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MqttUser) || string.IsNullOrEmpty(_options.MqttPass))
        {
            _state.SetDisconnected("尚未配置 MQTT 凭据，请填写本地 settings.json。");
            return;
        }
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_client.IsConnected)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(_options.RequestTimeout);
                        await _client.ConnectAsync(BuildClientOptions(), timeout.Token);
                    }
                    await SyncSubscriptionsAsync(stoppingToken);
                    failures = 0;
                    _registry.PruneExpired(_clock.GetUtcNow() - _options.DeviceTtl);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _state.SetDisconnected("MQTT 连接或订阅失败，将自动重连。");
                    _logger.LogWarning("MQTT 连接/订阅失败 ({Type})", ex.GetType().Name);
                    if (_client.IsConnected)
                    {
                        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
                        try { await _client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token); }
                        catch (Exception) { }
                    }
                    var seconds = Math.Min(60, Math.Pow(2, Math.Min(++failures, 6))) + Random.Shared.NextDouble();
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (_client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(_options.RequestTimeout);
                try { await _client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token); }
                catch (Exception) { }
            }
            _state.SetDisconnected("服务已停止。");
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var message = args.ApplicationMessage;
        var topic = message.Topic ?? "";
        var parts = topic.Split('/');
        if (parts.Length != 3 || parts[1] != "bxkt" || parts[2] != "esp"
            || !DeviceIdentity.IsValid(parts[0]) || message.Payload.Length > 16_384) return;
        if (!(await _store.LoadAsync()).Any(d => d.DeviceId == parts[0])) return;
        try
        {
            if (JsonNode.Parse(message.ConvertPayloadToString(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) is JsonObject payload)
                _registry.Upsert(parts[0], topic, payload, _clock.GetUtcNow(), message.Retain);
        }
        catch (JsonException) { _logger.LogDebug("忽略格式无效的设备遥测。"); }
    }

    private MqttClientOptions BuildClientOptions()
    {
        var uri = new Uri(_options.MqttBroker);
        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"hvacr-{Guid.NewGuid():N}")
            .WithTcpServer(uri.Host, uri.Port > 0 ? uri.Port : uri.Scheme == "mqtts" ? 8883 : 1883)
            .WithCredentials(_options.MqttUser, _options.MqttPass)
            .WithCleanSession().WithTimeout(_options.RequestTimeout)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        if (uri.Scheme == "mqtts") builder.WithTlsOptions(tls => tls.UseTls());
        return builder.Build();
    }

    public override void Dispose()
    {
        _client.Dispose();
        _subscriptionsGate.Dispose();
        base.Dispose();
    }
}

