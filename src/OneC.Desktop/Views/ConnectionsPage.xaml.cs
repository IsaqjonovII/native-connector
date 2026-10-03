using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>
/// The old Connector's "1C bazalarni boshqarish" (database-config-modal.tsx): every base in the
/// 1C launcher's list, plus any connected base the list no longer has. The kind (file, server,
/// web) is read from the list, never asked; a base is only ever connected or disconnected here,
/// never typed in. Which company a base belongs to is decided on the Infobases page.
/// </summary>
public sealed partial class ConnectionsPage : Page
{
    private static readonly string[] Columns = { "Name", "Type", "Location", "1C user", "Status" };
    private const int StatusColumn = 4;
    private const string NotConnected = "Not connected", WebNotYet = "Web — not supported yet";

    /// <summary>One row: a launcher base, a connected base, or both (same name).</summary>
    private sealed record Row(string Name, LauncherBase? Launcher, StoredBase? Stored)
    {
        public bool Connected => Stored is not null;
        public bool IsWeb => Stored is null && Launcher?.Kind == LauncherKind.Web;
        public string Type => Stored?.Kind switch
        {
            BaseKind.Server => "Server",
            BaseKind.File => "File",
            _ => Launcher?.Kind switch { LauncherKind.Server => "Server", LauncherKind.Web => "Web", _ => "File" }
        };
        public string Location => Stored?.Location ?? Launcher?.Location ?? "";
    }

    private List<LauncherBase> _launcher = new();
    private List<Row> _view = new();

    private static Controls.Pill? PillFor(int column, string text) => column != StatusColumn ? null : text switch
    {
        NotConnected or WebNotYet => Controls.Pill.Neutral,
        _ => StatusPill(text)
    };

    /// <summary>Engine status words → pill colour; shared with the Infobases page.</summary>
    internal static Controls.Pill StatusPill(string text) => text switch
    {
        "Ready" => Controls.Pill.Ok,
        "Working" => Controls.Pill.Info,
        "Starting" or "Restarting" => Controls.Pill.Warn,
        _ => Controls.Pill.Bad                    // engine off, cannot start
    };

    /// <summary>Plain words for people, not engine states, for one base on this computer.</summary>
    internal static string EngineStatus(string baseName, JsonArray? hosts)
    {
        var host = hosts?.FirstOrDefault(h => h!["bases"]!.AsArray().Any(n => n!.GetValue<string>() == baseName));
        var pool = host?["stats"]?["pools"]?.AsArray().FirstOrDefault(p => p!["baseName"]!.GetValue<string>() == baseName);
        var unplaced = App.Supervisor.Unplaceable.FirstOrDefault(u => u.Name == baseName);
        return App.Supervisor.State switch
        {
            SupervisorState.Running when unplaced.Name is not null => "Can't start: " + unplaced.Reason,
            SupervisorState.Running when host is null => "Starting",
            SupervisorState.Running when host!["state"]!.GetValue<string>() != "Ready" => "Restarting",
            SupervisorState.Running => (pool?["inUse"]?.GetValue<int>() ?? 0) > 0 ? "Working" : "Ready",
            SupervisorState.Starting => "Starting",
            _ => "Engine off"
        };
    }

    /// <summary>The engine's hosts, or null when it is not running or not answering.</summary>
    internal static async Task<JsonArray?> HostsAsync()
    {
        if (App.Supervisor is not { State: SupervisorState.Running, Client: { } client }) return null;
        try { return await client.Hosts(); }
        catch (Exception ex) when (ex is HttpRequestException or EdgeException or TaskCanceledException) { return null; }
    }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ConnectionsPage()
    {
        InitializeComponent();
        Table.PillFor = PillFor;
        Table.RowKey = r => r[0] ?? "";                    // the base name: a status change updates the row in place
        Table.PrimaryColumn = 0;
        Table.MutedColumns.UnionWith(new[] { 1, 2 });      // type and location support the name
        Table.FillColumn = 2;                              // spare width to the location; status and actions stay right
        Table.EmptyGlyph = "";
        Table.CommandsFor = Commands;
        // Double-click: connect, or change the login of a connected base.
        Table.RowInvoked += (_, i) => { if (i >= 0 && i < _view.Count) Connect(_view[i].Name); };
        // Refresh on engine state changes too, so rows never say "Starting…" under a status
        // bar that already says connected (the timer alone lagged up to 3 s).
        EventHandler onState = (_, _) => DispatcherQueue.TryEnqueue(async () => await Refresh());
        Loaded += async (_, _) =>
        {
            App.Supervisor.StateChanged += onState;
            _timer.Start();
            ReadLauncher();
            await Refresh();
            // --select <base>: start with that row selected (screenshots, scripted checks).
            if (App.Arg("select") is string pick)
                Table.Select(_view.FindIndex(x => x.Name.Equals(pick, StringComparison.OrdinalIgnoreCase)));
        };
        Unloaded += (_, _) => { App.Supervisor.StateChanged -= onState; _timer.Stop(); };
        _timer.Tick += async (_, _) => await Refresh();
    }

    /// <summary>What a row offers: Connect when it is not; Test on the row and the rest under "…" when it is.</summary>
    private IReadOnlyList<Controls.RowCommand> Commands(string?[] cells)
    {
        string name = cells[0] ?? "";
        var r = _view.FirstOrDefault(x => x.Name == name);
        if (r is null || r.IsWeb) return Array.Empty<Controls.RowCommand>();
        if (!r.Connected) return new[] { new Controls.RowCommand("Connect", "", () => Connect(name)) };
        return new[]
        {
            new Controls.RowCommand("Test", "", () => Test(name)),
            new Controls.RowCommand("Change login", "", () => Connect(name), MenuOnly: true),
            new Controls.RowCommand("Disconnect", "", () => Disconnect(name), Destructive: true, MenuOnly: true)
        };
    }

