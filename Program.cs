using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace NEDA.UI
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // ---------- SERILOG BOOTSTRAP ----------
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(
                    path: "logs/neda-ui-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .CreateLogger();

            try
            {
                Log.Information("NEDA.UI starting...");

                var host = CreateHostBuilder(args).Build();

                var app = new App();
                app.InitializeComponent();
                app.Run();

                host.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "NEDA.UI terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // ---------- CORE SERVICES ----------
                    services.AddLogging();

                    // ---------- UI SERVICES ----------
                    // services.AddSingleton<MainWindow>();
                    // services.AddSingleton<MainViewModel>();

                    // ---------- FUTURE EXTENSIONS ----------
                    // AI, Cloud, Brain, Communication vs burada eklenecek
                });
    }
}
