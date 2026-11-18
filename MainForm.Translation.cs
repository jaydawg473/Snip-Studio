using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private TranslationDialog translationDialog;

        private void ShowTranslation()
        {
            if (IsDisposed || Disposing) return;
            if (translationDialog != null && !translationDialog.IsDisposed)
            {
                if (!layoutTestMode)
                {
                    if (translationDialog.WindowState == FormWindowState.Minimized) translationDialog.WindowState = FormWindowState.Normal;
                    translationDialog.Show();
                    translationDialog.Activate();
                }
                return;
            }
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
                SetStatus(error.Message);
                return;
            }
            List<string> segments = new List<string>();
            foreach (TranslationSourceBlock block in snapshot.Blocks) segments.Add(block.SourceText);
            TranslationDialog dialog = new TranslationDialog(snapshot.Text, snapshot.SelectedOnly,
                preferences.TranslationServerUrl, preferences.TranslationTargetLanguage,
                preferences.TranslationProvider, preferences.TranslationSourceLanguage);
            translationDialog = dialog;
            bool choicesSaved = false;
            Action saveChoices = delegate
            {
                if (choicesSaved) return;
                choicesSaved = true;
                SaveTranslationChoices(dialog);
            };
            EventHandler ownerDisposed = delegate
            {
                saveChoices();
                if (!dialog.IsDisposed) dialog.Dispose();
            };
            Disposed += ownerDisposed;
            dialog.FormClosed += delegate { saveChoices(); };
            dialog.ChoicesClosing += delegate { saveChoices(); };
            dialog.Disposed += delegate
            {
                Disposed -= ownerDisposed;
                if (Object.ReferenceEquals(translationDialog, dialog)) translationDialog = null;
            };
            dialog.SourceSegments = segments;
            dialog.ReplaceInSnip = async delegate(IList<string> translated, IWin32Window owner)
            {
                if (IsDisposed || Disposing || !canvas.HasImage || !Object.ReferenceEquals(snapshot.ImageIdentity, canvas.ImageIdentity) || canvas.DocumentRevision != snapshot.Revision)
                {
                    dialog.ShowReplacementFeedback("The snip changed while translation was open. Your translation is kept; copy it, or close this window and reopen Translate for the current snip.", true);
                    return false;
                }
                System.Threading.CancellationToken replacementToken = dialog.ReplacementCancellationToken;
                if (replacementToken.IsCancellationRequested) return false;
                TranslationReplaceResult result = await canvas.ReplaceTranslatedTextAsync(snapshot, translated, replacementToken);
                if (!result.Applied)
                {
                    dialog.ShowReplacementFeedback(replacementToken.IsCancellationRequested
                        ? "Text replacement canceled. Your translation is kept." : result.Message, !replacementToken.IsCancellationRequested);
                    return false;
                }
                if (!IsDisposed && !Disposing) SetStatus(result.Message);
                return true;
            };
            if (layoutTestMode) dialog.Owner = this;
            else
            {
                try { dialog.Show(this); }
                catch { dialog.Dispose(); throw; }
            }
        }

        private void SaveTranslationChoices(TranslationDialog dialog)
        {
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
            if (layoutTestMode) return;
            try { preferences.Save(); }
            catch (Exception)
            {
                if (!IsDisposed && !Disposing) SetStatus("Your translation choices could not be saved. Your snip is still open.");
            }
        }
    }
}
