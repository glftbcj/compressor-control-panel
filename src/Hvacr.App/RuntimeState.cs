using System.Diagnostics;

namespace Hvacr.App;

public sealed class RuntimeState
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;
}

public sealed class MqttConnectionState
{
    private readonly object _sync = new();
    private bool _connected;
    private string? _lastError;

    public MqttStatusSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new MqttStatusSnapshot
            {
                Connected = _connected,
                LastError = _lastError
            };
        }
    }

    public void SetConnected()
    {
        lock (_sync)
        {
            _connected = true;
            _lastError = null;
        }
    }

    public void SetDisconnected(string? error)
    {
        lock (_sync)
        {
            _connected = false;
            _lastError = string.IsNullOrWhiteSpace(error) ? "Connection closed" : error;
        }
    }
}
