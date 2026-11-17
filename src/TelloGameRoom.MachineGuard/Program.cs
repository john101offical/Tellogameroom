using Serilog;
using TelloGameRoom.MachineGuard;

// "--cleanup" is run by the uninstaller: undo the Task Manager policy and exit.
if (args.Contains("--cleanup", StringComparer.OrdinalIgnoreCase))
{
    UserSession.RestoreTaskManagerForEveryone();
    return;
}

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TelloGameRoom", "Guard", "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(Path.Combine(logDirectory, "guard-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
        formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(o => o.ServiceName = "TelloGameRoomGuard");
    builder.Services.AddSerilog();
    builder.Services.AddHostedService<GuardWorker>();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "MachineGuard stopped unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
