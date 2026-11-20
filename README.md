<img src="assets/app-256.png" width="72" alt="Snip Studio icon">

# Snip Studio — WinUI 3 migration

Snip Studio brings capture, image editing, text extraction, recording and video review into a native WinUI 3 desktop interface. This separate Automation Forge repository preserves the history of [stable Snip Studio](https://github.com/jaydawg473/Snip-Studio), starting from 0.6.24 at `f7e4e778f2af7c73c22eec94c46a4b70180e68cd`.

**Current source: `0.7.0-preview.4`, file version `0.7.0.4`. Production release status: INSUFFICIENT EVIDENCE.** Source changes refine the compact capture row, align buttons and add icons throughout the capture menus. Window minimums and a temporary, scrollable Settings enlargement are intended to keep controls reachable while preserving manual sizing/movement. Current build and native verification are pending; preview.3 results remain historical. See [current verification status](VERIFICATION-WINUI.md).

The requested layout restoration preserves the product's identity and existing workflows. It does not claim improved OCR accuracy or faster capture/recording. The supplied dense filename example retains a measured recognition limit shared with stable 0.6.24; no identifiers are guessed or silently corrected.

## Available workflows in the current source

| Area | Controls and behavior |
| --- | --- |
| Capture | Rectangle, freeform, window, monitor, all monitors and scrolling capture; configurable delay, original-capture saving and clipboard copy. Partial scrolling results require a Keep/Discard choice. |
| Image editing | Open/Paste, multiple editor windows, Select and Move view, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse and emoji. Selected-object properties, resize/move, annotation erasing, crop, source-area removal, Undo and Redo. |
| Editable text | On-canvas text drafts, font family, image-pixel size, color, bold, italic and underline. Apply/Cancel retains a clear draft boundary. |
| OCR and translation | Local Windows OCR, original recognition output, selectable rows, source-style conversion and recognized-word selection. Conversion selects Text. Translation offers local processing or an explicitly chosen HTTPS server, with reviewed rows before applying. Font recovery remains approximate; review identifiers, leading zeros and duplicate rows. |
| View | Fit, zoom presets, pan, full screen and toolbar overflow. Resize to snip is enabled by default with a Settings choice. Source sizing preserves manual window sizing and clamps automatic sizing/minimums to the work area. Preview.4 minimum tracking and Settings restoration await native checks; earlier autosize/125% observations belong to preview.3. |
| Recording and video | Native WinUI recording/review window; display, region or window selection; microphone/system-audio device choices, pointer inclusion, 5–30 fps, recording boundaries and Stop and save. Local playback, playhead-based trim, original-copy saving and smaller MP4 exports. Recording is scaled to fit 1920 × 1080. Pause/Resume controls are not implemented. |
| Files and Windows | Flattened PNG/JPEG/BMP/PDF Save As, image/text clipboard actions, Windows Share, printing, recent snips/recordings, external-editor working copies, keyboard shortcuts and Help. Settings offer separate preview tray, startup and global-shortcut choices. |
| Recovery and release information | Cancellation and failure feedback, dirty-document prompts, atomic image/video-copy writes and recoverable preview preferences. Manual update checks read this migration repository's release information; they do not download or execute an installer. |

The main editor, recording/video review and settings use WinUI controls. Temporary capture selectors, scrolling controls and recording boundaries still use dedicated WinForms STA overlays. The renderer retains Windows GDI+/RichEdit dependencies. This is a Windows migration with an explicit hybrid boundary; it is not an all-WinUI rewrite.

## Using the preview

Use a signed candidate whose included validation, source/signature receipts and hash manifest identify its exact bytes and audit results. Extract the complete portable folder and launch `SnipStudio.WinUI3.Preview.exe`, keeping the adjacent runtime, resources, translation data and notices together. Verify valid timestamped Automation Forge signatures on our executables and valid original publisher signatures on bundled third-party binaries before execution. This source README does not predeclare a package audit result.

The preview has its own application identity and stores preferences at `%LocalAppData%\SnipStudio.WinUI3\preferences.json`. Tray behavior, sign-in startup and global shortcuts are deliberate Settings choices. The stable application and its preferences remain separate. Saving produces a flattened image or PDF; editable objects and Undo history remain in the open editor rather than in the exported file.

Images, OCR, drawing and recording are processed locally. Sharing and a configured translation server are explicit actions; the server receives selected text only when Translate is chosen. Manual update checks contact GitHub. Local startup diagnostics are bounded and redact known application/profile paths; they do not deliberately record images, clipboard contents or document text.

Preview.4 build, native minimum-size/Settings behavior and package evidence are pending. Earlier preview.3 native evidence is partial and tied to its candidate. Real capture/scrolling, recording/audio, physical DPI and installation remain unverified. Use [the verification checklist](VERIFICATION-WINUI.md) to distinguish source coverage from exercised behavior.

## Source and development

- [Migration design and remaining work](MIGRATION.md)
- [Preview.4 release notes](release-notes-0.7.0-preview.4.md)
- [Native WinUI frontend](src/SnipStudio.WinUI)
- [Windows backend adapters](src/SnipStudio.Backend)
- [Backend checks](tests/SnipStudio.Backend.Checks), [viewport checks](tests/SnipStudio.Viewport.Checks), [migration checks](tests/SnipStudio.Migration.Checks)
- [Historical preview.3 notes](release-notes-0.7.0-preview.3.md), [preview.2 notes](release-notes-0.7.0-preview.2.md), [preview.1 notes](release-notes-0.7.0-preview.1.md) and [stable 0.6.24 notes](release-notes-0.6.24.md)

The pinned toolchain is .NET SDK 10.0.404, Windows App SDK 2.7.0 and Windows SDK BuildTools 10.0.28000.2957. The x64 application targets `net10.0-windows10.0.26100.0` and publishes as an unpackaged, self-contained folder. Build/test/signing steps must preserve the signature requirements; never substitute `dotnet run` or an unsigned helper for the verified workflow. Signing credentials and private verification outputs are not included in this repository.

An independent Automation Forge app; not affiliated with Microsoft. No open-source license has been added to this repository. Retain the supplied third-party notices and original publisher attributions when distributing a verified package.
