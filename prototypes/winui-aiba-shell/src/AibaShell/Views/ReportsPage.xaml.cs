using System;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class ReportsPage : Page
{
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly ILogService _log = App.Get<ILogService>();

    public ReportsPage()
    {
        InitializeComponent();
        SubtitleText.Text = "Declarative reports built from synced data, not from a live 1C query.";
        ReportList.ItemsSource = _onec.Reports;
    }

    private void Build_Click(object sender, RoutedEventArgs e) =>
        _log.Write(Services.LogLevel.Info, "Reports", "Build queued for the selected report");

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Export",
            Content = "The report would be written as .xlsx next to the log folder.",
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
    }
}
