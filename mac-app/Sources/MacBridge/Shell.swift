import Foundation

/// Runs fixed system binaries. Never goes through a shell, and user-supplied text is only ever
/// passed as a separate argument (never spliced into a script or command line).
enum Shell {
    struct Result {
        let output: String
        let error: String
        let status: Int32
    }

    /// Runs a binary and returns trimmed stdout/stderr.
    @discardableResult
    static func run(_ launchPath: String, _ args: [String], timeout: TimeInterval = 5) -> Result {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = args
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err
        process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { return Result(output: "", error: "\(error)", status: -1) }

        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline { usleep(10_000) }
        if process.isRunning { process.terminate() }
        process.waitUntilExit()

        let trim = { (pipe: Pipe) in
            String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        }
        return Result(output: trim(out), error: trim(err), status: process.terminationStatus)
    }

    /// Runs a fixed AppleScript. `arguments` are available to the script as `item n of argv`.
    @discardableResult
    static func osascript(_ script: String, arguments: [String] = []) -> Result {
        // An argument starting with "-" could be read as an osascript option.
        let safeArgs = arguments.map { $0.hasPrefix("-") ? " " + $0 : $0 }
        let wrapped = arguments.isEmpty ? script : "on run argv\n\(script)\nend run"
        return run("/usr/bin/osascript", ["-e", wrapped] + safeArgs)
    }
}
