using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace OneC.Desktop.Services;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>
/// One way to tell the user something, two places it can land:
///   - the window is open and in front → an in-app toast (bottom-right, closes itself);
///   - the window is hidden in the notification area or behind other windows → a Windows
///     notification (kept in Notification Center).
///
/// Windows toasts through the classic ToastNotificationManager, not Windows App SDK app
/// notifications: those report IsSupported() = false for this self-contained, unpackaged app
/// (measured — they need the Windows App Runtime singleton package, which a copied folder does
/// not install). The classic API is part of Windows 10/11 and needs only the app's identity:
/// an AppUserModelID with a display name and icon under HKCU, no shortcut, no admin.
///
/// Windows asks no permission for notifications: an app may show them from the start, and the
/// user turns them off per app in Windows Settings. So nothing is asked here — Settings shows
/// when Windows has them off and opens the right Windows page.
/// </summary>
internal static class Notifier
{
    private const string Aumid = "AIBA.Connector";
    private static ToastNotifier? _toasts;

    /// <summary>Gives the process its Windows identity (name + logo in a toast). Call once, before the window.</summary>
    public static void Start()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
            {
                k.SetValue("DisplayName", "AIBA Connector");
                k.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png"));
            }
            // The registry entry alone is refused (0x80070490, measured): Windows resolves an
            // unpackaged app's toast identity through a Start-menu shortcut carrying the same ID.
            // It doubles as the app's Start-menu entry.
            StartMenuShortcut.Ensure("AIBA Connector", Environment.ProcessPath!, Aumid);
            SetCurrentProcessExplicitAppUserModelID(Aumid);
            _toasts = ToastNotificationManager.CreateToastNotifier(Aumid);
            App.Log("notifications: ready; " + (WindowsBlocks() ?? "Windows shows them"));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or SecurityException)
        {
            _toasts = null;
            App.Log($"notifications: not available: 0x{ex.HResult:X8} {ex.GetType().Name} {ex.Message} at {ex.StackTrace?.Trim().Split('\n')[0]}");
        }
    }

    /// <summary>What Windows allows, in the user's words; null when notifications are on.</summary>
    public static string? WindowsBlocks()
    {
        if (_toasts is null) return "Windows notifications are not available on this PC.";
        try
        {
            return _toasts.Setting switch
            {
                NotificationSetting.Enabled => null,
                NotificationSetting.DisabledByGroupPolicy => "Your organization has turned notifications off on this PC.",
                _ => "Notifications are turned off for AIBA Connector in Windows Settings.",
            };
        }
        // Windows cannot always answer for an unpackaged app (0x80070490 "element not found",
        // measured, while showing works). Not knowing is not "off": assume on, as Windows does.
        catch (COMException) { return null; }
    }

    /// <summary>
    /// Tells the user. <paramref name="page"/> is where a click leads (a navigation tag of the
    /// main window), or null for "just open the app".
    /// </summary>
    public static void Notify(NoticeKind kind, string title, string text, string? page = null)
    {
        App.Log($"notice {kind}: {title} - {text}");
        var w = App.Window;
        if (w is null) return;
        bool queued = w.DispatcherQueue.TryEnqueue(() =>
        {
            if (w.IsInFront) { w.ShowToast(kind, title, text); return; }
            if (!App.NotificationsOn || _toasts is null || WindowsBlocks() is not null)
            {
                App.Log($"notice not sent to Windows: on={App.NotificationsOn} ready={_toasts is not null} blocked={WindowsBlocks()}");
                return;
            }
            try
            {
                // Windows drops a toast whose button has empty arguments, silently (measured): "open"
                // stands for "just open the app".
                string args = Esc(page ?? "open");
                var xml = new XmlDocument();
                xml.LoadXml(
                    "<toast activationType=\"foreground\" launch=\"" + args + "\"><visual><binding template=\"ToastGeneric\">" +
                    "<text>" + Esc(title) + "</text><text>" + Esc(text) + "</text></binding></visual>" +
                    "<actions><action content=\"Open\" arguments=\"" + args + "\" activationType=\"foreground\" /></actions></toast>");
                var toast = new ToastNotification(xml);
                // A click while the app runs (it lives in the notification area): open the page.
                toast.Activated += (_, args) =>
                {
                    string target = (args as ToastActivatedEventArgs)?.Arguments ?? "";
                    App.Window?.DispatcherQueue.TryEnqueue(() =>
                    {
                        App.ShowWindow();
                        if (target.Length > 0 && target != "open") App.Window?.Go(target);
                    });
                };
                toast.Failed += (_, f) => App.Log($"notifications: Windows refused the toast: 0x{f.ErrorCode.HResult:X8}");
                _toasts.Show(toast);
            }
            catch (Exception ex) { App.Log($"notifications: show failed: 0x{ex.HResult:X8} {ex.GetType().Name} {ex.Message}"); }
        });
        if (!queued) App.Log("notice: the window's dispatcher refused it");
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
}
