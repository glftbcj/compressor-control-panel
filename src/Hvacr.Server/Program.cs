using Hvacr.App;

var options = HvacrAppOptions.FromEnvironment();
await using var host = await HvacrApplication.StartAsync(options);
Console.WriteLine($"[Server] 访问: {(options.IsLoopback ? options.LoopbackUrl : options.ListenUrl)}/");
Console.WriteLine($"[Server] {(options.Simulation ? "离线模拟" : "真实设备")} / {(options.ReadOnly ? "只读（可在界面启用控制）" : "控制已启用")}");
await host.WaitForShutdownAsync();

