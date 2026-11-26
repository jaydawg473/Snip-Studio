#!/bin/bash
set -euo pipefail

project="$(cd "$(dirname "$0")" && pwd -P)"
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
display_name="$(plutil -extract CFBundleDisplayName raw -o - "$project/Info.plist")"
version="$(plutil -extract CFBundleShortVersionString raw -o - "$project/Info.plist")"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo 'The trial needs a three-part bundle version.' >&2
    exit 1
fi
output="$destination/SnipDesk-$version-macOS-arm64-trial.dmg"
if [[ -e "$output" || -L "$output" ]]; then
    echo "An item already exists at $output." >&2
    exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
stage="$work/image"
bundle="$stage/$display_name.app"
mkdir "$stage"
bash "$project/stage-app.sh" "$bundle"

codesign --options runtime --timestamp --entitlements "$project/Trial.entitlements" \
    --sign "$SNIPSTUDIO_SIGNING_IDENTITY" "$bundle"
codesign --verify --strict --verbose=2 "$bundle"
"$bundle/Contents/MacOS/SnipStudioMac" --self-check

dmg="$work/trial.dmg"
hdiutil create -volname "$display_name" -srcfolder "$stage" -format UDZO "$dmg"
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
