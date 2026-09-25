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
                Table.SetData(new[] { "Time", "Engine process", "What happened" },
                    ev.Select(e => new string?[]
                    {
                        e!["utc"]!.GetValue<DateTime>().ToLocalTime().ToString("HH:mm:ss"),
                        e["host"]!.GetValue<string>(),
                        e["what"]!.GetValue<string>()
                    }).ToList(), keepLayout: keep);
            }
            else
            {
                var act = await client.Activity(300);
                Table.SetData(new[] { "Time", "Infobase", "Operation", "Result", "Time taken", "Details" },
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
