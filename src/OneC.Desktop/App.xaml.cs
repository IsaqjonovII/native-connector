using Microsoft.UI.Xaml;
using OneC.Desktop.Services;

namespace OneC.Desktop;

/// <summary>
/// App-wide services. Deliberately small: a base store, the supervisor child process, and
/// the window. Screens talk to 1C only through <see cref="SupervisorProcess.Client"/>.
/// </summary>
public partial class App : Application
{
    public static BaseStore Bases { get; } = new();
    public static SupervisorProcess Supervisor { get; } = new();
    public static MainWindow? Window { get; private set; }

    /// <summary>Command-line options used by screenshots and tests: --page, --theme, --import.</summary>
    public static string? Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, "--" + name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public App()
    {
        InitializeComponent();
        DesktopLog.Write = Log;
        UnhandledException += (_, e) =>
        {
            // Keep the window alive and leave a trace instead of vanishing.
            e.Handled = true;
            Log("unhandled: " + e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Arg("import") is string import && File.Exists(import))
            Bases.ImportConnectionStrings(import);

        Window = new MainWindow();
        Window.Activate();
        _ = RestartSupervisorAsync();
    }

    /// <summary>(Re)starts the supervisor with the current base list. Called after any edit.</summary>
    public static Task RestartSupervisorAsync() => Supervisor.StartAsync(Bases.SupervisorPayload());

    public static void Log(string line)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIBA", "Connector");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"), $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
