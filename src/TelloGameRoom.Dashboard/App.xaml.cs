using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TelloGameRoom.Dashboard.Services;
using TelloGameRoom.Dashboard.ViewModels;
using TelloGameRoom.Dashboard.Views;

namespace TelloGameRoom.Dashboard;

/// <summary>Composition root of the Dashboard: logging, dependency injection, theme and language, then the main window.</summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppSettings.Directory, "logs", "dashboard-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            // Keep the Dashboard alive during a busy shift: log, tell the user, carry on.
            Log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
            if (MainWindow is { IsVisible: true } owner)
            {
                MessageBox.Show(owner, args.Exception.Message, "TelloGameRoom", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(args.Exception.Message, "TelloGameRoom", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var settings = AppSettings.Load();
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(settings);
                services.AddSingleton<Session>();
                services.AddSingleton<Realtime>();
                services.AddSingleton<ThemeService>();
                services.AddSingleton<BranchContext>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<RootViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
        await _host.StartAsync();

        if (settings.Language is not null)
        {
            Loc.Instance.SetLanguage(settings.Language);
        }

        _host.Services.GetRequiredService<ThemeService>().Apply(settings.Theme);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        await _host.Services.GetRequiredService<RootViewModel>().StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.Services.GetRequiredService<Realtime>().StopAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
