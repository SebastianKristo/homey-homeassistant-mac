#!/bin/bash
# Builds MacBridge.app (menu bar app) into mac-app/build/.
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release
APP=build/MacBridge.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/MacBridge "$APP/Contents/MacOS/MacBridge"
iconutil -c icns Resources/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>no.kristo.macbridge</string>
    <key>CFBundleName</key><string>MacBridge</string>
    <key>CFBundleExecutable</key><string>MacBridge</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.1.1</string>
    <key>CFBundleVersion</key><string>3</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSUIElement</key><true/>
    <key>NSAppleEventsUsageDescription</key>
    <string>MacBridge controls playback in Music and Spotify, and restart/shutdown, when Home Assistant or Homey asks it to.</string>
    <key>NSLocalNetworkUsageDescription</key>
    <string>MacBridge serves an API on your local network for Home Assistant and Homey.</string>
    <key>NSBonjourServices</key>
    <array><string>_macbridge._tcp</string></array>
</dict>
</plist>
PLIST

# Ad-hoc sign with the hardened runtime and a single entitlement (Apple Events), so macOS
# remembers granted permissions and the app cannot load unsigned code.
codesign --force --options runtime --entitlements Resources/MacBridge.entitlements --sign - "$APP"
codesign --verify --strict "$APP"
echo "Built $APP – copy it to /Applications and add it to Login Items."
