using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Cloud;
using OneC.Desktop.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>
/// The header company's 1C bases in AIBA, listed as the old Connector does (accounting/page.tsx):
/// the company's cloud records, each matched to the 1C connection on this computer with the same
/// name — the record's odataName is the local base name, the old app's only key between the two.
/// One card per infobase (<see cref="InfobaseCard"/>): a company has a handful, each with a sync
/// state, a local state and a next step — a five-column table of them was mostly empty sheet.
/// </summary>
public sealed partial class InfobasesPage : Page
{
    private static readonly CultureInfo Numbers = new("ru-RU");         // 18 915 285

    /// <summary>
    /// The cloud's view of the sync (stored rows / expected rows, last error), whoever syncs the
    /// base — today the old Connector; this app uploads nothing yet (DECISIONS D41).
    /// </summary>
    private static (Pill Kind, string Title, string Detail, double? Progress) SyncOf(OneCRecord r)
    {
        if (r.LastError is { } e) return (Pill.Bad, "Sync error", e, null);
        if (r.TotalCount == 0) return (Pill.Neutral, "Not synced yet", "Nothing in AIBA yet", null);
        string total = r.TotalCount.ToString("N0", Numbers);
        if (r.Percentage >= 100) return (Pill.Ok, "Synced", $"{total} rows in AIBA", null);
        string done = ((long)(r.TotalCount * r.Percentage / 100)).ToString("N0", Numbers);
        return (Pill.Info, $"Syncing · {Math.Floor(r.Percentage):0}%", $"{done} of {total} rows", r.Percentage);
    }

    // Local status every 3 s (cheap); the company's records every 30 s, as the old app polls.
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<OneCRecord> _records = new();
    private string? _recordsCompany;
    private DateTime _recordsAt;
    private bool _loading;
    private List<LauncherBase> _launcher = new();
    private readonly Dictionary<string, InfobaseCard> _cards = new();
    private Action? _emptyRun;
    private Dictionary<string, JsonObject> _engine = new(StringComparer.OrdinalIgnoreCase);   // sync state, by local base name

