using System;
using System.Linq;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ISettingsService _settings = App.Get<ISettingsService>();
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        StartWithWindows.IsOn = _settings.StartWithWindows;
        MinimizeToTray.IsOn = _settings.MinimizeToTray;
        AutoRestart.IsOn = _settings.AutoRestartWorkers;
        BulkMode.IsOn = _settings.BulkMode;
        ParallelReads.IsOn = _settings.ParallelReads;

        MaxWorkers.Value = _settings.MaxWorkers;
        ColdWorkers.Value = _settings.ColdReadWorkers;
        MemBudget.Value = _settings.WorkerMemoryBudgetMb;
        PollInterval.Value = _settings.PollIntervalSeconds;
        Retention.Value = _settings.LogRetentionDays;

        Select(ThemeBox, _settings.Theme);
        Select(ComVersionBox, _settings.ComVersion);
        Select(LogLevelBox, _settings.LogLevel);

        _loading = false;
    }

    private static void Select(ComboBox box, string value)
    {
        var match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as string ?? i.Content?.ToString()) == value);
        box.SelectedItem = match ?? box.Items.FirstOrDefault();
    }

    private static string Text(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string
        ?? (box.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? "";

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.StartWithWindows = StartWithWindows.IsOn;
        _settings.MinimizeToTray = MinimizeToTray.IsOn;
        _settings.AutoRestartWorkers = AutoRestart.IsOn;
        _settings.BulkMode = BulkMode.IsOn;
        _settings.ParallelReads = ParallelReads.IsOn;
    }

    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        _settings.MaxWorkers = (int)MaxWorkers.Value;
        _settings.ColdReadWorkers = (int)ColdWorkers.Value;
        _settings.WorkerMemoryBudgetMb = (int)MemBudget.Value;
        _settings.PollIntervalSeconds = (int)PollInterval.Value;
        _settings.LogRetentionDays = (int)Retention.Value;
    }

    private void Combo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.ComVersion = Text(ComVersionBox);
        _settings.LogLevel = Text(LogLevelBox);
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = Text(ThemeBox);
        _settings.Theme = value;

        // Element-level only. Mica and the NavigationView pane follow the APPLICATION theme,
        // which WinUI fixes before the first window exists, so the chrome keeps the system
        // theme until the app restarts. A shipping build would set RequestedTheme in App.xaml
        // from stored settings at startup.
        if (AppShell.Window.Content is FrameworkElement root)
        {
            root.RequestedTheme = value switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            App.IsDarkTheme = root.ActualTheme == ElementTheme.Dark;
        }

        ThemeNote.IsOpen = value != "System";
        ThemeNote.Message = "The content area switches now. The title bar and navigation pane " +
                            "follow the application theme, which only changes on restart.";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Export logs",
            Content = "Would write %LOCALAPPDATA%\\AIBA\\logs\\export-" +
                      DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip",
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
    }
}
