# Snip Studio for macOS

The first trial targets Apple Silicon with macOS 14 or later. It uses Snip Studio's Windows layout for the launcher, capture controls, editor tools, contextual options, and zoom footer. It supports rectangle, window, full-screen, and all-monitor capture, image open and paste, pen, highlighter, shapes, erasing, Undo and Redo, on-device OCR through Vision, image and text clipboard copy, printing, and PNG, JPEG, BMP, and PDF export. [Windows feature checklist and Mac acceptance tests](PARITY.md)

GitHub Actions builds the trial branch on macOS and verifies the app bundle layout, Apple Silicon architecture, macOS 14 minimum, and icon. For a signed trial disk image, use the GitHub signing job below or a Mac with Xcode, a Developer ID Application identity in its keychain, and a notarization credential configured in a local `notarytool` keychain profile.

From the repository root, set `SNIPSTUDIO_SIGNING_IDENTITY` to the exact certificate name and `SNIPSTUDIO_NOTARY_PROFILE` to the local profile name. Run `bash src/SnipStudio.Mac/package-trial.sh <output-directory>`. The script signs the app and runs checks for full-resolution PNG export, ink positioning, Undo, OCR row ordering, and native Vision recognition. It then signs and notarizes the disk image, staples the ticket, and verifies the disk image before copying it to the output directory.

## First-trial checks

- Install from the signed disk image and open Snip Studio. Grant screen capture access when macOS asks.
- Capture a Retina region and a window, then check full-screen and multi-display captures. Check dimensions and display placement. Cancel a capture with Escape and make sure the current image stays open.
- Draw near the top-left, add and erase a shape, then undo and redo. Check Fit and 100% at Retina scale, copy the image, and reopen each export format. Check Print Preview without submitting a physical job.
- Run Text Actions on multiple lines including a filename with leading zeros. Check the overlay and paste copied text and a table into their intended apps. Test automatic copy and original saving with each option enabled and disabled.

The trial also includes crop, object movement and rotation, video review with a filmstrip, MP4 trim, and size-limited export for email. On macOS 15 or later, Video mode records a display, adjustable display region, or complete window. Optional system audio and microphone input are available. Select Stop to save the MP4 to Movies/Snip Studio and open it for review. Scrolling capture and editable projects are still pending. The file menu checks the original Snip Studio repository for an Apple Silicon download.

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
