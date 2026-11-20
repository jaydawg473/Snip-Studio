# Snip Studio 0.7.0-preview.3

November 19, 2026. Application file version `0.7.0.3`. **Production release status: INSUFFICIENT EVIDENCE. Clean build/publication and static installer/ZIP checks passed; native/device/installation acceptance remains incomplete.**

This update restores Snip Studio's previous compact layout, spacing and tool placement while keeping the new icons. Two 54/46-DIP rows replace the large header, with the blue New button, grouped Shapes, contextual controls, canvas and bottom zoom presets. Product identity and existing capture, editing, text, saving and recording workflows remain the scope.

## Scope

- Restore the established compact toolbar/canvas/status arrangement and reachable actions at smaller window sizes; retain the newer icons.
- Add Resize to snip as an enabled-by-default Settings choice. Source sizing preserves manual window sizes and clamps automatic sizing to the monitor work area.
- Retain existing OCR review, Text activation after conversion, precise font-size handling, editing/history and atomic save behavior.
- Keep the WinUI main editor/settings/video interface and existing Windows backend adapters. Capture/scrolling/recording-boundary overlays and GDI+/RichEdit dependencies remain; this is not an all-WinUI rewrite.

No OCR accuracy improvement is claimed. The earlier supplied dense filename snip recovered six rows but 0/6 exact strings, with the same native output as stable 0.6.24. Recognition remains reviewable; no identifiers, leading zeros, duplicate rows or quantities are silently corrected. A layout change does not establish a performance improvement.

## Verification status

Fresh signed preview.3 suites passed **1,673 component assertions**: 77 backend, 1,335 viewport and 261 migration, against the final source. Historical counts are not added to this total.

A signed reference candidate rendered the compact controls without New clipping or extra capture overflow, automatically shrank around a 301 × 169 image with visually balanced margins, showed readable Text options and accepted the 125% preset. No precise outer-window measurement is claimed. Later 100%/Ctrl+0 attempts were inconclusive when the desktop tool observed another foreground application; no pass is claimed.

That test exposed a clipped empty-launcher hint and Fit dropping when Text context opened. Smaller empty layout and a 40-DIP options reserve are implemented; final signed native retesting remains pending. The final clean build/publication and static installer/ZIP checks passed, as did 7 local translation and 47 synthetic video assertions bound to the final signed backend. The user confirmed active desktop use, so further interaction testing is paused. Preview.2's assertions and partial native observations remain historical, separate from the fresh preview.3 component run.

Current checks must cover compact sizing, toolbar/context/overflow access, OCR conversion and Text selection, precise font values, save/history, zoom/Fit and shortcut routing. Real screenshot/scrolling capture, recording/audio hardware, clipboard/print/share, physical DPI/multiple monitors and installation remain separate acceptance gates. Recording Pause/Resume remains unavailable; image/PDF export remains flattened.

Every executed or delivered binary must have a valid timestamped signature: Automation Forge for our software and valid original publisher signatures for third-party binaries. A delivered candidate's validation/source/signature/hash receipts identify its exact verified scope. No unsigned fallback, security bypass, automatic installation or public release is implied by these notes.

See [current verification](VERIFICATION-WINUI.md), [migration scope](MIGRATION.md) and [historical preview.2 notes](release-notes-0.7.0-preview.2.md). The stable application retains its separate identity and preferences.
