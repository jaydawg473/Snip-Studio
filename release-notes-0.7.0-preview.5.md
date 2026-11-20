# Snip Studio 0.7.0-preview.5

Windows file version `0.7.0.5`. This WinUI preview brings back workflows from the previous Snip Studio.

- **Text actions.** Drag across words on the snip, then choose **Copy selection** or press Ctrl+C. Click a recognized line to edit it. **Apply** commits the edit and **Cancel** keeps the original pixels. To make recognized lines editable in place, open **Convert to text** in the Text actions menu. Conversion selects the Text tool. Review recognition errors and estimated formatting before saving.
- **Video editing.** Open a recording to see the thumbnail filmstrip. Click or drag on the strip to seek. Drag the start and end handles, or enter exact times. **Save trimmed copy** keeps the original file and opens the copy for review.
- **Shapes and Emoji.** The square and circle button opens the shape menu. The separate smile button lets you pick a sticker, then click the snip to place it.
- **Clipboard.** Captures and committed image edits copy to the clipboard by default. An existing off setting stays off. Change this under **Copy captures and edits to the clipboard** in Settings.
- **Recording area.** Use **Adjust area** on the recording controls to change the selection before starting.

Build and static package verification passed. The installer and portable ZIP contain the same 739 files. Windows verified 583 timestamped PE signatures and the installer contains the full signed uninstaller.

The signed backend passed 1,682 component, 7 local translation, and 74 generated-video checks. The generated-snip OCR draft and Undo test passed. Windows OCR found all six rows on the dense filename snip, with zero exact filenames. Selection order, verbatim conversion, Undo, and Redo passed on that image.

Hands-on checks are pending for native pointer and clipboard workflows, live recording and audio, and installation.
