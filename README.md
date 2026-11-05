<img src="assets/app-256.png" width="72" alt="Snip Studio icon">

# Snip Studio

A Windows screenshot and screen-recording workspace by [Automation Forge](https://automation-forge.com/).

Capture across monitors, annotate images, work with recognized text, and export the result locally. The app starts compact and can resize around a new snip.

## Features

- Rectangle, freeform, window, and full-desktop screenshots, including selections across monitors.
- Movable pen strokes, highlights, arrows, boxes, and text notes, with Undo and Redo.
- Arrow tip and tail handles for rotation and length changes.
- Editable text notes with font-size controls. Drag a note's side handles to change its width; text wraps and its height grows.
- Local Windows OCR. Copy recognized text, edit one recognized line, or delete a selected range. OCR edits support Undo.
- PNG, JPEG, BMP, and image-backed PDF export; clipboard copy, print, and Windows Share.
- Screen recording with selectable microphone and system-audio sources, local MP4 review, trimming, and smaller-video exports.
- Smaller-copy presets and target-size options for email or chat attachments. The original recording is preserved.
- Optional taskbar hiding for screenshots and video. The main monitor's taskbar can remain visible.
- A themed color palette with swatches, recent ink colors, and hex input.
- Tooltips on by default, with a switch in Settings.
- Installed-editor discovery and a working-copy workflow for editors such as Photoshop or Paint.

## Install or use a portable release

Use an official release installer when one is available. Installation is per Windows account; the desktop shortcut is optional. Setup does not launch the app or register Windows startup automatically.

For a portable release, extract its ZIP and run `Snip Studio.exe`. Keep `Snip Studio.exe.config` beside it.

Requires Windows 10 or 11 and .NET Framework 4.8. No account is required for the app.

## Build from source

Run in PowerShell on Windows:

```powershell
.\build.ps1 -UseExistingIcons
```

The application is written in C# 5 for .NET Framework 4.8. The build uses the installed Windows Framework compiler and Windows WinRT metadata; no NuGet packages are needed. Output is written to `dist` by default. A local build is unsigned.

The checked-in icon assets are sufficient for a normal build. Omitting `-UseExistingIcons` compiles and runs the icon-generation tool from `tools/BuildIcons.cs`.

Private signing credentials and generated development data are not part of this repository. The source build script only builds the application; release verification and signing are separate steps.

The `installer` folder contains NSIS packaging sources for the official publisher release. Its README describes the expected release payload and compiler configuration.

## Useful workflows

- **Edit recognized text:** turn on Text actions, select words within one line, then choose **Edit line**. The dialog starts with those words selected. Apply the change to replace the line on the snip. Use **Delete selection** for removal, or Undo to restore the previous content.
- **Reshape a text note:** select it with Move and drag a side handle. Double-click it to edit the wording, size, width, or ink color. The toolbar also offers text size.
- **Rotate an arrow:** select it with Move, then drag its tip or tail handle.
- **Save a PDF:** choose **More → Save as PDF**, or select PDF in Save as.
- **Make a recording smaller:** open its review window and choose **Make smaller**. Pick a preset or target file size, then save a separate MP4.
- **Use an external editor:** choose the application in Settings, select **Edit in application**, save the working copy in that editor, then choose **Reload external edit** in Snip Studio.

Original screenshots can save automatically to the folder selected in Settings. Annotations are edits; choose Save to keep them. Closing the last window exits by default. Optional notification-area mode makes Close hide the window instead.

## Limits and behavior

- Screenshot files retain the captured pixel dimensions. A scaled preview can look softer; use Actual size (100%) and PNG to judge lossless screenshot output.
- OCR can misread text. Screenshot pixels do not identify their original font or background reliably. Line replacement uses an estimated font and a selected solid background fill. Ambiguous overlaps with editable text notes are guarded rather than silently rewritten.
- PDFs preserve the rendered snip as a lossless image. They are not searchable text documents or editable PDF forms. Transparent areas become white.
- Recording is currently up to 1920 × 1080 at 15 fps, with larger regions scaled proportionally. Smaller exports trade detail for file size. Target sizes are estimates, and attachment limits depend on the receiving service; actual output size is reported.
- Image exports are flattened. Annotations remain editable in the open app window.
- Window screenshots capture visible desktop content, including anything covering the selected window. Protected content can appear black.
- HDR correction is experimental; live HDR color fidelity is not established.
- Scrolling capture is not included.

## Privacy

Editing, OCR, recording, PDF export, and video compression run locally. Sharing, opening an external editor, and visiting the website happen when selected by the user.

Preferences and working copies are stored in the app's local Windows application-data folder. Uninstalling preserves screenshots, recordings, preferences, and working copies.

An independent Automation Forge app; not affiliated with Microsoft. No open-source license has been added to this repository.
