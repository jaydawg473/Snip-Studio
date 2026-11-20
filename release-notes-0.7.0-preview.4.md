# Snip Studio 0.7.0-preview.4

November 20, 2026. File version `0.7.0.4`. **Source candidate; build and native verification pending. Production release status: INSUFFICIENT EVIDENCE.**

- Open Record in a compact recording setup. Keep Start/Stop reachable and show playback, trim and export after a video is available.
- Refine the compact native capture row with aligned buttons and icons throughout capture menus, keeping the selected layout and newer icons.
- Make recording follow the snip sequence: select video mode, choose Rectangle/Window, then New or Ctrl+N to select an area. Cancel preserves the image/draft and opens no recorder. After selection, compact microphone/system-audio/Start/options controls appear; Start generates a unique MP4 in the configured recording folder, and Stop opens review. Open video remains separate.
- Request minimum window sizes of 520 × 260 DIP for the launcher, 520 × 320 DIP for an image editor, 640 × 260 DIP for compact recording and 640 × 480 DIP for advanced recording/review. Source converts these for display scale and clamps them to the available work area.
- Temporarily enlarge the Settings host toward 680 × 640 DIP, keep its content vertically scrollable and actions within the dialog bounds. On closing, source restores the earlier frame unless the user resized; a deliberate move retains the new position.

These describe source behavior, not completed native recording/minimum-size/Settings tests. The new recording route still needs its final build, signed checks, native workflows and package audit. Earlier provisional preview.4 checks predate it; [preview.3 evidence](release-notes-0.7.0-preview.3.md) is historical. Neither is counted as final validation of this route.

No OCR accuracy or performance improvement is claimed. Raw recognition and identifiers remain unverified/reviewable without guessed corrections. The main UI uses WinUI while capture overlays and rendering retain their existing WinForms/GDI+/RichEdit boundary. Real capture/recording/audio, physical monitor/DPI and installation acceptance remain incomplete.

Use only a candidate with matching validation/source/signature/hash receipts: Automation Forge signatures for our executables and original publisher signatures for third-party binaries, all valid and timestamped. Installation and public release remain separate actions. See [current verification](VERIFICATION-WINUI.md).
