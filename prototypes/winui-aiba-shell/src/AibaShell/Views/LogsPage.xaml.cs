using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class LogsPage : Page
{
    private readonly ILogService _log = App.Get<ILogService>();

    private bool _ready;
    private string _search = "";
    private string _level = "all";
    private string _component = "all";

    public ObservableCollection<LogEntry> Visible { get; } = new();

    public LogsPage()
    {
        InitializeComponent();

        SubtitleText.Text = "Structured stream from every component. The real viewer would tail the same ring buffer the crash reporter ships.";
        LogList.ItemsSource = Visible;

        ((INotifyCollectionChanged)_log.Entries).CollectionChanged += OnEntriesChanged;
        Unloaded += (s, e) => ((INotifyCollectionChanged)_log.Entries).CollectionChanged -= OnEntriesChanged;

        // Fill after the ListView is in the visual tree: ScrollIntoView throws on a
        // list that has not been realised yet.
        Loaded += (s, e) => Rebuild();
        _ready = true;
        RefillVisible();
        UpdateCount();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (LogEntry entry in e.NewItems)
                if (Matches(entry))
                    Visible.Add(entry);

            while (Visible.Count > 600) Visible.RemoveAt(0);
            UpdateCount();
            ScrollToTail();
            return;
        }

        Rebuild();
    }

    private bool Matches(LogEntry e)
    {
        if (_component != "all" && e.Component != _component) return false;

        var min = _level switch
        {
            "debug" => Services.LogLevel.Debug,
            "info" => Services.LogLevel.Info,
            "warning" => Services.LogLevel.Warning,
            "error" => Services.LogLevel.Error,
            _ => Services.LogLevel.Debug
        };
        if (_level == "error" && e.Level != Services.LogLevel.Error) return false;
        if (_level != "all" && e.Level < min) return false;

        if (!string.IsNullOrWhiteSpace(_search) &&
            !e.Message.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
            !e.Component.Contains(_search, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void RefillVisible()
    {
        Visible.Clear();
        foreach (var e in _log.Entries.Where(Matches).ToList()) Visible.Add(e);
    }

    private void Rebuild()
    {
        RefillVisible();
        UpdateCount();
        ScrollToTail();
    }

    private void ScrollToTail()
    {
        if (AutoScrollButton.IsChecked != true || Visible.Count == 0) return;
        try { LogList.ScrollIntoView(Visible[^1]); }
        catch { /* list not realised yet; the next append scrolls instead */ }
    }

    private void UpdateCount() =>
        CountText.Text = Visible.Count + " of " + _log.Entries.Count + " lines";

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text;
        Rebuild();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _level = (LevelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        _component = (ComponentBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        Rebuild();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _log.IsPaused = PauseButton.IsChecked == true;
        LiveText.Text = _log.IsPaused ? "paused" : "live";
        LiveDot.Opacity = _log.IsPaused ? 0.35 : 1;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        Rebuild();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var l in Visible)
            sb.AppendLine("[" + l.TimeText + "] [" + l.LevelText + "] " + l.ComponentText + " " + l.Message);

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(sb.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Export logs",
            Content = "A zip of the last 14 days would be written to %LOCALAPPDATA%\\AIBA\\logs\\export.\n\n" +
                      "Nothing is written in the prototype.",
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
    }
}
