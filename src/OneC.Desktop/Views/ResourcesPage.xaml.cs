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
        using var me = System.Diagnostics.Process.GetCurrentProcess();

        var procs = new List<string?[]>
        {
            new[] { "This window", me.Id.ToString(), "Running", $"{app:N0}", $"{me.PrivateMemorySize64 / 1024 / 1024:N0}",
                    me.Threads.Count.ToString(), me.HandleCount.ToString(), "" },
            new[] { "Engine supervisor", sup["pid"]!.ToString(), "Running", $"{supWs:N0}", $"{sup["privateMb"]:N0}",
                    sup["threads"]!.ToString(), sup["handles"]!.ToString(), "" }
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

            procs.Add(new[]
            {
                $"1C engine {h["key"]}", h["pid"]?.ToString() ?? "", h["state"]!.GetValue<string>(),
                $"{ws:N0}", $"{h["privateMb"]:N0}", stats?["threads"]?.ToString() ?? "",
                stats?["handles"]?.ToString() ?? "", string.Join(", ", h["bases"]!.AsArray().Select(b => b!.GetValue<string>()))
            });

            foreach (var p in stats?["pools"]?.AsArray() ?? new JsonArray())
                pools.Add(new[]
                {
                    p!["baseName"]!.GetValue<string>(), p["kind"]!.GetValue<string>(), h["key"]!.GetValue<string>(),
                    p["live"]!.ToString(), p["inUse"]!.ToString(), p["idle"]!.ToString(),
                    $"{p["rents"]:N0}", p["created"]!.ToString(),
                    $"{p["retired"]} idle · {p["evicted"]} evicted · {p["broken"]} failed"
                });
        }

        TotalRam.Text = $"{total:N0} MB";
        TotalSessions.Text = sessions.ToString();
        TotalHosts.Text = hosts.Count.ToString();
        TotalRestarts.Text = restarts.ToString();

        ProcessTable.SetData(
            new[] { "Process", "PID", "State", "Memory, MB", "Private, MB", "Threads", "Handles", "Infobases" },
            procs, numericColumns: new[] { 3, 4, 5, 6 }, keepLayout: keep);
        PoolTable.SetData(
            new[] { "Infobase", "Type", "Engine", "Open", "Busy", "Idle", "Requests served", "Opened", "Closed" },
            pools, numericColumns: new[] { 3, 4, 5, 6, 7 }, keepLayout: keep);
    }
}
