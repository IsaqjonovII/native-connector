using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OneC.Desktop.Controls;
using OneC.Desktop.Services;
using OneC.Desktop.Views;

namespace OneC.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "AIBA Connector";

        int w = int.TryParse(App.Arg("width"), out int pw) ? pw : 1360;
        int h = int.TryParse(App.Arg("height"), out int ph) ? ph : 860;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        // --theme light|dark forces a theme (screenshots); otherwise follow Windows.
        if (App.Arg("theme") is string theme)
            RootGrid.RequestedTheme = theme == "dark" ? ElementTheme.Dark : ElementTheme.Light;
        // The caption buttons belong to the system title bar and follow the WINDOWS theme,
        // not ours — dark-on-dark when the window is forced dark on a light system. Keep them
        // in step with whatever theme the content actually shows.
        RootGrid.ActualThemeChanged += (_, _) => SyncCaptionButtons();
        SyncCaptionButtons();

        ContentFrame.NavigationFailed += (_, e) =>
        {
            e.Handled = true;
            App.Log($"navigation to {e.SourcePageType.Name} failed: {e.Exception}");
        };

        App.Supervisor.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => _ = RefreshStatus());
        _statusTimer.Tick += async (_, _) => await RefreshStatus();
        _statusTimer.Start();

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            App.Supervisor.Stop();          // closes its stdin → supervisor and hosts exit
        };

        string start = App.Arg("page") ?? "bases";
        var items = Nav.MenuItems.OfType<NavigationViewItem>().ToList();
        Nav.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == start) ?? items[0];
    }

    private void SyncCaptionButtons()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA0)
                                                : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x78);
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                                             : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        Type page = (string)item.Tag switch
        {
            "browse" => typeof(BrowsePage),
            "activity" => typeof(ActivityPage),
            "resources" => typeof(ResourcesPage),
            _ => typeof(InfobasesPage)
        };
        ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
    }

    private async void RestartLink_Click(object sender, RoutedEventArgs e)
    {
        RestartLink.Visibility = Visibility.Collapsed;
        await App.RestartSupervisorAsync();
    }

    private async Task RefreshStatus()
    {
        var s = App.Supervisor;
        (StatusDot.Fill, StatusText.Text) = s.State switch
        {
            SupervisorState.Running => (ThemeBrushes.Get(RootGrid, "OkBrush"), "Connected to the 1C engine"),
            SupervisorState.Starting => (ThemeBrushes.Get(RootGrid, "WarnBrush"), "Starting the 1C engine…"),
            SupervisorState.Failed => (ThemeBrushes.Get(RootGrid, "BadBrush"), "1C engine stopped: " + s.LastError),
            _ => (ThemeBrushes.Get(RootGrid, "WarnBrush"), "1C engine is off")
        };
        ToolTipService.SetToolTip(StatusText, StatusText.Text);
        RestartLink.Visibility = s.State is SupervisorState.Failed or SupervisorState.Stopped
            ? Visibility.Visible : Visibility.Collapsed;

        if (s.State != SupervisorState.Running || s.Client is null) { StatusRight.Text = ""; return; }
        try
        {
            var hosts = await s.Client.Hosts();
            var sup = await s.Client.Supervisor();
            long ram = sup["workingSetMb"]!.GetValue<long>() + hosts.Sum(h => h!["workingSetMb"]!.GetValue<long>())
                       + ProcessMemory.SelfMb();
            int sessions = hosts.Sum(h => h!["stats"]?["sessions"]?.GetValue<int>() ?? 0);
            StatusRight.Text = $"{hosts.Count} host{(hosts.Count == 1 ? "" : "s")} · {sessions} session{(sessions == 1 ? "" : "s")} · {ram} MB in use";
        }
        catch (Exception ex) when (ex is HttpRequestException or EdgeException or TaskCanceledException)
        {
            StatusRight.Text = "";
        }
    }
}

public static class ProcessMemory
{
    public static long SelfMb()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1024 / 1024;
    }
}
