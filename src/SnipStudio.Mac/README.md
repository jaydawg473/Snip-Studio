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
