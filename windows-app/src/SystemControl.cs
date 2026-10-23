using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text;
using Microsoft.Win32;

namespace MacBridge;

/// <summary>
/// Power and session commands. Each one does exactly one fixed thing through a Windows API; none
/// starts a program or takes a path, URL, script or command line from the network.
/// </summary>
static class SystemControl
{
    public static string AppVersion { get; } =
        typeof(SystemControl).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Set by the tray app: shows a notification from its tray icon.</summary>
    public static Action<string, string>? ShowNotification { get; set; }

    public static void Sleep()
    {
        if (!Native.SetSuspendState(false, false, false))
            Trace.WriteLine($"MacBridge: sleep failed ({Marshal.GetLastWin32Error()})");
    }

    public static void DisplaySleep() =>
        Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, 2);

    /// <summary>Wakes the display (the network side of "wake" is Wake-on-LAN, sent by HA/Homey).</summary>
    public static void WakeDisplay()
    {
        Native.SetThreadExecutionState(Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED);
        // A one-pixel mouse nudge (and back) is what reliably turns monitors on again.
        var inputs = new[] { MouseMove(1), MouseMove(-1) };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT MouseMove(int dx) => new()
    {
        Type = Native.INPUT_MOUSE,
        U = new Native.InputUnion { Mouse = new Native.MOUSEINPUT { Dx = dx, Flags = Native.MOUSEEVENTF_MOVE } },
    };

    public static void Lock()
    {
        if (!Native.LockWorkStation()) Trace.WriteLine($"MacBridge: lock failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Asks Windows to restart the normal way (apps get to save and close; nothing is forced).</summary>
    public static void Restart() => ExitWindows(Native.EWX_REBOOT);

    /// <summary>Asks Windows to shut down the normal way (apps get to save and close; nothing is forced).</summary>
    public static void Shutdown() => ExitWindows(Native.EWX_SHUTDOWN | Native.EWX_POWEROFF);

    private static void ExitWindows(uint flags)
    {
        EnableShutdownPrivilege();
        if (!Native.ExitWindowsEx(flags, Native.SHTDN_REASON_MAJOR_OTHER | Native.SHTDN_REASON_FLAG_PLANNED))
            Trace.WriteLine($"MacBridge: shutdown/restart failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Signed-in users hold SeShutdownPrivilege, but it must be enabled before ExitWindowsEx.</summary>
    private static void EnableShutdownPrivilege()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out var token))
            return;
        try
        {
            if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return;
            var privileges = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Native.SE_PRIVILEGE_ENABLED };
            Native.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    // Speech and notifications

    private static readonly object SpeechGate = new();
    private static SpeechSynthesizer? synthesizer;

    public static void Say(string text)
    {
        lock (SpeechGate)
        {
            synthesizer ??= new SpeechSynthesizer();
            synthesizer.SetOutputToDefaultAudioDevice();
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(Clean(text, 500));
        }
    }

    public static void Notify(string title, string message) =>
        ShowNotification?.Invoke(Clean(title, 100), Clean(message, 500));

    /// <summary>Strips control characters and caps the length of text from the network.</summary>
    public static string Clean(string text, int limit)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (count++ >= limit) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    // Device info

    public static Dictionary<string, object> DeviceInfo()
    {
        var info = new Dictionary<string, object>
        {
            ["id"] = DeviceId(),
            ["name"] = ComputerName(),
            ["model"] = BiosValue("SystemProductName") ?? "PC",
            ["manufacturer"] = BiosValue("SystemManufacturer") ?? "",
            ["platform"] = "windows",
            ["os_name"] = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10",
            ["os_version"] = Environment.OSVersion.Version.ToString(3),
            ["app_version"] = AppVersion,
        };
        if (MacAddress() is { } mac) info["mac_address"] = mac; // for Wake-on-LAN
        return info;
    }

    public static string ComputerName()
    {
        try { return Dns.GetHostName(); } catch (SocketException) { return Environment.MachineName; }
    }

    private static string? deviceId;

    /// <summary>
    /// Stable, anonymous id for this PC: a hash of the Windows MachineGuid, so the id itself is
    /// never shared (not in mDNS, not in the API).
    /// </summary>
    public static string DeviceId()
    {
        if (deviceId is not null) return deviceId;
        string? guid = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"MacBridge:{guid ?? ComputerName()}"));
        return deviceId = Auth.Hex(digest[..12]);
    }

    private static string? BiosValue(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var value = (key?.GetValue(name) as string)?.Trim();
            return string.IsNullOrEmpty(value) ? null : Clean(value, 100);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hardware address of the active network adapter (wired preferred, it wakes most reliably),
    /// skipping virtual, VPN and loopback adapters.
    /// </summary>
    public static string? MacAddress()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211)
                .Where(n => !IsVirtual(n.Description))
                .Select(n => (Adapter: n, Properties: n.GetIPProperties()))
                .Where(n => n.Properties.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderByDescending(n => n.Properties.GatewayAddresses.Any())
                .ThenBy(n => n.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(n => n.Adapter)
                .FirstOrDefault();
            var bytes = adapter?.GetPhysicalAddress().GetAddressBytes();
            return bytes is { Length: 6 } ? string.Join(":", bytes.Select(b => b.ToString("x2"))) : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsVirtual(string description) =>
        new[] { "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "wireguard", "tailscale", "zerotier", "loopback" }
            .Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase));
}
