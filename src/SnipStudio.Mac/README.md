# Snip Studio for macOS

The first trial targets Apple Silicon with macOS 14 or later. It supports region and window capture, opening images, pen annotations with Undo, on-device OCR through Vision, image and text clipboard copy, and PNG save.

GitHub Actions builds the trial branch on macOS. For a signed trial disk image, use a Mac with Xcode, a Developer ID Application identity in its keychain, and a notarization credential configured in a local `notarytool` keychain profile.

From the repository root, set `SNIPSTUDIO_SIGNING_IDENTITY` to the exact certificate name and `SNIPSTUDIO_NOTARY_PROFILE` to the local profile name. Run `bash src/SnipStudio.Mac/package-trial.sh <output-directory>`. The script builds and signs the app and disk image, waits for Apple notarization, staples the ticket, and verifies the disk image before copying it to the output directory.

## First-trial checks

- Install from the signed disk image and open Snip Studio. Grant screen capture access when macOS asks.
- Capture a Retina region, capture a window, and cancel a capture with Escape. Check the dimensions and that cancellation preserves the current image.
- Draw near the top-left of a screenshot and undo one stroke. Copy and save a PNG, then check that the saved image matches the editor at full resolution.
- Run OCR on a screenshot with multiple lines and a filename with leading zeros. Check what Vision recognized and paste the copied text into another app.

Screen and audio recording, scrolling capture, and editable Windows projects are outside this trial.

## Signing on GitHub Actions

Use the `macos-trial-signing` environment in the repository settings. Limit deployments to `feature/macos-trial`. Set its environment variable `MACOS_DEVELOPER_ID_IDENTITY` to the full Developer ID Application certificate name.

Add these values through GitHub's environment secrets form.

| Secret | Value |
| --- | --- |
| `MACOS_DEVELOPER_ID_P12_BASE64` | Base64 of the password-protected Developer ID Application certificate and private key exported as a P12 |
| `MACOS_DEVELOPER_ID_P12_PASSWORD` | Password used for that export |
| `MACOS_NOTARY_APPLE_ID` | Apple Account email |
| `MACOS_NOTARY_TEAM_ID` | Apple Developer Team ID |
| `MACOS_NOTARY_APP_PASSWORD` | Apple app-specific password for notarization |

Finally set the repository Actions variable `SNIPSTUDIO_MACOS_SIGNING_READY` to `true`. Re-run the trial workflow or push a new commit to its branch. The signing job applies the environment’s deployment rules and uploads the verified disk image as an Actions artifact.
