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
        // A 48 px header holds the company picker; tall caption buttons match it.
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        Title = "AIBA Connector";

        int w = int.TryParse(App.Arg("width"), out int pw) ? pw : 1360;
        int h = int.TryParse(App.Arg("height"), out int ph) ? ph : 860;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        // --theme light|dark forces a theme for screenshots (not saved); otherwise the header
        // switcher's saved choice, "system" = follow Windows.
        ApplyTheme(App.Arg("theme") ?? App.ThemeChoice);
        // The caption buttons belong to the system title bar and follow the WINDOWS theme,
        // not ours — dark-on-dark when the window is forced dark on a light system. Keep them
        // in step with whatever theme the content actually shows. The status dot's brush is
        // resolved in code, so it is refreshed too.
        RootGrid.ActualThemeChanged += (_, _) => { SyncCaptionButtons(); _ = RefreshStatus(); };
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

        Go(App.Arg("page") ?? "bases");

        // backend/1c "status" heartbeat for bases this app linked as new records (StatusHeartbeat).
        _heartbeatTimer.Tick += async (_, _) => await Heartbeat(stopping: false);
        _heartbeatTimer.Start();
        App.CloudChanged += (_, _) => { _ = RefreshStatus(); ShowAccount(); };
        App.CompaniesChanged += (_, _) => ShowCompanies();
        // Keep the header's interactive part clear of the system caption buttons.
        RootGrid.Loaded += (_, _) =>
        {
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
            if (AppWindow.TitleBar.RightInset > 0) CaptionColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        };
        ShowAccount();
        Closed += (_, _) =>
        {
            _heartbeatTimer.Stop();
            // Best effort, 3 s at most: tell the cloud these bases went offline (old app: on quit).
            try { Task.Run(() => Heartbeat(stopping: true)).Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        };
    }

    /// <summary>Header right: who is signed in (initials + first name), or "Sign in".</summary>
    private void ShowAccount()
    {
        if (App.Cloud.Session is { } s)
        {
            AccountText.Text = s.FirstName.Length > 0 ? s.FirstName : s.Phone;
            AccountInitials.Text = string.Concat(new[] { s.FirstName, s.LastName }.Where(n => n.Length > 0).Select(n => char.ToUpperInvariant(n[0])))
                                   is { Length: > 0 } i ? i : "A";
            ToolTipService.SetToolTip(AccountButton, $"{s.DisplayName} · {s.Phone}");
        }
        else
        {
            AccountText.Text = "Sign in";
            AccountInitials.Text = "?";
            ToolTipService.SetToolTip(AccountButton, "Sign in to your AIBA account");
        }
        // Only a non-production cloud is worth mentioning; users never see this in production.
        bool prod = App.Cloud.Env.Key == OneC.Cloud.CloudEnvironment.Prod.Key;
        EnvBadge.Visibility = prod ? Visibility.Collapsed : Visibility.Visible;
        EnvBadgeText.Text = App.Cloud.Env.Label.ToUpperInvariant();
        ShowCompanies();
    }

    /// <summary>Header company picker: the chosen company's name on the button.</summary>
    private void ShowCompanies()
    {
        CompanyButton.Visibility = App.Cloud.SignedIn && App.Companies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompanyButtonText.Text = App.SelectedCompany?.Name ?? "Choose a company";
        ToolTipService.SetToolTip(CompanyButton, App.SelectedCompany is { } c ? $"{c.Name} · INN {c.Inn}" : null);
    }

    /// <summary>Opened from Click (not Button.Flyout), so keyboard, mouse and UI Automation all open it.</summary>
    private void CompanyButton_Click(object sender, RoutedEventArgs e) =>
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(CompanyButton);

    private void CompanyFlyout_Opened(object sender, object e)
    {
        CompanySearch.Text = "";
        FilterCompanies("");
        CompanyList.SelectedItem = App.SelectedCompany;
        if (App.SelectedCompany is { } c) CompanyList.ScrollIntoView(c);
        CompanySearch.Focus(FocusState.Programmatic);
    }

    private void CompanySearch_TextChanged(object sender, TextChangedEventArgs e) => FilterCompanies(CompanySearch.Text);

    /// <summary>Every word must appear in the name or the INN; case, quotes and «» don't matter.</summary>
    private void FilterCompanies(string query)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(ch => ch is not ('"' or '«' or '»' or '\'')).ToArray());
        var words = Norm(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = App.Companies.Where(c => words.All(w => Norm(c.Name).Contains(w) || c.Inn.Contains(w))).ToList();
        CompanyList.ItemsSource = hits;
        CompanyNone.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hits.Count > 0 && words.Length > 0) CompanyList.SelectedIndex = 0;
    }

    private void CompanySearch_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            var pick = CompanyList.SelectedItem as OneC.Cloud.CloudCompany ?? CompanyList.Items.FirstOrDefault() as OneC.Cloud.CloudCompany;
            if (pick is not null) PickCompany(pick);
        }
        else if (e.Key == Windows.System.VirtualKey.Down && CompanyList.Items.Count > 0)
        {
            e.Handled = true;
            if (CompanyList.SelectedIndex < 0) CompanyList.SelectedIndex = 0;
            (CompanyList.ContainerFromIndex(CompanyList.SelectedIndex) as Control)?.Focus(FocusState.Keyboard);
        }
    }

    private void CompanyList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && CompanyList.SelectedItem is OneC.Cloud.CloudCompany c)
        {
            e.Handled = true;
            PickCompany(c);
        }
    }

    private void CompanyList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OneC.Cloud.CloudCompany c) PickCompany(c);
    }

    private void PickCompany(OneC.Cloud.CloudCompany c)
    {
        CompanyFlyout.Hide();
        App.SelectCompany(c);
    }

    /// <summary>The sign-in dialog, for screens that invite a signed-out user to sign in.</summary>
    public Task SignInAsync() => Views.SignInDialog.ShowAsync(RootGrid.XamlRoot);

    /// <summary>Signed out: the sign-in dialog. Signed in: who, and Sign out. No account page.</summary>
    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Cloud.Session is not { } s)
        {
            await Views.SignInDialog.ShowAsync(RootGrid.XamlRoot);
            return;
        }
        var menu = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,              // inside the window, like the company picker
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["SolidMenuPresenterStyle"]
        };
        menu.Items.Add(new MenuFlyoutItem { Text = $"{s.DisplayName}  ·  {s.Phone}", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        var signOut = new MenuFlyoutItem { Text = "Sign out", Icon = new FontIcon { Glyph = "" } };
        signOut.Click += async (_, _) =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Sign out of AIBA?",
                Content = "Linked infobases stay linked; they are used again when you sign in with the same account.",
                PrimaryButtonText = "Sign out",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) App.Cloud.SignOut();
        };
        menu.Items.Add(signOut);
        menu.ShowAt(AccountButton);
    }

    private static readonly (string Key, string Label, string Glyph)[] Themes =
    {
        ("light", "Light", ""),
        ("dark", "Dark", ""),
        ("system", "Same as Windows", "")
    };

    private string _theme = "system";

    private void ApplyTheme(string theme)
    {
        _theme = theme;
        RootGrid.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        var t = Themes.FirstOrDefault(x => x.Key == theme, Themes[2]);
        ThemeIcon.Glyph = t.Glyph;
        ToolTipService.SetToolTip(ThemeButton, "Theme: " + t.Label);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["SolidMenuPresenterStyle"]
        };
        foreach (var t in Themes)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = t.Label, GroupName = "theme", IsChecked = t.Key == _theme,
                Icon = new FontIcon { Glyph = t.Glyph }
            };
            item.Click += (_, _) =>
            {
                App.SaveTheme(t.Key);
                ApplyTheme(t.Key);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(ThemeButton);
    }

    private void EnvSwitch_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                   Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        App.CycleCloudEnvironment();
    }

    private readonly DispatcherTimer _heartbeatTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private HashSet<string> _readyBases = new(StringComparer.OrdinalIgnoreCase);

    private async Task Heartbeat(bool stopping)
    {
        var links = App.MyLinks();
        if (!App.Cloud.SignedIn || links.Count == 0) return;
        var ready = _readyBases;
        try { await OneC.Cloud.StatusHeartbeat.BeatAsync(App.Cloud, links, b => !stopping && ready.Contains(b)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { App.Log("cloud heartbeat: " + ex.Message); }
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

    private object? _navParameter;

    private bool _quietSelect;

    /// <summary>
    /// Opens a page from code (a card's Browse, Settings' tools, --page). Pages kept out of the menu
    /// (Browse data: a support tool, user 2026-09-30) open with Settings highlighted.
    /// </summary>
    public void Go(string tag, object? parameter = null)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is null)
        {
            if (Page(tag) is not { } hidden) return;
            _quietSelect = true;
            // A base's sync screen belongs to Infobases, where it is opened from.
            Nav.SelectedItem = tag == "sync"
                ? Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == "bases") ?? Nav.SettingsItem
                : Nav.SettingsItem;
            _quietSelect = false;
            ContentFrame.Navigate(hidden, parameter, new SuppressNavigationTransitionInfo());
            return;
        }
        _navParameter = parameter;
        if (ReferenceEquals(Nav.SelectedItem, item)) ContentFrame.Navigate(ContentFrame.CurrentSourcePageType, parameter, new SuppressNavigationTransitionInfo());
        else Nav.SelectedItem = item;
    }

    private static Type? Page(string tag) => tag switch
    {
        "bases" => typeof(InfobasesPage),
        "connections" => typeof(ConnectionsPage),
        "browse" => typeof(BrowsePage),
        "activity" => typeof(ActivityPage),
        "resources" => typeof(ResourcesPage),
        "settings" => typeof(SettingsPage),
        "sync" => typeof(BaseSyncPage),
        _ => null
    };

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_quietSelect) return;
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage), null, new SuppressNavigationTransitionInfo());
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item) return;
        Type page = Page((string)item.Tag) ?? typeof(InfobasesPage);
        var parameter = _navParameter;
        _navParameter = null;
        ContentFrame.Navigate(page, parameter, new SuppressNavigationTransitionInfo());
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
            SupervisorState.Running => (ThemeBrushes.Get(RootGrid, "OkBrush"), "1C engine is running"),
            SupervisorState.Starting => (ThemeBrushes.Get(RootGrid, "WarnBrush"), "Starting the 1C engine…"),
            SupervisorState.Failed => (ThemeBrushes.Get(RootGrid, "BadBrush"), "1C engine stopped: " + s.LastError),
            _ => (ThemeBrushes.Get(RootGrid, "WarnBrush"), "1C engine is off")
        };
        ToolTipService.SetToolTip(StatusText, StatusText.Text);
        RestartLink.Visibility = s.State is SupervisorState.Failed or SupervisorState.Stopped
            ? Visibility.Visible : Visibility.Collapsed;

        // One calm line: memory, hosts and sessions live on the Resources page; the account is
        // in the header. The right side only names the count of this computer's 1C connections.
        StatusRight.Text = App.Bases.Bases.Count == 0 ? "" : $"1C connections: {App.Bases.Bases.Count}";
        if (s.State != SupervisorState.Running || s.Client is null) { _readyBases = new(); return; }
        try
        {
            var hosts = await s.Client.Hosts();
            // Bases on a Ready host count as healthy for the cloud status heartbeat.
            _readyBases = hosts.Where(h => h!["state"]?.GetValue<string>() == "Ready")
                               .SelectMany(h => h!["bases"]!.AsArray().Select(n => n!.GetValue<string>()))
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or EdgeException or TaskCanceledException) { }
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