    private void ReadLauncher()
    {
        try { _launcher = LauncherBases.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(InfoBarSeverity.Warning, "Could not read the 1C launcher's list: " + ex.Message);
        }
    }

    private Row? Selected => Table.SelectedIndex >= 0 && Table.SelectedIndex < _view.Count ? _view[Table.SelectedIndex] : null;
    private Row? Find(string name) => _view.FirstOrDefault(x => x.Name == name);

    private async Task Refresh()
    {
        // Launcher bases, then connected bases the launcher list doesn't have; merged by name.
        var rows = new List<Row>();
        foreach (var l in _launcher)
            if (!rows.Any(x => x.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase)))
                rows.Add(new Row(l.Name, l, App.Bases.Bases.FirstOrDefault(b => b.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase))));
        foreach (var b in App.Bases.Bases)
            if (!rows.Any(x => x.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)))
                rows.Add(new Row(b.Name, null, b));

        // Counts on the filter, so the state of the whole list reads at a glance.
        int connected = rows.Count(r => r.Connected), notConnected = rows.Count(r => !r.Connected && !r.IsWeb);
        FilterAll.Text = $"All  {rows.Count}";
        FilterConnected.Text = $"Connected  {connected}";
        FilterNotConnected.Text = $"Not connected  {notConnected}";

        string q = Search.Text.Trim();
        var keep = Selected?.Name;
        _view = rows.Where(r => Filter.SelectedItem == FilterConnected ? r.Connected
                              : Filter.SelectedItem == FilterNotConnected ? !r.Connected && !r.IsWeb : true)
                    .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                r.Location.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var hosts = await HostsAsync();
        var cells = _view.Select(r => new string?[]
        {
            r.Name, r.Type, r.Location, r.Stored?.User ?? "",
            r.Connected ? EngineStatus(r.Name, hosts) : r.IsWeb ? WebNotYet : NotConnected
        }).ToList();
        if (q.Length > 0 || Filter.SelectedItem != FilterAll)
        {
            Table.EmptyTitle = "Nothing matches";
            Table.EmptyText = "Try another search or filter.";
            Table.SetEmptyAction(null, null);
        }
        else
        {
            Table.EmptyTitle = "No 1C bases on this computer";
            Table.EmptyText = "Add your bases in 1C's own launcher first; they appear here on their own.";
            Table.SetEmptyAction("Refresh", () => Refresh_Click(this, new RoutedEventArgs()));
        }
        // Polling keeps the layout (columns only grow, no jitter); a new filter or search measures
        // afresh, so a short list isn't squeezed into the widths of the long one.
        Table.SetData(Columns, cells, keepLayout: true, remeasure: _remeasure);
        _remeasure = false;
        // Keep the selected base selected when rows move (search, a base connected).
        if (keep is not null && _view.FindIndex(x => x.Name == keep) is var i and >= 0 && i != Table.SelectedIndex) Table.Select(i);
    }

    private bool _remeasure;

    private async void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _remeasure = true;
        await Refresh();
    }

    private async void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!IsLoaded) return;
        _remeasure = true;
        await Refresh();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ReadLauncher();
        await Refresh();
    }

    private async void Connect(string name)
    {
        if (Find(name) is not { IsWeb: false } r) return;
        var b = await ConnectDialog.ShowAsync(XamlRoot, r.Name, r.Launcher, r.Stored);
        if (b is null) return;
        App.Bases.Upsert(b);
        await AfterChange($"Connected “{b.Name}” (1C {b.PlatformVersion}). Restarting the engine…");
    }

    private async void Disconnect(string name)
    {
        if (Find(name) is not { Connected: true } r) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Disconnect “{r.Name}”?",
            Content = "AIBA stops reading this base on this computer and forgets its login. The base itself and AIBA's data are not changed.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        App.Bases.Remove(r.Name);
        await AfterChange($"Disconnected “{r.Name}”.");
    }

    private async void Test(string name)
    {
        if (Find(name) is not { Connected: true, Stored: { } b }) return;
        if (App.Supervisor.Client is not { } client || App.Supervisor.State != SupervisorState.Running)
        {
            Show(InfoBarSeverity.Warning, "The 1C engine is not running yet. Try again in a moment.");
            return;
        }
        Show(InfoBarSeverity.Informational, $"Connecting to “{b.Name}”…");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t = await client.Test(b.Name);
            string config = t["synonym"]?.GetValue<string>() is { Length: > 0 } syn ? syn : t["configuration"]?.GetValue<string>() ?? "";
            Show(InfoBarSeverity.Success,
                 $"“{b.Name}” works: {config} {t["configurationVersion"]} on 1C {t["platformVersion"]} " +
                 $"({sw.ElapsedMilliseconds:N0} ms).");
        }
        catch (EdgeException ex) { Show(InfoBarSeverity.Error, ex.Friendly); }
        catch (HttpRequestException ex) { Show(InfoBarSeverity.Error, "The engine is not reachable: " + ex.Message); }
    }

    private async Task AfterChange(string message)
    {
        Show(InfoBarSeverity.Informational, message);
        await Refresh();
        await App.RestartSupervisorAsync();
        await Refresh();
        if (App.Supervisor.State == SupervisorState.Failed)
            Show(InfoBarSeverity.Error, "The engine did not start: " + App.Supervisor.LastError);
    }

    private void Show(InfoBarSeverity severity, string text)
    {
        Message.Severity = severity;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
