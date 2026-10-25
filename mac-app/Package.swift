// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "MacBridge",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "MacBridge",
            path: "Sources/MacBridge",
            linkerSettings: [
                .linkedFramework("IOKit"),
                .linkedFramework("AppKit"),
                .linkedFramework("Network"),
                .linkedFramework("CoreAudio"),
                .linkedFramework("CoreMediaIO"),
                .linkedFramework("IOBluetooth"),
                .linkedFramework("UserNotifications"),
                .linkedFramework("AVFoundation"),
            ]
        )
    ]
)
