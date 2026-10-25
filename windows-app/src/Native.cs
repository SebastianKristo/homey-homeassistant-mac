using System.Runtime.InteropServices;

namespace MacBridge;

/// <summary>The Win32 functions MacBridge uses. Every call is a fixed action; nothing here runs programs.</summary>
static class Native
{
    // Power and session

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ExitWindowsEx(uint flags, uint reason);

    public const uint EWX_SHUTDOWN = 0x1, EWX_REBOOT = 0x2, EWX_POWEROFF = 0x8;
    public const uint SHTDN_REASON_MAJOR_OTHER = 0, SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static readonly IntPtr HWND_BROADCAST = new(0xffff);
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_MONITORPOWER = 0xF170;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    public const uint ES_DISPLAY_REQUIRED = 0x2, ES_SYSTEM_REQUIRED = 0x1;

    // Shutdown privilege (needed by ExitWindowsEx; every interactive user has it, just not enabled)

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    // Input (media keys when no media session is available, and nudging the display awake)

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, MOUSEEVENTF_MOVE = 0x1;
    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint Type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    // Statistics

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    // Clipboard

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();
    // Sensors

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint Size; public uint Time; }

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSEnumerateSessions(IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    public const int WTSUserName = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO { public uint SessionId; public IntPtr WinStationName; public int State; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    public static Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    public const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;

    // Performance counters (PDH), read with fixed English counter paths only.

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);

    public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }
}
