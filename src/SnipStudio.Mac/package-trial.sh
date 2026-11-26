#!/bin/bash
set -euo pipefail

project="$(cd "$(dirname "$0")" && pwd -P)"
repo="$(cd "$project/../.." && pwd -P)"
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo 'The trial package needs an Apple Silicon Mac with Xcode.' >&2
    exit 1
fi

: "${SNIPSTUDIO_SIGNING_IDENTITY:?Set the Developer ID Application identity}"
: "${SNIPSTUDIO_NOTARY_PROFILE:?Set the notarytool keychain profile name}"
case "$SNIPSTUDIO_SIGNING_IDENTITY" in
    "Developer ID Application: "*) ;;
    *) echo 'Choose an Apple Developer ID Application identity.' >&2; exit 1 ;;
esac
if [[ "$#" -ne 1 ]]; then
    echo 'Usage: bash package-trial.sh <output-directory>' >&2
    exit 1
fi

destination="$(mkdir -p "$1" && cd "$1" && pwd -P)"
output="$destination/SnipStudio-0.1.0-macOS-arm64-trial.dmg"
if [[ -e "$output" || -L "$output" ]]; then
    echo "An item already exists at $output." >&2
    exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
stage="$work/image"
bundle="$stage/Snip Studio.app"
icons="$work/SnipStudio.iconset"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources" "$icons"

swift build --package-path "$project" --configuration release
binaries="$(swift build --package-path "$project" --configuration release --show-bin-path)"
install -m 755 "$binaries/SnipStudioMac" "$bundle/Contents/MacOS/SnipStudioMac"
cp "$project/Info.plist" "$bundle/Contents/Info.plist"
plutil -lint "$bundle/Contents/Info.plist"

for size in 16 32 128 256; do
    name="icon_${size}x${size}"
    sips -z "$size" "$size" "$repo/assets/app-512.png" --out "$icons/$name.png" >/dev/null
    doubled=$((size * 2))
    sips -z "$doubled" "$doubled" "$repo/assets/app-512.png" --out "$icons/${name}@2x.png" >/dev/null
done
cp "$repo/assets/app-512.png" "$icons/icon_512x512.png"
iconutil -c icns "$icons" -o "$bundle/Contents/Resources/SnipStudio.icns"

codesign --options runtime --timestamp --sign "$SNIPSTUDIO_SIGNING_IDENTITY" "$bundle"
codesign --verify --strict --verbose=2 "$bundle"

dmg="$work/trial.dmg"
hdiutil create -volname "Snip Studio" -srcfolder "$stage" -format UDZO "$dmg"
codesign --timestamp --sign "$SNIPSTUDIO_SIGNING_IDENTITY" "$dmg"
notary_options=(--keychain-profile "$SNIPSTUDIO_NOTARY_PROFILE")
if [[ -n "${SNIPSTUDIO_NOTARY_KEYCHAIN:-}" ]]; then
    notary_options+=(--keychain "$SNIPSTUDIO_NOTARY_KEYCHAIN")
fi
xcrun notarytool submit "$dmg" "${notary_options[@]}" --wait
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
codesign --verify --strict --verbose=2 "$dmg"
spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg"
cp -n "$dmg" "$output"
cmp "$dmg" "$output"
shasum -a 256 "$output"
