using PcBridge.Agent.Core;
using PcBridge.Agent.Service;
using PcBridge.Agent.Windows;
using Serilog;

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PC Bridge Agent");
Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("Application", "PC Bridge Agent")
    .WriteTo.File(Path.Combine(dataDirectory, "logs", "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "PC Bridge Agent");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(new SettingsStore(dataDirectory));
    builder.Services.AddSingleton<ICredentialStore>(new DpapiCredentialStore(dataDirectory));
    builder.Services.AddSingleton<IConnectionStatusStore, ConnectionStatusStore>();
    builder.Services.AddSingleton<KeepAwakeController>();
    builder.Services.AddSingleton<ICommandHandler>(sp => sp.GetRequiredService<KeepAwakeController>());
    builder.Services.AddSingleton<ICommandHandler, AudioCommandHandler>();
    builder.Services.AddSingleton<ICommandHandler, PowerCommandHandler>();
    builder.Services.AddSingleton<HomeAssistantConnection>();
    builder.Services.AddHostedService<AgentWorker>();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
}
finally { await Log.CloseAndFlushAsync(); }
