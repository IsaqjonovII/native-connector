using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Cloud;

namespace OneC.Desktop.Views;

/// <summary>
/// Sign in to AIBA from the header: phone + password, as the old Connector
/// (specs/2026-09-30-auth-and-cloud-link.md §1). The password leaves this dialog only in the
/// login request; the tokens are kept DPAPI-protected by <see cref="SessionStore"/>.
/// </summary>
public static class SignInDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        var themeSource = root.Content as FrameworkElement;
        var phone = new TextBox { Header = "Phone number", PlaceholderText = "+998 90 123 45 67", InputScope = TelephoneScope() };
        var password = new PasswordBox { Header = "Password" };
        var help = new TextBlock
        {
            Style = (Style)Application.Current.Resources["MetricLabelStyle"], TextWrapping = TextWrapping.Wrap,
            Text = "The same phone and password as in the AI yordamchi app. The password goes to AIBA only and is never saved here."
        };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Foreground = themeSource is null ? null : Controls.ThemeBrushes.Get(themeSource, "BadBrush")
        };

        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = "Sign in to AIBA",
            Content = new StackPanel { Spacing = 14, Width = 340, Children = { phone, password, help, error } },
            PrimaryButtonText = "Sign in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = themeSource?.ActualTheme ?? ElementTheme.Default
        };
        void Fail(string text) { error.Text = text; error.Visibility = Visibility.Visible; }

        dlg.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (CloudClient.NormalizePhone(phone.Text).Length < 8) { Fail("Enter your phone number."); args.Cancel = true; return; }
                if (password.Password.Length == 0) { Fail("Enter your password."); args.Cancel = true; return; }
                sender.IsPrimaryButtonEnabled = false;
                await App.Cloud.LoginAsync(phone.Text, password.Password);
            }
            catch (CloudException ex) { Fail(ex.Message); args.Cancel = true; }
            catch (HttpRequestException) { Fail("AIBA is not reachable. Check the internet connection and try again."); args.Cancel = true; }
            catch (TaskCanceledException) { Fail("AIBA did not answer in time. Try again."); args.Cancel = true; }
            finally
            {
                sender.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        dlg.Opened += (_, _) => phone.Focus(FocusState.Programmatic);

        bool ok = await dlg.ShowAsync() == ContentDialogResult.Primary;
        password.Password = "";
        if (ok) App.RaiseCloudChanged();
        return ok;
    }

    private static Microsoft.UI.Xaml.Input.InputScope TelephoneScope()
    {
        var scope = new Microsoft.UI.Xaml.Input.InputScope();
        scope.Names.Add(new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.TelephoneNumber));
        return scope;
    }
}
