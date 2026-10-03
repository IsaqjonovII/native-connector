using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

public sealed partial class ResourcesPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ResourcesPage()
    {
        InitializeComponent();
        ProcessTable.PillFor = (c, text) => c == 1 ? (text == "Running" ? Controls.Pill.Ok : Controls.Pill.Warn) : null;
        // Memory and counters change every tick: rows are updated in place by part / infobase.
        ProcessTable.RowKey = r => r[0] ?? "";
        PoolTable.RowKey = r => r[0] ?? "";
        ProcessTable.PrimaryColumn = PoolTable.PrimaryColumn = 0;
        ProcessTable.MutedColumns.Add(3);                  // which bases a part serves
        PoolTable.FillColumn = 0;                          // spare width to the name, not after the numbers
        _timer.Tick += async (_, _) => await Refresh(keep: true);
        Loaded += async (_, _) => { _timer.Start(); await Refresh(keep: false); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private async Task Refresh(bool keep)
    {
        if (App.Supervisor.Client is not { } client)
        {
            ProcessTable.Clear();
            PoolTable.Clear();
            TotalRam.Text = $"{ProcessMemory.SelfMb()} MB";
            TotalSessions.Text = TotalHosts.Text = TotalRestarts.Text = "–";
            return;
        }

        JsonArray hosts;
        JsonObject sup;
        try
        {
            hosts = await client.Hosts();
            sup = await client.Supervisor();
        }
        catch (Exception ex) when (ex is HttpRequestException or EdgeException or TaskCanceledException) { return; }

        long app = ProcessMemory.SelfMb();
        long supWs = sup["workingSetMb"]!.GetValue<long>();

        // People's names for the parts, one state pill, memory, and what each 1C engine serves.
        // Process ids, threads, handles and private bytes are for developers (OneC.Host mem).
        var procs = new List<string?[]>
        {
            new[] { "AIBA Connector (this window)", "Running", $"{app:N0} MB", "" },
            new[] { "Engine manager", "Running", $"{supWs:N0} MB", "" }
        };
        var pools = new List<string?[]>();
        long total = app + supWs;
        int sessions = 0, restarts = 0;

        foreach (var h in hosts)
        {
            long ws = h!["workingSetMb"]!.GetValue<long>();
            total += ws;
            restarts += h["restarts"]!.GetValue<int>();
            var stats = h["stats"] as JsonObject;
            sessions += stats?["sessions"]?.GetValue<int>() ?? 0;

            // "8.3.15.1565-x64" → "1C engine 8.3.15"
            string key = h["key"]!.GetValue<string>();
            string version = string.Join('.', key.Split('-')[0].Split('.').Take(3));
            procs.Add(new[]
            {
                $"1C engine {version}", h["state"]!.GetValue<string>() == "Ready" ? "Running" : h["state"]!.GetValue<string>(),
                $"{ws:N0} MB", string.Join(", ", h["bases"]!.AsArray().Select(b => b!.GetValue<string>()))
            });

            foreach (var p in stats?["pools"]?.AsArray() ?? new JsonArray())
                pools.Add(new[]
                {
                    p!["baseName"]!.GetValue<string>(), p["live"]!.ToString(), p["inUse"]!.ToString(), $"{p["rents"]:N0}"
                });
        }

        TotalRam.Text = $"{total:N0} MB";
        TotalSessions.Text = sessions.ToString();
        TotalHosts.Text = hosts.Count.ToString();
        TotalRestarts.Text = restarts.ToString();

        ProcessTable.SetData(new[] { "Part", "State", "Memory", "Serves" }, procs, numericColumns: new[] { 2 }, keepLayout: keep);
        PoolTable.SetData(new[] { "Infobase", "Open connections", "Busy now", "Requests answered" },
                          pools, numericColumns: new[] { 1, 2, 3 }, keepLayout: keep);
    }
}
