using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private void ShowTranslation()
        {
            if (!canvas.HasImage || canvas.IsRecognizingText || capturePending) return;
            bool selected = canvas.HasSelectedRecognizedText;
            string source = selected ? canvas.SelectedRecognizedText : canvas.AllRecognizedText;
            if (String.IsNullOrWhiteSpace(source))
            {
                SetStatus("No recognized text is available to translate. Try Text actions on a clearer snip.");
                return;
            }
            TranslationSourceSnapshot snapshot;
            try { snapshot = canvas.CaptureTranslationSource(selected); }
            catch (InvalidOperationException error)
            {
                MessageBox.Show(this, error.Message, "Translate text", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> segments = new List<string>();
            foreach (TranslationSourceBlock block in snapshot.Blocks) segments.Add(block.SourceText);
            using (TranslationDialog dialog = new TranslationDialog(snapshot.Text, snapshot.SelectedOnly,
                preferences.TranslationServerUrl, preferences.TranslationTargetLanguage,
                preferences.TranslationProvider, preferences.TranslationSourceLanguage))
            {
                dialog.SourceSegments = segments;
                dialog.ReplaceInSnip = async delegate(IList<string> translated, IWin32Window owner)
                {
                    if (IsDisposed || Disposing || !canvas.HasImage || !Object.ReferenceEquals(snapshot.ImageIdentity, canvas.ImageIdentity) || canvas.DocumentRevision != snapshot.Revision)
                    {
                        MessageBox.Show(owner, "The snip changed while the translation was open. Copy the translation, then reopen Text actions for the current snip.",
                            "Snip changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    System.Threading.CancellationToken replacementToken = dialog.ReplacementCancellationToken;
                    TranslationReplaceResult result = await canvas.ReplaceTranslatedTextAsync(snapshot, translated, replacementToken);
                    if (!result.Applied)
                    {
                        if (!replacementToken.IsCancellationRequested)
                            MessageBox.Show(owner, result.Message, "Translation kept", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    SetStatus("Original text replaced with translation. Review the result; Undo restores every original line.");
                    return true;
                };
                dialog.ShowDialog(this);
                preferences.TranslationServerUrl = dialog.SelectedEndpoint;
                preferences.TranslationTargetLanguage = dialog.SelectedTargetCode;
                preferences.TranslationProvider = dialog.SelectedProvider;
                preferences.TranslationSourceLanguage = dialog.SelectedSourceCode;
                foreach (Form form in Application.OpenForms)
                {
                    MainForm other = form as MainForm;
                    if (other == null || other.IsDisposed) continue;
                    other.preferences.TranslationServerUrl = preferences.TranslationServerUrl;
                    other.preferences.TranslationTargetLanguage = preferences.TranslationTargetLanguage;
                    other.preferences.TranslationProvider = preferences.TranslationProvider;
                    other.preferences.TranslationSourceLanguage = preferences.TranslationSourceLanguage;
                }
                try { preferences.Save(); }
                catch (Exception error) { ShowFailure("Your translation choices could not be saved. The snip is still open", error); }
            }
        }
    }
}
