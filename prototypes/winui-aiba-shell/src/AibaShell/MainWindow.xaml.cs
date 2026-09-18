using System;
using System.Linq;
using AibaShell.Services;
using AibaShell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AibaShell;

public sealed partial class MainWindow : Window
{
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly IBankService _banks = App.Get<IBankService>();
    private readonly IProcessSupervisor _procs = App.Get<IProcessSupervisor>();
    private readonly ISystemMetricsService _metrics = App.Get<ISystemMetricsService>();

    public MainWindow()
    {
        InitializeComponent();
        AppShell.Window = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "AIBA Connector";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 940));

        App.IsDarkTheme = RootGrid.ActualTheme == ElementTheme.Dark;
        RootGrid.ActualThemeChanged += (s, e) => App.IsDarkTheme = RootGrid.ActualTheme == ElementTheme.Dark;

        _metrics.Changed += (s, e) => UpdateStatusStrip();
        UpdateStatusStrip();

        // A page that throws in its constructor otherwise fails silently and the
        // shell just keeps showing the previous page. Make that loud.
        ContentFrame.NavigationFailed += (s, e) =>
        {
            e.Handled = true;
            var text = "Navigation failed: " + e.SourcePageType.Name + "\n" + e.Exception;
            System.Diagnostics.Debug.WriteLine(text);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aibashell-nav-errors.log"),
                DateTime.Now.ToString("s") + " " + text + "\n\n");
            App.Get<ILogService>().Write(Services.LogLevel.Error, "App", text.Replace("\n", " "));
        };

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
        ContentFrame.Navigate(typeof(DashboardPage), null, new SuppressNavigationTransitionInfo());

        // --page <tag> opens the shell straight on one screen. Used for capturing
        // documentation screenshots without driving the UI by hand.
        var args = Environment.GetCommandLineArgs();

        var flag = Array.IndexOf(args, "--page");
        if (flag >= 0 && flag + 1 < args.Length)
        {
            var tag = args[flag + 1];
            if (tag == "base")
            {
                var b = _onec.InfoBases.FirstOrDefault(x => x.HasProblem) ?? _onec.InfoBases.First();
                SelectNav("bases");
                ShowBase(b);
            }
            else
            {
                SelectNav(tag);
            }
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string ?? "dashboard";

        Type page = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "onec" => typeof(InfoBasesPage),
            "bases" => typeof(InfoBasesPage),
            "sync" => typeof(SyncPage),
            "documents" => typeof(DocumentsPage),
            "reports" => typeof(ReportsPage),
            "banks" => typeof(BanksPage),
            "processes" => typeof(ProcessesPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    /// <summary>Jump to a base detail page from anywhere (dashboard, sync, processes).</summary>
    public void ShowBase(InfoBase b) =>
        ContentFrame.Navigate(typeof(BaseDetailPage), b, new DrillInNavigationTransitionInfo());

    public void SelectNav(string tag)
    {
        var all = Nav.MenuItems.OfType<NavigationViewItem>()
            .SelectMany(i => new[] { i }.Concat(i.MenuItems.OfType<NavigationViewItem>()))
            .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>());
        var match = all.FirstOrDefault(i => (i.Tag as string) == tag);
        if (match is not null) Nav.SelectedItem = match;
    }

    private void UpdateStatusStrip()
    {
        var connected = _onec.InfoBases.Count(b => b.Status is StatusKind.Healthy or StatusKind.Busy);
        var workers = _procs.Processes.Count(p => p.Kind == "oscript" && p.State != "stopped");
        var queue = _onec.InfoBases.Sum(b => b.QueueDepth);

        StatusBases.Text = connected + "/" + _onec.InfoBases.Count + " bases connected";
        StatusWorkers.Text = workers + " workers";
        StatusQueue.Text = "queue " + queue;
        StatusBanks.Text = _banks.Banks.Count + " banks · " + _banks.WarningCount + " need attention";

        StatusCpu.Text = "CPU " + _metrics.CpuPercent.ToString("N1") + "%";
        StatusRam.Text = "RAM " + (_metrics.RamUsedMb / 1024).ToString("N1") + " / " + (_metrics.RamTotalMb / 1024).ToString("N0") + " GB";
        StatusUptime.Text = "uptime " + Format.Duration(_metrics.Uptime);
    }
}
