using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        // Translation remains a draft until the rich-text editor returns Apply.
        // Adding it creates a separate vector note; screenshot pixels stay exact.
        public bool AddTranslatedTextNote(string text, IWin32Window dialogOwner)
        {
            if (disposingCanvas || !HasImage || String.IsNullOrWhiteSpace(text)) return false;
            IWin32Window owner = dialogOwner ?? (IWin32Window)FindForm() ?? this;
            if (text.Length > 16000)
            {
                MessageBox.Show(owner, "Keep each translated text note within 16,000 characters. The translation is still available in the translation window.",
                    "Add translation to snip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            object identity = ImageIdentity;
            long revision = DocumentRevision;
            Size imageSize = ImageSize;
            int marginX = Math.Min(16, Math.Max(0, (imageSize.Width - 12) / 2));
            int marginY = Math.Min(16, Math.Max(0, (imageSize.Height - 16) / 2));
            PointF location = new PointF(marginX, marginY);
            int maximumWidth = Math.Min(32768, imageSize.Width - marginX * 2);
            int maximumHeight = imageSize.Height - marginY * 2;
            if (maximumWidth < 12 || maximumHeight < 1)
            {
                ExplainTranslationDoesNotFit(owner);
                return false;
            }

            string draftText = text, draftRtf;
            float draftSize, draftWidth = maximumWidth;
            Color draftInk = Color.Black;
            if (!TryFitInitialTranslation(text, draftWidth, maximumHeight, out draftRtf, out draftSize))
            {
                ExplainTranslationDoesNotFit(owner);
                return false;
            }

            while (TranslationTargetUnchanged(identity, revision))
            {
                using (TextEditorDialog dialog = new TextEditorDialog("Add translation to snip", draftText,
                    draftSize, draftWidth, maximumWidth, draftInk, Color.White, false, draftRtf))
                {
                    if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                    // Keep the entire formatted draft between fit attempts.
                    draftText = dialog.EnteredText;
                    draftRtf = dialog.Rtf;
                    draftSize = dialog.TextSize;
                    draftWidth = dialog.BoxWidth;
                    draftInk = dialog.TextColor;
                }
                if (!TranslationTargetUnchanged(identity, revision)) break;
                if (String.IsNullOrWhiteSpace(draftText)) return false;

                string fitError;
                if (!TranslationFits(draftRtf, draftWidth, maximumWidth, maximumHeight, out fitError))
                {
                    string explanation = "That text would extend beyond this snip. Reduce the font size, make the box wider, or shorten the note.\r\n\r\n" +
                        "Choose OK to adjust the complete formatted draft, or Cancel to leave the translation window unchanged.";
                    if (!String.IsNullOrWhiteSpace(fitError)) explanation += "\r\n\r\n" + fitError;
                    if (MessageBox.Show(owner, explanation, "Text does not fit", MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) != DialogResult.OK) return false;
                    continue;
                }

                // Tool restoration must happen before selecting the new note.
                // Restoring a highlighter's previous ink cannot recolor either
                // an existing note or the translated note being inserted.
                CancelGesture();
                TextActionsMode = false;
                Tool = EditorTool.Select;
                if (!TranslationTargetUnchanged(identity, revision)) break;
                int oldCount = document.AnnotationCount;
                document.AddRichText(draftText, draftRtf, location, draftInk, draftSize, draftWidth);
                if (document.AnnotationCount != oldCount + 1) return false;
                selectedIndex = document.AnnotationCount - 1;
                textFontSize = draftSize;
                defaultTextBoxWidth = draftWidth;
                RaiseChanged();
                return true;
            }

            if (!disposingCanvas && !IsDisposed)
                MessageBox.Show(owner, "The snip changed while you were editing. Nothing was added. Your translation is still available in the translation window.",
                    "Snip changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private bool TranslationTargetUnchanged(object identity, long revision)
        {
            return !disposingCanvas && !IsDisposed && HasImage && Object.ReferenceEquals(identity, ImageIdentity) && DocumentRevision == revision;
        }

        private static bool TryFitInitialTranslation(string text, float width, float availableHeight, out string rtf, out float size)
        {
            rtf = null;
            size = 20;
            // Only the initial draft is fitted automatically. Once the user has
            // chosen formatting, overflow returns to that draft instead of
            // silently shrinking, clipping, or dropping translated characters.
            int initialSize = Math.Min(20, (int)Math.Floor(width / 1.5f));
            initialSize -= initialSize % 2;
            for (int candidate = initialSize; candidate >= 8; candidate -= 2)
            {
                try
                {
                    string formatted = RichTextLayout.CreatePlainRtf(text, "Segoe UI", candidate, FontStyle.Regular, Color.Black);
                    string error;
                    if (TranslationFits(formatted, width, width, availableHeight, out error))
                    {
                        rtf = formatted;
                        size = candidate;
                        return true;
                    }
                }
                catch (ArgumentException) { return false; }
                catch (InvalidOperationException) { }
            }
            return false;
        }

        private static bool TranslationFits(string rtf, float width, float availableWidth, float availableHeight, out string error)
        {
            error = null;
            if (Single.IsNaN(width) || Single.IsInfinity(width) || width < 12 || width > availableWidth) return false;
            try
            {
                RichTextMetrics metrics = RichTextLayout.Measure(rtf, width);
                RectangleF ink = metrics.InkBounds;
                return metrics.Size.Width <= availableWidth && metrics.Size.Height <= availableHeight &&
                    ink.Left >= 0 && ink.Top >= 0 && ink.Right <= availableWidth && ink.Bottom <= availableHeight;
            }
            catch (ArgumentException exception) { error = exception.Message; return false; }
            catch (InvalidOperationException exception) { error = exception.Message; return false; }
        }

        private static void ExplainTranslationDoesNotFit(IWin32Window owner)
        {
            MessageBox.Show(owner, "The complete translation does not fit inside this snip, even at the minimum initial font size. Shorten it or add separate text notes. Nothing was added; the translation remains in the translation window.",
                "Translation does not fit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
