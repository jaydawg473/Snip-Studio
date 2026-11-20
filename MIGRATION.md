# Snip Studio WinUI 3 migration

Updated November 20, 2026 for `0.7.0-preview.4` (`0.7.0.4`). Current UI fixes are source changes awaiting build and native verification. Production release status remains **INSUFFICIENT EVIDENCE**; earlier preview results are historical.

This repository preserves the Git history of [stable Snip Studio](https://github.com/jaydawg473/Snip-Studio), starting at 0.6.24 commit `f7e4e778f2af7c73c22eec94c46a4b70180e68cd`. GitHub's self-fork request returned the original repository, so the migration has a separate repository rather than a fork-network entry. Preview identity, settings, startup entry, global shortcuts and update metadata are deliberately isolated from the stable application.

## Preview.4 reachability fixes

The compact native capture row uses aligned controls and icons for capture menu entries, retaining the chosen layout and newer icons. Source minimums are 520 × 260 DIP for the empty launcher, 520 × 320 DIP for an editor with an image, and 640 × 480 DIP for recording; they scale with the display and are clamped to its available work area.

Opening Settings temporarily requests at least 680 × 640 DIP when possible, with vertically scrollable content and bounded dialog height. Closing restores the prior frame when the user has not resized it; an intentional resize is retained, and a move retains the chosen position. These are source contracts, not observed native results. Current build and minimum/Settings retests are pending.

## Preview.3 compact editor history

Preview.3 restores the previous layout, spacing and tool placement while retaining the new icons. The source uses two 54/46-DIP toolbar rows, removes the large header, retains the blue New button, groups shape choices and puts zoom presets at the bottom. Contextual options and the canvas keep the established arrangement. Existing editing, OCR, capture and recording adapters remain the architecture; this change does not replace the OCR engine or establish a performance improvement.

A signed reference-layout candidate rendered the main controls without a clipped New label or extra capture overflow, automatically shrank around the supplied 301 × 169 image with visually balanced canvas margins, exposed readable Text options and accepted 125% zoom. No precise outer-window measurement is claimed. Subsequent 100%/Ctrl+0 attempts were inconclusive because the desktop tool repeatedly observed another foreground application; no successful result or application regression is inferred from them.

Resize to snip defaults on and has a Settings toggle. Source sizing retains a user's manual size per window and clamps automatic sizing to the monitor work area. Native testing exposed a clipped empty-launcher hint and Fit dropping from 100% to 76.3% when Text options appeared. The smaller empty layout and a 40-DIP options reserve are implemented; final signed retesting is pending. The earlier preview.2 tests and observations below remain historical and are not new preview.3 results.

## Workflow scope

The first preview supplied a small image-editor slice; preview.2 connected the broader workflows retained in the current source. Native observations in this table apply to preview.2 only:

| Surface | Current implementation | Remaining acceptance |
| --- | --- | --- |
| Main editor | Native WinUI commands, overflow menus, context tools, image viewport, text draft editor, Help and status/cancellation feedback. One native Open/resize/Save As sequence observed. | Fresh-instance shortcut routing, broader pointer workflows, clipboard, text scale and physical DPI/monitor behavior. |
| Editing backend | Existing document/rendering engine behind public session/snapshot APIs; drawing, shapes, emoji, selection, move/resize, text, eraser, crop/remove area, history and atomic export. Native text Apply/Undo/Redo observed. | Complete native tool/failure-recovery workflows beyond that sequence. |
| OCR and translation | Windows OCR adapters, raw output/recovery rows, original row indices, reviewed source-style conversion, word selection, local translation and explicit configured-server review. Native six-row review, visible Convert text action, Text activation, character drag-selection, exact editing and font-size value/display round trips observed. | Recognition accuracy remains limited; broader visual font/color/position matching, OCR-band copy and translation dialogs remain. |
| Capture | Rectangle/freeform/window/monitor/all-monitor and scrolling coordinators, delay, cancellation, HDR option and partial-capture review. | Real pixels, scrolling controls, capture exclusion, HDR and device-specific failures. |
| Recording and review | Native WinUI area/audio choices, Start/Stop, local playback, trim, copy and MP4 export; recording-boundary overlay. | Real video/audio, endpoint changes, output playback/codec correctness, capture exclusion and stop/recovery behavior. Pause/Resume remains unavailable. |
| Desktop integration | Preview preferences/recent history, Share/Print, trusted external-editor copies, optional tray/startup/global shortcuts and manual migration-repository release metadata. | Real Windows Shell, printers, selected external editors, restart, shortcuts/tray, startup, network failures and installed acceptance. |

Being reachable in source is not equivalent to successful native acceptance. [VERIFICATION-WINUI.md](VERIFICATION-WINUI.md) records exercised checks and the remaining gates.

## Architecture and ownership

`src/SnipStudio.WinUI` owns the native main editor, viewport, settings/dialogs and recording/video window. `src/SnipStudio.Backend` adapts the reviewed document, shape, emoji, rich-text, OCR, capture, audio, recording, video and translation implementations. Linked legacy source remains in the repository where it supplies working behavior; it does not imply that a legacy screen is the new frontend.

The renderer depends on Windows Desktop/GDI+ and native RichEdit. Document/render/text operations remain on their owning STA. Detached encoding and bounded filesystem/video work run away from the WinUI dispatcher. Sessions, immutable snapshots, revision checks and explicit ownership protect pending saves or selections from applying to a different document. An image opened from disk starts clean; a pasted capture starts unsaved. A completed save marks only its captured revision as saved.

Capture selectors and scrolling controls use a dedicated STA with a real WinForms message loop. Recording boundaries likewise use an owned STA overlay around the existing boundary implementation, with capture-exclusion capability feedback. This hybrid boundary is intentional for this preview. Its existence does not establish that native capture, HDR, audio or mixed-monitor operation has been tested.

The WinUI frontend exposes text drafts and reviewed OCR replacement rather than guessing source corrections. Identifiers, leading zeros, case, quantities and duplicate rows must survive unchanged unless the user edits them. Source font reconstruction is an estimate. OCR review labels recognition unverified and defaults to Close. One supplied dense filename fixture has a confirmed accuracy limit shared with stable 0.6.24; tested pixel preparations did not improve it, and the recognizer remains unchanged. Translation keeps source rows for review and requires one reviewed replacement per source row before applying.

Image/PDF exports are flattened. Editable annotation objects and history are session state, not a new project-file format. Video-copy saving preserves the original extension and uses a temporary file plus replacement; actual video decoding/encoding is a separate acceptance concern.

## Preview integration and privacy

Preferences use the separate preview profile. Settings reads and writes coordinate on the existing bounded named mutex before initial disk loading; writes merge changed fields/recent paths, preserve unknown JSON and disclose backup/default recovery. The preview.2 concurrent public API regression passed 64 writes and 128 reads without errors; the preview.3 component suite also passed concurrent settings validation. Persistent native replacement conflicts fail with data preserved, with a bounded retry for transient conflicts. Startup changes occur when Settings is saved. Tray and global shortcuts are optional; shortcut conflicts produce feedback. Native restart and Shell actions remain unverified.

The manual update service queries only this WinUI repository's release endpoint, bounds the response and timeout, rejects redirected or out-of-repository release links, and displays reported metadata for review. A different tag alone is not treated as proof of a newer signed release. The service has no installer-download or execution path.

Local image/OCR/recording work does not require upload. Configured translation discloses the destination and sends selected text only on an explicit Translate action; its API key is dialog-local. External-editor execution and the local translation runtime have signature/hash trust gates. A signing failure must block execution rather than trigger an unsigned fallback. Native privacy/security acceptance remains outstanding.

## Build and distribution

The repository pins .NET SDK 10.0.404, Microsoft.WindowsAppSDK 2.7.0 and Microsoft.Windows.SDK.BuildTools 10.0.28000.2957. The application targets `net10.0-windows10.0.26100.0`, declares a platform floor of `10.0.19041.0`, and uses an x64 unpackaged, self-contained, multiple-file deployment. Those declarations are not device compatibility results.

NuGet restore requires package signatures. Authenticode validation separately covers executed tools/helpers and delivered executable dependencies: Automation Forge signs our outputs with timestamps; valid original third-party signatures remain intact. A precompiled, signed manifest build task avoids execution of an unsigned generated inline task. Compiled applications are not executed before signing.

The publish target explicitly requires the application's PRI and compiled App/MainWindow/EditorViewport/RecordingWindow XAML resources. The first preview's missing-resource launch failure established why dependency/PE checks alone do not cover application resource closure. Each preview requires its own build, signature, resource, notice, privacy and archive audit; earlier artifacts do not satisfy current gates.

Installation, stable-channel replacement and public GitHub Release publication require separate authorization and are not performed by this preview's update check. Source commits and packages must identify their exact source freeze and verification scope.

## Remaining work

Build the current preview.4 source and verify a signed candidate. Native checks must cover capture-row alignment/menu access, launcher/editor/recording minimum tracking, Settings scrolling and Save/Cancel restoration, deliberate resizing/movement, and small/mixed-DPI work areas. Prior component/native/package evidence remains version-scoped in [VERIFICATION-WINUI.md](VERIFICATION-WINUI.md).

Package-specific source/signature/hash and audit evidence belongs with the delivered candidate. Required broader native/device checks remain: Fit/manual zoom, mixed DPI, real capture/scrolling, recording/audio/video, clipboard/print/share, preferences/recovery, Shell/startup and installation. Measure startup, real capture/recording and longer-running memory with stated inputs before claiming a performance improvement. Component timings, compilation, one native sequence and package verification do not close all acceptance gates.
