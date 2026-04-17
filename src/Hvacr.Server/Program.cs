using System.Net;
using System.Net.Sockets;
using Hvacr.App;

var configuredHost = Environment.GetEnvironmentVariable("HOST")?.Trim();
var options = HvacrAppOptions.FromEnvironment(
	overrideHost: string.IsNullOrWhiteSpace(configuredHost) ? "0.0.0.0" : configuredHost);
await using var host = await HvacrApplication.StartAsync(options);

Console.WriteLine($"[Server] 本机访问: {options.LoopbackUrl}/");

foreach (var lanAddress in GetLanAccessUrls(options.Port))
{
	Console.WriteLine($"[Server] 局域网访问: {lanAddress}");
}

await host.WaitForShutdownAsync();

static IEnumerable<string> GetLanAccessUrls(int port)
{
	return Dns.GetHostAddresses(Dns.GetHostName())
		.Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
		.Select(address => $"http://{address}:{port}/")
		.Distinct(StringComparer.OrdinalIgnoreCase);
}
