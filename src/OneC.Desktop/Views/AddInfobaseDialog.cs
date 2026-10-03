using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Cloud;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>
/// "Add infobase", the old Connector's create form (accounting/ui/info-base-form) minus its 1C
/// user and password — those live with the 1C connection on this computer. The company is the
/// header's; the configuration version is read from 1C, not asked. The cloud record's odataName
/// is the local base's name, which is what ties the two together (old app: adapterDbName).
/// </summary>
public static class AddInfobaseDialog
{
    public static async Task<OneCRecord?> ShowAsync(XamlRoot root, CloudCompany company, IReadOnlyList<OneCRecord> existing)
    {
        var themeSource = root.Content as FrameworkElement;
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Foreground = themeSource is null ? null : Controls.ThemeBrushes.Get(themeSource, "BadBrush")
        };
        var intro = new TextBlock
        {
            Style = (Style)Application.Current.Resources["MetricLabelStyle"], TextWrapping = TextWrapping.Wrap,
            Text = $"Adds one of this computer's 1C bases to {company.Name}."
        };

        // Shown capitalised; backend/1c wants the lower-case id (onec.py provider pattern).
        var provider = new RadioButtons { Header = "Accounting software", MaxColumns = 2 };
        provider.Items.Add("Unisoft");
        provider.Items.Add("Venkon");
        provider.SelectedIndex = 0;

        // This computer's 1C connections. One this company already has stays visible but can't be
        // picked (old app: "Allaqachon ulangan").
        var used = existing.Select(r => r.OdataName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseBox = new ComboBox { Header = "1C base", PlaceholderText = "Choose a 1C base", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var b in App.Bases.Bases.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool taken = used.Contains(b.Name);
            baseBox.Items.Add(new ComboBoxItem { Content = taken ? $"{b.Name}  (already added)" : b.Name, Tag = b.Name, IsEnabled = !taken });
        }
        var none = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "No 1C base is connected on this computer yet. Connect one on the “1C connections” page first.",
            Visibility = App.Bases.Bases.Count == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Kansler" };
        // Picking a base fills the name with it, as in the old form.
        baseBox.SelectionChanged += (_, _) => { if (baseBox.SelectedItem is ComboBoxItem { Tag: string n }) name.Text = n; };
        var busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };

        var form = new StackPanel { Spacing = 12, Width = 420, Children = { intro, provider, baseBox, none, name, busy, error } };
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = "Add infobase",
            Content = new ScrollViewer { Content = form, MaxHeight = 560 },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = App.Bases.Bases.Count > 0,
            RequestedTheme = themeSource?.ActualTheme ?? ElementTheme.Default
        };

        void Fail(string text) { error.Text = text; error.Visibility = Visibility.Visible; }

        OneCRecord? result = null;
        dlg.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                error.Visibility = Visibility.Collapsed;
                if (baseBox.SelectedItem is not ComboBoxItem { Tag: string baseName }) { Fail("Choose a 1C base."); args.Cancel = true; return; }
                string recName = name.Text.Trim();
                if (recName.Length == 0) { Fail("Enter a name."); args.Cancel = true; return; }
                string software = ((string)provider.SelectedItem!).ToLowerInvariant();

                dlg.IsPrimaryButtonEnabled = false;
                busy.Visibility = Visibility.Visible;
                string? version = await ConfigVersionAsync(baseName);
                try
                {
                    result = await App.Cloud.OneCCreateAsync(company.Id, recName, baseName, software, version);
                }
                catch (CloudException ex) when (ex.Status == 409)
                {
                    // Same company + name + base already in AIBA: use it (old app, 1UZ path). Not
                    // made here, so it gets no status heartbeat (D41).
                    var again = await App.Cloud.OneCListAsync(company.Id);
                    result = again.FirstOrDefault(r => r.Name == recName && r.OdataName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                             ?? throw new CloudException(409, "", "An infobase with this name already exists in this company.");
                    return;
                }
                if (App.Cloud.Session is { } s)
                    App.Links.Set(new CloudLink(s.Env, s.UserId, baseName, result.Id, company.Id, company.Name, result.Name,
                                                result.OdataName, result.Provider, CreatedHere: true, DateTime.UtcNow));
            }
            catch (CloudException ex) { Fail(ex.Message); args.Cancel = true; }
            catch (HttpRequestException ex) { Fail("The AIBA cloud is not reachable: " + ex.Message); args.Cancel = true; }
            finally
            {
                busy.Visibility = Visibility.Collapsed;
                dlg.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        return await dlg.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    /// <summary>
    /// The configuration version as the old app sends it: major.minor, read from 1C
    /// (onec-version.ts:201-217). Null when 1C does not answer within 15 s — a file base can stall
    /// for minutes (MIGRATION_STATUS 5.7) — and backend/1c accepts a record without one.
    /// </summary>
    private static async Task<string?> ConfigVersionAsync(string baseName)
    {
        if (App.Supervisor.Client is not { } client) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string? v = (await client.Test(baseName, cts.Token))["configurationVersion"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(v)) return null;
            var parts = v.Split('.');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : v;
        }
        catch (Exception ex) when (ex is EdgeException or HttpRequestException or OperationCanceledException) { return null; }
    }
}
