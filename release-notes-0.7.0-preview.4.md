# Snip Studio 0.7.0-preview.4

November 20, 2026. File version `0.7.0.4`. **Source candidate; build and native verification pending. Production release status: INSUFFICIENT EVIDENCE.**

- Open Record in a compact recording setup. Keep Start/Stop reachable and show playback, trim and export after a video is available.
- Refine the compact native capture row with aligned buttons and icons throughout capture menus, keeping the selected layout and newer icons.
- Request minimum window sizes of 520 × 260 DIP for the launcher, 520 × 320 DIP for an image editor and 640 × 480 DIP for recording. Source converts these for display scale and clamps them to the available work area.
- Temporarily enlarge the Settings host toward 680 × 640 DIP, keep its content vertically scrollable and actions within the dialog bounds. On closing, source restores the earlier frame unless the user resized; a deliberate move retains the new position.

These describe implemented source behavior, not successful native minimum-size or Settings tests. Current compilation, signed checks, native Save/Cancel/scroll/restore checks and package audits remain pending. [Preview.3 evidence](release-notes-0.7.0-preview.3.md) is historical and is not counted as preview.4 validation.

No OCR accuracy or performance improvement is claimed. Raw recognition and identifiers remain unverified/reviewable without guessed corrections. The main UI uses WinUI while capture overlays and rendering retain their existing WinForms/GDI+/RichEdit boundary. Real capture/recording/audio, physical monitor/DPI and installation acceptance remain incomplete.

Use only a candidate with matching validation/source/signature/hash receipts: Automation Forge signatures for our executables and original publisher signatures for third-party binaries, all valid and timestamped. Installation and public release remain separate actions. See [current verification](VERIFICATION-WINUI.md).
