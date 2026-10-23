#!/bin/bash
# Builds MacBridge.app and packages it as mac-app/build/MacBridge.dmg with a
# drag-to-Applications installer window. Requires `brew install create-dmg`.
set -euo pipefail
cd "$(dirname "$0")/.."

./scripts/build-app.sh

DMG=build/MacBridge.dmg
STAGING=build/dmg
rm -rf "$STAGING" "$DMG"
mkdir -p "$STAGING"
cp -R build/MacBridge.app "$STAGING/"

# Retina-ready background (1x + 2x in one TIFF).
tiffutil -cathidpicheck Resources/dmg-background.png Resources/dmg-background@2x.png -out build/dmg-background.tiff

create-dmg \
  --volname "MacBridge" \
  --volicon build/MacBridge.app/Contents/Resources/AppIcon.icns \
  --background build/dmg-background.tiff \
  --window-pos 200 120 \
  --window-size 660 400 \
  --icon-size 112 \
  --text-size 13 \
  --icon "MacBridge.app" 170 205 \
  --hide-extension "MacBridge.app" \
  --app-drop-link 490 205 \
  --no-internet-enable \
  "$DMG" "$STAGING"

rm -rf "$STAGING"
echo "Built $DMG"
