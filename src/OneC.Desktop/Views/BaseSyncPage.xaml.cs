using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>
/// One base's Sync screen (plan S14), opened from its infobase card: the state in words,
/// a row of figures (last 1C change, tables copied, changes waiting, things to decide), what needs
/// the user — refused deletes, rows AIBA did not take, organisations not linked — and every table
/// with its state, rows, waiting work, problems and last send. Copying one table again (D-3) is in
/// the table row's menu and asks first. Polls every 3 s; the table patches rows in place.
/// </summary>
public sealed partial class BaseSyncPage : Page
{
    private static readonly string[] Columns = { "Table", "Type", "Status", "Rows", "Waiting", "Problems", "Last sent" };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _base;
    private string? _mode;
    private string _attentionJson = "";
    private readonly Dictionary<string, string> _tableIds = new(StringComparer.Ordinal);   // row key → table id
    private bool _busy;
    private bool _remeasure = true;
    private JsonArray? _tablesCache;
    private int _polls;

    public BaseSyncPage()
    {
        InitializeComponent();
        Table.PrimaryColumn = 0;
        Table.MutedColumns.UnionWith(new[] { 1, 6 });       // type and time support the name and the state
        Table.FillColumn = 0;
        Table.RowKey = Key;
        Table.PillFor = (c, text) => c == 2 ? TablePill(text) : null;
        Table.CommandsFor = cells => _tableIds.TryGetValue(Key(cells), out var id)
            ? new[] { new RowCommand("Copy this table again…", "",() => _ = ConfirmRebuildAsync(id), Destructive: true, MenuOnly: true) }
            : Array.Empty<RowCommand>();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private static string Key(string?[] cells) => cells[0] + "|" + cells[1];

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var next = e.Parameter as string;
        if (next != _base)
        {
            _base = next;
            _mode = null;
            _attentionJson = "";
            _remeasure = true;
            _tablesCache = null;                                   // another base: read its tables now
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (App.Supervisor.Client is not { } client)
            {
                ShowEmpty("Starting…", "The connector is starting.");
                return;
            }
            JsonArray bases, tables, dead;
            try
            {
                bases = await client.SyncStatus();
                var b0 = bases.OfType<JsonObject>().FirstOrDefault(x => (string?)x["baseId"] == _base);
                if (b0 is null)
                {
                    ShowEmpty($"{_base ?? "This base"} is not synced yet",
                              "The sync engine works with the 1C bases connected on this computer. Connect it on the 1C connections screen.");
                    return;
                }
                // The per-table counts scan sync.db under its lock: every 5th poll (15 s), not every 3 s
                // (2026-10-01 review); the status figures above stay at 3 s.
                tables = _tablesCache is { } cached && _polls++ % 5 != 0 && !_remeasure ? cached : (_tablesCache = await client.SyncTables(_base!));
                _ = AddSyncTableDialog.Prefetch(_base!);       // "Add table" opens with the list ready
                dead = (b0["deadLetters"]?.GetValue<int>() ?? 0) > 0 ? await client.SyncDeadLetters(_base!) : new JsonArray();
                Show(b0, tables, dead);
            }
            catch (EdgeException e) when (e.Status == 404) { ShowEmpty("Starting…", "The connector is restarting with the sync engine."); }
            catch (HttpRequestException) { }
        }
        finally { _busy = false; }
    }