    /// <summary>The sync engine's state of each local base; empty while the engine is not up.</summary>
    private static async Task<Dictionary<string, JsonObject>> EngineStateAsync()
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (App.Supervisor.Client is not { } client) return map;
        try
        {
            foreach (var b in (await client.SyncStatus()).OfType<JsonObject>())
                map[(string)b["baseId"]!] = b;
        }
        catch (Exception e) when (e is EdgeException or HttpRequestException or TaskCanceledException) { }
        return map;
    }

    /// <summary>The engine's words for the sync column, and the link to the base's sync screen.</summary>
    private static (Pill Kind, string Title, string Detail, double? Progress) SyncOf(JsonObject b)
    {
        var (title, pill) = SyncText.State(b);
        int done = b["tablesDone"]?.GetValue<int>() ?? 0, total = b["tablesTotal"]?.GetValue<int>() ?? 0;
        int dead = b["deadLetters"]?.GetValue<int>() ?? 0;
        string detail = dead > 0 ? $"{SyncText.Count(dead, "thing")} to decide · test target"
                      : (string)b["mode"]! == "snapshot" ? "copying to the test target"
                      : $"{total} tables · test target";
        return (pill, title, detail, (string)b["mode"]! == "snapshot" && total > 0 ? 100.0 * done / total : null);
    }

    private static RowCommand SyncOpen(string baseName) => new("Sync details", "",() => App.Window?.Go("sync", baseName));

    /// <summary>
    /// A base the engine syncs that has no AIBA record here (or nobody is signed in): it still gets a
    /// card, so its sync is visible on this screen.
    /// </summary>
    private static InfobaseCardModel LocalModel(string baseName, JsonObject b, JsonArray? hosts)
    {
        var sync = SyncOf(b);
        string state = ConnectionsPage.EngineStatus(baseName, hosts);
        return new InfobaseCardModel("local:" + baseName, baseName, $"1C base {baseName}  ·  only on this computer, not in AIBA",
                                     sync.Kind, sync.Title, sync.Detail, sync.Progress,
                                     ConnectionsPage.StatusPill(state), state, "on this computer",
                                     new RowCommand("Browse", "", () => App.Window?.Go("browse", baseName)), Array.Empty<RowCommand>())
        { SyncOpen = SyncOpen(baseName) };
    }

    public InfobasesPage()
    {
        InitializeComponent();
        // Kept alive between visits (user: "loading this screen every time"): the cards stay, and the
        // list refreshes in the background — polling below — instead of reloading on every open.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        EventHandler onCloud = (_, _) => DispatcherQueue.TryEnqueue(async () => await Refresh(reload: true));
        EventHandler onState = (_, _) => DispatcherQueue.TryEnqueue(async () => await Refresh(reload: false));
        Loaded += async (_, _) =>
        {
            App.CompaniesChanged += onCloud;
            App.CloudChanged += onCloud;
            App.Supervisor.StateChanged += onState;
            _timer.Start();
            await Refresh(reload: false);              // cached cards at once; reloads only when 30 s old
            Cards.ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
            {
                new Microsoft.UI.Xaml.Media.Animation.AddDeleteThemeTransition()
            };
        };
        Unloaded += (_, _) =>
        {
            App.CompaniesChanged -= onCloud;
            App.CloudChanged -= onCloud;
            App.Supervisor.StateChanged -= onState;
            _timer.Stop();
            Cards.ChildrenTransitions = null;          // no entrance replay on the next visit
        };
        _timer.Tick += async (_, _) => await Refresh(reload: false);
    }

    private async Task Refresh(bool reload)
    {
        var company = App.SelectedCompany;
        AddButton.IsEnabled = App.Cloud.SignedIn && company is not null;
        Subtitle.Text = company is null
            ? "The 1C databases of the company chosen at the top."
            : $"The 1C databases of {company.Name} in AIBA.";
        _engine = await EngineStateAsync();
        if (!App.Cloud.SignedIn || company is null)
        {
            _records = new();
            _recordsCompany = null;
            if (_engine.Count > 0)
            {
                // Not signed in to AIBA: the engine's bases only, and how to see the rest.
                var hostsNow = await ConnectionsPage.HostsAsync();
                Render(_engine.Keys.Order(StringComparer.CurrentCultureIgnoreCase).Select(n => LocalModel(n, _engine[n], hostsNow)).ToList());
                Empty.Visibility = Visibility.Collapsed;
                AddButton.Visibility = Visibility.Collapsed;
                Subtitle.Text = App.Cloud.SignedIn
                    ? "Choose a company at the top to see its infobases. Below: the bases this computer syncs."
                    : "Sign in to AIBA to see your companies' infobases. Below: the bases this computer syncs.";
                return;
            }
            if (!App.Cloud.SignedIn)
                ShowEmpty("Sign in to AIBA", "Your companies' infobases appear here.", "Sign in", () => _ = App.Window?.SignInAsync());
            else
                ShowEmpty("Choose a company", App.CompaniesError ?? "Pick one at the top right.");
            return;
        }

        bool stale = reload || _recordsCompany != company.Id || DateTime.UtcNow - _recordsAt > TimeSpan.FromSeconds(30);
        if (stale && !_loading)
        {
            _loading = true;
            if (_recordsCompany != company.Id) ShowEmpty("", "Loading…");
            try
            {
                // The 1C launcher's list: which records this computer could connect.
                try { _launcher = LauncherBases.Read(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var list = await App.Cloud.OneCListAsync(company.Id);
                if (App.SelectedCompany?.Id == company.Id)          // else the company changed meanwhile; next tick reloads
                {
                    _records = list;
                    _recordsCompany = company.Id;
                    _recordsAt = DateTime.UtcNow;
                }
            }
            catch (CloudException ex) { LoadFailed(ex.Message); }
            catch (HttpRequestException) { LoadFailed("The AIBA cloud is not reachable."); }
            catch (TaskCanceledException) { LoadFailed("The AIBA cloud did not answer in time."); }
            finally { _loading = false; }
        }
        if (_recordsCompany != company.Id) return;

        var hosts = await ConnectionsPage.HostsAsync();
        var models = _records.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => Model(r, hosts)).ToList();
        models.AddRange(_engine.Keys.Where(n => !_records.Any(r => r.OdataName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                               .Order(StringComparer.CurrentCultureIgnoreCase).Select(n => LocalModel(n, _engine[n], hosts)));
        Render(models);
        if (models.Count == 0)
            ShowEmpty("No infobases here yet", $"Add one of this computer's 1C bases to {company.Name}.",
                      "Add infobase", () => Add_Click(this, new RoutedEventArgs()));
        else
        {
            Empty.Visibility = Visibility.Collapsed;
            AddButton.Visibility = Visibility.Visible;
        }
    }

    private InfobaseCardModel Model(OneCRecord r, JsonArray? hosts)
    {
        // The engine's state of the local base replaces the cloud's numbers.
        var engine = _engine.GetValueOrDefault(r.OdataName);
        var sync = engine is not null ? SyncOf(engine) : SyncOf(r);
        var here = HereOf(r, hosts);
        string meta = $"1C base {r.OdataName}  ·  {Software(r.Provider)}" + (r.IsMultiOrg ? "  ·  shared by several organisations" : "");
        return new InfobaseCardModel(r.Id, r.Name, meta, sync.Kind, sync.Title, sync.Detail, sync.Progress,
                                     here.Kind, here.Title, here.Detail, here.Next,
                                     new[] { new RowCommand("Delete from AIBA", "", () => Delete(r.Id), Destructive: true) })
        { SyncOpen = engine is not null ? SyncOpen((string)engine["baseId"]!) : null };
    }

    /// <summary>
    /// The record's base on this computer — the local connection named as its odataName, as the old
    /// app matches — and the natural next step: Browse it, or Connect it when 1C's list has it.
    /// </summary>
    private (Pill Kind, string Title, string Detail, RowCommand? Next) HereOf(OneCRecord r, JsonArray? hosts)
    {
        var local = App.Bases.Bases.FirstOrDefault(b => b.Name.Equals(r.OdataName, StringComparison.OrdinalIgnoreCase));
        if (local is not null)
        {
            string state = ConnectionsPage.EngineStatus(local.Name, hosts);
            return (ConnectionsPage.StatusPill(state), state, "on this computer",
                    new RowCommand("Browse", "", () => App.Window?.Go("browse", local.Name)));
        }
        if (r.IsMultiOrg) return (Pill.Neutral, "Shared base", "served by its main connection", null);
        var inList = _launcher.FirstOrDefault(l => l.Kind != LauncherKind.Web && l.Name.Equals(r.OdataName, StringComparison.OrdinalIgnoreCase));
        return inList is not null
            ? (Pill.Neutral, "Not connected here", "it is in 1C's list on this computer",
               new RowCommand("Connect", "", () => ConnectHere(inList)))
            : (Pill.Neutral, "Not on this computer", $"no 1C base named “{r.OdataName}” here", null);
    }

    /// <summary>Cards in order, updated in place by record id: polling never re-creates one.</summary>
    private void Render(IReadOnlyList<InfobaseCardModel> models)
    {
        var keys = models.Select(m => m.Key).ToHashSet();
        foreach (var gone in _cards.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            Cards.Children.Remove(_cards[gone]);
            _cards.Remove(gone);
        }
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            if (!_cards.TryGetValue(m.Key, out var card))
            {
                card = new InfobaseCard(m.Key);
                _cards[m.Key] = card;
                Cards.Children.Insert(Math.Min(i, Cards.Children.Count), card);
            }
            else if (Cards.Children.IndexOf(card) != i)
            {
                Cards.Children.Remove(card);
                Cards.Children.Insert(Math.Min(i, Cards.Children.Count), card);
            }
            card.Update(m);
        }
    }

    private void ShowEmpty(string title, string text, string? button = null, Action? run = null)
    {
        Render(Array.Empty<InfobaseCardModel>());
        AddButton.Visibility = Visibility.Collapsed;          // the empty state has its own button; never two
        EmptyTitle.Text = title;
        EmptyTitle.Visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = text;
        _emptyRun = run;
        EmptyButton.Content = button;
        EmptyButton.Visibility = button is not null && run is not null ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = Visibility.Visible;
    }

    private void EmptyButton_Click(object sender, RoutedEventArgs e) => _emptyRun?.Invoke();

    /// <summary>Connect this computer's base of the same name, right from its card.</summary>
    private async void ConnectHere(LauncherBase l)
    {
        var existing = App.Bases.Bases.FirstOrDefault(b => b.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
        var b = await ConnectDialog.ShowAsync(XamlRoot, l.Name, l, existing);
        if (b is null) return;
        App.Bases.Upsert(b);
        Show(InfoBarSeverity.Informational, $"Connected “{b.Name}”. Starting the 1C engine with it…");
        await App.RestartSupervisorAsync();
        await Refresh(reload: false);
    }

    private async void Delete(string id)
    {
        if (_records.FirstOrDefault(r => r.Id == id) is not { } rec || App.SelectedCompany is not { } company) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Delete “{rec.Name}” from AIBA?",
            Content = $"This deletes the infobase and all its data synced to AIBA, for everyone in {company.Name}. " +
                      "It can't be undone. The 1C base on this computer is not changed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await App.Cloud.OneCDeleteAsync(rec.Id);
            if (App.Cloud.Session is { } s) App.Links.RemoveRecord(s.Env, s.UserId, rec.Id);   // no more heartbeat for it
            Show(InfoBarSeverity.Success, $"Deleted “{rec.Name}” from {company.Name}. AIBA removes its data in the background.");
            await Refresh(reload: true);
        }
        catch (CloudException ex) { Show(InfoBarSeverity.Error, "Could not delete: " + ex.Message); }
        catch (HttpRequestException) { Show(InfoBarSeverity.Error, "Could not delete: the AIBA cloud is not reachable."); }
    }

    private void LoadFailed(string text)
    {
        _recordsAt = DateTime.UtcNow;                                // retry in 30 s, not every tick
        if (_recordsCompany != App.SelectedCompany?.Id)
            ShowEmpty("Could not load the infobases", text, "Try again", () => _ = Refresh(reload: true));
        else Show(InfoBarSeverity.Warning, "Could not refresh the list: " + text);
    }

    private static string Software(string provider) => provider switch
    {
        "unisoft" => "Unisoft",
        "venkon" => "Venkon",
        "1uz" => "1UZ",
        _ => provider
    };

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (App.SelectedCompany is not { } company) return;
        var existing = _recordsCompany == company.Id ? _records : new List<OneCRecord>();
        var rec = await AddInfobaseDialog.ShowAsync(XamlRoot, company, existing);
        if (rec is null) return;
        Show(InfoBarSeverity.Success, $"Added “{rec.Name}” to {company.Name}.");
        await Refresh(reload: true);
    }

    private void Show(InfoBarSeverity severity, string text)
    {
        Message.Severity = severity;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
