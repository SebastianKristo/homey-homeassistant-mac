using System.Diagnostics;
using System.Drawing.Printing;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace MacBridge;

/// <summary>
/// Read-only sensors (who is using the PC, what is in front, mic/camera in use, …). Every value
/// comes from a fixed Windows API or registry key; nothing here takes a query, script, path or
/// command from the network, and nothing reads window contents, screens or location.
/// Slow values are refreshed in the background so a status request never waits on them.
/// </summary>
sealed class Sensors : IDisposable
{
    private readonly Config config = Config.Shared;
    private readonly object gate = new();
    private readonly System.Threading.Timer fastTimer, slowTimer, updatesTimer;
    private readonly PerformanceCounters counters = new();

    private double? gpuUsage, cpuClockMhz;
    private string[] bluetooth = Array.Empty<string>(), bluetoothLe = Array.Empty<string>();
    private string? audioOutput, audioInput;
    private int? pendingUpdates;
    private bool sessionLocked;
    private bool displayOn = true;
    private (string State, DateTimeOffset At)? lastStateChange;

    /// <summary>Called whenever the system state changes (sleep, wake, lock, …), so status can be pushed.</summary>
    public Action<string>? OnStateChange { get; set; }

    public Sensors()
    {
        fastTimer = new(_ => RefreshFast(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        slowTimer = new(_ => RefreshSlow(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
        // Searching for updates is slow and uses the network, so only every 6 hours.
        updatesTimer = new(_ => RefreshUpdates(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public Dictionary<string, object> Snapshot()
    {
        var s = new Dictionary<string, object>();
        var (app, _) = ForegroundApp();
        s["active_app"] = app;
        var idle = IdleSeconds();
        s["idle_seconds"] = idle;
        s["last_active"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - idle;
        s["logged_user"] = Environment.UserName;
        s["logged_users"] = LoggedUsers();
        s["notification_state"] = NotificationState();
        var (micActive, micApps) = CapabilityInUse("microphone");
        s["microphone_active"] = micActive;
        s["microphone_apps"] = micApps;
        var (camActive, camApps) = CapabilityInUse("webcam");
        s["webcam_active"] = camActive;
        s["webcam_apps"] = camApps;
        var screens = Screen.AllScreens;
        s["displays"] = screens.Length;
        if (Screen.PrimaryScreen is { } primary) s["display_resolution"] = $"{primary.Bounds.Width}x{primary.Bounds.Height}";
        if (AccentColor() is { } accent) s["accent_color"] = accent;
        s["printers"] = Printers();
        if (PrimaryNetwork() is { } network)
        {
            s["ip_address"] = network.Ip;
            s["network_interface"] = network.Name;
        }
        if (config.WatchedProcesses.Length > 0) s["processes"] = WatchedProcesses();
        if (config.WatchedServices.Length > 0) s["services"] = WatchedServices();
        lock (gate)
        {
            s["session_locked"] = sessionLocked;
            s["display_on"] = displayOn;
            if (gpuUsage is { } gpu) s["gpu_usage"] = Math.Round(gpu);
            if (cpuClockMhz is { } mhz) s["cpu_clock_mhz"] = Math.Round(mhz);
            if (audioOutput is not null) s["audio_output"] = audioOutput;
            if (audioInput is not null) s["audio_input"] = audioInput;
            if (pendingUpdates is { } updates) s["windows_updates"] = updates;
            if (config.ReportBluetoothDevices)
            {
                s["bluetooth_devices"] = bluetooth;
                s["bluetooth_le_devices"] = bluetoothLe;
            }
            if (lastStateChange is { } change)
            {
                s["last_state_change"] = change.State;
                s["last_state_change_at"] = change.At.ToUnixTimeSeconds();
            }
        }
        return s;
    }

    // System state changes

    /// <summary>Called by the tray app's hidden window when the console display turns on or off.</summary>
    public void SetDisplayState(int state)
    {
        var on = state != 0; // 0 off, 1 on, 2 dimmed
        lock (gate)
        {
            if (displayOn == on) return;
            displayOn = on;
        }
        Record(on ? "display_on" : "display_off");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var state = e.Reason switch
        {
            SessionSwitchReason.SessionLock => "locked",
            SessionSwitchReason.SessionUnlock => "unlocked",
            SessionSwitchReason.SessionLogon => "logon",
            SessionSwitchReason.SessionLogoff => "logoff",
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => "user_switched_out",
            SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => "user_switched_in",
            _ => null,
        };
        if (state is null) return;
        lock (gate)
        {
            if (state == "locked") sessionLocked = true;
            if (state == "unlocked") sessionLocked = false;
        }
        Record(state);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Record("sleep");
        else if (e.Mode == PowerModes.Resume) Record("wake");
    }

    private void Record(string state)
    {
        lock (gate) lastStateChange = (state, DateTimeOffset.UtcNow);
        OnStateChange?.Invoke(state);
    }

    // Quick values (read on every status request)

    private static (string App, int Pid) ForegroundApp()
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return ("", 0);
        Native.GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string? description = null;
            try { description = process.MainModule?.FileVersionInfo.FileDescription; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } // elevated app
            return (SystemControl.Clean(string.IsNullOrWhiteSpace(description) ? process.ProcessName : description, 100), (int)pid);
        }
        catch (ArgumentException)
        {
            return ("", 0);
        }
    }

    private static long IdleSeconds()
    {
        var info = new Native.LASTINPUTINFO { Size = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.Time) / 1000;
    }

    private static string[] LoggedUsers()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out var count)) return names.ToArray();
        try
        {
            var size = Marshal.SizeOf<Native.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<Native.WTS_SESSION_INFO>(sessions + i * size);
                if (!Native.WTSQuerySessionInformation(IntPtr.Zero, session.SessionId, Native.WTSUserName, out var buffer, out _))
                    continue;
                var name = Marshal.PtrToStringUni(buffer);
                Native.WTSFreeMemory(buffer);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        finally
        {
            Native.WTSFreeMemory(sessions);
        }
        return names.ToArray();
    }

    private static string NotificationState()
    {
        if (Native.SHQueryUserNotificationState(out var state) != 0) return "unknown";
        return state switch
        {
            1 => "not_present",
            2 => "busy",
            3 => "fullscreen_game",
            4 => "presentation",
            5 => "accepts_notifications",
            6 => "quiet_time",
            7 => "fullscreen_app",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Whether any app uses the microphone/webcam right now, and which, from the same registry
    /// keys behind the privacy indicator in the taskbar.
    /// </summary>
    private static (bool Active, string[] Apps) CapabilityInUse(string capability)
    {
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
            if (store is null) return (false, Array.Empty<string>());
            foreach (var name in store.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var nonPackaged = store.OpenSubKey(name);
                    foreach (var exe in nonPackaged?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var key = nonPackaged!.OpenSubKey(exe);
                        if (InUse(key)) apps.Add(Path.GetFileNameWithoutExtension(exe.Replace('#', '\\')));
                    }
                }
                else
                {
                    using var key = store.OpenSubKey(name);
                    if (InUse(key)) apps.Add(name.Split('_')[0].Split('.').Last());
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var list = apps.Select(a => SystemControl.Clean(a, 60)).ToArray();
        return (list.Length > 0, list);
    }

    private static bool InUse(RegistryKey? key) =>
        key?.GetValue("LastUsedTimeStart") is long start && start > 0
        && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    private static string? AccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int abgr) return null;
            var b = (abgr >> 16) & 0xff; var g = (abgr >> 8) & 0xff; var r = abgr & 0xff;
            return $"#{r:x2}{g:x2}{b:x2}";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string[] Printers()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().Select(p => SystemControl.Clean(p, 100)).ToArray();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private static (string Name, string Ip)? PrimaryNetwork()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => (Adapter: n, Properties: n.GetIPProperties()))
                .Where(n => n.Properties.GatewayAddresses.Any())
                .SelectMany(n => n.Properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => (Name: SystemControl.Clean(n.Adapter.Name, 60), Ip: a.Address.ToString())))
                .Cast<(string Name, string Ip)?>()
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private Dictionary<string, bool> WatchedProcesses()
    {
        var result = new Dictionary<string, bool>();
        foreach (var name in config.WatchedProcesses)
        {
            var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            var processes = Process.GetProcessesByName(bare);
            result[name] = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
        }
        return result;
    }

    private Dictionary<string, string> WatchedServices()
    {
        var result = new Dictionary<string, string>();
        foreach (var name in config.WatchedServices)
        {
            try
            {
                using var service = new ServiceController(name);
                result[name] = service.Status switch
                {
                    ServiceControllerStatus.Running => "running",
                    ServiceControllerStatus.Stopped => "stopped",
                    ServiceControllerStatus.Paused => "paused",
                    ServiceControllerStatus.StartPending => "starting",
                    ServiceControllerStatus.StopPending => "stopping",
                    _ => "other",
                };
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                result[name] = "not_found";
            }
        }
        return result;
    }

    // Background refresh

    private void RefreshFast()
    {
        var (gpu, clock) = counters.Sample();
        lock (gate)
        {
            gpuUsage = gpu;
            cpuClockMhz = clock;
        }
    }

    private void RefreshSlow()
    {
        var output = DeviceName(MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
        var input = DeviceName(MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default));
        string[] classic = Array.Empty<string>(), le = Array.Empty<string>();
        if (config.ReportBluetoothDevices)
        {
            classic = DeviceNames(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            le = DeviceNames(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        }
        lock (gate)
        {
            audioOutput = output;
            audioInput = input;
            bluetooth = classic;
            bluetoothLe = le;
        }
    }

    private static string? DeviceName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var task = DeviceInformation.CreateFromIdAsync(id).AsTask();
            return task.Wait(TimeSpan.FromSeconds(5)) ? SystemControl.Clean(task.Result.Name, 100) : null;
        }
        catch (Exception e) when (e is COMException or AggregateException or ArgumentException)
        {
            return null;
        }
    }

    private static string[] DeviceNames(string selector)
    {
        try
        {
            var task = DeviceInformation.FindAllAsync(selector).AsTask();
            if (!task.Wait(TimeSpan.FromSeconds(10))) return Array.Empty<string>();
            return task.Result.Select(d => SystemControl.Clean(d.Name, 100)).Where(n => n.Length > 0)
                .Distinct().OrderBy(n => n).ToArray();
        }
        catch (Exception e) when (e is COMException or AggregateException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Number of available, not yet installed Windows updates (Windows Update Agent).</summary>
    private void RefreshUpdates()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is null) return;
            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            int count = result.Updates.Count;
            lock (gate) pendingUpdates = count;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Trace.WriteLine($"MacBridge: Windows Update search failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        fastTimer.Dispose();
        slowTimer.Dispose();
        updatesTimer.Dispose();
        counters.Dispose();
    }
}

/// <summary>GPU load and current CPU clock from Windows performance counters (fixed counter paths).</summary>
sealed class PerformanceCounters : IDisposable
{
    private readonly object gate = new();
    private IntPtr query, gpu, performance, frequency;

    public PerformanceCounters()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
        // Missing counters (e.g. no GPU counters on old drivers) simply stay unreported.
        if (Native.PdhAddEnglishCounter(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out gpu) != 0) gpu = IntPtr.Zero;
        if (Native.PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out performance) != 0) performance = IntPtr.Zero;
        if (Native.PdhAddEnglishCounter(query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out frequency) != 0) frequency = IntPtr.Zero;
        Native.PdhCollectQueryData(query);
    }

    public (double? Gpu, double? ClockMhz) Sample()
    {
        lock (gate)
        {
            if (query == IntPtr.Zero || Native.PdhCollectQueryData(query) != 0) return (null, null);
            double? clock = null;
            if (Value(performance) is { } percent && Value(frequency) is { } mhz) clock = mhz * percent / 100;
            return (GpuTotal(), clock);
        }
    }

    private static double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return Native.PdhGetFormattedCounterValue(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var value) == 0
            && value.CStatus == 0 ? value.DoubleValue : null;
    }

    /// <summary>Sum of the 3D engine load over all processes, capped at 100 (what Task Manager shows as "3D").</summary>
    private double? GpuTotal()
    {
        if (gpu == IntPtr.Zero) return null;
        uint size = 0;
        var status = Native.PdhGetFormattedCounterArray(gpu, Native.PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (status != Native.PDH_MORE_DATA || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArray(gpu, Native.PDH_FMT_DOUBLE, ref size, out var count, buffer) != 0) return null;
            var itemSize = Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM>();
            double total = 0;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus == 0 && double.IsFinite(item.Value.DoubleValue)) total += item.Value.DoubleValue;
            }
            return Math.Clamp(total, 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (query != IntPtr.Zero) Native.PdhCloseQuery(query);
            query = IntPtr.Zero;
        }
    }
}
