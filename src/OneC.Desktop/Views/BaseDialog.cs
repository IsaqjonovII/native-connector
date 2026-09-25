using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>Add / edit an infobase. Built in code: one small form, no XAML compiler surprises.</summary>
public static class BaseDialog
{
    public static async Task<StoredBase?> ShowAsync(XamlRoot root, StoredBase? existing)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Kansler", Text = existing?.Name ?? "" };
        var kind = new RadioButtons { Header = "Where the database is", MaxColumns = 2 };
        kind.Items.Add("On a 1C server");
        kind.Items.Add("In a folder (file base)");
        kind.SelectedIndex = existing?.Kind == BaseKind.File ? 1 : 0;

        var server = new TextBox { Header = "1C server", PlaceholderText = "e.g. WIN-11-2070", Text = existing?.Server ?? "" };
        var refName = new TextBox { Header = "Database name on the server", PlaceholderText = "e.g. KAN", Text = existing?.Ref ?? "" };
        var folder = new TextBox { Header = "Folder", PlaceholderText = @"e.g. D:\1C\bilim", Text = existing?.FilePath ?? "" };
        var user = new TextBox { Header = "1C user", Text = existing?.User ?? "" };
        var pwd = new PasswordBox
        {
            Header = "Password",
            PlaceholderText = existing is null ? "" : "Leave empty to keep the saved password"
        };

        var versions = BaseStore.InstalledPlatforms().ToList();
        var version = new ComboBox
        {
            Header = "1C platform version",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = versions
        };
        // An editable ComboBox drops Text set before it loads (the box opened empty): select the
        // installed version, or set a typed-in one once the control exists.
        string initialVersion = existing?.PlatformVersion ?? versions.LastOrDefault() ?? "";
        if (versions.Contains(initialVersion)) version.SelectedItem = initialVersion;
        else version.Loaded += (_, _) => version.Text = initialVersion;
        var versionHelp = new TextBlock
        {
            Text = "Server bases need exactly the server's version. File bases open on this version or newer.",
            Style = (Style)Application.Current.Resources["MetricLabelStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var themeSource = root.Content as FrameworkElement;
        var error = new TextBlock
        {
            Foreground = themeSource is null
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BadBrush"]
                : Controls.ThemeBrushes.Get(themeSource, "BadBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        void Sync()
        {
            bool isServer = kind.SelectedIndex == 0;
            server.Visibility = refName.Visibility = isServer ? Visibility.Visible : Visibility.Collapsed;
            folder.Visibility = isServer ? Visibility.Collapsed : Visibility.Visible;
        }
        kind.SelectionChanged += (_, _) => Sync();
        Sync();

        var form = new StackPanel { Spacing = 12, Width = 420 };
        foreach (var el in new UIElement[] { name, kind, server, refName, folder, user, pwd, version, versionHelp, error })
            form.Children.Add(el);

        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = existing is null ? "Add infobase" : $"Edit “{existing.Name}”",
            Content = new ScrollViewer { Content = form, MaxHeight = 560 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            // Dialogs live in the popup layer; give them the window's theme explicitly.
            RequestedTheme = themeSource?.ActualTheme ?? ElementTheme.Default
        };

        StoredBase? result = null;
        // Named parameters, not (_, args): inside this lambda `out _` would bind to the sender.
        dlg.PrimaryButtonClick += (sender, args) =>
        {
            string? problem =
                string.IsNullOrWhiteSpace(name.Text) ? "Enter a name." :
                App.Bases.Bases.Any(b => b.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                         !ReferenceEquals(b, existing)) ? "Another infobase already has this name." :
                kind.SelectedIndex == 0 && (string.IsNullOrWhiteSpace(server.Text) || string.IsNullOrWhiteSpace(refName.Text))
                    ? "Enter the server and the database name." :
                kind.SelectedIndex == 1 && string.IsNullOrWhiteSpace(folder.Text) ? "Enter the folder." :
                !Version.TryParse(version.Text, out _) ? "Enter the 1C version, e.g. 8.3.15.1565." :
                null;
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            result = new StoredBase
            {
                Name = name.Text.Trim(),
                Kind = kind.SelectedIndex == 0 ? BaseKind.Server : BaseKind.File,
                Server = server.Text.Trim(),
                Ref = refName.Text.Trim(),
                FilePath = folder.Text.Trim(),
                User = user.Text,
                PasswordProtected = pwd.Password.Length > 0 || existing is null
                    ? BaseStore.Protect(pwd.Password)
                    : existing.PasswordProtected,
                PlatformVersion = version.Text.Trim()
            };
        };

        return await dlg.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }
}
