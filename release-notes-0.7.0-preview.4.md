# Snip Studio 0.7.0-preview.4

November 20, 2026. File version `0.7.0.4`. **Source candidate; build and native verification pending. Production release status: INSUFFICIENT EVIDENCE.**

- Open Record in a compact recording setup. Keep Start/Stop reachable and show playback, trim and export after a video is available.
- Refine the compact native capture row with aligned buttons and icons throughout capture menus, keeping the selected layout and newer icons.
- Make recording follow video mode → New/Ctrl+N → Rectangle/Window selection. Cancel preserves the image/draft. The replacement capture bar targets 460 × 128 DIP, with inline Start or Stop, timer, microphone/system-audio boxes, Options and small area/status rows; no large heading, instructions, Open video or setup form appears in capture mode.
- Put one left-aligned advanced form in an Options flyout without growing the parent. Use native caption close. Start prepares a unique configured-folder MP4 and minimizes; reopen from the taskbar to Stop and review. Open video remains separate.
- Request minimums of 520 × 260 DIP for the launcher, 520 × 320 DIP for an image editor and 640 × 480 DIP for review. Capture warning details are bounded at 96 DIP with a temporary 260-DIP floor. Source sizes scale with the display and clamp to the work area.
- Temporarily enlarge the Settings host toward 680 × 640 DIP, keep its content vertically scrollable and actions within the dialog bounds. On closing, source restores the earlier frame unless the user resized; a deliberate move retains the new position.

These describe source behavior, not completed clean-bar/minimum/Settings tests. The user reported the previous controls worked but rejected the large form. This new bar still needs its build, signed checks, native workflows and package audit. The prior D51 candidate's 1,673/7/47 checks and static closure remain provisional; [preview.3 evidence](release-notes-0.7.0-preview.3.md) is historical. Neither validates the replacement layout or recording/audio hardware.

No OCR accuracy or performance improvement is claimed. Raw recognition and identifiers remain unverified/reviewable without guessed corrections. The main UI uses WinUI while capture overlays and rendering retain their existing WinForms/GDI+/RichEdit boundary. Real capture/recording/audio, physical monitor/DPI and installation acceptance remain incomplete.

Use only a candidate with matching validation/source/signature/hash receipts: Automation Forge signatures for our executables and original publisher signatures for third-party binaries, all valid and timestamped. Installation and public release remain separate actions. See [current verification](VERIFICATION-WINUI.md).
