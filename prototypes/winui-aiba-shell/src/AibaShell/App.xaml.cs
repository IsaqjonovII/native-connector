using System;
using System.Collections.Generic;
using AibaShell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AibaShell;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static bool IsDarkTheme { get; set; }

    private Window? _window;
    private SimulationEngine? _engine;

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aibashell-nav-errors.log"),
                DateTime.Now.ToString("s") + " UNHANDLED\n" + e.Exception + "\n\n");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = BuildServices();

        _window = new MainWindow();
        _window.Activate();

        // One heartbeat drives every mock. Started after the window so the timer
        // lives on the UI dispatcher queue.
        _engine = new SimulationEngine(
            DispatcherQueue.GetForCurrentThread(),
            new ITickable[]
            {
                (ITickable)Services.GetRequiredService<ILogService>(),
                (ITickable)Services.GetRequiredService<IOneCService>(),
                (ITickable)Services.GetRequiredService<ISyncService>(),
                (ITickable)Services.GetRequiredService<IProcessSupervisor>(),
                (ITickable)Services.GetRequiredService<IBankService>(),
                (ITickable)Services.GetRequiredService<ISystemMetricsService>()
            });
        _engine.Start();
    }

    private static IServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ILogService, MockLogService>();
        sc.AddSingleton<IOneCService, MockOneCService>();
        sc.AddSingleton<ISyncService, MockSyncService>();
        sc.AddSingleton<IProcessSupervisor, MockProcessSupervisor>();
        sc.AddSingleton<IBankService, MockBankService>();
        sc.AddSingleton<ISystemMetricsService, MockSystemMetricsService>();
        sc.AddSingleton<ISettingsService, MockSettingsService>();
        return sc.BuildServiceProvider();
    }

    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}
