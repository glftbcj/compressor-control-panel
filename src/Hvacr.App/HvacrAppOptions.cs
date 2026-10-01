using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Hvacr.App;

public sealed record HvacrAppOptions
{
    public string AppTitle { get; init; } = "压缩机控制面板";
    public string MqttBroker { get; init; } = "mqtt://www.cndq.xyz:1883";
    public string MqttUser { get; init; } = "";
    public string MqttPass { get; init; } = "";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 3000;
    public bool Simulation { get; init; }
    public bool ReadOnly { get; init; } = true;
    public string AccessToken { get; init; } = "";
    public TimeSpan TelemetryMaxAge { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan DeviceTtl { get; init; } = TimeSpan.FromMinutes(30);
    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HVACR");
    public string ListenUrl => $"http://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
    public string LoopbackUrl => $"http://127.0.0.1:{Port}";
    public string KnownDevicesPath => Path.Combine(DataDirectory, "devices.json");
    public string AppearancePath => Path.Combine(DataDirectory, "appearance.json");
    public string WebViewUserDataDirectory => Path.Combine(DataDirectory, "webview2");
    public bool IsLoopback => Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(Host, out var ip) && IPAddress.IsLoopback(ip));

    public static HvacrAppOptions FromEnvironment(int? overridePort = null, string? overrideHost = null,
        string? fallbackSettingsJson = null, bool preferDataDirectory = false)
    {
        var dataDirectory = Read("HVACR_DATA_DIR", new HvacrAppOptions().DataDirectory);
        var besideExecutable = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var savedSettings = Path.Combine(dataDirectory, "settings.json");
        var explicitPath = Environment.GetEnvironmentVariable("HVACR_SETTINGS");
        var defaultPath = (preferDataDirectory || fallbackSettingsJson is not null) && File.Exists(savedSettings) ? savedSettings
            : File.Exists(besideExecutable) ? besideExecutable : savedSettings;
        var path = explicitPath ?? defaultPath;
        var json = File.Exists(path) ? File.ReadAllText(path) : explicitPath is null ? fallbackSettingsJson : null;
        LocalSettings settings = json is not null
            ? JsonSerializer.Deserialize<LocalSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("本地配置不能为空。")
            : new();
        var options = new HvacrAppOptions
        {
            AppTitle = Read("APP_TITLE", "压缩机控制面板"),
            MqttBroker = Read("MQTT_BROKER", settings.MqttBroker ?? "mqtt://www.cndq.xyz:1883"),
            MqttUser = Read("MQTT_USER", settings.MqttUser ?? ""),
            MqttPass = Read("MQTT_PASS", settings.MqttPass ?? ""),
            AccessToken = Read("HVACR_ACCESS_TOKEN", settings.AccessToken ?? ""),
            Host = overrideHost ?? Read("HOST", "127.0.0.1"),
            Port = overridePort ?? ReadPort(),
            Simulation = ReadBool("HVACR_SIMULATION", false),
            ReadOnly = ReadBool("HVACR_READ_ONLY", settings.ReadOnly ?? true),
            DataDirectory = Path.GetFullPath(dataDirectory)
        };
        options.Validate();
        if ((preferDataDirectory || fallbackSettingsJson is not null) && explicitPath is null && json is not null)
            SaveInitialSettings(Path.Combine(options.DataDirectory, "settings.json"), json);
        return options;
    }

    private static void SaveInitialSettings(string path, string json)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { /* Preserve configuration created concurrently. */ }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new InvalidDataException("PORT 须为 1–65535。");
        if (!IPAddress.TryParse(Host, out _) && !Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HOST 须为 IP 地址或 localhost。");
        if (!IsLoopback && AccessToken.Length < 32)
            throw new InvalidDataException("局域网监听需要至少 32 字符的 HVACR_ACCESS_TOKEN。");
        if (!Uri.TryCreate(MqttBroker, UriKind.Absolute, out var broker)
            || broker.Scheme is not ("mqtt" or "mqtts") || string.IsNullOrEmpty(broker.Host)
            || !string.IsNullOrEmpty(broker.UserInfo) || broker.AbsolutePath != "/"
            || !string.IsNullOrEmpty(broker.Query) || !string.IsNullOrEmpty(broker.Fragment))
            throw new InvalidDataException("MQTT_BROKER 须为 mqtt://host:port 或 mqtts://host:port。");
        if (TelemetryMaxAge <= TimeSpan.Zero || ConfirmationTimeout <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero)
            throw new InvalidDataException("超时时间必须大于零。");
    }

    public static int FindAvailablePort(int preferredPort = 3000)
    {
        try
        {
            using var preferred = new TcpListener(IPAddress.Loopback, preferredPort);
            preferred.Start();
            return preferredPort;
        }
        catch (SocketException)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private static string Read(string key, string fallback) => Environment.GetEnvironmentVariable(key) ?? fallback;
    private static int ReadPort() => int.TryParse(Read("PORT", "3000"), out var port)
        ? port : throw new InvalidDataException("PORT 必须为整数。");
    private static bool ReadBool(string key, bool fallback) => bool.TryParse(Read(key, fallback.ToString()), out var value)
        ? value : throw new InvalidDataException($"{key} 必须为 true 或 false。");

    private sealed class LocalSettings
    {
        public string? MqttBroker { get; set; }
        public string? MqttUser { get; set; }
        public string? MqttPass { get; set; }
        public string? AccessToken { get; set; }
        public bool? ReadOnly { get; set; }
    }
}

