using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>
/// "Add table": every catalog, document, chart of accounts and register of the base's 1C, searched in
/// either alphabet (<see cref="Translit"/>: "kontr" finds Контрагенты). Several can be ticked at once;
/// tables already synced show as such and cannot be ticked. Returns the chosen entries of the host's
/// table list, or null when cancelled.
/// </summary>
internal static class AddSyncTableDialog
{
    private const int MaxShown = 200;

    // The first read of a large configuration's metadata takes up to a minute (bilim: 55 s; the
    // host's cache makes the next one 0.5 s). So the list is read in the background as soon as a
    // base's sync screen opens, kept per base on disk, and a kept list is shown at once while a
    // fresh one is read once per run.
    private static readonly Dictionary<string, Task<List<JsonObject>>> Loads = new(StringComparer.OrdinalIgnoreCase);

    private static string CacheFile(string baseName) =>
        Path.Combine(App.DataDir, "sync", "tables-" + string.Concat(baseName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".json");

    /// <summary>The table list kept on disk from an earlier run, or null.</summary>
    private static List<JsonObject>? Kept(string baseName)
    {
        try { return File.Exists(CacheFile(baseName)) ? JsonNode.Parse(File.ReadAllText(CacheFile(baseName)))?.AsArray().OfType<JsonObject>().ToList() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Starts (once per run) reading the base's table list; the sync screen calls it on open.</summary>
    public static Task<List<JsonObject>> Prefetch(string baseName)
    {
        lock (Loads)
        {
            if (Loads.TryGetValue(baseName, out var running) && !running.IsFaulted) return running;
            return Loads[baseName] = Task.Run(async () =>
            {
                if (App.Supervisor.Client is not { } client) throw new InvalidOperationException("the connector is not running");
                var list = (await client.OneCTables(baseName)).OfType<JsonObject>().Select(e => (JsonObject)e.DeepClone()).ToList();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CacheFile(baseName))!);
                    File.WriteAllText(CacheFile(baseName), new JsonArray(list.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString());
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log("table list cache: " + e.Message); }
                return list;
            });
        }
    }

    public static async Task<List<JsonObject>?> ShowAsync(XamlRoot root, ElementTheme theme, string baseName, ISet<string> synced)
    {
        var search = new TextBox { PlaceholderText = "Search in Latin or Cyrillic: kontragent, реализация, xozraschet…" };
        AutomationProperties.SetName(search, "Search tables");
        var count = new TextBlock { FontSize = 14, Foreground = ThemeBrushes.Get(search, "TextFillColorSecondaryBrush") };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 380 };
        var loading = new StackPanel
        {
            Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = $"Reading the tables of {baseName}…", HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = "The first time takes up to a minute for a large 1C. After that it opens at once.", FontSize = 14,
                    Foreground = ThemeBrushes.Get(search, "TextFillColorSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
        var body = new Grid { RowSpacing = 8, Width = 680 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(380) });
        Grid.SetRow(count, 1);
        Grid.SetRow(list, 2);
        Grid.SetRow(loading, 2);
        body.Children.Add(search);
        body.Children.Add(count);
        body.Children.Add(list);
        body.Children.Add(loading);

        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = theme,
            Title = $"Add tables to sync · {baseName}",
            Content = body,
            PrimaryButtonText = "Add", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;

        var all = new List<JsonObject>();
        var chosen = new Dictionary<string, JsonObject>(StringComparer.Ordinal);    // kept across searches
        bool filling = false;

        void Fill()
        {
            filling = true;
            list.Items.Clear();
            string q = search.Text.Trim();
            var hits = all.Select(e => (Entry: e, Score: Translit.Score(q, (string)e["name"]!, (string?)e["synonym"], SyncText.Kind((string)e["table"]!))))
                          .Where(x => x.Score > 0)
                          .OrderByDescending(x => x.Score).ThenBy(x => (string)x.Entry["name"]!, StringComparer.CurrentCultureIgnoreCase)
                          .ToList();
            foreach (var (e, _) in hits.Take(MaxShown))
            {
                string table = (string)e["table"]!;
                bool already = synced.Contains(table);
                var item = new ListViewItem { Content = Row(e, already, list), Tag = e, IsEnabled = !already };
                AutomationProperties.SetName(item, $"{e["name"]} ({SyncText.Kind(table)}){(already ? ", already synced" : "")}");
                list.Items.Add(item);
                if (chosen.ContainsKey(table)) list.SelectedItems.Add(item);
            }
            count.Text = hits.Count == 0 ? $"Nothing matches “{q}”."
                       : hits.Count > MaxShown ? $"First {MaxShown} of {hits.Count} tables. Type more to narrow it down."
                       : q.Length == 0 ? $"{hits.Count} tables in this 1C." : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}.";
            filling = false;
        }

        void Chosen()
        {
            dialog.IsPrimaryButtonEnabled = chosen.Count > 0;
            dialog.PrimaryButtonText = chosen.Count switch { 0 => "Add", 1 => "Add 1 table", var n => $"Add {n} tables" };
        }

        list.SelectionChanged += (_, e) =>
        {
            if (filling) return;
            foreach (var i in e.AddedItems.OfType<ListViewItem>()) chosen[(string)((JsonObject)i.Tag)["table"]!] = (JsonObject)i.Tag;
            foreach (var i in e.RemovedItems.OfType<ListViewItem>()) chosen.Remove((string)((JsonObject)i.Tag)["table"]!);
            Chosen();
        };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        debounce.Tick += (_, _) => { debounce.Stop(); Fill(); };
        search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };

        dialog.Opened += async (_, _) =>
        {
            search.Focus(FocusState.Programmatic);
            var load = Prefetch(baseName);
            if (!load.IsCompletedSuccessfully && Kept(baseName) is { Count: > 0 } kept)
            {
                all = kept;                                   // last run's list now; the fresh one replaces it below
                loading.Visibility = Visibility.Collapsed;
                Fill();
            }
            try
            {
                all = await load;
                loading.Visibility = Visibility.Collapsed;
                Fill();
            }
            catch (Exception e) when (e is EdgeException or HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                if (all.Count > 0) return;                    // the kept list stays usable
                loading.Children.Clear();
                loading.Children.Add(new TextBlock { Text = "Could not read the tables: " + e.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? chosen.Values.ToList() : null;
    }

    /// <summary>Name first (semibold), the 1C synonym under it (muted), the type on the right.</summary>
    private static Grid Row(JsonObject e, bool already, FrameworkElement theme)
    {
        string table = (string)e["table"]!, name = (string)e["name"]!, synonym = (string?)e["synonym"] ?? "";
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var who = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (synonym.Length > 0 && synonym != name)
            who.Children.Add(new TextBlock
            {
                Text = synonym, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeBrushes.Get(theme, "TextFillColorSecondaryBrush")
            });
        ToolTipService.SetToolTip(who, synonym.Length > 0 ? $"{name}\n{synonym}" : name);
        g.Children.Add(who);
        var kind = new TextBlock
        {
            Text = already ? "Already synced" : SyncText.Kind(table), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushes.Get(theme, already ? "OkBrush" : "TextFillColorSecondaryBrush")
        };
        Grid.SetColumn(kind, 1);
        g.Children.Add(kind);
        return g;
    }
}
