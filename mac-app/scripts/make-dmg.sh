#!/bin/bash
# Builds MacBridge.app and packages it as mac-app/build/MacBridge.dmg.
set -euo pipefail
cd "$(dirname "$0")/.."

./scripts/build-app.sh

STAGING=build/dmg
DMG=build/MacBridge.dmg
rm -rf "$STAGING" "$DMG"
mkdir -p "$STAGING"
cp -R build/MacBridge.app "$STAGING/"
ln -s /Applications "$STAGING/Applications"

hdiutil create -volname "MacBridge" -srcfolder "$STAGING" -ov -format UDZO "$DMG"
rm -rf "$STAGING"
echo "Built $DMG"
