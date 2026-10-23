import Foundation
import IOKit.ps
import Darwin

/// Collects performance statistics (CPU, memory, disk, battery, uptime, thermal).
final class SystemStats {
    private var previousTicks: (user: UInt64, system: UInt64, idle: UInt64, nice: UInt64)?

    func snapshot() -> [String: Any] {
        var stats: [String: Any] = [:]
        stats["cpu_usage"] = round1(cpuUsage())
        stats.merge(memory()) { $1 }
        stats.merge(disk()) { $1 }
        stats.merge(battery()) { $1 }
        stats["uptime"] = Int(ProcessInfo.processInfo.systemUptime)
        stats["thermal_state"] = thermalState()
        let load = loadAverage()
        stats["load_1m"] = (load.0 * 100).rounded() / 100
        stats["load_5m"] = (load.1 * 100).rounded() / 100
        stats["cpu_cores"] = ProcessInfo.processInfo.activeProcessorCount
        return stats
    }

    // MARK: CPU

    private func cpuUsage() -> Double {
        var info = host_cpu_load_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count)
            }
        }
        guard result == KERN_SUCCESS else { return 0 }

        let user = UInt64(info.cpu_ticks.0)
        let system = UInt64(info.cpu_ticks.1)
        let idle = UInt64(info.cpu_ticks.2)
        let nice = UInt64(info.cpu_ticks.3)
        defer { previousTicks = (user, system, idle, nice) }

        guard let prev = previousTicks else {
            let total = Double(user + system + idle + nice)
            return total > 0 ? Double(user + system + nice) / total * 100 : 0
        }
        let dUser = Double(user &- prev.user)
        let dSystem = Double(system &- prev.system)
        let dIdle = Double(idle &- prev.idle)
        let dNice = Double(nice &- prev.nice)
        let total = dUser + dSystem + dIdle + dNice
        return total > 0 ? (dUser + dSystem + dNice) / total * 100 : 0
    }

    private func loadAverage() -> (Double, Double) {
        var loads = [Double](repeating: 0, count: 3)
        getloadavg(&loads, 3)
        return (loads[0], loads[1])
    }

    // MARK: Memory

    private func memory() -> [String: Any] {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        let total = Double(ProcessInfo.processInfo.physicalMemory)
        guard result == KERN_SUCCESS else { return ["memory_total_gb": round1(total / gb)] }

        let page = Double(vm_kernel_page_size)
        let used = Double(UInt64(stats.active_count) + UInt64(stats.wire_count) + UInt64(stats.compressor_page_count)) * page
        return [
            "memory_total_gb": round1(total / gb),
            "memory_used_gb": round1(used / gb),
            "memory_usage": round1(used / total * 100),
        ]
    }

    // MARK: Disk

    private func disk() -> [String: Any] {
        let url = URL(fileURLWithPath: "/")
        guard let values = try? url.resourceValues(forKeys: [
            .volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey,
        ]),
            let total = values.volumeTotalCapacity,
            let free = values.volumeAvailableCapacityForImportantUsage
        else { return [:] }
        let totalD = Double(total), freeD = Double(free)
        return [
            "disk_total_gb": round1(totalD / gb),
            "disk_free_gb": round1(freeD / gb),
            "disk_usage": round1((totalD - freeD) / totalD * 100),
        ]
    }

    // MARK: Battery

    private func battery() -> [String: Any] {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef]
        else { return [:] }
        for source in list {
            guard let desc = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
                  (desc[kIOPSTypeKey] as? String) == kIOPSInternalBatteryType
            else { continue }
            let current = desc[kIOPSCurrentCapacityKey] as? Int ?? 0
            let max = desc[kIOPSMaxCapacityKey] as? Int ?? 100
            return [
                "battery_level": max > 0 ? Int(Double(current) / Double(max) * 100) : current,
                "battery_charging": desc[kIOPSIsChargingKey] as? Bool ?? false,
                "power_source": (desc[kIOPSPowerSourceStateKey] as? String) == kIOPSACPowerValue ? "ac" : "battery",
            ]
        }
        return ["power_source": "ac"]
    }

    private func thermalState() -> String {
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "unknown"
        }
    }

    private let gb = 1_073_741_824.0
    private func round1(_ v: Double) -> Double { (v * 10).rounded() / 10 }
}
