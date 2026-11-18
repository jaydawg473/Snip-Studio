# Snip Studio 0.6.23

Release candidate for local review. Installation and publication require approval.

## Text and editing

- Image-to-text activates the Text tool. Small snips receive bounded recognition retries to recover missing filename rows, retain source order, and protect supported file icons during editing.
- Corrected small ClearType color sampling and source-region sizing. OCR and source-font reconstruction remain estimates; identifiers are never silently rewritten.
- Drag selection preserves filename separators, spacing, and neighboring columns. Selection geometry and repeated font refinement reuse validated results.
- Failed image replacement and rejected pastes preserve unfinished text. Undo and Redo reflect pending drafts. Very low zoom no longer passes an invalid zoom factor to the native text editor.

## Windows, saving and review

- Enlarged snip windows can return to compact dimensions. Tool changes and edits preserve a deliberate window size.
- Saving encodes a frozen image in the background, shows progress, publishes atomically, and defers closing until completion. Rendering the snapshot still runs on the UI thread.
- Recent snips retains useful rows and previews while loading. Timeouts and late-result guards avoid indefinite UI waits; footer actions wrap at narrow sizes.
- Video review has a bounded metadata wait, keeps Save as available while loading, and prevents expired results from replacing a newer file. In-flight native metadata work is bounded but cannot be forcibly canceled.
- Recording startup failures allow retry. Notifications from an earlier recorder cannot alter a new recording session. Scrolling selection shows the correct instructions.

## Security and delivery

Application, installer and generated uninstaller require valid timestamped Automation Forge signatures. The bundled Node runtime retains its valid original publisher signature. Reviewed NSIS tools and native installer plugins carry Automation Forge signatures; their upstream originals were unsigned. The build no longer launches unsigned check executables through the legacy `-Check` switch.

The bundled offline translation runtime uses Node 24.22.3, verified against the official release checksum with its original OpenJS signature and timestamp. Existing translation models and attribution remain.

Measured results and final binary identity are recorded in `VALIDATION.txt`. Test helpers and private screenshots are excluded from release packages. Live microphone/system audio, physical monitor/HDR behavior, external Clipchamp import and installed-upgrade acceptance remain separate checks.

Installer: save your work, close older Snip Studio windows, then run `SnipStudio-0.6.23-Setup.exe` after approval. Portable: extract all files from `SnipStudio-0.6.23-signed-portable.zip` into one folder and run `Snip Studio.exe`.
