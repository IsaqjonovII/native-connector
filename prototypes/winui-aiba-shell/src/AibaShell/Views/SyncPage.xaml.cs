using System;
using System.Linq;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class SyncPage : Page
{
    private readonly ISyncService _sync = App.Get<ISyncService>();
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly ISettingsService _settings = App.Get<ISettingsService>();

    public SyncPage()
    {
        InitializeComponent();

        SubtitleText.Text = "Parallel cold read. Each lane owns a date slice and its own COM connection in its own process.";
        LaneList.ItemsSource = _sync.Lanes;
        QueueList.ItemsSource = _onec.InfoBases;

        _sync.Changed += OnSyncChanged;
        Unloaded += (s, e) => _sync.Changed -= OnSyncChanged;

        Refresh();
    }

    private void OnSyncChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        ActiveBaseText.Text = _sync.CurrentBase?.Name ?? "no base selected";
        ActiveObjectText.Text = _sync.CurrentObject;
        MainBar.Value = _sync.Progress;
        Ring.IsActive = _sync.IsRunning;

        TotalRateText.Text = _sync.RowsPerSecond.ToString("N0");
        RemainingText.Text = _sync.IsRunning ? Format.Duration(_sync.Eta) : "—";
        OverallProgressText.Text = _sync.Progress.ToString("N1") + " %";
        TotalRowsText.Text = _sync.RowsProcessed.ToString("N0");
        RunElapsedText.Text = Format.Duration(_sync.Elapsed);
        ReadStrategyText.Text = _settings.BulkMode ? "bulk XDTO" : "JSON pages";

        var active = _sync.Lanes.Count(l => l.State == "reading");
        LaneSummaryText.Text = active + " of " + _sync.Lanes.Count + " lanes reading · " +
                               _settings.ColdReadWorkers + " cold-read workers configured";
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _sync.Start();
    private void Stop_Click(object sender, RoutedEventArgs e) => _sync.Stop();

    private async void Lanes_Click(object sender, RoutedEventArgs e)
    {
        var box = new NumberBox
        {
            Header = "Cold-read workers",
            Minimum = 1,
            Maximum = 8,
            Value = _settings.ColdReadWorkers,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var note = new TextBlock
        {
            Text = "Measured on KAN: 4 lanes gave 2.63x over a single lane. Beyond 4 the COM STA " +
                   "contention on one machine eats the gain.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75
        };
        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(box);
        panel.Children.Add(note);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Lane configuration",
            Content = panel,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.ColdReadWorkers = (int)box.Value;
            Refresh();
        }
    }
}
