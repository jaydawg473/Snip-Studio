using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    // Translation is an explicit text-only request. Opening this dialog never
    // connects to a server, and neither source nor translated text is persisted.
    internal sealed class TranslationDialog : Form
    {
        private const string LocalEndpoint = "http://127.0.0.1:5000";
        private const string SetupGuide = "https://docs.libretranslate.com/guides/installation/";
        private readonly string sourceText;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly Panel scroll;
        private readonly TableLayoutPanel content, languagesGrid, textGrid;
        private readonly TableLayoutPanel sourceField, targetField, originalCard, translatedCard;
        private readonly FlowLayoutPanel footer;
        private readonly ComboBox providerChoice, serverChoice, sourceLanguage, targetLanguage;
        private readonly TextBox endpoint, original, translated;
        private readonly Label destination, status, translationCaption, providerDescription, replacementGuidance;
        private readonly List<Label> wrappingLabels = new List<Label>();
        private readonly List<Control> serverControls = new List<Control>();
        private readonly StudioButton connect, translate, cancelRequest, copy, close, retryOffline, addToSnipButton;
        private readonly List<TranslationLanguage> languages = new List<TranslationLanguage>();
        private ITextTranslationService service;
        private CancellationTokenSource requestCancellation;
        private int requestGeneration;
        private bool busy, updating, reflowing, disposed, resultCurrent, settingResult, shown, offlineLoadAttempted, addingToSnip, closeWhenReplacementFinishes;
        private float layoutScale = 1f;
        private string lastEndpoint, lastTargetCode, lastSourceCode, customEndpoint;
        private Func<IList<string>, IWin32Window, Task<bool>> replaceInSnip;
        private List<string> sourceSegments;
        private int translatedSegmentCount = 1;
        private CancellationToken replacementToken;

        internal string SelectedEndpoint { get { return lastEndpoint; } }
        internal Func<IList<string>, IWin32Window, Task<bool>> ReplaceInSnip
        {
            get { return replaceInSnip; }
            set
            {
                replaceInSnip = value;
                if (addToSnipButton != null && !disposed && !IsDisposed && !Disposing) UpdateActions();
            }
        }
        internal CancellationToken ReplacementCancellationToken
        { get { return addingToSnip ? replacementToken : CancellationToken.None; } }
        internal IList<string> SourceSegments
        {
            get { return sourceSegments == null ? null : sourceSegments.AsReadOnly(); }
            set
            {
                if (busy || addingToSnip) throw new InvalidOperationException("Wait for the current translation action to finish before changing its source.");
                sourceSegments = value == null ? null : new List<string>(value);
                if (sourceSegments != null && sourceSegments.Count == 0) sourceSegments = null;
                original.Text = sourceSegments == null ? sourceText : String.Join("\r\n", sourceSegments);
                InvalidateResult("Source text changed. Select Translate to update the result.");
                replacementGuidance.Visible = sourceSegments != null && sourceSegments.Count > 1;
                UpdateActions();
            }
        }
        internal string SelectedProvider { get { return IsOffline ? "Offline" : "LibreTranslate"; } }
        internal string SelectedSourceCode
        {
            get
            {
                LanguageChoice choice = sourceLanguage.SelectedItem as LanguageChoice;
                return choice == null ? lastSourceCode : choice.Code;
            }
        }
        private bool IsOffline { get { return providerChoice.SelectedIndex == 0; } }
        private bool RequiresSourceLanguage
        {
            get
            {
                IOfflineTextTranslationService offline = service as IOfflineTextTranslationService;
                return offline != null && offline.RequiresExplicitSourceLanguage;
            }
        }
        internal string SelectedTargetCode
        {
            get
            {
                LanguageChoice choice = targetLanguage.SelectedItem as LanguageChoice;
                return choice == null ? lastTargetCode : choice.Code;
            }
        }

        internal TranslationDialog(string sourceText, bool selectedOnly, string initialEndpoint, string initialTargetCode)
            : this(sourceText, selectedOnly, initialEndpoint, initialTargetCode, "Offline", "") { }

        internal TranslationDialog(string sourceText, bool selectedOnly, string initialEndpoint, string initialTargetCode,
            string initialProvider, string initialSourceCode)
        {
            this.sourceText = sourceText ?? "";
            lastEndpoint = String.IsNullOrWhiteSpace(initialEndpoint) ? LocalEndpoint : initialEndpoint.Trim();
            if (String.Equals(lastEndpoint.TrimEnd('/'), LocalEndpoint, StringComparison.OrdinalIgnoreCase)) lastEndpoint = LocalEndpoint;
            customEndpoint = lastEndpoint == LocalEndpoint ? "https://" : lastEndpoint;
            lastTargetCode = initialTargetCode ?? "";
            lastSourceCode = initialSourceCode ?? "";
            Text = "Translate text — Snip Studio";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(930, 740);
            MinimumSize = new Size(560, 460);
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            MainForm.TrySetAppIcon(this);

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Margin = Padding.Empty, Padding = Padding.Empty, BackColor = StudioTheme.Surface };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            Label heading = Body("Translate your snip", false);
            heading.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
            heading.Margin = new Padding(20, 16, 20, 4);
            root.Controls.Add(heading, 0, 0);
            scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty,
                Padding = Padding.Empty, BackColor = StudioTheme.Surface };
            root.Controls.Add(scroll, 0, 1);
            content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(20, 8, 20, 12),
                Margin = Padding.Empty, BackColor = StudioTheme.Surface };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(content);
            AddRow(Body(selectedOnly ? "Translate the words you selected. Review the result before replacing text in your snip."
                : "Translate all recognized text. Review the result before replacing text in your snip.", true));
            providerChoice = Choice("Translation method");
            providerChoice.Items.AddRange(new object[] { "Offline — included", "LibreTranslate server (advanced)" });
            AddRow(Field("Translation method", providerChoice));
            providerDescription = Body("", true);
            AddRow(providerDescription);
            retryOffline = Button("Check included files", false, "Check the bundled offline translation files again after repairing the installation.");
            FlowLayoutPanel offlineActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
            offlineActions.Controls.Add(retryOffline);
            AddRow(offlineActions);

            serverChoice = Choice("Translation server");
            serverChoice.Items.AddRange(new object[] { "Local server (this PC)", "Custom server" });
            TableLayoutPanel serverField = Field("Translation server", serverChoice);
            serverControls.Add(serverField);
            AddRow(serverField);
            endpoint = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
                BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, AccessibleName = "LibreTranslate server address",
                Margin = new Padding(0, 0, 0, 6) };
            TableLayoutPanel addressField = Field("Server address", endpoint);
            serverControls.Add(addressField);
            AddRow(addressField);
            FlowLayoutPanel serverActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            connect = Button("Connect", false, "Load the languages offered by the server at the address above.");
            serverActions.Controls.Add(connect);
            LinkLabel setup = new LinkLabel { Text = "Free local server setup guide", AutoSize = true,
                LinkColor = StudioTheme.Accent, ActiveLinkColor = StudioTheme.AccentHover, VisitedLinkColor = StudioTheme.Accent,
                Margin = new Padding(12, 9, 0, 6), AccessibleName = "Open the official LibreTranslate installation guide" };
            setup.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo(SetupGuide) { UseShellExecute = true }); }
                catch (Exception) { SetStatus("The guide could not open. Visit docs.libretranslate.com for local server setup.", true); }
            };
            serverActions.Controls.Add(setup);
            serverControls.Add(serverActions);
            AddRow(serverActions);
            destination = Body("", true);
            destination.AccessibleName = "Translation destination and privacy information";
            AddRow(destination);

            sourceLanguage = Choice("Source language");
            targetLanguage = Choice("Translate into language");
            sourceField = Field("From", sourceLanguage);
            targetField = Field("To", targetLanguage);
            languagesGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8) };
            languagesGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            languagesGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            languagesGrid.Controls.Add(sourceField, 0, 0);
            languagesGrid.Controls.Add(targetField, 1, 0);
            AddRow(languagesGrid);
            replacementGuidance = Body("Each line replaces its matching line in the image. Keep the same number of lines when correcting the translation.", true);
            replacementGuidance.Visible = false;
            AddRow(replacementGuidance);
            original = TextArea("Original recognized text", true);
            original.Text = this.sourceText;
            translated = TextArea("Translated text", false);
            Label originalHeading;
            originalCard = TextCard("Original", original, out originalHeading);
            Label resultHeading;
            translatedCard = TextCard("Translation", translated, out resultHeading);
            translationCaption = resultHeading;
            textGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                AutoSize = false, Height = 300, Margin = new Padding(0, 0, 0, 8) };
            textGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            textGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            textGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            textGrid.Controls.Add(originalCard, 0, 0);
            textGrid.Controls.Add(translatedCard, 1, 0);
            AddRow(textGrid);
            status = Body("Preparing included offline languages…", true);
            status.AccessibleName = "Translation status";
            AddRow(status);
            AddRow(Body("Recognition and translation can make mistakes. Review names, numbers, and important instructions before using the result.", true));

            footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true, Padding = new Padding(16, 10, 16, 12), Margin = Padding.Empty,
                BackColor = StudioTheme.Surface };
            close = Button("Close", false, "Close translation and keep your screenshot unchanged.");
            close.DialogResult = DialogResult.Cancel;
            copy = Button("Copy translation", false, "Copy the translated text, including any corrections you typed.");
            addToSnipButton = Button("Replace text in snip", false, "Replace the recognized words in their original positions with the translated text. Undo restores the original text.");
            cancelRequest = Button("Cancel request", false, "Stop waiting for this request. Earlier text remains available.");
            translate = Button("Translate", true, "Translate the recognized text using the selected method.");
            footer.Controls.Add(close);
            footer.Controls.Add(copy);
            footer.Controls.Add(addToSnipButton);
            footer.Controls.Add(cancelRequest);
            footer.Controls.Add(translate);
            root.Controls.Add(footer, 0, 2);
            CancelButton = close;
            AcceptButton = translate;

            serverChoice.SelectedIndex = lastEndpoint == LocalEndpoint ? 0 : 1;
            endpoint.Text = lastEndpoint;
            endpoint.ReadOnly = serverChoice.SelectedIndex == 0;
            providerChoice.SelectedIndex = String.Equals(initialProvider, "LibreTranslate", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            providerChoice.SelectedIndexChanged += async delegate
            {
                if (updating) return;
                ChangeProvider();
                if (shown && IsOffline) await LoadOfflineLanguagesAsync();
            };
            serverChoice.SelectedIndexChanged += delegate
            {
                if (updating) return;
                updating = true;
                try
                {
                    if (!endpoint.ReadOnly) customEndpoint = endpoint.Text;
                    endpoint.ReadOnly = serverChoice.SelectedIndex == 0;
                    endpoint.Text = endpoint.ReadOnly ? LocalEndpoint : customEndpoint;
                }
                finally { updating = false; }
                DisconnectForChangedEndpoint();
            };
            endpoint.TextChanged += delegate { if (!updating) DisconnectForChangedEndpoint(); };
            sourceLanguage.SelectedIndexChanged += delegate
            {
                if (updating) return;
                LanguageChoice selected = sourceLanguage.SelectedItem as LanguageChoice;
                if (selected != null && !String.IsNullOrEmpty(selected.Code)) lastSourceCode = selected.Code;
                InvalidateResult("Language changed. Select Translate to update the result.");
                FillTargetLanguages(SelectedTargetCode);
                SetOriginalDirection();
                UpdateActions();
            };
            targetLanguage.SelectedIndexChanged += delegate
            {
                if (updating) return;
                LanguageChoice selected = targetLanguage.SelectedItem as LanguageChoice;
                if (selected != null) lastTargetCode = selected.Code;
                InvalidateResult("Language changed. Select Translate to update the result.");
                UpdateActions();
            };
            connect.Click += async delegate { await ConnectAsync(); };
            retryOffline.Click += async delegate { await LoadOfflineLanguagesAsync(); };
            translate.Click += async delegate { await TranslateAsync(); };
            cancelRequest.Click += delegate { CancelPending(true); };
            copy.Click += delegate { CopyTranslation(); };
            addToSnipButton.Click += async delegate { await ReplaceTranslationInSnipAsync(); };
            translated.TextChanged += delegate
            {
                if (settingResult) return;
                if (resultCurrent) translationCaption.Text = "Translation — edited";
                UpdateActions();
            };
            scroll.SizeChanged += delegate { Reflow(); };
            Shown += delegate
            {
                shown = true;
                RefreshLayoutScale();
                Rectangle work = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(Ui(560), Math.Max(320, work.Width - 24)), Math.Min(Ui(460), Math.Max(300, work.Height - 24)));
                Size = new Size(Math.Min(Width, Math.Max(320, work.Width - 24)), Math.Min(Height, Math.Max(300, work.Height - 24)));
                Reflow();
                original.Select(0, 0);
                providerChoice.Focus();
                if (IsOffline)
                {
                    // Queue the local manifest/file check after the heading's
                    // first paint. The offline engine starts only on Translate.
                    PaintEventHandler firstPaint = null;
                    firstPaint = delegate
                    {
                        heading.Paint -= firstPaint;
                        if (disposed || IsDisposed || !IsOffline) return;
                        try { BeginInvoke((MethodInvoker)async delegate { if (IsOffline && service == null && !busy) await LoadOfflineLanguagesAsync(); }); }
                        catch (InvalidOperationException) { }
                    };
                    heading.Paint += firstPaint;
                    heading.Invalidate();
                }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (addingToSnip)
                {
                    args.Cancel = true;
                    closeWhenReplacementFinishes = true;
                    CancelPending(true);
                }
                else CancelPending(false);
            };
            StudioTheme.ApplyTo(this);
            UpdateProviderPresentation();
            if (!IsOffline) SetStatus("Advanced: start a free LibreTranslate server, then select Connect. No connection has been made.", false);
            UpdateDestination();
            UpdateActions();
            Reflow();
        }

        private void ChangeProvider()
        {
            InvalidateResult(IsOffline ? "Preparing included offline languages…" : "Select Connect to load languages from your LibreTranslate server.");
            if (service != null) { service.Dispose(); service = null; }
            ClearLanguages();
            UpdateProviderPresentation();
            UpdateDestination();
            UpdateActions();
        }

        private void UpdateProviderPresentation()
        {
            foreach (Control control in serverControls) control.Visible = !IsOffline;
            providerDescription.Text = IsOffline
                ? "English, French, German, and Spanish translation is included. It runs on this PC, with no account or internet connection needed. Choose the language in your snip before translating."
                : "Advanced: use a free LibreTranslate server on this PC, or enter a server you trust. Connect loads its available languages; Translate sends only the text shown below.";
            tips.SetToolTip(translate, IsOffline ? "Translate on this PC with the included offline language packs."
                : "Send the recognized text to the connected LibreTranslate server.");
        }

        private async Task LoadOfflineLanguagesAsync()
        {
            if (busy || disposed || !IsOffline) return;
            offlineLoadAttempted = true;
            ITextTranslationService candidate = null;
            CancellationTokenSource request = BeginRequest();
            CancellationToken token = request.Token;
            int generation = requestGeneration;
            SetStatus("Checking included offline translation files…", false);
            try
            {
                IList<TranslationLanguage> available = await Task.Run(async delegate
                {
                    token.ThrowIfCancellationRequested();
                    candidate = new OfflineTranslationService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translation"));
                    return await candidate.GetLanguagesAsync(token).ConfigureAwait(false);
                }, token);
                if (!RequestIsCurrent(generation, request, token) || !IsOffline) return;
                if (available == null || available.Count == 0) throw new InvalidOperationException("The included files did not offer any translation languages.");
                if (service != null) service.Dispose();
                service = candidate;
                candidate = null;
                languages.Clear();
                foreach (TranslationLanguage language in available) if (language != null) languages.Add(language);
                FillSourceLanguages();
                InvalidateResultWithoutCancel();
                SetStatus(targetLanguage.Items.Count == 0 ? "The included files did not offer a usable language pair. Repair or reinstall Snip Studio."
                    : String.IsNullOrEmpty(SelectedSourceCode) ? "Included languages are ready. Choose the language in your snip, then Translate."
                    : "Included languages are ready. Select Translate to translate on this PC.", targetLanguage.Items.Count == 0);
                UpdateDestination();
            }
            catch (OperationCanceledException)
            {
                if (RequestIsCurrent(generation, request, token)) SetStatus("The file check was canceled. Your text is kept.", false);
            }
            catch (Exception error)
            {
                if (RequestIsCurrent(generation, request, token))
                    SetStatus("Included translation files could not load. Repair or reinstall Snip Studio, then select Check included files. " + error.Message, true);
            }
            finally
            {
                if (candidate != null) candidate.Dispose();
                EndRequest(generation, request);
            }
        }

        private async Task ConnectAsync()
        {
            if (busy || disposed || IsOffline) return;
            string address = endpoint.Text.Trim();
            ITextTranslationService candidate = null;
            CancellationTokenSource request = BeginRequest();
            CancellationToken token = request.Token;
            int generation = requestGeneration;
            SetStatus("Connecting to the server and loading its languages…", false);
            try
            {
                candidate = new LibreTranslateService(address);
                IList<TranslationLanguage> available = await candidate.GetLanguagesAsync(token);
                if (!RequestIsCurrent(generation, request, token)) return;
                if (available == null || available.Count == 0) throw new InvalidOperationException("The server did not offer any translation languages.");
                if (service != null) service.Dispose();
                service = candidate;
                candidate = null;
                lastEndpoint = address;
                languages.Clear();
                foreach (TranslationLanguage language in available) if (language != null) languages.Add(language);
                FillSourceLanguages();
                InvalidateResultWithoutCancel();
                SetStatus(targetLanguage.Items.Count == 0 ? "Connected, but this server did not offer a usable translation pair. Choose another server."
                    : "Connected. Choose a language, then Translate. No screenshot image is sent.", targetLanguage.Items.Count == 0);
                UpdateDestination();
            }
            catch (OperationCanceledException)
            {
                if (RequestIsCurrent(generation, request, token)) SetStatus("Connection canceled. Your text is kept.", false);
            }
            catch (Exception error)
            {
                if (RequestIsCurrent(generation, request, token))
                    SetStatus("Could not connect. Start the local LibreTranslate server or check the server address. " + error.Message, true);
            }
            finally
            {
                if (candidate != null) candidate.Dispose();
                EndRequest(generation, request);
            }
        }

        private async Task TranslateAsync()
        {
            LanguageChoice from = sourceLanguage.SelectedItem as LanguageChoice;
            LanguageChoice to = targetLanguage.SelectedItem as LanguageChoice;
            if (busy || disposed || service == null || from == null || to == null || String.IsNullOrWhiteSpace(sourceText)) return;
            if (RequiresSourceLanguage && String.IsNullOrEmpty(from.Code))
            {
                SetStatus("Choose the language in your snip before translating.", false);
                sourceLanguage.Focus();
                return;
            }
            ITextTranslationService currentService = service;
            InvalidateResultWithoutCancel();
            CancellationTokenSource request = BeginRequest();
            CancellationToken token = request.Token;
            int generation = requestGeneration;
            SetStatus(IsOffline ? "Translating on this PC. The first translation may take a moment…" : "Translating the text shown above…", false);
            try
            {
                TranslationDraft result = await TranslateDraftAsync(currentService, from.Code, to.Code, token);
                if (!RequestIsCurrent(generation, request, token)) return;
                if (result == null) throw new InvalidOperationException("The translation engine returned no translation.");
                settingResult = true;
                try
                {
                    translated.RightToLeft = to.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
                    translated.Text = result.Text ?? "";
                    translated.Select(0, 0);
                }
                finally { settingResult = false; }
                resultCurrent = true;
                translatedSegmentCount = result.SegmentCount;
                translationCaption.Text = "Translation";
                TranslationLanguage detected = FindLanguage(result.SourceCode);
                if (String.IsNullOrEmpty(from.Code) && detected != null)
                    original.RightToLeft = detected.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
                SetStatus("Translated into " + to.Name + ". Correct the result, then copy it or replace text in the snip.", false);
            }
            catch (OperationCanceledException)
            {
                if (RequestIsCurrent(generation, request, token)) SetStatus("Translation canceled. Earlier text is kept.", false);
            }
            catch (Exception error)
            {
                if (RequestIsCurrent(generation, request, token)) SetStatus("Translation could not finish. Your original and earlier result are kept. " + error.Message, true);
            }
            finally { EndRequest(generation, request); }
        }

        private async Task<TranslationDraft> TranslateDraftAsync(ITextTranslationService currentService, string fromCode, string toCode, CancellationToken token)
        {
            List<string> blocks = sourceSegments == null ? new List<string> { sourceText } : new List<string>(sourceSegments);
            if (blocks.Count == 1)
            {
                TranslationResult single = await currentService.TranslateAsync(blocks[0], fromCode, toCode, token);
                if (single == null) throw new InvalidOperationException("The translation engine returned no translation.");
                if (!String.IsNullOrWhiteSpace(blocks[0]) && String.IsNullOrWhiteSpace(single.Text))
                    throw new InvalidOperationException("The translation engine returned empty text. No image changes were made.");
                return new TranslationDraft { Text = single.Text, SourceCode = single.SourceCode, SegmentCount = 1 };
            }
            IList<string> results;
            ISegmentedTextTranslationService segmented = currentService as ISegmentedTextTranslationService;
            string detectedCode = fromCode;
            if (segmented != null)
            {
                SetStatus("Translating " + blocks.Count + " matching lines" + (IsOffline ? " on this PC…" : "…"), false);
                results = await segmented.TranslateSegmentsAsync(blocks.AsReadOnly(), fromCode, toCode, token);
            }
            else
            {
                List<string> serial = new List<string>();
                for (int index = 0; index < blocks.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    SetStatus("Translating line " + (index + 1) + " of " + blocks.Count + "…", false);
                    TranslationResult next = await currentService.TranslateAsync(blocks[index], fromCode, toCode, token);
                    if (next == null) throw new InvalidOperationException("The translation engine returned no translation.");
                    if (index == 0) detectedCode = next.SourceCode;
                    serial.Add(next.Text);
                }
                results = serial;
            }
            token.ThrowIfCancellationRequested();
            if (results == null || results.Count != blocks.Count) throw new InvalidOperationException("The translation engine returned a different number of matching lines. No image changes were made.");
            List<string> display = new List<string>();
            for (int index = 0; index < results.Count; index++)
            {
                if (!String.IsNullOrWhiteSpace(blocks[index]) && String.IsNullOrWhiteSpace(results[index]))
                    throw new InvalidOperationException("The translation engine returned an empty matching line. No image changes were made.");
                display.Add(OneDisplayLine(results[index]));
            }
            return new TranslationDraft { Text = String.Join("\r\n", display), SourceCode = detectedCode, SegmentCount = blocks.Count };
        }

        private static string OneDisplayLine(string text)
        {
            return (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
                .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');
        }

        private sealed class TranslationDraft
        {
            internal string Text, SourceCode;
            internal int SegmentCount;
        }

        private CancellationTokenSource BeginRequest()
        {
            CancelPending(false);
            requestCancellation = new CancellationTokenSource();
            busy = true;
            UpdateActions();
            return requestCancellation;
        }

        private bool RequestIsCurrent(int generation, CancellationTokenSource request, CancellationToken token)
        {
            return !disposed && !IsDisposed && !Disposing && requestGeneration == generation &&
                Object.ReferenceEquals(requestCancellation, request) && !token.IsCancellationRequested;
        }

        private void EndRequest(int generation, CancellationTokenSource request)
        {
            if (requestGeneration == generation && Object.ReferenceEquals(requestCancellation, request))
            {
                requestCancellation = null;
                request.Dispose();
                busy = false;
                if (!disposed && !IsDisposed && !Disposing) UpdateActions();
            }
        }

        private void CancelPending(bool showStatus)
        {
            CancellationTokenSource pending = requestCancellation;
            requestCancellation = null;
            requestGeneration++;
            busy = false;
            if (pending != null)
            {
                try { pending.Cancel(); }
                finally { pending.Dispose(); }
            }
            if (!disposed && !IsDisposed && !Disposing)
            {
                if (showStatus) SetStatus("Request canceled. Your original and earlier result are kept.", false);
                UpdateActions();
            }
        }

        private void DisconnectForChangedEndpoint()
        {
            if (IsOffline) return;
            InvalidateResult("Server address changed. Select Connect to load its languages.");
            if (service != null) { service.Dispose(); service = null; }
            ClearLanguages();
            UpdateDestination();
            UpdateActions();
        }

        private void ClearLanguages()
        {
            languages.Clear();
            updating = true;
            try { sourceLanguage.Items.Clear(); targetLanguage.Items.Clear(); }
            finally { updating = false; }
            original.RightToLeft = RightToLeft.No;
        }

        private void InvalidateResult(string message)
        {
            CancelPending(false);
            InvalidateResultWithoutCancel();
            SetStatus(message, false);
        }

        private void InvalidateResultWithoutCancel()
        {
            resultCurrent = false;
            translationCaption.Text = translated.TextLength == 0 ? "Translation" : "Previous translation — out of date";
        }

        private void FillSourceLanguages()
        {
            updating = true;
            try
            {
                sourceLanguage.Items.Clear();
                sourceLanguage.Items.Add(new LanguageChoice("", RequiresSourceLanguage ? "Choose source language" : "Auto-detect", false));
                int preferred = 0;
                foreach (TranslationLanguage language in languages)
                {
                    int at = sourceLanguage.Items.Add(new LanguageChoice(language.Code, language.Name, language.IsRightToLeft));
                    if (String.Equals(language.Code, lastSourceCode, StringComparison.OrdinalIgnoreCase)) preferred = at;
                }
                sourceLanguage.SelectedIndex = preferred;
            }
            finally { updating = false; }
            FillTargetLanguages(lastTargetCode);
            SetOriginalDirection();
        }

        private void FillTargetLanguages(string preferredCode)
        {
            LanguageChoice selected = sourceLanguage.SelectedItem as LanguageChoice;
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool unrestrictedTargets = false;
            foreach (TranslationLanguage language in languages)
            {
                if (selected != null && !String.IsNullOrEmpty(selected.Code) && !String.Equals(selected.Code, language.Code, StringComparison.OrdinalIgnoreCase)) continue;
                // Older LibreTranslate servers omit targets. The client treats
                // that as an unspecified pair list, rather than no languages.
                if (language.Targets == null || language.Targets.Count == 0) unrestrictedTargets = true;
                else foreach (string code in language.Targets) if (!String.IsNullOrEmpty(code)) allowed.Add(code);
            }
            if (unrestrictedTargets) foreach (TranslationLanguage language in languages) allowed.Add(language.Code);
            updating = true;
            try
            {
                targetLanguage.Items.Clear();
                int preferred = -1, uiLanguage = -1, english = -1;
                string currentLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                foreach (TranslationLanguage language in languages)
                {
                    if (!allowed.Contains(language.Code)) continue;
                    int at = targetLanguage.Items.Add(new LanguageChoice(language.Code, language.Name, language.IsRightToLeft));
                    if (String.Equals(language.Code, preferredCode, StringComparison.OrdinalIgnoreCase)) preferred = at;
                    if (String.Equals(language.Code, currentLanguage, StringComparison.OrdinalIgnoreCase)) uiLanguage = at;
                    if (String.Equals(language.Code, "en", StringComparison.OrdinalIgnoreCase)) english = at;
                }
                if (targetLanguage.Items.Count > 0)
                    targetLanguage.SelectedIndex = preferred >= 0 ? preferred : uiLanguage >= 0 ? uiLanguage : english >= 0 ? english : 0;
                LanguageChoice to = targetLanguage.SelectedItem as LanguageChoice;
                if (to != null) lastTargetCode = to.Code;
            }
            finally { updating = false; }
        }

        private TranslationLanguage FindLanguage(string code)
        {
            foreach (TranslationLanguage language in languages)
                if (String.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) return language;
            return null;
        }

        private void SetOriginalDirection()
        {
            LanguageChoice selected = sourceLanguage.SelectedItem as LanguageChoice;
            original.RightToLeft = selected != null && selected.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
        }

        private void UpdateDestination()
        {
            if (IsOffline)
            {
                destination.Text = "Offline — on this PC. Your recognized text and screenshot stay here. No account, server setup, or internet connection is needed for the included languages.";
                Reflow();
                return;
            }
            Uri address;
            bool valid = Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out address);
            bool local = valid && address.IsLoopback;
            destination.Text = local
                ? "Destination: " + endpoint.Text.Trim() + " on this PC. Translate sends recognized text to your local server; the screenshot image stays in Snip Studio."
                : "Destination: " + endpoint.Text.Trim() + ". Translate sends the recognized text to this server. Only use a server you trust; the screenshot image stays in Snip Studio.";
            Reflow();
        }

        private void CopyTranslation()
        {
            if (translated.TextLength == 0) return;
            try
            {
                MainForm.RegisterClipboardIntent();
                Clipboard.SetDataObject(translated.Text, true, 4, 100);
                SetStatus(resultCurrent ? "Translation copied." : "Previous translation copied. It is out of date for the current choices.", false);
            }
            catch (Exception) { SetStatus("The clipboard is busy. Your translation is kept; try Copy translation again.", true); }
        }

        private async Task ReplaceTranslationInSnipAsync()
        {
            Func<IList<string>, IWin32Window, Task<bool>> callback = replaceInSnip;
            if (callback == null || busy || addingToSnip || !resultCurrent || String.IsNullOrWhiteSpace(translated.Text)) return;
            List<string> edited = new List<string>();
            if (translatedSegmentCount <= 1) edited.Add(translated.Text);
            else edited.AddRange(translated.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new char[] { '\n' }));
            if (edited.Count != translatedSegmentCount)
            {
                SetStatus("Keep " + translatedSegmentCount + " translation lines so each one replaces its matching line in the image. No text was replaced.", true);
                translated.Focus();
                return;
            }
            // Exact edited blocks retain the immutable source snapshot's order.
            // The canvas validates ownership, fit, and an atomic undoable apply.
            addingToSnip = true;
            CancellationTokenSource request = BeginRequest();
            CancellationToken token = request.Token;
            int generation = requestGeneration;
            bool closeAfterReplacement = false;
            replacementToken = token;
            SetStatus("Replacing the matching text in your snip…", false);
            try
            {
                bool applied = await callback(edited.AsReadOnly(), this);
                if (!RequestIsCurrent(generation, request, token)) return;
                if (applied) closeAfterReplacement = true;
                else SetStatus("No text was replaced. Your translation is still here.", false);
            }
            catch (OperationCanceledException)
            {
                if (RequestIsCurrent(generation, request, token)) SetStatus("Text replacement canceled. Your translation is kept.", false);
            }
            catch (Exception)
            {
                if (RequestIsCurrent(generation, request, token))
                    SetStatus("The text could not be replaced. Your translation is kept; try again or copy it.", true);
            }
            finally
            {
                addingToSnip = false;
                EndRequest(generation, request);
                if (!disposed && !IsDisposed && !Disposing)
                {
                    UpdateActions();
                    if (closeAfterReplacement || closeWhenReplacementFinishes)
                    {
                        closeWhenReplacementFinishes = false;
                        Close();
                    }
                }
            }
        }

        private void UpdateActions()
        {
            if (connect == null || translate == null) return;
            bool locked = busy || addingToSnip;
            connect.Enabled = !IsOffline && !locked && !String.IsNullOrWhiteSpace(endpoint.Text);
            providerChoice.Enabled = !locked;
            serverChoice.Enabled = !IsOffline && !locked;
            endpoint.Enabled = !IsOffline && !locked;
            retryOffline.Visible = IsOffline && !locked && service == null && offlineLoadAttempted;
            sourceLanguage.Enabled = !locked && service != null && sourceLanguage.Items.Count > 0;
            targetLanguage.Enabled = !locked && service != null && targetLanguage.Items.Count > 0;
            LanguageChoice from = sourceLanguage.SelectedItem as LanguageChoice;
            translate.Enabled = !locked && service != null && from != null && (!RequiresSourceLanguage || !String.IsNullOrEmpty(from.Code))
                && targetLanguage.SelectedItem != null && !String.IsNullOrWhiteSpace(sourceText);
            cancelRequest.Visible = busy;
            copy.Enabled = !locked && translated.TextLength > 0;
            addToSnipButton.Enabled = !locked && resultCurrent && replaceInSnip != null && !String.IsNullOrWhiteSpace(translated.Text);
            copy.Text = resultCurrent || translated.TextLength == 0 ? "Copy translation" : "Copy previous translation";
            translated.ReadOnly = locked;
            close.Enabled = !addingToSnip;
            Reflow();
        }

        private void SetStatus(string message, bool failure)
        {
            if (status == null) return;
            status.Text = message;
            status.ForeColor = failure ? (StudioTheme.IsDark ? Color.FromArgb(255, 179, 157) : Color.FromArgb(150, 42, 25)) : StudioTheme.Muted;
            Reflow();
        }

        private int Ui(int value)
        {
            return Math.Max(1, (int)Math.Round(value * layoutScale));
        }

        private void RefreshLayoutScale()
        {
            if (IsHandleCreated) using (Graphics graphics = CreateGraphics()) layoutScale = graphics.DpiX / 96f;
        }

        private void Reflow()
        {
            if (reflowing || scroll == null || content == null || languagesGrid == null || textGrid == null) return;
            reflowing = true;
            try
            {
                RefreshLayoutScale();
                int available = Math.Max(Ui(240), scroll.ClientSize.Width - content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - Ui(4));
                foreach (ComboBox choices in new ComboBox[] { providerChoice, serverChoice, sourceLanguage, targetLanguage })
                    ((StudioComboBox)choices).UpdateMetrics(layoutScale);
                foreach (Label label in wrappingLabels) label.MaximumSize = new Size(available, 0);
                bool stacked = available < Ui(700);
                SetGridLayout(languagesGrid, sourceField, targetField, stacked, true);
                SetGridLayout(textGrid, originalCard, translatedCard, stacked, false);
                textGrid.Height = Ui(stacked ? 470 : 290);
                originalCard.Margin = new Padding(0, 0, stacked ? 0 : Ui(8), stacked ? Ui(8) : 0);
                translatedCard.Margin = new Padding(stacked ? 0 : Ui(8), 0, 0, 0);
                sourceField.Margin = new Padding(0, 0, stacked ? 0 : Ui(8), Ui(6));
                targetField.Margin = new Padding(stacked ? 0 : Ui(8), 0, 0, Ui(6));
                foreach (StudioButton button in new StudioButton[] { connect, translate, cancelRequest, copy, close, retryOffline, addToSnipButton })
                {
                    if (button == null) continue;
                    button.Width = Math.Max(Ui(98), button.PreferredContentWidth(layoutScale) + Ui(12));
                    button.Height = Ui(36);
                }
                content.PerformLayout();
            }
            finally { reflowing = false; }
        }

        private static void SetGridLayout(TableLayoutPanel grid, Control first, Control second, bool stacked, bool autoSize)
        {
            if (grid.ColumnCount == (stacked ? 1 : 2) && grid.RowCount == (stacked ? 2 : 1)) return;
            grid.SuspendLayout();
            grid.ColumnCount = stacked ? 1 : 2;
            grid.RowCount = stacked ? 2 : 1;
            grid.ColumnStyles.Clear();
            grid.RowStyles.Clear();
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, stacked ? 100 : 50));
            if (!stacked) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(autoSize ? SizeType.AutoSize : SizeType.Percent, stacked ? 50 : 100));
            if (stacked) grid.RowStyles.Add(new RowStyle(autoSize ? SizeType.AutoSize : SizeType.Percent, 50));
            grid.SetCellPosition(first, new TableLayoutPanelCellPosition(0, 0));
            grid.SetCellPosition(second, new TableLayoutPanelCellPosition(stacked ? 0 : 1, stacked ? 1 : 0));
            grid.ResumeLayout(true);
        }

        private Label Body(string text, bool muted)
        {
            Label label = new Label { Text = text, AutoSize = true, UseMnemonic = false,
                ForeColor = muted ? StudioTheme.Muted : StudioTheme.Text, Margin = new Padding(0, 0, 0, 8) };
            wrappingLabels.Add(label);
            return label;
        }

        private void AddRow(Control control)
        {
            int row = content.RowCount++;
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Fill;
            content.Controls.Add(control, 0, row);
        }

        private TableLayoutPanel Field(string title, Control control)
        {
            TableLayoutPanel field = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2,
                Margin = new Padding(0, 0, 0, 8), Padding = Padding.Empty };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            field.Controls.Add(Body(title, false), 0, 0);
            control.Dock = DockStyle.Fill;
            control.Margin = Padding.Empty;
            field.Controls.Add(control, 0, 1);
            return field;
        }

        private TableLayoutPanel TextCard(string title, TextBox box, out Label caption)
        {
            TableLayoutPanel card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = Padding.Empty, Padding = Padding.Empty };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            caption = Body(title, false);
            caption.Font = new Font(Font, FontStyle.Bold);
            card.Controls.Add(caption, 0, 0);
            card.Controls.Add(box, 0, 1);
            return card;
        }

        private TextBox TextArea(string name, bool readOnly)
        {
            return new CopyAwareTextBox { Dock = DockStyle.Fill, Multiline = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, ReadOnly = readOnly, BorderStyle = BorderStyle.FixedSingle,
                BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, Font = new Font("Segoe UI", 10.5f),
                AccessibleName = name, Margin = Padding.Empty, MaxLength = 0 };
        }

        private ComboBox Choice(string name)
        {
            return new StudioComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false, DropDownHeight = 300, BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text,
                AccessibleName = name, Margin = Padding.Empty };
        }

        private StudioButton Button(string label, bool primary, string hint)
        {
            StudioButton button = new StudioButton { Text = label, Primary = primary, AccessibleName = label,
                Size = new Size(130, 36), Margin = new Padding(6, 0, 0, 6) };
            tips.SetToolTip(button, hint);
            return button;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (addingToSnip && keyData == Keys.Escape)
            {
                closeWhenReplacementFinishes = true;
                CancelPending(true);
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                CancelPending(false);
                if (service != null) { service.Dispose(); service = null; }
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class LanguageChoice
        {
            internal readonly string Code, Name;
            internal readonly bool IsRightToLeft;
            internal LanguageChoice(string code, string name, bool rightToLeft)
            { Code = code; Name = String.IsNullOrWhiteSpace(name) ? code : name; IsRightToLeft = rightToLeft; }
            public override string ToString() { return String.IsNullOrEmpty(Code) ? Name : Name + " (" + Code + ")"; }
        }

        private sealed class CopyAwareTextBox : TextBox
        {
            protected override void WndProc(ref Message message)
            {
                if ((message.Msg == 0x0300 || message.Msg == 0x0301) && SelectionLength > 0) MainForm.RegisterClipboardIntent();
                base.WndProc(ref message);
            }
        }
    }
}
