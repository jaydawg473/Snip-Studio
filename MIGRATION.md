# Snip Studio WinUI 3 migration

Updated November 19, 2026 for `0.7.0-preview.2` (`0.7.0.2`). The workflow surfaces below are implemented. Migration acceptance remains in progress; production release status is **INSUFFICIENT EVIDENCE** until required native/device checks are completed.

This repository preserves the Git history of [stable Snip Studio](https://github.com/jaydawg473/Snip-Studio), starting at 0.6.24 commit `f7e4e778f2af7c73c22eec94c46a4b70180e68cd`. GitHub's self-fork request returned the original repository, so the migration has a separate repository rather than a fork-network entry. Preview identity, settings, startup entry, global shortcuts and update metadata are deliberately isolated from the stable application.

## Implemented scope

The first preview supplied a small image-editor slice. Preview.2 now connects the broader application workflows in source:

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

Preferences use the separate preview profile. Settings reads and writes coordinate on the existing bounded named mutex before initial disk loading; writes merge changed fields/recent paths, preserve unknown JSON and disclose backup/default recovery. The concurrent public API regression passed 64 writes and 128 reads without errors. Persistent native replacement conflicts fail with data preserved, with a bounded retry for transient conflicts. Startup changes occur when Settings is saved. Tray and global shortcuts are optional; shortcut conflicts produce feedback. Native restart and Shell actions remain unverified.

The manual update service queries only this WinUI repository's release endpoint, bounds the response and timeout, rejects redirected or out-of-repository release links, and displays reported metadata for review. A different tag alone is not treated as proof of a newer signed release. The service has no installer-download or execution path.

Local image/OCR/recording work does not require upload. Configured translation discloses the destination and sends selected text only on an explicit Translate action; its API key is dialog-local. External-editor execution and the local translation runtime have signature/hash trust gates. A signing failure must block execution rather than trigger an unsigned fallback. Native privacy/security acceptance remains outstanding.

## Build and distribution

The repository pins .NET SDK 10.0.404, Microsoft.WindowsAppSDK 2.7.0 and Microsoft.Windows.SDK.BuildTools 10.0.28000.2957. The application targets `net10.0-windows10.0.26100.0`, declares a platform floor of `10.0.19041.0`, and uses an x64 unpackaged, self-contained, multiple-file deployment. Those declarations are not device compatibility results.

NuGet restore requires package signatures. Authenticode validation separately covers executed tools/helpers and delivered executable dependencies: Automation Forge signs our outputs with timestamps; valid original third-party signatures remain intact. A precompiled, signed manifest build task avoids execution of an unsigned generated inline task. Compiled applications are not executed before signing.

The publish target explicitly requires the application's PRI and compiled App/MainWindow/EditorViewport/RecordingWindow XAML resources. The first preview's missing-resource launch failure established why dependency/PE checks alone do not cover application resource closure. Preview.2 needs its own build, signature, resource, notice, privacy and archive audit; historical preview.1 artifacts do not satisfy those gates.

Installation, stable-channel replacement and public GitHub Release publication require separate authorization and are not performed by this preview's update check. Source commits and packages must identify their exact source freeze and verification scope.

## Remaining work

Signed suites passed 1,673 unique component assertions; manifest-task, local translation and generated-media results are documented separately. Native evidence establishes one supplied-image Open/OCR/Text/edit/history/Save As sequence and reduction from 1086 × 754 to 713 × 493 with usable toolbar/status overflow. A fresh instance passed F11 to 1920 × 1080 full screen and back before dialogs. The old instance's Ctrl+O/Ctrl+S/Ctrl+0/F11 results after picker/system-sizing interactions remain inconclusive; a global registration failure is not established.

The shortened Convert text label is now natively confirmed fully visible, and conversion activates Text. The two-decimal size display was retested: the precise recovered value displayed as 11.33, survived focus/Tab unchanged, and accepted an explicit 12.5 edit. These are scoped passes for the current UX changes, not a general keyboard or source-font accuracy claim.

Package-specific source/signature/hash and audit evidence belongs with the delivered candidate. Required broader native/device checks remain: Fit/manual zoom, mixed DPI, real capture/scrolling, recording/audio/video, clipboard/print/share, preferences/recovery, Shell/startup and installation. Measure startup, real capture/recording and longer-running memory with stated inputs before claiming a performance improvement. Component timings, compilation, one native sequence and package verification do not close all acceptance gates.
