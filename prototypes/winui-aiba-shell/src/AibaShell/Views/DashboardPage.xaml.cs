using System;
using System.Collections.ObjectModel;
using System.Linq;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AibaShell.Views;

public sealed partial class DashboardPage : Page
{
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly ISyncService _sync = App.Get<ISyncService>();
    private readonly IBankService _banks = App.Get<IBankService>();
    private readonly IProcessSupervisor _procs = App.Get<IProcessSupervisor>();
    private readonly ISystemMetricsService _metrics = App.Get<ISystemMetricsService>();
    private readonly ILogService _log = App.Get<ILogService>();

    public ObservableCollection<ActivityItem> Activity { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();

        SubtitleText.Text = "Local agent on WIN-11-2070 · adapter :55899 · relay connected";
        BaseMiniList.ItemsSource = _onec.InfoBases;
        ActivityList.ItemsSource = Activity;

        SeedActivity();

        _metrics.Changed += OnMetricsChanged;
        _sync.Changed += OnSyncChanged;

        Unloaded += (s, e) =>
        {
            _metrics.Changed -= OnMetricsChanged;
            _sync.Changed -= OnSyncChanged;
        };

        Refresh();
    }

    private void SeedActivity()
    {
        void A(string glyph, string title, string detail, StatusKind k, int minutesAgo) =>
            Activity.Add(new ActivityItem
            {
                Glyph = glyph,
                Title = title,
                Detail = detail,
                Status = k,
                At = DateTimeOffset.Now.AddMinutes(-minutesAgo)
            });

        A("", "Worker started", "oscript Worker 3 attached to MIREL (pid 18422)", StatusKind.Busy, 0);
        A("", "Sync completed", "Kanstik · 18 402 775 rows · 6m 12s", StatusKind.Healthy, 2);
        A("", "COM reconnect", "signum · V83.ComConnector re-acquired after 1 retry", StatusKind.Warning, 5);
        A("", "Bank sync finished", "Kapitalbank · 38 statement rows imported", StatusKind.Healthy, 6);
        A("", "COM version mismatch", "Production-01 · client 8.3.24 vs server 8.3.25", StatusKind.Error, 11);
        A("", "Documents posted", "4 bank documents posted with provodki", StatusKind.Healthy, 14);
        A("", "Log rotated", "connector.log -> connector.log.1 (48 MB)", StatusKind.Idle, 22);
        A("", "Relay reconnected", "tunnel agent re-dialled after 12h JWT refresh", StatusKind.Warning, 31);
        A("", "Report built", "Sverka razvernutoe · signum · 2026-08 · 4.1 s", StatusKind.Healthy, 38);
        A("", "Bank needs login", "SQB session expired, operator action required", StatusKind.Warning, 96);
    }

    private void OnMetricsChanged(object? sender, EventArgs e) => Refresh();
    private void OnSyncChanged(object? sender, EventArgs e) => RefreshSyncCard();

    private void Refresh()
    {
        var connected = _onec.InfoBases.Count(b => b.Status is StatusKind.Healthy or StatusKind.Busy);
        BasesValue.Text = connected.ToString();
        WorkersValue.Text = _procs.Processes.Count(p => p.Kind == "oscript" && p.State != "stopped").ToString();
        QueueValue.Text = _onec.InfoBases.Sum(b => b.QueueDepth).ToString();

        var last = _onec.InfoBases.Where(b => b.LastSync.HasValue).Max(b => b.LastSync!.Value);
        LastSyncValue.Text = Format.Ago(last);

        CpuValue.Text = _metrics.CpuPercent.ToString("N0") + "%";
        CpuBar.Value = _metrics.CpuPercent;
        RamValue.Text = (_metrics.RamUsedMb / 1024).ToString("N1") + " / " + (_metrics.RamTotalMb / 1024).ToString("N0") + " GB";
        ProcValue.Text = _metrics.ProcessCount.ToString();
        UptimeValue.Text = Format.Duration(_metrics.Uptime);

        BanksValue.Text = _banks.Banks.Count(b => b.Status == StatusKind.Healthy).ToString();
        BankWarnValue.Text = _banks.WarningCount.ToString();
        var lastBank = _banks.Banks.Max(b => b.LastSync);
        BankLastValue.Text = Format.Ago(lastBank);

        RefreshSyncCard();
    }

    private void RefreshSyncCard()
    {
        SyncBaseValue.Text = _sync.CurrentBase?.Name ?? "no base";
        SyncObjectValue.Text = _sync.CurrentObject;
        SyncBar.Value = _sync.Progress;
        SyncRateValue.Text = _sync.RowsPerSecond.ToString("N0") + " rows/s";
        SyncRowsValue.Text = _sync.RowsProcessed.ToString("N0");
        SyncEtaValue.Text = _sync.IsRunning ? Format.Duration(_sync.Eta) : "stopped";
    }

    private void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        _sync.Start();
        _log.Write(Services.LogLevel.Info, "App", "Operator triggered sync on all eligible bases");
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _onec.InfoBases.Where(b => b.Status == StatusKind.Idle || b.Status == StatusKind.Error))
            _onec.Connect(b);
    }

    private async void Heal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Heal external connection",
            Content = "Tick «Внешнее соединение» on every common module that blocks COM writes, " +
                      "then rebuild the database configuration.\n\nThis is a dry run in the prototype.",
            PrimaryButtonText = "Run dry run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            _log.Write(Services.LogLevel.Info, "1C", "heal-external-connection: dry run, 3 modules would change");
    }

    private void OpenProblemBase_Click(object sender, RoutedEventArgs e)
    {
        var b = _onec.InfoBases.FirstOrDefault(x => x.HasProblem);
        if (b is not null) Shell.ShowBase(b);
    }

    private void OpenBases_Click(object sender, RoutedEventArgs e) => Shell.SelectNav("bases");

    private MainWindow Shell => AppShell.Window;
}
