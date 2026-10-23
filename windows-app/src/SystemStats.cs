using System.Runtime.InteropServices;

namespace MacBridge;

/// <summary>Collects performance statistics (CPU, memory, disk, battery, uptime).</summary>
sealed class SystemStats
{
    private const double Gb = 1_073_741_824.0;
    private (long Idle, long Kernel, long User)? previousTimes;

    public Dictionary<string, object> Snapshot()
    {
        var stats = new Dictionary<string, object> { ["cpu_usage"] = Round1(CpuUsage()) };
        foreach (var (key, value) in Memory()) stats[key] = value;
        foreach (var (key, value) in Disk()) stats[key] = value;
        foreach (var (key, value) in Battery()) stats[key] = value;
        stats["uptime"] = Environment.TickCount64 / 1000;
        stats["cpu_cores"] = Environment.ProcessorCount;
        return stats;
    }

    private double CpuUsage()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var previous = previousTimes;
        previousTimes = (idle, kernel, user);
        // Kernel time includes idle time.
        var (dIdle, dKernel, dUser) = previous is { } p ? (idle - p.Idle, kernel - p.Kernel, user - p.User) : (idle, kernel, user);
        var total = (double)(dKernel + dUser);
        return total > 0 ? Math.Clamp((total - dIdle) / total * 100, 0, 100) : 0;
    }

    private static Dictionary<string, object> Memory()
    {
        var status = new Native.MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return new();
        var total = (double)status.TotalPhys;
        var used = total - status.AvailPhys;
        return new()
        {
            ["memory_total_gb"] = Round1(total / Gb),
            ["memory_used_gb"] = Round1(used / Gb),
            ["memory_usage"] = Round1(used / total * 100),
        };
    }

    private static Dictionary<string, object> Disk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            double total = drive.TotalSize, free = drive.AvailableFreeSpace;
            if (total <= 0) return new();
            return new()
            {
                ["disk_total_gb"] = Round1(total / Gb),
                ["disk_free_gb"] = Round1(free / Gb),
                ["disk_usage"] = Round1((total - free) / total * 100),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new();
        }
    }

    private static Dictionary<string, object> Battery()
    {
        if (!Native.GetSystemPowerStatus(out var status)) return new();
        const byte NoBattery = 128, Unknown = 255, Charging = 8;
        if ((status.BatteryFlag & NoBattery) != 0 || status.BatteryFlag == Unknown || status.BatteryLifePercent > 100)
            return new() { ["power_source"] = "ac" };
        return new()
        {
            ["battery_level"] = (int)status.BatteryLifePercent,
            ["battery_charging"] = (status.BatteryFlag & Charging) != 0,
            ["power_source"] = status.ACLineStatus == 1 ? "ac" : "battery",
        };
    }

    private static double Round1(double v) => Math.Round(v, 1);
}
