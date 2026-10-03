using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

public sealed partial class ActivityPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ActivityPage()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await Refresh(keep: true);
        Loaded += async (_, _) => { _timer.Start(); await Refresh(keep: false); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private async void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => await Refresh(keep: false);

    private async Task Refresh(bool keep)
    {
        if (App.Supervisor.Client is not { } client)
        {
            Table.EmptyText = "The 1C engine is not running.";
            Table.Clear();
            return;
        }
        // The empty-state text must describe the CURRENT state: this page can load before the
        // engine is up, and it once kept saying "not running" after the engine had started.
        Table.EmptyText = Tabs.SelectedItem == EventsTab ? "No engine events yet." : "No requests to 1C yet.";
        try
        {
            if (Tabs.SelectedItem == EventsTab)
            {
                var ev = await client.Events(300);
                Table.PillFor = (c, text) => c == 2 ? EventPill(text) : null;
                Table.PrimaryColumn = 1;
                Table.MutedColumns.Clear();
                Table.MutedColumns.UnionWith(new[] { 0, 3 });      // time and raw details support the event
                Table.SetData(new[] { "Time", "Part", "What happened", "Details" },
                    ev.Select(e =>
                    {
                        string what = e!["what"]!.GetValue<string>();
                        return new string?[]
                        {
                            e["utc"]!.GetValue<DateTime>().ToLocalTime().ToString("HH:mm:ss"),
                            PartName(e["host"]!.GetValue<string>()), PlainEvent(what), what
                        };
                    }).ToList(), keepLayout: keep);
            }
            else
            {
                var act = await client.Activity(300);
                Table.PillFor = (c, text) => c == 3 ? (text == "OK" ? Controls.Pill.Ok : Controls.Pill.Bad) : null;
                Table.PrimaryColumn = 2;
                Table.MutedColumns.Clear();
                Table.MutedColumns.UnionWith(new[] { 0, 4, 5 });   // time, duration and error text support the request
                Table.SetData(new[] { "Time", "Infobase", "What", "Result", "Took", "Details" },
                    // ASP.NET Core serializes the Activity record in camelCase.
                    act.Select(a => new string?[]
                    {
                        a!["utc"]!.GetValue<DateTime>().ToLocalTime().ToString("HH:mm:ss"),
                        a["base"]!.GetValue<string>(),
                        OpName(a["op"]!.GetValue<string>()),
                        // 2xx is success: a created document answers 201 (D38).
                        a["status"]!.GetValue<int>() is >= 200 and < 300 ? "OK" : $"Failed ({a["status"]})",
                        $"{a["ms"]!.GetValue<long>():N0} ms",
                        a["error"]?.GetValue<string>() ?? ""
                    }).ToList(), numericColumns: new[] { 4 }, keepLayout: keep);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or EdgeException or TaskCanceledException) { }
    }

    /// <summary>"8.3.15.1565-x64" → "1C engine 8.3.15"; the supervisor → "Engine manager".</summary>
    private static string PartName(string key) =>
        key == "supervisor" ? "Engine manager" : "1C engine " + string.Join('.', key.Split('-')[0].Split('.').Take(3));

    /// <summary>The engine's log lines (OneC.Supervisor/Supervisor.cs) in plain words.</summary>
    private static string PlainEvent(string what) => what switch
    {
        _ when what.StartsWith("ready") => "Started",
        _ when what.StartsWith("did not become ready") => "Could not start",
        _ when what.StartsWith("restart:") => "Stopped unexpectedly, restarted",
        _ when what.StartsWith("recycle:") => "Restarted to free memory",
        _ when what.StartsWith("host stuck") => "Hung while closing, stopped",
        _ when what.StartsWith("stats failed") => "Did not answer a health check",
        _ when what.StartsWith("monitor pass failed") => "Health check failed",
        _ => what
    };

    private static Controls.Pill EventPill(string plain) => plain switch
    {
        "Started" or "Restarted to free memory" => Controls.Pill.Ok,
        "Stopped unexpectedly, restarted" or "Did not answer a health check" => Controls.Pill.Warn,
        _ => Controls.Pill.Bad
    };

    private static string OpName(string op) => op switch
    {
        "read" => "Read",
        "catalog" => "Read catalog",
        "document" => "Read documents",
        "register" => "Read register",
        "slices" => "Plan a bulk read",
        "changes" => "Read change log",
        "version" => "Version check",
        "test" => "Connection test",
        "create" => "Create document",
        "update" => "Update document",
        "post" => "Post document",
        "unpost" => "Unpost document",
        "markDeleted" => "Mark for deletion",
        "delete" => "Delete document",
        _ => op
    };
}
