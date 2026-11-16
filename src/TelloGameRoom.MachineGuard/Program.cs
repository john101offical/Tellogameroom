// Watchdog service skeleton. The supervision logic is implemented in Phase 8.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "TelloGameRoom MachineGuard");
await builder.Build().RunAsync();
