using System.Security.Cryptography;
using Microsoft.Win32;

namespace MacBridge;

/// <summary>
/// Persistent settings. Non-secret settings live in the registry (HKCU\Software\MacBridge);
/// the token lives in Windows Credential Manager.
/// </summary>
sealed class Config
{
    public static readonly Config Shared = new();
    private const string KeyPath = @"Software\MacBridge";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MacBridge";
    private readonly object gate = new();
    private string? cachedToken;

    public int Port
    {
        get { var v = ReadInt("Port", 8765); return v is > 0 and <= 65535 ? v : 8765; }
        set => WriteInt("Port", value);
    }

    /// <summary>Shared secret used by Home Assistant / Homey to sign requests. Never sent over the network.</summary>
    public string Token
    {
        get
        {
            lock (gate)
            {
                if (cachedToken is not null) return cachedToken;
                var stored = CredentialStore.Read();
                if (!string.IsNullOrEmpty(stored)) return cachedToken = stored;
                var fresh = GenerateToken();
                CredentialStore.Write(fresh);
                return cachedToken = fresh;
            }
        }
    }

    public void RegenerateToken()
    {
        var fresh = GenerateToken();
        lock (gate)
        {
            CredentialStore.Write(fresh);
            cachedToken = fresh;
        }
    }

    /// <summary>
    /// Optional http(s) URLs that receive a signed POST with the status JSON on every interval
    /// (e.g. a Home Assistant webhook).
    /// </summary>
    public string[] PushUrls
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return (key?.GetValue("PushUrls") as string[] ?? Array.Empty<string>()).Where(IsAllowedPushUrl).ToArray();
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("PushUrls", value.Where(IsAllowedPushUrl).ToArray(), RegistryValueKind.MultiString);
        }
    }

    public static bool IsAllowedPushUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    public int PushIntervalSeconds
    {
        get { var v = ReadInt("PushInterval", 10); return v > 0 ? Math.Max(v, 2) : 10; }
        set => WriteInt("PushInterval", value);
    }

    /// <summary>Restart/shutdown can be turned off from the tray menu.</summary>
    public bool AllowPowerCommands
    {
        get => ReadInt("AllowPowerCommands", 1) != 0;
        set => WriteInt("AllowPowerCommands", value ? 1 : 0);
    }

    /// <summary>Speech and notifications can be turned off from the tray menu.</summary>
    public bool AllowSpeechAndNotifications
    {
        get => ReadInt("AllowSpeechAndNotifications", 1) != 0;
        set => WriteInt("AllowSpeechAndNotifications", value ? 1 : 0);
    }

    /// <summary>Opt-in: report names of connected Bluetooth devices.</summary>
    public bool ReportBluetoothDevices
    {
        get => ReadInt("ReportBluetoothDevices", 0) != 0;
        set => WriteInt("ReportBluetoothDevices", value ? 1 : 0);
    }

    /// <summary>
    /// Program names (e.g. "steam", "zoom") whose running state is reported. Chosen locally in the
    /// tray menu only; nothing on the network can change this list.
    /// </summary>
    public string[] WatchedProcesses
    {
        get => ReadList("WatchedProcesses");
        set => WriteList("WatchedProcesses", value);
    }

    /// <summary>Windows service names whose state is reported. Chosen locally in the tray menu only.</summary>
    public string[] WatchedServices
    {
        get => ReadList("WatchedServices");
        set => WriteList("WatchedServices", value);
    }

    private static string[] ReadList(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string[] ?? Array.Empty<string>();
    }

    private static void WriteList(string name, IEnumerable<string> values)
    {
        var clean = values.Select(v => v.Trim()).Where(v => v.Length is > 0 and <= 100).Take(20).ToArray();
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, clean, RegistryValueKind.MultiString);
    }

    /// <summary>Start MacBridge when the user signs in (HKCU Run key, no admin rights needed).</summary>
    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value && Environment.ProcessPath is { } path) key.SetValue(RunValueName, $"\"{path}\"");
            else key.DeleteValue(RunValueName, false);
        }
    }

    /// <summary>Keeps the autostart entry pointing at this exe if it was moved or updated.</summary>
    public void RefreshStartWithWindows()
    {
        if (StartWithWindows) StartWithWindows = true;
    }

    private static int ReadInt(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    private static void WriteInt(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static string GenerateToken() => Auth.Hex(RandomNumberGenerator.GetBytes(32));
}
