using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

public sealed partial class InfobasesPage : Page
{
    private static readonly string[] Columns =
        { "Name", "Type", "Location", "User", "1C version", "Status", "Sessions", "Engine process" };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public InfobasesPage()
    {
        InitializeComponent();
        Table.SelectionChanged += (_, _) => UpdateButtons();
        Table.RowInvoked += (_, _) => UpdateButtons();
        // Refresh on engine state changes too, so rows never say "Starting…" under a status
        // bar that already says connected (the timer alone lagged up to 3 s).
        EventHandler onState = (_, _) => DispatcherQueue.TryEnqueue(async () => { await Refresh(); UpdateButtons(); });
        Loaded += async (_, _) => { App.Supervisor.StateChanged += onState; _timer.Start(); await Refresh(); };
        Unloaded += (_, _) => { App.Supervisor.StateChanged -= onState; _timer.Stop(); };
        _timer.Tick += async (_, _) => { await Refresh(); UpdateButtons(); };
    }

    private StoredBase? Selected =>
        Table.SelectedIndex >= 0 && Table.SelectedIndex < App.Bases.Bases.Count ? App.Bases.Bases[Table.SelectedIndex] : null;

    private void UpdateButtons()
    {
        bool one = Selected is not null;
        EditButton.IsEnabled = one;
        RemoveButton.IsEnabled = one;
        TestButton.IsEnabled = one && App.Supervisor.State == SupervisorState.Running;
    }

    private async Task Refresh()
    {
        JsonArray? hosts = null;
        if (App.Supervisor is { State: SupervisorState.Running, Client: { } client })
        {
            try { hosts = await client.Hosts(); }
            catch (Exception ex) when (ex is HttpRequestException or EdgeException or TaskCanceledException) { }
        }

        var rows = App.Bases.Bases.Select(b =>
        {
            var host = hosts?.FirstOrDefault(h => h!["bases"]!.AsArray().Any(n => n!.GetValue<string>() == b.Name));
            var pool = host?["stats"]?["pools"]?.AsArray().FirstOrDefault(p => p!["baseName"]!.GetValue<string>() == b.Name);
            var unplaced = App.Supervisor.Unplaceable.FirstOrDefault(u => u.Name == b.Name);

            string status = App.Supervisor.State switch
            {
                SupervisorState.Running when unplaced.Name is not null => "Cannot start: " + unplaced.Reason,
                SupervisorState.Running when host is null => "Waiting for the engine",
                SupervisorState.Running => host!["state"]!.GetValue<string>() == "Ready"
                                               ? (pool is null ? "Ready (idle)" : "Ready")
                                               : "Engine " + host["state"]!.GetValue<string>().ToLowerInvariant(),
                SupervisorState.Starting => "Starting…",
                _ => "Engine off"
            };
            string sessions = pool is null ? "0"
                : $"{pool["live"]} open, {pool["inUse"]} busy";
            string engine = host is null ? "" : $"{host["key"]} · pid {host["pid"]}";

            return new string?[]
            {
                b.Name, b.Kind == BaseKind.Server ? "Server" : "File", b.Location, b.User,
                string.IsNullOrEmpty(b.PlatformVersion) ? "any" : b.PlatformVersion, status, sessions, engine
            };
        }).ToList();

        Table.SetData(Columns, rows, keepLayout: true);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var b = await BaseDialog.ShowAsync(XamlRoot, null);
        if (b is null) return;
        App.Bases.Upsert(b);
        await AfterChange($"Added “{b.Name}”. Restarting the engine with the new list…");
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } current) return;
        var b = await BaseDialog.ShowAsync(XamlRoot, current);
        if (b is null) return;
        App.Bases.Upsert(b, current.Name);
        await AfterChange($"Saved “{b.Name}”. Restarting the engine…");
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } b) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove “{b.Name}”?",
            Content = "This only removes it from this computer's list. Nothing in the 1C database is changed.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        App.Bases.Remove(b.Name);
        await AfterChange($"Removed “{b.Name}”.");
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } b || App.Supervisor.Client is not { } client) return;
        TestButton.IsEnabled = false;
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
        finally { UpdateButtons(); }
    }

    private async void ImportConnector_Click(object sender, RoutedEventArgs e)
    {
        var found = BaseStore.FindOldConnectorConfigs();
        if (found.Count == 0)
        {
            Show(InfoBarSeverity.Warning, "No AIBA Connector settings were found on this computer.");
            return;
        }
        try
        {
            var report = App.Bases.ImportOldConnectorConfig(found[0]);
            string skipped = report.Skipped.Count == 0 ? "" : $" Skipped: {string.Join(", ", report.Skipped)}.";
            await AfterChange($"Imported {report.Imported} infobase{(report.Imported == 1 ? "" : "s")} from the AIBA Connector.{skipped}");
        }
        catch (Exception ex) { Show(InfoBarSeverity.Error, "Could not read the AIBA Connector settings: " + ex.Message); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            int n = App.Bases.ImportConnectionStrings(file.Path);
            await AfterChange($"Imported {n} infobase{(n == 1 ? "" : "s")}. The file itself was not changed.");
        }
        catch (Exception ex) { Show(InfoBarSeverity.Error, "Could not import that file: " + ex.Message); }
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
