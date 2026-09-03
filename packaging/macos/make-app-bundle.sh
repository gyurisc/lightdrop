#!/usr/bin/env bash
# Builds LightDrop.app -- a double-clickable launcher for `lightdrop ui`.
#
# An .app bundle is a directory with a required shape, not a compiled artifact, so this needs no
# Xcode and no Swift. The bundle launches the binary; it does not contain it, which keeps the
# single-executable story intact.
#
# Usage: ./make-app-bundle.sh /path/to/lightdrop [output-directory]
set -euo pipefail

BINARY="${1:?usage: make-app-bundle.sh /path/to/lightdrop [output-directory]}"
OUTPUT="${2:-$PWD}"

if [ ! -x "$BINARY" ]; then
  echo "No executable at $BINARY" >&2
  echo "Publish one first, then pass its path:" >&2
  echo "  dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true" >&2
  exit 1
fi

BINARY="$(cd "$(dirname "$BINARY")" && pwd)/$(basename "$BINARY")"
APP="$OUTPUT/LightDrop.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>LightDrop</string>
  <key>CFBundleIdentifier</key><string>dev.lightdrop.launcher</string>
  <key>CFBundleVersion</key><string>1.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>LightDrop</string>
</dict>
</plist>
PLIST

# Single-quoted, with embedded single quotes escaped, so the path lands in the launcher as a
# shell string literal -- backticks or $(...) in it must never be interpreted by the launcher.
QUOTED_BINARY="'$(printf '%s' "$BINARY" | sed "s/'/'\\\\''/g")'"

cat > "$APP/Contents/MacOS/LightDrop" <<LAUNCHER
#!/bin/sh
exec $QUOTED_BINARY ui
LAUNCHER

chmod +x "$APP/Contents/MacOS/LightDrop"

echo "Built $APP"
echo "It launches: $BINARY ui"
