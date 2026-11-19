# Snip Studio WinUI 3 migration

Started November 18, 2026 from Snip Studio 0.6.24, commit `f7e4e778f2af7c73c22eec94c46a4b70180e68cd`.

The migration has its own repository and preserves the original Git history. GitHub's self-fork request returned the original repository, so this is a separate repository rather than a GitHub fork-network entry. The stable repository is [jaydawg473/Snip-Studio](https://github.com/jaydawg473/Snip-Studio).

## First milestone

Build a native WinUI 3 image editor around the existing Windows document/rendering engine. The initial workflow is Open/Paste, Fit/manual zoom and pan, Pen/Highlighter/Rectangle, Undo/Redo, and atomic Save As to PNG/JPEG/BMP/PDF. A native XAML viewport replaces the WinForms viewport for this workflow.

The backend links the reviewed legacy document, shape, emoji, rich-text and PDF sources. It retains a Windows Desktop dependency for GDI+ and native RichEdit; it is not a platform-independent core. Rendering/text operations stay on the owning STA thread. Detached image encoding runs in bounded background work. Existing files start clean; pasted images start unsaved. Save results retain the captured revision so a late completion cannot mark newer work as saved.

The preview has a separate application identity. It does not run the stable startup, updater, tray, global hotkey, file-association, preferences or shortcut broker code. Unmigrated features remain in the inherited source for future stages and are not represented as functioning WinUI features.

## Subsequent stages

1. Verify the first milestone on signed binaries, including real native pointer/file-picker/clipboard behavior, theme/text scale and multiple DPI settings.
2. Migrate capture and OCR as independently verified services. Preserve raw OCR, identifiers, duplicate rows, reading order and user edits. Port editable text, selection and source-style matching only with the supplied regression examples.
3. Migrate the remaining drawing/editing tools, recent snips, video review, scrolling capture and recording/audio.
4. Introduce deliberate preview preferences and verified Windows integrations, then migrate installation/update/recovery behavior. Stable installations must remain isolated throughout the preview.
5. Measure startup, large-image preview/encoding, OCR, capture and recording with comparable inputs before making performance claims. Build/sign/package checks do not establish hardware acceptance.

## Pinned toolchain

- .NET SDK 10.0.404, released November 10, 2026.
- Microsoft.WindowsAppSDK 2.7.0, stable release November 17, 2026.
- Microsoft.Windows.SDK.BuildTools 10.0.28000.2957.
- Target `net10.0-windows10.0.26100.0`, initial x64 unpackaged, self-contained, multiple-file deployment.

Official references: [Microsoft .NET downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads), [WinForms migration patterns](https://learn.microsoft.com/en-us/windows/apps/get-started/line-of-business/migrate-winforms-patterns), [deployment options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview).

Restore uses only NuGet.org and requires package signatures. Package signatures do not replace Authenticode checks on executed tools and delivered binaries. Every executed helper/application must be signed and timestamped; Automation Forge signs our outputs and valid third-party publisher signatures remain intact. Builds do not launch the unsigned output. Sign and verify the complete executable dependency closure before native checks.

## Verification status

Implementation is in progress. The application has built and published successfully. Signed component checks passed 77 backend assertions and 1,335 viewport assertions; nine signed manifest assertions and a safe override-priority fixture also passed. An initial signed launch exposed missing published application XAML resources. Adding those exact resources with executable bytes unchanged produced a window and accessible controls. The publish target now includes and validates them automatically; its output matches the compiled resource hashes.

Windows was locked during the native observation. No rendered application screenshot, Open/Draw/Undo/Save input sequence, native picker, clipboard, theme, physical DPI or monitor acceptance is claimed. See [verification details](VERIFICATION-WINUI.md). The old 0.6.24 totals remain historical evidence and are not added to WinUI results. No installation, stable-channel replacement or public GitHub Release was performed.
