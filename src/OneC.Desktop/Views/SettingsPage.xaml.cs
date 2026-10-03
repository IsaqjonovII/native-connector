using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OneC.Desktop.Views;

/// <summary>
/// Settings: tools kept out of the main menu (user 2026-09-30: Browse data only from here). Sync has
/// no switch here: it always runs for the connected bases (user 2026-10-03, D50).
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    private void Browse_Click(object sender, RoutedEventArgs e) => App.Window?.Go("browse");
}
