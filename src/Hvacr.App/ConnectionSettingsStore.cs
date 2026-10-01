using System.Text;
using System.Text.Json;

namespace Hvacr.App;

/// <summary>Persists desktop connection setup without touching device or appearance records.</summary>
public static class ConnectionSettingsStore
{
    public static void Save(HvacrAppOptions options, string? settingsPath = null)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(options.MqttUser) || string.IsNullOrWhiteSpace(options.MqttPass))
            throw new InvalidDataException("请填写 MQTT 账号和密码。");
        var path = Path.GetFullPath(settingsPath ?? Path.Combine(options.DataDirectory, "settings.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new
        {
            mqttBroker = options.MqttBroker, mqttUser = options.MqttUser,
            mqttPass = options.MqttPass, accessToken = options.AccessToken, readOnly = options.ReadOnly
        }, new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
