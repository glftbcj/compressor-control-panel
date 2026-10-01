using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Hvacr.App;

public sealed class LocalApiGuard(HvacrAppOptions options)
{
    public string SessionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task<bool> ValidateAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var isIp = IPAddress.TryParse(host, out var hostIp);
        var validHost = options.IsLoopback
            ? host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (isIp && IPAddress.IsLoopback(hostIp!))
            : isIp && (IPAddress.IsLoopback(hostIp!) || hostIp!.Equals(context.Connection.LocalIpAddress)
                || hostIp.Equals(context.Connection.LocalIpAddress?.MapToIPv4()));
        if (!validHost) return await Deny(context, 403, "访问地址不被允许。");
        if (!context.Request.Path.StartsWithSegments("/api")) return true;

        var site = context.Request.Headers["Sec-Fetch-Site"].ToString();
        var origin = context.Request.Headers.Origin.ToString();
        if (site is "cross-site" or "same-site"
            || (!string.IsNullOrEmpty(origin) && !string.Equals(origin,
                $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
            return await Deny(context, 403, "拒绝跨站接口访问。");

        if (!string.IsNullOrEmpty(options.AccessToken))
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !EqualsSecret(auth[7..], options.AccessToken))
                return await Deny(context, 401, "请输入面板访问密钥。");
        }
        if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method))
        {
            if (!EqualsSecret(context.Request.Headers["X-Hvacr-Session"].ToString(), SessionToken))
                return await Deny(context, 403, "面板会话已失效，请刷新页面。");
            if (!context.Request.HasJsonContentType())
                return await Deny(context, 415, "请求须使用 application/json。");
        }
        return true;
    }

    private static bool EqualsSecret(string value, string secret) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(secret));
    private static async Task<bool> Deny(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error });
        return false;
    }
}

