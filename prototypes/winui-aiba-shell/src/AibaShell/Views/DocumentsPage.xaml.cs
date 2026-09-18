using System;
using AibaShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AibaShell.Views;

public sealed partial class DocumentsPage : Page
{
    private readonly IOneCService _onec = App.Get<IOneCService>();
    private readonly ILogService _log = App.Get<ILogService>();

    public DocumentsPage()
    {
        InitializeComponent();
        SubtitleText.Text = "Documents this machine wrote into 1C. Every row carries its AIBA marker so a re-send never duplicates.";
        DocList.ItemsSource = _onec.Documents;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) =>
        _log.Write(Services.LogLevel.Info, "1C", "Retrying 1 rejected document (SP-000420)");

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Post drafts",
            Content = "2 drafts would be posted with provodki in their own bases.\n\n" +
                      "Posting is the step that actually fails in production; the prototype only pretends.",
            PrimaryButtonText = "Post",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _log.Write(Services.LogLevel.Info, "1C", "2 documents posted with provodki");
    }
}
