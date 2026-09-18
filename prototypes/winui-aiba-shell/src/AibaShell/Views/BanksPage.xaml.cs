using System;
using System.Linq;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class BanksPage : Page
{
    private readonly IBankService _banks = App.Get<IBankService>();

    public BanksPage()
    {
        InitializeComponent();
        SubtitleText.Text = "Statement pull and payment signing. Browser automation stays out of the UI process.";
        BankRepeater.ItemsSource = _banks.Banks;
        AttentionBar.IsOpen = _banks.WarningCount > 0;
        AttentionBar.Title = _banks.WarningCount + " banks need an operator";
    }

    private static BankConnection? FromButton(object sender) =>
        (sender as FrameworkElement)?.DataContext as BankConnection;

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (FromButton(sender) is { } b) _banks.Reconnect(b);
    }

    private async void Statements_Click(object sender, RoutedEventArgs e)
    {
        if (FromButton(sender) is not { } b) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = b.Name + " statements",
            Content = "Last pull: " + b.LastSyncText + "\n" + b.Detail +
                      "\n\nStatement rows would open in a table here.",
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
    }

    private void Pull_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _banks.Banks.Where(b => b.Status == StatusKind.Healthy))
            _banks.Reconnect(b);
    }

    private async void Keys_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Key manager",
            Content = "ePass2003 token present · 3 keys bound · PIN cached for this session.\n\n" +
                      "Real key handling never leaves the signing daemon.",
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
    }
}
