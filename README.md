<img src="assets/app-256.png" width="72" alt="Snip Studio icon">

# Snip Studio

Snip Studio brings capture, image editing, text extraction, recording and video review into a native WinUI 3 desktop interface. Stable releases use the [original Snip Studio repository](https://github.com/jaydawg473/Snip-Studio).

## Current release source

The source version is **0.7.0** (file version **0.7.0.0**). **Check for updates** offers releases from the original Snip Studio update channel. Use the validation and release notes alongside a signed package to identify its verified source and test results.

## Preview.4 reference

**Historical reference: `0.7.0-preview.4`, file version `0.7.0.4`.** That build passed 1,673 component assertions, 7 local translation checks, 47 generated-video checks and static installer/ZIP verification. Its layout introduced the selected-shape icon dropdown, a mode hint and image-only tools and zoom after opening an image. See [the preview.4 verification record](VERIFICATION-WINUI.md).

The layout reference preserves the product's existing controls and workflows. Consult each signed build's validation for its OCR, capture and recording results.

## Available workflows in the current source

| Area | Controls and behavior |
| --- | --- |
| Capture | Rectangle, freeform, window, monitor, all monitors and scrolling capture; configurable delay, original-capture saving and clipboard copy. Partial scrolling results require a Keep/Discard choice. |
| Image editing | Open/Paste, multiple editor windows, Select and Move view, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse and emoji. Selected-object properties, resize/move, annotation erasing, crop, source-area removal, Undo and Redo. |
| Editable text | On-canvas text drafts, font family, image-pixel size, color, bold, italic and underline. Apply/Cancel retains a clear draft boundary. |
| OCR and translation | Local Windows OCR, original recognition output, selectable rows, source-style conversion and recognized-word selection. Conversion selects Text. Translation offers local processing or an explicitly chosen HTTPS server, with reviewed rows before applying. Font recovery remains approximate; review identifiers, leading zeros and duplicate rows. |
| View | Fit, zoom presets, pan, full screen and toolbar overflow. Resize to snip is enabled by default with a Settings choice. Source sizing preserves manual window sizing and clamps automatic sizing/minimums to the work area. |
| Recording and video | Camera and video modes share New/Ctrl+N. In video mode, choose a rectangle or window, adjust the area and audio, then select Start. The recording bar keeps Stop available and positions the controls outside the recorded area when space permits. Stop and save opens the MP4 for review, trimming or export. Open video is also available from the file menu. |
| Files and Windows | Flattened PNG/JPEG/BMP/PDF Save As, image/text clipboard actions, Windows Share, printing, recent snips/recordings, external-editor working copies, keyboard shortcuts and Help. Settings offer tray, startup and global-shortcut choices. |
| Recovery and release information | Cancellation and failure feedback, dirty-document prompts, atomic image/video-copy writes and recoverable preferences. Update checks read the original repository and offer the verified release URL for a browser download. |

The main editor, recording/video review and settings use WinUI controls. Temporary capture selectors, scrolling controls and recording boundaries still use dedicated WinForms STA overlays. The renderer retains Windows GDI+/RichEdit dependencies. This is a Windows migration with an explicit hybrid boundary; it is not an all-WinUI rewrite.

The final bundle excludes six unused upstream shell benchmark utilities. All 107 retained model/runtime/license hashes and model definitions are unchanged. The backend keeps strict manifest, file-count, signature, hash, path and lease checks; translation passed after repinning. No shell utilities are shipped or were executed. The earlier 745-file candidate is held and superseded.

## Using Snip Studio

Use a signed package with its validation and hash manifest. For a portable build, extract the complete folder and open `Snip Studio.exe` with the adjacent runtime, resources, translation data and notices. The application and Setup carry timestamped Automation Forge signatures. Bundled third-party binaries with publisher signatures retain those signatures.

Snip Studio stores preferences at `%LocalAppData%\SnipStudio\preferences.json`. Legacy `preferences.xml` is kept for recovery and Settings import. Tray behavior, sign-in startup and global shortcuts are available in Settings. Use Save editable snip to reopen the image and continue editing its annotations. PNG, JPEG, BMP and PDF exports flatten the image.

Images, OCR, drawing and recording are processed locally. A configured translation server receives selected text when you choose Translate. Update checks contact GitHub. Settings controls automatic checks. Startup diagnostics record fixed stages, exception types, error codes and method names.

The signed local package includes `VALIDATION.md` for the exact tests, hardware and live workflow coverage, and installation status of that candidate.

## Source and development

- [Migration design and remaining work](MIGRATION.md)
- [Snip Studio 0.7.0 release notes](release-notes-0.7.0.md)
- [Preview.4 release notes](release-notes-0.7.0-preview.4.md)
- [Native WinUI frontend](src/SnipStudio.WinUI)
- [Windows backend adapters](src/SnipStudio.Backend)
- [Backend checks](tests/SnipStudio.Backend.Checks), [viewport checks](tests/SnipStudio.Viewport.Checks), [migration checks](tests/SnipStudio.Migration.Checks)
- [Historical preview.3 notes](release-notes-0.7.0-preview.3.md), [preview.2 notes](release-notes-0.7.0-preview.2.md), [preview.1 notes](release-notes-0.7.0-preview.1.md) and [stable 0.6.24 notes](release-notes-0.6.24.md)

The pinned toolchain is .NET SDK 10.0.404, Windows App SDK 2.7.0 and Windows SDK BuildTools 10.0.28000.2957. The x64 application targets `net10.0-windows10.0.26100.0` and publishes as an unpackaged, self-contained folder. Build/test/signing steps must preserve the signature requirements; never substitute `dotnet run` or an unsigned helper for the verified workflow. Signing credentials and private verification outputs are not included in this repository.

An independent Automation Forge app; not affiliated with Microsoft. No open-source license has been added to this repository. Retain the supplied third-party notices and original publisher attributions when distributing a verified package.
