# Snip Studio 0.7.0-preview.4

November 20, 2026. File version `0.7.0.4`. **Build/publish, signed checks and static installer/ZIP verification passed. Full native/device and installation acceptance remain incomplete. Production release status: INSUFFICIENT EVIDENCE.**

- Refine the compact capture row with a 58-DIP-wide selected-shape icon dropdown, menu icons and a simple mode hint. Image-only actions/zoom appear after loading an image; screenshot delay is disabled in video mode.
- Make recording follow video mode → New/Ctrl+N → Rectangle/Window selection. Cancel preserves the image/draft. The icon bar targets 460 × 128 DIP with Start or Stop, timer, microphone/system-audio icon toggles and Options; no large heading, instructions, Open video or setup form appears in capture mode.
- Put one left-aligned advanced form in an Options flyout without growing the parent. Use native caption close. Start prepares a unique configured-folder MP4 and minimizes; reopen from the taskbar to Stop and review. Open video remains separate.
- Request minimums of 520 × 260 DIP for the launcher, 520 × 320 DIP for an image editor and 640 × 480 DIP for review. Capture warning details are bounded at 96 DIP with a temporary 260-DIP floor. Source sizes scale with the display and clamp to the work area.
- Temporarily enlarge the Settings host toward 680 × 640 DIP, keep its content vertically scrollable and actions within the dialog bounds. On closing, source restores the earlier frame unless the user resized; a deliberate move retains the new position.

Current build/publish passed. The signed backend passed 1,673 component assertions, 7 local translation checks and 47 generated-video checks. Final static reconciliation passed for 739 exact payload files and all 583 timestamp-valid PE files. Installer/portable paths and bytes match; the complete signed embedded uninstaller matches its hook capture, and NSIS CRC/protected bytes remained valid after signing. Generated-video export used a labeled padded fixture; it does not prove better compression of the compact original or audible audio correctness.

Native evidence is partial on the preceding code9a5f7bc UI candidate; final c642955 binaries were not launched while the desktop was in use. UI source is unchanged by the bundle cleanup. The earlier verified signed launcher reopened and showed a native file chooser, but tool filename entry failed and a later refresh showed another app. Inputs stopped while the user continued desktop work. Complete file-open, icon-bar/flyout, Start/Stop/audio/review and Settings/minimum acceptance remain unverified. Setup and Uninstall were not run; no installation or public GitHub Release was performed. Prior candidates and [preview.3 evidence](release-notes-0.7.0-preview.3.md) remain historical.

Signed Setup and portable ZIP are local under `artifacts/release-0.7.0-preview.4`; exact artifact hashes, sizes and audit binding are in [verification](VERIFICATION-WINUI.md). Git source publication is a separate step, not implied by build/publish or package success.

The final bundle excludes six unused upstream shell benchmark utilities. All 107 retained model/runtime/license hashes and model definitions are unchanged. The backend keeps strict manifest, file-count, signature, hash, path and lease checks; translation passed after repinning. No shell utilities are shipped or were executed. The earlier 745-file candidate is held and superseded.

Microsoft reference review combined bounded static archive inventory with limited visual/runtime comparisons. It does not establish deep native-code analysis, complete internal equivalence or unobserved features. No copied Microsoft implementation or performance guarantee is claimed.

No OCR accuracy or performance improvement is claimed. Raw recognition and identifiers remain unverified/reviewable without guessed corrections. The main UI uses WinUI while capture overlays and rendering retain their existing WinForms/GDI+/RichEdit boundary. Real capture/recording/audio, physical monitor/DPI and installation acceptance remain incomplete.

Use only a candidate with matching validation/source/signature/hash receipts: Automation Forge signatures for our executables and original publisher signatures for third-party binaries, all valid and timestamped. Installation and public release remain separate actions. See [current verification](VERIFICATION-WINUI.md).
