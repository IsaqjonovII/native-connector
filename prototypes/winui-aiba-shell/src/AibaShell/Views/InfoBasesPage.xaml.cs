using System;
using System.Collections.ObjectModel;
using System.Linq;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class InfoBasesPage : Page
{
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly ISyncService _sync = App.Get<ISyncService>();

    private bool _ready;
    private string _filter = "";
    private string _state = "all";

    public ObservableCollection<InfoBase> Visible { get; } = new();

    public InfoBasesPage()
    {
        InitializeComponent();
        BaseList.ItemsSource = Visible;
        _ready = true;
        SubtitleText.Text = "Every 1C base this machine owns. One COM connection per worker process; workers are shared across bases.";
        Apply();
    }

    private void Apply()
    {
        if (!_ready) return;
        var q = _onec.InfoBases.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            var f = _filter.Trim();
            q = q.Where(b =>
                b.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                b.Location.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                b.Organization.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        q = _state switch
        {
            "connected" => q.Where(b => b.Status == StatusKind.Healthy),
            "syncing" => q.Where(b => b.State == "Syncing"),
            "problems" => q.Where(b => b.Status is StatusKind.Error or StatusKind.Warning || b.HasProblem),
            _ => q
        };

        Visible.Clear();
        foreach (var b in q) Visible.Add(b);
        CountText.Text = Visible.Count + " of " + _onec.InfoBases.Count + " bases";
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _filter = sender.Text;
        Apply();
    }

    private void StateFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _state = sender.SelectedItem?.Tag as string ?? "all";
        Apply();
    }

    private void BaseList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InfoBase b) AppShell.Window.ShowBase(b);
    }

    private InfoBase? Selected => BaseList.SelectedItem as InfoBase ?? Visible.FirstOrDefault();

    private static InfoBase? FromMenu(object sender) =>
        (sender as FrameworkElement)?.DataContext as InfoBase;

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var b = FromMenu(sender);
        if (b is not null) AppShell.Window.ShowBase(b);
    }

    private void MenuSync_Click(object sender, RoutedEventArgs e)
    {
        var b = FromMenu(sender);
        if (b is not null) _sync.Start(b);
    }

    private void MenuRestart_Click(object sender, RoutedEventArgs e)
    {
        var b = FromMenu(sender);
        if (b is not null) _onec.RestartWorker(b);
    }

    private async void MenuDisconnect_Click(object sender, RoutedEventArgs e)
    {
        var b = FromMenu(sender);
        if (b is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Disconnect " + b.Name + "?",
            Content = "The worker keeps running for other bases. Any in-flight read on this base is dropped.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _onec.Disconnect(b);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => ShowAddDialog();

    private async void ShowAddDialog()
    {
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        var name = new TextBox { Header = "Display name", PlaceholderText = "e.g. Production-02" };
        var kind = new ComboBox { Header = "Connection type", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.Items.Add("Server (1C cluster)");
        kind.Items.Add("File base");
        kind.Items.Add("1uz direct SQL");
        var server = new TextBox { Header = "Server", PlaceholderText = "1c-srv-01" };
        var db = new TextBox { Header = "Database", PlaceholderText = "prod02" };
        var user = new TextBox { Header = "1C user", PlaceholderText = "aiba_svc" };
        panel.Children.Add(name);
        panel.Children.Add(kind);
        panel.Children.Add(server);
        panel.Children.Add(db);
        panel.Children.Add(user);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add information base",
            Content = panel,
            PrimaryButtonText = "Probe connection",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
    }

    private void SyncSelected_Click(object sender, RoutedEventArgs e)
    {
        var b = Selected;
        if (b is not null) _sync.Start(b);
    }

    private void RestartWorker_Click(object sender, RoutedEventArgs e)
    {
        var b = Selected;
        if (b is not null) _onec.RestartWorker(b);
    }
}
