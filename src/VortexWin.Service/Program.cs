using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VortexWin.Core.Config;
using VortexWin.Core.Logging;
using VortexWin.Service.Engine;
using VortexWin.Service.Ipc;
using VortexWin.Service.Workers;

namespace VortexWin.Service;

public static class Program
{
    public static void Main(string[] args)
    {
        // Configure Serilog for service logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VortexWin", "logs", "service.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            Log.Information("Vortex Win Service starting...");

            // Set process priority to BelowNormal when not debugging
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                try
                {
                    System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                        System.Diagnostics.ProcessPriorityClass.BelowNormal;
                }
                catch { /* May fail on some configurations */ }
            }

            bool isDryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

            var builder = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "VortexWinService";
                })
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Core services
                    services.AddSingleton<ConfigManager>();
                    services.AddSingleton<AuditLogger>();
                    services.AddSingleton<TimerEngine>();
                    services.AddSingleton<DesktopWatcher>();
                    services.AddSingleton<SentinelManager>();
                    services.AddSingleton<AlertDispatcher>();

                    services.AddSingleton(new ShutdownExecutor(isDryRun));

                    // IPC server
                    services.AddSingleton<IpcServer>();

                    // Main worker
                    services.AddHostedService<VortexWorker>();
                });

            var host = builder.Build();
            host.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Vortex Win Service terminated unexpectedly.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
