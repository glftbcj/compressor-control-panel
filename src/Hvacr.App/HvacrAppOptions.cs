using System.Net;
using System.Net.Sockets;

namespace Hvacr.App;

public sealed record HvacrAppOptions
{
    public const string ApplicationDirectoryName = "HVACR";

    public string AppTitle { get; init; } = "压缩机控制面板";
    public string MqttBroker { get; init; } = "mqtt://www.cndq.xyz:1883";
    public string MqttUser { get; init; } = "cndq_bxkt";
    public string MqttPass { get; init; } = "08210012Abc";
    public string CommandSuffix { get; init; } = "/app";
    public string SubscriptionTopic { get; init; } = "+/bxkt/#";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 3000;
    public TimeSpan DeviceTtl { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public string DataDirectory { get; init; } = GetDefaultDataDirectory();

    public string ListenUrl => $"http://{Host}:{Port}";
    public string LoopbackUrl => $"http://127.0.0.1:{Port}";
    public string KnownDevicesPath => Path.Combine(DataDirectory, "devices.json");
    public string WebViewUserDataDirectory => Path.Combine(DataDirectory, "webview2");

    public static HvacrAppOptions FromEnvironment(int? overridePort = null, string? overrideHost = null)
    {
        return new HvacrAppOptions
        {
            AppTitle = ReadString("APP_TITLE", "压缩机控制面板"),
            MqttBroker = ReadString("MQTT_BROKER", "mqtt://www.cndq.xyz:1883"),
            MqttUser = ReadString("MQTT_USER", "cndq_bxkt"),
            MqttPass = ReadString("MQTT_PASS", "08210012Abc"),
            CommandSuffix = ReadString("CMD_SUFFIX", "/app"),
            SubscriptionTopic = ReadString("SUB_TOPIC", "+/bxkt/#"),
            Host = overrideHost ?? ReadString("HOST", "127.0.0.1"),
            Port = overridePort ?? ReadInt("PORT", 3000),
            DataDirectory = ReadString("HVACR_DATA_DIR", GetDefaultDataDirectory())
        };
    }

    public static int FindAvailablePort(int preferredPort = 3000)
    {
        if (CanBind(preferredPort))
        {
            return preferredPort;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string GetDefaultDataDirectory()
    {
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(baseDirectory, ApplicationDirectoryName);
    }

    private static string ReadString(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadInt(string key, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(key), out var value) && value > 0
            ? value
            : fallback;
    }
}
