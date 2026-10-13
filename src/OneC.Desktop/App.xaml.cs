using Microsoft.UI.Xaml;
using OneC.Desktop.Services;

namespace OneC.Desktop;

/// <summary>
/// App-wide services. Deliberately small: a base store, the supervisor child process, and
/// the window. Screens talk to 1C only through <see cref="SupervisorProcess.Client"/>.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The app's data folder: %LOCALAPPDATA%\AIBA\Connector, or AIBA_CONNECTOR_DATA (development:
    /// a scripted or second copy that must not touch the real session, links and bases).
    /// </summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("AIBA_CONNECTOR_DATA") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIBA", "Connector");

    public static BaseStore Bases { get; } = new(Path.Combine(DataDir, "bases.json"));
    public static SupervisorProcess Supervisor { get; } = new();
    public static MainWindow? Window { get; private set; }

    // AIBA cloud account (specs/2026-09-30-auth-and-cloud-link.md). Production by default.
    public static OneC.Cloud.SessionStore CloudStore { get; } = new(DataDir);
    public static OneC.Cloud.LinkStore Links { get; } = new(DataDir);
    private static readonly string Device = OneC.Cloud.DeviceId.Get();
    public static OneC.Cloud.CloudClient Cloud { get; private set; } = NewCloud(CloudStore.LoadEnvKey());
    /// <summary>Signed in, signed out, or switched environment.</summary>
    public static event EventHandler? CloudChanged;

    private static OneC.Cloud.CloudClient NewCloud(string envKey)
    {
        var c = new OneC.Cloud.CloudClient(OneC.Cloud.CloudEnvironment.Find(CloudStore.Dir, envKey), CloudStore, Device);
        c.SignedOut += (_, _) => RaiseCloudChanged();
        c.SessionEnded += (_, _) => Notifier.Notify(NoticeKind.Warning, "Signed out of AIBA",
            "Your AIBA session ended. Sign in again so the bases keep reaching the cloud.");
        return c;
    }

    /// <summary>Environment change = a different cloud: the session ends (as in the old Connector).</summary>
    public static void SwitchCloud(string envKey)
    {
        if (envKey == Cloud.Env.Key) return;
        Cloud.SignOut();
        Cloud.Dispose();
        CloudStore.SaveEnvKey(envKey);
        Cloud = NewCloud(envKey);
        RaiseCloudChanged();
    }

    public static void RaiseCloudChanged() =>
        Window?.DispatcherQueue.TryEnqueue(async () =>
        {
            CloudChanged?.Invoke(null, EventArgs.Empty);
            await LoadCompaniesAsync();
        });

    /// <summary>The account's companies (header picker) and the chosen one; empty when signed out.</summary>
    public static IReadOnlyList<OneC.Cloud.CloudCompany> Companies { get; private set; } = Array.Empty<OneC.Cloud.CloudCompany>();
    public static OneC.Cloud.CloudCompany? SelectedCompany { get; private set; }
    public static string? CompaniesError { get; private set; }
    public static event EventHandler? CompaniesChanged;

    /// <summary>Reloads the company list. Keeps the chosen company if still listed, else the account's
    /// default, else the first — the old Connector's rule (connector src/providers/seed.tsx:94-103).</summary>
    public static async Task LoadCompaniesAsync()
    {
        CompaniesError = null;
        if (Cloud.Session is not { } s)
        {
            Companies = Array.Empty<OneC.Cloud.CloudCompany>();
            SelectedCompany = null;
        }
        else
        {
            try
            {
                Companies = await Cloud.CompaniesAsync();
                string? saved = CloudStore.LoadCompany(s.Env, s.UserId);
                SelectedCompany = Companies.FirstOrDefault(c => c.Id == saved)
                                  ?? Companies.FirstOrDefault(c => c.IsDefault) ?? Companies.FirstOrDefault();
            }
            catch (Exception ex) when (ex is OneC.Cloud.CloudException or HttpRequestException or TaskCanceledException)
            {
                CompaniesError = ex is OneC.Cloud.CloudException ce ? ce.Message : "The AIBA cloud is not reachable.";
            }
        }
        CompaniesChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SelectCompany(OneC.Cloud.CloudCompany c)
    {
        if (Cloud.Session is not { } s || SelectedCompany?.Id == c.Id) return;
        SelectedCompany = c;
        CloudStore.SaveCompany(s.Env, s.UserId, c.Id);
        CompaniesChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Hidden developer switch (Ctrl+Shift+D, as in the old Connector): Production ↔
    /// Development, plus the local stub when cloud.json exists. Switching signs out.</summary>
    public static void CycleCloudEnvironment()
    {
        var envs = OneC.Cloud.CloudEnvironment.Available(CloudStore.Dir);
        int i = envs.ToList().FindIndex(e => e.Key == Cloud.Env.Key);
        SwitchCloud(envs[(i + 1) % envs.Count].Key);
    }

    /// <summary>The signed-in user's links in the current environment.</summary>
    public static IReadOnlyList<OneC.Cloud.CloudLink> MyLinks() =>
        Cloud.Session is { } s ? Links.For(s.Env, s.UserId) : Array.Empty<OneC.Cloud.CloudLink>();

    /// <summary>The header theme switcher's choice: "light", "dark" or "system" (follow Windows). Kept in ui.json.</summary>
    public static string ThemeChoice { get; private set; } = LoadTheme();
    private static string UiFile => Path.Combine(DataDir, "ui.json");

    private static string LoadTheme()
    {
        try
        {
            string? t = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(UiFile))?["theme"]?.GetValue<string>();
            return t is "light" or "dark" ? t : "system";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidOperationException) { return "system"; }
    }

    public static void SaveTheme(string theme)
    {
        ThemeChoice = theme;
        SaveUi("theme", theme);
    }

    private static string? LoadUi(string key)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(UiFile))?[key]?.ToString(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidOperationException) { return null; }
    }

    /// <summary>One key of ui.json; the others are kept.</summary>
    private static void SaveUi(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            System.Text.Json.Nodes.JsonObject ui;
            try { ui = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(UiFile)) as System.Text.Json.Nodes.JsonObject ?? new(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { ui = new(); }
            ui[key] = value;
            File.WriteAllText(UiFile, ui.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("ui.json: " + ex.Message); }
    }

    /// <summary>
    /// Per base, the tables the user added to sync (sync-tables.json in the data folder).
    /// </summary>
    private static string SyncTablesFile => Path.Combine(DataDir, "sync-tables.json");

    /// <summary>The tables the engine copies for a base: the user's choice (Add table), else the default set.</summary>
    public static System.Text.Json.Nodes.JsonArray SyncTables(string baseName)
    {
        try
        {
            if (File.Exists(SyncTablesFile) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SyncTablesFile)) is System.Text.Json.Nodes.JsonObject all &&
                all[baseName] is System.Text.Json.Nodes.JsonArray chosen)
                return (System.Text.Json.Nodes.JsonArray)chosen.DeepClone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Log("sync-tables.json: " + ex.Message); }
        return DefaultSyncTables();
    }

    /// <summary>
    /// Adds tables (entries of the host's table list) to a base's sync and restarts the engine with
    /// them: each new table gets its first copy, the others go on from where they were.
    /// </summary>
    public static async Task AddSyncTablesAsync(string baseName, IEnumerable<System.Text.Json.Nodes.JsonObject> entries)
    {
        var tables = SyncTables(baseName);
        var have = tables.Select(t => (string?)t!["table"]).ToHashSet(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            string table = (string)e["table"]!, family = (string)e["family"]!;
            if (!have.Add(table)) continue;
            var t = new System.Text.Json.Nodes.JsonObject
            {
                ["table"] = table, ["name"] = (string)e["name"]!, ["family"] = family, ["isMovement"] = e["isMovement"]?.GetValue<bool>() ?? false
            };
            // Default policy, as for Хозрасчетный: registers written by documents from 3 months back;
            // a register written on its own is compared hourly (D-6: tuned per table from measurements).
            if (family is "reg_accounting" or "reg_accumulation" or "reg_info_recorded") t["from"] = DateTime.Today.AddMonths(-3).ToString("yyyy-MM-dd");
            if (family == "reg_info_independent") t["refreshEveryMinutes"] = 60;
            tables.Add(t);
        }
        System.Text.Json.Nodes.JsonObject all;
        try { all = File.Exists(SyncTablesFile) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SyncTablesFile)) as System.Text.Json.Nodes.JsonObject ?? new() : new(); }
        catch (System.Text.Json.JsonException) { all = new(); }
        all[baseName] = tables;
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SyncTablesFile, all.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        await RestartSupervisorAsync();
    }

    private static System.Text.Json.Nodes.JsonArray DefaultSyncTables() =>
        new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["table"] = "Catalog_Контрагенты", ["name"] = "Контрагенты", ["family"] = "catalog", ["isMovement"] = false },
            new System.Text.Json.Nodes.JsonObject { ["table"] = "Catalog_Номенклатура", ["name"] = "Номенклатура", ["family"] = "catalog", ["isMovement"] = false },
            new System.Text.Json.Nodes.JsonObject { ["table"] = "Document_ПоступлениеТоваровУслуг", ["name"] = "ПоступлениеТоваровУслуг", ["family"] = "document", ["isMovement"] = true },
            new System.Text.Json.Nodes.JsonObject { ["table"] = "Document_РеализацияТоваровУслуг", ["name"] = "РеализацияТоваровУслуг", ["family"] = "document", ["isMovement"] = true },
            new System.Text.Json.Nodes.JsonObject
            {
                ["table"] = "AccountingRegister_Хозрасчетный", ["name"] = "Хозрасчетный", ["family"] = "reg_accounting", ["isMovement"] = true,
                ["from"] = DateTime.Today.AddMonths(-3).ToString("yyyy-MM-dd")
            });

    /// <summary>
    /// The sync config for the connected bases: the local test target, the sync state in the data
    /// folder, and each base's tables (tables a base does not have are skipped by the engine).
    /// Rewritten on every start, so the pre-rename <c>sync2.json</c> holds nothing worth moving.
    /// </summary>
    private static string WriteSyncConfig()
    {
        var cfg = new System.Text.Json.Nodes.JsonObject
        {
            ["db"] = Path.Combine(DataDir, "sync", "sync.db"),
            ["target"] = "stub",
            ["bases"] = new System.Text.Json.Nodes.JsonArray(Bases.Bases.Select(b => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = b.Name, ["connectionId"] = b.Name, ["tables"] = SyncTables(b.Name)
            }).ToArray())
        };
        string path = Path.Combine(DataDir, "sync.json");
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(path, cfg.ToJsonString());
        try { File.Delete(Path.Combine(DataDir, "sync2.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>Command-line options used by screenshots and tests: --page, --theme, --import.</summary>
    public static string? Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, "--" + name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public App()
    {
        InitializeComponent();
        DesktopLog.Write = Log;
        UnhandledException += (_, e) =>
        {
            // Keep the window alive and leave a trace instead of vanishing.
            e.Handled = true;
            Log("unhandled: " + e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Which build a support log came from (tools/publish.ps1 stamps -p:Version).
        Log("start " + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unversioned"));
        // One copy per data folder: a second start (a click on the shortcut while the app sits in the
        // notification area, or the Run entry at sign-in) only brings the running copy forward. Two
        // copies would run two engines against the same bases. A dev copy with its own
        // AIBA_CONNECTOR_DATA is a different folder and still runs alongside.
        string key = "AIBA.Connector." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DataDir.ToUpperInvariant())))[..16];
        _single = new Mutex(true, @"Local\" + key, out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\" + key + ".show", out var other)) { other.Set(); other.Dispose(); }
            Log("already running: brought the running copy forward");
            // Application.Exit() before any window exists leaves the process running: end it outright.
            Environment.Exit(0);
        }
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + ".show");
        ThreadPool.RegisterWaitForSingleObject(_showRequest,
            (_, _) => Window?.DispatcherQueue.TryEnqueue(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        if (Arg("import") is string import && File.Exists(import))
            Bases.ImportConnectionStrings(import);

        Notifier.Start();
        // The engine dying on its own (not a restart, not Quit) is worth a notice.
        var lastState = Supervisor.State;
        Supervisor.StateChanged += (_, _) =>
        {
            var now = Supervisor.State;
            if (now == SupervisorState.Failed && lastState != SupervisorState.Failed && !Quitting)
                Notifier.Notify(NoticeKind.Error, "Sync stopped", "The 1C engine stopped: " + Supervisor.LastError, "resources");
            lastState = now;
        };

        Window = new MainWindow();
        // A window that was hidden to the notification area and shown again does not end the app
        // when it finally closes (measured): end it explicitly, after MainWindow's own Closed
        // handlers (registered in its constructor) have stopped the engine.
        Window.Closed += (_, _) => Exit();
        Tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(Window), IconPath, "AIBA Connector", ShowWindow, Quit);
        StartupAndPower.RepairStartEntry();
        StartupAndPower.KeepAwake(KeepAwake);
        // Started by the Run entry at sign-in: stay in the notification area, sync runs anyway.
        if (Environment.GetCommandLineArgs().Contains(StartupAndPower.BackgroundArg))
        {
            Window.HideSplash();
            Log("started in the background");
        }
        else
        {
            Window.Activate();
            Window.PlaySplash();
        }
        // Screenshot/test switch, like --page and --theme: one sample in-app toast per kind.
        if (Arg("demo-toasts") is not null)
            foreach (var k in new[] { NoticeKind.Error, NoticeKind.Warning, NoticeKind.Success })
                Window.ShowToast(k, k + " sample", "This is how AIBA Connector tells you about " + k.ToString().ToLowerInvariant() + " events.");
        _ = RestartSupervisorAsync();
        _ = LoadCompaniesAsync();                       // restored session → header company picker
    }

    private static Mutex? _single;
    private static EventWaitHandle? _showRequest;

    /// <summary>The logo, next to the exe (window caption and notification area).</summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    internal static TrayIcon? Tray { get; private set; }

    /// <summary>True once the user chose Quit: closing the window then really ends the app.</summary>
    public static bool Quitting { get; private set; }

    /// <summary>Closing the window keeps the app (and sync) running in the notification area. Default on.</summary>
    public static bool RunInBackground
    {
        get => LoadUi("background") != "false";
        set => SaveUi("background", value ? "true" : "false");
    }

    /// <summary>Windows notifications for sync problems while the window is not in front. Default on.</summary>
    public static bool NotificationsOn
    {
        get => LoadUi("notifications") != "false";
        set => SaveUi("notifications", value ? "true" : "false");
    }

    /// <summary>Windows does not go to sleep while the app runs. Default off.</summary>
    public static bool KeepAwake
    {
        get => LoadUi("keepAwake") == "true";
        set { SaveUi("keepAwake", value ? "true" : "false"); StartupAndPower.KeepAwake(value); }
    }

    /// <summary>Shown once, the first time closing the window hides it instead of quitting.</summary>
    public static void HiddenToTray()
    {
        if (LoadUi("trayHintShown") == "true") return;
        SaveUi("trayHintShown", "true");
        // A Windows notification when Windows allows them, else the old tray balloon.
        if (NotificationsOn && Notifier.WindowsBlocks() is null)
            Notifier.Notify(NoticeKind.Info, "AIBA Connector is still running", "Sync goes on. Open it or quit from its icon in the notification area.");
        else
            Tray?.Notify("AIBA Connector is still running", "Sync goes on. Open it or quit from this icon.");
    }

    public static void ShowWindow()
    {
        if (Window is null) return;
        Window.AppWindow.Show();
        Window.Activate();
    }

    /// <summary>Ends the app: the window's Closed handlers stop the engine and report the bases offline.</summary>
    public static void Quit()
    {
        Quitting = true;
        Tray?.Dispose();
        Tray = null;
        Window?.Close();
    }

    /// <summary>
    /// (Re)starts the supervisor with the current base list. Called after any edit. Sync always runs
    /// for the connected bases (user 2026-10-03, D50) — still to the local test target only, nothing
    /// is uploaded to AIBA until the developer's go (D-2).
    /// </summary>
    public static Task RestartSupervisorAsync() =>
        Supervisor.StartAsync(Bases.SupervisorPayload(), default, Bases.Bases.Count > 0 ? WriteSyncConfig() : null);

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "app.log"), $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
