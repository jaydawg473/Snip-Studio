# Snip Studio 0.7.0-preview.6

- **Updating.** After you choose Install, Setup asks the registered WinUI preview to close before it changes files. Save or discard any open image edits, and finish saving any recording you want to keep. Canceling closure blocks the update. If an older preview stays in the notification area, choose **Exit** there and retry Setup.
- **Text Actions.** The toolbar now uses a magic wand. Editable text excludes folded-document icons when their pixels identify them as graphics. Real punctuation stays available, and original OCR output remains available for review.
- **Text clarity.** OCR style matching samples the full source bounds, and replacement text uses whole image pixels where it fits. The canvas aligns the image to display pixels at 100%. Font matching remains an estimate because screenshots do not carry font metadata.

Build and static package verification passed. The installer and portable ZIP contain the same 739 files. Windows verified 583 timestamped PE signatures and the installer contains the full signed uninstaller.

The signed build passed 1,910 component assertions, plus focused folded-icon rejection, punctuation, line edit/Undo, and source-pixel placement checks. Windows OCR found six rows on the dense filename snip, with zero exact filenames. Selection order, conversion without changing recognized strings, aligned placement, Undo, and Redo passed.

The installer exit path was reviewed in source. An actual upgrade and visual font comparison remain pending.
