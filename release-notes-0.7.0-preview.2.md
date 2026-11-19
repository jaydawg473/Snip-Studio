# Snip Studio 0.7.0-preview.2

November 19, 2026. Application file version `0.7.0.2`. **Production release status: INSUFFICIENT EVIDENCE; native acceptance is partial and required device/workflow checks remain incomplete.** This source update extends the WinUI migration beyond preview.1's initial image-editor slice. It does not replace the stable Snip Studio installation.

## Implemented in this preview

- Native WinUI capture commands for rectangle, freeform, window, monitor, all monitors and scrolling capture, with delay, cancellation, original-save/clipboard choices and partial scrolling-result review.
- Editing tools for text, highlighter text snapping, line/arrow/ellipse, emoji, object selection/properties/move/resize, eraser, crop and source-area removal, alongside Undo/Redo and the existing pen/rectangle tools.
- Local OCR review with raw/recovery output and selectable rows, reviewed conversion with Text selected afterward, recognized-word selection and copy, plus local or explicitly configured-server translation. Source font matching remains approximate; technical strings are not silently corrected.
- Native WinUI recording/video window with region/window/display choice, microphone and system-audio devices, pointer/FPS controls, recording boundaries, Stop and save, playback, trim, original-copy saving and smaller MP4 export.
- Preview-specific Settings, recent files, image/text clipboard actions, Share/Print, trusted external-editor copies, new windows, full screen, Help and keyboard shortcuts. Optional tray, startup and global shortcuts use separate preview identity/settings.
- Manual release-information checks for this migration repository, bounded failure feedback, settings recovery and atomic file-copy/save behavior. No automatic installer download or execution.

The main editor, settings and recording/review interface are native WinUI. Capture/scrolling and recording-boundary overlays remain owned WinForms STA components around the existing Windows implementations. GDI+/RichEdit and Windows media dependencies remain part of the backend.

## Verification

The current signed suites passed **1,673 unique component assertions**: 77 backend, 1,335 viewport and 261 migration. A real settings-reader race was fixed by acquiring the existing bounded writer mutex before initial loading in all five public disk paths. The unchanged workload now passes 64/64 writes and 128/128 actual public `Load`/`RecentPaths` samples without errors. These were real store calls, not raw file readers. Settings backup recovery, draft preservation and a bounded 310 ms transient-conflict retry retain atomic replacement without a delete/move fallback. A held-backup native replacement failure is separately expected and checked for preservation/retry. The proposed global text-cache disposal diagnosis was retracted after public lifecycle checks passed.

Nine manifest-task structural assertions and the registration-priority fixture passed separately. Failed/partial runs, earlier totals and overlapping recovery checks are not added to the current component total.

Separately, seven real local German-to-English translation assertions passed, preserving protected strings, leading zeros, duplicate and blank rows. The ten-segment batch took 953 ms, 1.22 seconds overall. Another 47 generated-MP4 adapter assertions passed inspection, frame decoding, copy, trim, export and failure/cancellation checks in 1.07 seconds. Positive smaller-export used explicit artificial free-box padding; it does not show better compression of the already compact original. Those earlier smoke runs exercised unchanged media/translation source. Actual recording, device audio, native playback and other translation directions remain unverified; the smoke counts are separate from 1,673.

Real Windows OCR recovered six rows from the supplied filename snip but none of the six strings matched exactly. Selection indices, unchanged conversion order and Undo/Redo passed. Unchanged pixels measured 47 character edits over 271 reference characters (17.34%); none of 16 alternative preparations improved it. Stable 0.6.24 returned the same six native strings. The production recognizer remains unchanged: review labels text unverified, preserves it verbatim and defaults to Close. No guessed identifier correction is included.

One later native sequence on a signed candidate observed Open, six-row OCR review, conversion selecting Text, character drag-selection, exact typed editing on Apply, Undo/Redo and PNG Save As. The saved image was 301 × 169, 7,761 bytes, and the supplied input remained unchanged. Window reduction from 1086 × 754 to 713 × 493 retained usable toolbar/status overflow. A fresh instance passed F11 full-screen/restore before dialogs. The old instance's Ctrl+O/Ctrl+S/Ctrl+0/F11 routing after picker/system-sizing remains inconclusive; no general keyboard pass or global registration failure is claimed.

That observation exposed a clipped conversion label and excessive font-size decimals. The updated frontend passed native retesting: Convert text is fully visible and selects Text; a precise recovered size displays as 11.33, retains its exact value through focus/Tab, and accepts an explicit 12.5 edit. These are scoped UX passes; source-font recovery remains approximate.

The current controlled 4096 × 2160 colored-panel check measured 21.37 ms to open, 79.44 ms for the first preview, 83.18 ms mean over eight edit/preview operations and 213.51 ms for atomic PNG save. These are individual check-process measurements, not startup times or evidence of a speedup. See [verification scope and memory samples](VERIFICATION-WINUI.md).

## Remaining gates and limits

- An intermediate preview.2 build/publish succeeded. A delivered candidate's included validation/source/signature receipts and hash manifest identify its exact build/package audit outcomes; these source notes do not predeclare them. Preview.1 artifacts/results remain historical.
- Native evidence is partial, with a successful retest of the latest label/font-display changes. Post-dialog shortcut routing, real capture/scrolling, recording/audio/video, clipboard/printing/Shell and physical DPI/monitor workflows remain unverified.
- Recording has Start/Stop and save; Pause/Resume is not implemented. Codec/device compatibility and HDR depend on the Windows environment and require actual checks.
- Image/PDF saves are flattened exports. Editable annotation objects and Undo history remain in the open editor.
- No installation, stable-channel replacement or public GitHub Release has been performed. Every executed or delivered binary must pass valid timestamped signature checks; there is no unsigned fallback.

Use a complete signed candidate with matching verification receipts, keeping its runtime, compiled resources, translation files and third-party notices together. A verified package remains a candidate while required native/device acceptance is incomplete. Source coverage and remaining acceptance are described in [MIGRATION.md](MIGRATION.md) and [VERIFICATION-WINUI.md](VERIFICATION-WINUI.md).
