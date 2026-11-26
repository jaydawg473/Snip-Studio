#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 1 || "$1" != *.app ]]; then
    echo 'Usage: bash stage-app.sh <new-app-path>' >&2
    exit 1
fi
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo 'The trial bundle needs an Apple Silicon Mac with Xcode.' >&2
    exit 1
fi

project="$(cd "$(dirname "$0")" && pwd -P)"
repo="$(cd "$project/../.." && pwd -P)"
if [[ -e "$1" || -L "$1" ]]; then
    echo "An item already exists at $1." >&2
    exit 1
fi
bundle="$1"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
icons="$work/SnipStudio.iconset"
mkdir "$icons"

swift build --package-path "$project" --configuration release
binaries="$(swift build --package-path "$project" --configuration release --show-bin-path)"
executable="$binaries/SnipStudioMac"
test "$(lipo -archs "$executable")" = arm64
test "$(xcrun vtool -show-build "$executable" | awk '$1 == "minos" { print $2 }')" = 14.0
plutil -lint "$project/Info.plist"
test "$(plutil -extract CFBundleExecutable raw -o - "$project/Info.plist")" = SnipStudioMac
test "$(plutil -extract LSMinimumSystemVersion raw -o - "$project/Info.plist")" = 14.0
test "$(plutil -extract CFBundleIconFile raw -o - "$project/Info.plist")" = SnipStudio.icns

for size in 16 32 128 256; do
    name="icon_${size}x${size}"
    sips -z "$size" "$size" "$repo/assets/app-512.png" --out "$icons/$name.png" >/dev/null
    doubled=$((size * 2))
    sips -z "$doubled" "$doubled" "$repo/assets/app-512.png" --out "$icons/${name}@2x.png" >/dev/null
done
cp "$repo/assets/app-512.png" "$icons/icon_512x512.png"
iconutil -c icns "$icons" -o "$work/SnipStudio.icns"
test -s "$work/SnipStudio.icns"

mkdir "$bundle"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
install -m 755 "$executable" "$bundle/Contents/MacOS/SnipStudioMac"
cp "$project/Info.plist" "$bundle/Contents/Info.plist"
cp "$work/SnipStudio.icns" "$bundle/Contents/Resources/SnipStudio.icns"
cmp "$executable" "$bundle/Contents/MacOS/SnipStudioMac"
echo "Verified app bundle layout at $bundle (arm64, macOS 14.0 minimum)."