    private void Show(JsonObject b, JsonArray tables, JsonArray dead)
    {
        Empty.Visibility = Visibility.Collapsed;
        if (TablesPanel.Visibility != Visibility.Visible)
        {
            Summary.Visibility = TablesTitle.Visibility = TablesPanel.Visibility = Visibility.Visible;
            UpdateLayout();          // the table sizes its columns to its width: measure it shown, not collapsed
        }
        BaseTitle.Text = _base;

        // Status and figures.
        var (state, pill) = SyncText.State(b);
        StateText.Text = state;
        ToolTipService.SetToolTip(StateText, (string?)b["reason"] is { Length: > 0 } why ? $"{state} ({why})" : state);
        bool quiet = pill == Pill.Neutral;
        StatePill.Background = quiet ? null : ThemeBrushes.Get(this, pill + "PillBrush");
        StatePill.Padding = quiet ? new Thickness(0) : new Thickness(8, 3, 10, 4);
        StateText.Foreground = ThemeBrushes.Get(this, pill + "Brush");
        // The time large, the day in the label: "19:29" over "Last change in 1C · yesterday".
        var last = SyncText.Time(b["lastEventAt"])?.ToLocalTime();
        LastChange.Text = last is { } l ? l.ToString("HH:mm") : "—";
        LastChangeLabel.Text = last is { } d ? "Last change in 1C · " + SyncText.Day(d) : "Last change in 1C";
        int done = b["tablesDone"]?.GetValue<int>() ?? 0, total = b["tablesTotal"]?.GetValue<int>() ?? 0;
        TablesSynced.Text = $"{done} of {total}";
        Waiting.Text = SyncText.Number(b["pendingWork"]?.GetValue<long>() ?? 0);
        Problems.Text = SyncText.Number(dead.Count);

        string mode = (string)b["mode"]!;
        if (mode != _mode)
        {
            _mode = mode;
            bool paused = mode == "paused";
            PauseGlyph.Glyph = paused ? "" : "";          // Play / Pause
            PauseText.Text = paused ? "Resume" : "Pause";
            AutomationProperties.SetName(PauseButton, PauseText.Text);
            ToolTipService.SetToolTip(PauseButton, paused ? "Start copying changes again" : "Stop copying this base for now");
            PauseButton.Visibility = Visibility.Visible;
        }

        // What needs the user: rebuilt only when it changed (it holds buttons).
        string attention = (string?)b["lastError"] + b["unmappedOrgs"]?.ToJsonString() + dead.ToJsonString();
        if (attention != _attentionJson)
        {
            _attentionJson = attention;
            BuildAttention(b, dead);
        }

        // Tables.
        _tableIds.Clear();
        var rows = new List<string?[]>();
        foreach (var t in tables.OfType<JsonObject>())
        {
            string id = (string)t["table"]!;
            string st = (string)t["state"]!;
            long rowsIn = st == "copying" ? t["copiedSoFar"]?.GetValue<long>() ?? 0 : t["rows"]?.GetValue<long>() ?? 0;
            long pending = t["pending"]?.GetValue<long>() ?? 0;
            int failed = t["failed"]?.GetValue<int>() ?? 0;
            var cells = new string?[]
            {
                SyncText.Name(id), SyncText.Kind(id), SyncText.TableState(t).Text,
                st is "missing" or "waiting" ? "—" : SyncText.Number(rowsIn),
                pending > 0 ? SyncText.Number(pending) : "—",
                failed > 0 ? SyncText.Number(failed) : "—",
                SyncText.When(SyncText.Time(t["lastSentAt"]))
            };
            _tableIds[Key(cells)] = id;
            rows.Add(cells);
        }
        Table.SetData(Columns, rows, numericColumns: new[] { 3, 4, 5 }, keepLayout: true, remeasure: _remeasure);
        _remeasure = false;
    }

    private static Pill? TablePill(string text) => text switch
    {
        "Synced" => Pill.Ok,
        "Copying" or "Sending" => Pill.Info,
        "Problem" => Pill.Bad,
        "Waiting" or "Not in this 1C" => Pill.Neutral,
        _ => null
    };

    // ---------------- needs attention ----------------

