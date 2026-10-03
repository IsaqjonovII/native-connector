using Microsoft.Win32;

namespace OneC.Cloud;

/// <summary>
/// The <c>X-Device-Id</c> the old Connector sends: Windows' MachineGuid (connector
/// src-tauri/src/utils/fingerprint.rs:1-7, the machine_uid crate). Read-only registry access.
/// It only feeds the main API's audit log (spec §4).
/// </summary>
public static class DeviceId
{
    public static string Get()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                     .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g && g.Length > 0) return g;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return "unknown-device";
    }
}
