using System;
using System.Linq;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class ProcessesPage : Page
{
    private readonly IProcessSupervisor _procs = App.Get<IProcessSupervisor>();
    private readonly ISystemMetricsService _metrics = App.Get<ISystemMetricsService>();

    public ProcessesPage()
    {
        InitializeComponent();

        SubtitleText.Text = "One supervisor owns every child. COM is STA, so real parallelism means more processes, not more threads.";
        ProcList.ItemsSource = _procs.Processes;

        _metrics.Changed += OnChanged;
        Unloaded += (s, e) => _metrics.Changed -= OnChanged;

        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var running = _procs.Processes.Count(p => p.State != "stopped");
        var ram = _procs.Processes.Sum(p => p.MemoryMb);
        var cpu = _procs.Processes.Sum(p => p.Cpu);
        TotalsText.Text = running + " running · " + ram.ToString("N0") + " MB · " + cpu.ToString("N1") + " % CPU total";
        SupervisorText.Text = "auto-restart on: crash, 3 failed health probes, or RSS over budget";
    }

    private ProcessNode? Selected => ProcList.SelectedItem as ProcessNode;

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) _procs.Restart(n);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } n) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Stop " + n.Name + "?",
            Content = "Any base served by this process drops to disconnected until it is started again.",
            PrimaryButtonText = "Stop process",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _procs.Stop(n);
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => AppShell.Window.SelectNav("logs");

    private void RestartAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _procs.Processes.Where(p => p.Kind == "oscript").ToList())
            _procs.Restart(p);
    }
}