    private void BuildAttention(JsonObject b, JsonArray dead)
    {
        Attention.Children.Clear();
        if ((string?)b["lastError"] is { Length: > 0 } err)
            Attention.Children.Add(Bar(InfoBarSeverity.Warning, "Last problem", err));
        if (b["missingTables"] is JsonObject missing && missing.Count > 0)
            Attention.Children.Add(Bar(InfoBarSeverity.Informational, "Skipped tables",
                $"This 1C has no {string.Join(", ", missing.Select(m => SyncText.Name(m.Key)))}. Nothing to copy there."));
        if (b["unmappedOrgs"] is JsonArray orgs && orgs.Count > 0)
        {
            long n = orgs.Sum(o => o!["rows"]!.GetValue<long>());
            int k = orgs.Select(o => (string)o!["orgRef"]!).Distinct().Count();
            Attention.Children.Add(Bar(InfoBarSeverity.Warning, "Organisations not linked",
                $"{SyncText.Count(n, "row")} of {k} organisation{(k == 1 ? "" : "s")} wait until the organisation is linked to a company in AIBA."));
        }
        if (dead.Count > 0) Attention.Children.Add(DeadLetters(dead));
        Attention.Visibility = Attention.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static InfoBar Bar(InfoBarSeverity severity, string title, string message) =>
        new() { Severity = severity, Title = title, Message = message, IsOpen = true, IsClosable = false };

    /// <summary>Dead letters as things to decide, not error codes; the 1C object's key and the raw error in the tooltip.</summary>
    private Border DeadLetters(JsonArray dead)
    {
        var list = new StackPanel { Spacing = 0 };
        list.Children.Add(new TextBlock
        {
            Text = $"Need your decision ({dead.Count})", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6)
        });
        int i = 0;
        foreach (var d in dead.OfType<JsonObject>().Take(50))
        {
            long id = d["id"]!.GetValue<long>();
            string category = (string)d["category"]!, table = SyncText.Name((string)d["table"]!);
            string what = category switch
            {
                "needs_approval" => $"Delete {(string?)d["payloadHint"] ?? "a row"} of {table} in the target?",
                "validation" => $"{table}: the target did not accept a row",
                "missing_config" => $"{table}: not in this 1C",
                "transient_exhausted" => $"{table}: could not send for a long time",
                "unmapped_org" => $"{table}: organisation not linked",
                _ => $"{table}: {category}"
            };
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 4, 6), CornerRadius = new CornerRadius(4) };
            if (i++ % 2 == 1) row.Background = ThemeBrushes.Get(this, "SubtleFillColorSecondaryBrush");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = what, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = $"{(string?)d["error"]}", FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeBrushes.Get(this, "TextFillColorSecondaryBrush")
            });
            ToolTipService.SetToolTip(text, $"{what}\n{(string?)d["objectKey"]}\n{(string?)d["error"]}");
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (category == "needs_approval")
            {
                buttons.Children.Add(Action("Delete", () => App.Supervisor.Client!.SyncApprove(id)));
                buttons.Children.Add(Action("Keep", () => App.Supervisor.Client!.SyncDismiss(id)));
            }
            else buttons.Children.Add(Action("Try again", () => App.Supervisor.Client!.SyncRetry(id)));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            list.Children.Add(row);
        }
        return new Border { Style = (Style)Application.Current.Resources["PanelStyle"], Padding = new Thickness(14, 10, 10, 10), Child = list };
    }

    // ---------------- actions ----------------

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_base is not { } id || App.Supervisor.Client is not { } client) return;
        PauseButton.IsEnabled = false;
        try { await (_mode == "paused" ? client.SyncResume(id) : client.SyncPause(id)); }
        catch (Exception ex) when (ex is EdgeException or HttpRequestException) { Notify(InfoBarSeverity.Error, PauseText.Text, ex.Message); }
        finally { PauseButton.IsEnabled = true; }
        await RefreshAsync();
    }

    /// <summary>D-3: copying again deletes the table in the target first; the user confirms that one table.</summary>
    private async Task ConfirmRebuildAsync(string table)
    {
        if (_base is not { } id) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Copy {SyncText.Name(table)} again?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Every row of {SyncText.Name(table)} ({SyncText.Kind(table).ToLowerInvariant()}) from {id} is deleted in the target, " +
                       "then copied again from 1C. Until the copy finishes, the target has only part of this table."
            },
            PrimaryButtonText = "Delete and copy again",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // Called from a menu as fire-and-forget: every failure is shown here, never lost (2026-10-01 review).
        try
        {
            var (ok, message) = await App.Supervisor.Client!.SyncRebuild(id, table);
            Notify(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, ok ? "Copying again" : "Not done", message);
        }
        catch (Exception e) when (e is EdgeException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NullReferenceException)
        {
            Notify(InfoBarSeverity.Error, "Not done", e.Message);
        }
        await RefreshAsync();
    }

    /// <summary>Any table of this 1C, found by search in either alphabet; the engine restarts with it and copies it first.</summary>
    private async void AddTable_Click(object sender, RoutedEventArgs e)
    {
        if (_base is not { } id) return;
        // The engine reports backend names (…Хозрасчетный_RecordType); the 1C list has the plain ones.
        var synced = _tableIds.Values.Select(t => t.EndsWith("_RecordType", StringComparison.Ordinal) ? t[..^"_RecordType".Length] : t)
                                     .ToHashSet(StringComparer.Ordinal);
        var chosen = await AddSyncTableDialog.ShowAsync(XamlRoot, ActualTheme, id, synced);
        if (chosen is not { Count: > 0 }) return;
        string names = string.Join(", ", chosen.Select(c => (string)c["name"]!));
        Notify(InfoBarSeverity.Informational, chosen.Count == 1 ? "Adding 1 table" : $"Adding {chosen.Count} tables",
               $"{names}. The sync engine restarts and copies {(chosen.Count == 1 ? "it" : "them")} first; the other tables go on from where they were.");
        AddTableButton.IsEnabled = false;
        try
        {
            // The list has names only; how each one is written in 1C is read now, for these few.
            var details = await App.Supervisor.Client!.OneCTableDetails(id, chosen.Select(c => (string)c["table"]!));
            await App.AddSyncTablesAsync(id, details.OfType<JsonObject>());
        }
        catch (Exception ex) when (ex is EdgeException or HttpRequestException or TaskCanceledException)
        {
            Notify(InfoBarSeverity.Error, "Not added", ex.Message);
        }
        finally { AddTableButton.IsEnabled = true; }
        _remeasure = true;
        await RefreshAsync();
    }

    private Button Action(string text, Func<Task<JsonObject>> run)
    {
        var b = new Button { Content = text, MinWidth = 84 };
        AutomationProperties.SetName(b, text);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await run(); }
            catch (Exception e) when (e is EdgeException or HttpRequestException) { Notify(InfoBarSeverity.Error, text, e.Message); }
            finally { b.IsEnabled = true; }
            await RefreshAsync();
        };
        return b;
    }

    private void Notify(InfoBarSeverity severity, string title, string message)
    {
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    private void ShowEmpty(string title, string text)
    {
        BaseTitle.Text = _base ?? "Sync";
        Summary.Visibility = TablesTitle.Visibility = TablesPanel.Visibility = Attention.Visibility = PauseButton.Visibility = Visibility.Collapsed;
        _mode = null;
        _attentionJson = "";
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        Empty.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.Window?.Go("bases");
}
