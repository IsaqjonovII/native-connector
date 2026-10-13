using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OneC.Desktop.Views;

/// <summary>
/// Settings: startup and power switches, and tools kept out of the main menu (user 2026-09-30: Browse data only from here). Sync has
/// no switch here: it always runs for the connected bases (user 2026-10-03, D50).
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        StartupToggle.IsOn = Services.StartupAndPower.StartsWithWindows;
        BackgroundToggle.IsOn = App.RunInBackground;
        AwakeToggle.IsOn = App.KeepAwake;
        NotifyToggle.IsOn = App.NotificationsOn;
        _loading = false;
        ShowNotifyState();
        // Back from Windows Settings: show the new state without leaving the page.
        if (App.Window is { } w)
        {
            TypedEventHandler<object, WindowActivatedEventArgs> refresh = (_, _) => ShowNotifyState();
            w.Activated += refresh;
            Unloaded += (_, _) => w.Activated -= refresh;
        }
    }

    private void ShowNotifyState()
    {
        string? why = Services.Notifier.WindowsBlocks();
        NotifyBlocked.Message = why ?? "";
        NotifyBlocked.IsOpen = why is not null && NotifyToggle.IsOn;
    }

    private void Notify_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) App.NotificationsOn = NotifyToggle.IsOn;
        ShowNotifyState();
    }

    private async void NotifySettings_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { Services.StartupAndPower.SetStartsWithWindows(StartupToggle.IsOn); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            App.Log("start with Windows: " + ex.Message);
            _loading = true;
            StartupToggle.IsOn = Services.StartupAndPower.StartsWithWindows;
            _loading = false;
        }
    }

    private void Background_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) App.RunInBackground = BackgroundToggle.IsOn;
    }

    private void Awake_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) App.KeepAwake = AwakeToggle.IsOn;
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => App.Window?.Go("browse");
}
