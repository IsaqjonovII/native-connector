using System;
using System.ComponentModel;
using AibaShell.Common;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AibaShell.Views;

public sealed partial class BaseDetailPage : Page
{
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly ISyncService _sync = App.Get<ISyncService>();
    private readonly StatusToBrushConverter _brush = new();

    private InfoBase? _base;

    public BaseDetailPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _base = e.Parameter as InfoBase;
        if (_base is null) return;
        _base.PropertyChanged += OnBaseChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_base is not null) _base.PropertyChanged -= OnBaseChanged;
    }

    private void OnBaseChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_base is null) return;
        var b = _base;

        NameText.Text = b.Name;
        StateText.Text = b.State;
        StateDot.Fill = (Microsoft.UI.Xaml.Media.Brush)_brush.Convert(b.Status, typeof(object), null!, "");
        SubtitleText.Text = b.Organization + " · " + b.Location;

        KindText.Text = b.Kind;
        LocationText.Text = b.Location;
        OrgText.Text = b.Organization;
        VersionText.Text = b.OneCVersion;
        ComVersionText.Text = b.ComVersion;
        ComStateText.Text = b.ComState;

        WorkerText.Text = b.Worker;
        PidText.Text = b.WorkerPidText;
        PortText.Text = b.WorkerPort == 0 ? "-" : b.WorkerPort.ToString();
        MemText.Text = b.WorkerMemoryText;
        UptimeText.Text = b.WorkerUptimeText;
        ReqText.Text = b.RequestsServed.ToString("N0");
        QueueText.Text = b.QueueDepth.ToString();

        CurrentObjectText.Text = b.CurrentObject;
        ProgressBarCtl.Value = b.Progress;
        ProgressText.Text = b.ProgressText;
        RowsText.Text = b.RowsReadText;
        RateText.Text = b.RowsPerSecondText;
        LastSyncText.Text = b.LastSyncText;

        ProblemBar.IsOpen = b.HasProblem;
        ProblemBar.Title = "This base cannot run";
        ProblemBar.Message = b.Problem;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => AppShell.Window.SelectNav("bases");

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_base is not null) _onec.Connect(_base);
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_base is not null) _onec.RestartWorker(_base);
    }

    private void StartSync_Click(object sender, RoutedEventArgs e)
    {
        if (_base is not null) _sync.Start(_base);
    }

    private void StopSync_Click(object sender, RoutedEventArgs e) => _sync.Stop();

    private void Logs_Click(object sender, RoutedEventArgs e) => AppShell.Window.SelectNav("logs");
}
