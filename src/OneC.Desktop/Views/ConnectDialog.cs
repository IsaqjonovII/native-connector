using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

/// <summary>
/// "Connect" for one base from the 1C launcher list — the old Connector's "Baza ulanishini
/// sozlash" (database-config-modal.tsx:967-1100). Only the login is asked; where the base is and
/// what kind it is come from the launcher list. Before anything is saved the connection is
/// checked once (OneC.Host probe) and a wrong 1C version is corrected automatically, as the old
/// app did (filesystem.rs:3504-3666).
/// </summary>
public static class ConnectDialog
{
    public static async Task<StoredBase?> ShowAsync(XamlRoot root, string name, LauncherBase? launcher, StoredBase? existing)
    {
        var themeSource = root.Content as FrameworkElement;
        bool isServer = existing?.Kind == BaseKind.Server || existing is null && launcher?.Kind == LauncherKind.Server;
        string location = existing?.Location ?? launcher?.Location ?? "";
        var old = existing is null ? BaseStore.OldConnectorLogin(name) : null;       // the old Connector's saved login

        var where = new TextBlock
        {
            Style = (Style)Application.Current.Resources["MetricLabelStyle"], TextWrapping = TextWrapping.Wrap,
            Text = (isServer ? "Server base · " : "File base · ") + location
        };
        var user = new TextBox { Header = "1C user", PlaceholderText = "e.g. Administrator", Text = existing?.User ?? old?.User ?? "" };
        var pwd = new PasswordBox
        {
            Header = "Password",
            Password = old?.Password ?? "",
            PlaceholderText = existing is null ? "" : "Leave empty to keep the saved password"
        };
        var noPwd = new CheckBox { Content = "No password" };
        noPwd.Checked += (_, _) => pwd.IsEnabled = false;
        noPwd.Unchecked += (_, _) => pwd.IsEnabled = true;
        var version = new TextBox
        {
            Header = "1C version (optional)", PlaceholderText = "e.g. 8.3.15.1565",
            Text = existing?.PlatformVersion ?? old?.Version ?? ""
        };
        var versionHelp = new TextBlock
        {
            Style = (Style)Application.Current.Resources["MetricLabelStyle"], TextWrapping = TextWrapping.Wrap,
            Text = "Usually leave it empty: the right version is found and saved automatically."
        };
        var busy = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        var busyText = new TextBlock { Style = (Style)Application.Current.Resources["MetricLabelStyle"], TextWrapping = TextWrapping.Wrap };
        busy.Children.Add(new ProgressBar { IsIndeterminate = true });
        busy.Children.Add(busyText);
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, IsTextSelectionEnabled = true,
            Foreground = themeSource is null ? null : Controls.ThemeBrushes.Get(themeSource, "BadBrush")
        };

        var form = new StackPanel { Spacing = 12, Width = 420, Children = { where, user, pwd, noPwd, version, versionHelp, busy, error } };
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = existing is null ? $"Connect “{name}”" : $"Change login for “{name}”",
            Content = new ScrollViewer { Content = form, MaxHeight = 560 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = themeSource?.ActualTheme ?? ElementTheme.Default
        };

        void Fail(string text) { error.Text = text; error.Visibility = Visibility.Visible; }

        StoredBase? result = null;
        dlg.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                error.Visibility = Visibility.Collapsed;
                string login = user.Text.Trim();
                if (login.Length == 0) { Fail("Enter the 1C user."); args.Cancel = true; return; }
                string password = noPwd.IsChecked == true ? ""
                    : pwd.Password.Length == 0 && existing is not null ? SavedPassword(existing) : pwd.Password;

                var b = new StoredBase
                {
                    Name = name,
                    Kind = isServer ? BaseKind.Server : BaseKind.File,
                    Server = existing?.Server ?? launcher?.Server ?? "",
                    Ref = existing?.Ref ?? launcher?.Ref ?? "",
                    FilePath = existing?.FilePath ?? launcher?.FilePath ?? "",
                    User = login
                };

