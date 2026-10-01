using System.Text.Json;

namespace Hvacr.App;

public sealed class AppearanceStore(HvacrAppOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool Exists => File.Exists(options.AppearancePath);

    public async Task<AppearanceSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Exists) return AppearanceSettings.Default;
            await using var stream = File.OpenRead(options.AppearancePath);
            return Normalize(await JsonSerializer.DeserializeAsync<AppearanceSettings>(stream, JsonOptions, ct));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("外观配置损坏，原文件已保留。", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<AppearanceSettings> SaveAsync(AppearanceSettings? settings, CancellationToken ct = default)
    {
        var normalized = Normalize(settings);
        await _gate.WaitAsync(ct);
        var temp = options.AppearancePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }
            if (Exists) File.Copy(options.AppearancePath, options.AppearancePath + ".bak", true);
            File.Move(temp, options.AppearancePath, true);
            return normalized;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            _gate.Release();
        }
    }

    private static AppearanceSettings Normalize(AppearanceSettings? settings)
    {
        if (settings?.Theme is not ("light" or "dark" or "system")
            || settings.Accent is not { Length: 7 } accent || accent[0] != '#'
            || !accent.Skip(1).All(Uri.IsHexDigit))
            throw new ArgumentException("外观需要有效的显示模式和六位十六进制主颜色。");
        return settings with { Accent = accent.ToLowerInvariant() };
    }
}
