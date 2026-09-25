namespace OneC.Desktop.Services;

/// <summary>
/// The one logging hook the core needs. The app points it at its log file; tests leave the
/// default (append to a file under %LOCALAPPDATA%\AIBA\Connector).
/// </summary>
public static class DesktopLog
{
    public static Action<string> Write { get; set; } = Default;

    private static void Default(string line)
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