                dlg.IsPrimaryButtonEnabled = false;
                busy.Visibility = Visibility.Visible;
                var installed = BaseStore.InstalledPlatforms().ToList();
                string ver = version.Text.Trim() is { Length: > 0 } typed ? typed : DefaultVersion(installed);
                var tried = new List<string>();
                while (true)
                {
                    tried.Add(ver);
                    busyText.Text = $"Connecting to 1C {ver}…";
                    JsonElement r;
                    // A file base can stall for minutes in 1C's own engine (MIGRATION_STATUS 5.7).
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                        r = await App.Supervisor.ProbeAsync(name, b.ConnectionString(password), ver, cts.Token);
                    if (r.GetProperty("ok").GetBoolean())
                    {
                        b.PasswordProtected = BaseStore.Protect(password);
                        b.PlatformVersion = ver;
                        result = b;
                        return;
                    }
                    string code = r.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    string message = r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    // A version mismatch names the version 1C wants: retry once with it if it is installed.
                    if (tried.Count < 3 && WantedVersion(message, installed, tried) is { } next) { ver = next; continue; }
                    Fail(Friendly(code, message, installed));
                    args.Cancel = true;
                    return;
                }
            }
            catch (OperationCanceledException) { Fail("1C did not answer within 2 minutes. Try again, or check the base in 1C itself."); args.Cancel = true; }
            catch (InvalidOperationException ex) { Fail(ex.Message); args.Cancel = true; }
            finally
            {
                busy.Visibility = Visibility.Collapsed;
                dlg.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        return await dlg.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    private static string SavedPassword(StoredBase b)
    {
        try { return BaseStore.Unprotect(b.PasswordProtected); }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }
    }

    /// <summary>The version the other connected bases use (one engine process serves them all), else the newest installed.</summary>
    private static string DefaultVersion(List<string> installed) =>
        App.Bases.Bases.GroupBy(x => x.PlatformVersion).Where(g => installed.Contains(g.Key))
           .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
        ?? installed.LastOrDefault() ?? "8.3.15.1565";

    /// <summary>
    /// A mismatch error names the version 1C wants ("versions of client and server are different
    /// (8.3.15 - 8.3.18)", or "version X or above"). Returns an installed version from the message
    /// that was not tried yet; for "or above", the newest installed one at least that high.
    /// </summary>
    private static string? WantedVersion(string message, List<string> installed, List<string> tried)
    {
        var named = Regex.Matches(message, @"\d+\.\d+\.\d+\.\d+").Select(x => x.Value).Where(v => !tried.Contains(v)).ToList();
        if (named.Count == 0) return null;
        if (named.FirstOrDefault(installed.Contains) is { } exact) return exact;
        bool orAbove = Regex.IsMatch(message, "or (above|higher|later)|или выше|и выше", RegexOptions.IgnoreCase);
        if (!orAbove) return null;
        var min = Version.Parse(named[0]);
        return installed.Where(v => Version.Parse(v) >= min && !tried.Contains(v)).OrderBy(Version.Parse).LastOrDefault();
    }

    private static string Friendly(string code, string message, List<string> installed)
    {
        var named = Regex.Matches(message, @"\d+\.\d+\.\d+\.\d+").Select(x => x.Value).FirstOrDefault(v => !installed.Contains(v));
        bool versionError = named is not null && (message.Contains("верси", StringComparison.OrdinalIgnoreCase) ||
                                                  message.Contains("version", StringComparison.OrdinalIgnoreCase));
        if (versionError) return $"This base needs 1C {named}, which is not installed on this computer. 1C said: {message}";
        return code switch
        {
            "auth" => "Wrong 1C user or password.",
            "license" => "1C has no free license for this computer. 1C said: " + message,
            "path" => "The base's folder was not found, or it is not a 1C base.",
            "permission" => "Windows denied access to the base. 1C said: " + message,
            "network" => "The 1C server did not answer. 1C said: " + message,
            _ => "1C refused the connection: " + message
        };
    }
}
