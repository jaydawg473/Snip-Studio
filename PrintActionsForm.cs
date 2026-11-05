using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed class PrintActionsForm : Form
    {
        private Bitmap snapshot;
        private readonly PrintDocument document = new PrintDocument();
        private readonly SafePrintPreviewControl preview = new SafePrintPreviewControl();
        private readonly StudioComboBox printers = new StudioComboBox();
        private readonly Label status = new Label();
        private readonly Label details = new Label();
        private readonly Label unavailable = new Label();
        private readonly StudioButton print = new StudioButton();
        private readonly StudioButton pageSetup = new StudioButton();
        private readonly StudioButton fit = new StudioButton();
        private readonly StudioButton refresh = new StudioButton();
        private readonly StudioButton close = new StudioButton();
        private bool updatingPrinters, printing, previewFailed;
        private bool landscape;
        private Margins margins = new Margins(36, 36, 36, 36);

        public PrintActionsForm(Bitmap image)
        {
            if (image == null) throw new ArgumentNullException("image");
            snapshot = new Bitmap(image);
            landscape = image.Width > image.Height;
            Text = "Print your snip — Snip Studio";
            Font = new Font("Segoe UI", 10f);
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1000, 790);
            MinimumSize = new Size(760, 550);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            document.DocumentName = "Snip Studio screenshot";
            document.OriginAtMargins = true;
            document.PrintPage += OnPreviewPage;
            BuildLayout();
            Shown += delegate { LoadPrinters(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (printing)
                {
                    e.Cancel = true;
                    SetStatus("Windows is still receiving this page. Please wait before closing; check the print queue if it takes longer.", true);
                }
            };
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(26, 24, 26, 20);
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            Label heading = new Label();
            heading.Text = "A page worth keeping.";
            heading.Font = new Font("Segoe UI", 20, FontStyle.Bold);
            heading.ForeColor = StudioTheme.Text;
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(heading, 0, 0);

            Label description = new Label();
            description.Text = "Your snip fits one page without stretching. Choose a printer, then review the Windows print options.";
            description.ForeColor = StudioTheme.Muted;
            description.AutoSize = true;
            description.MaximumSize = new Size(900, 0);
            description.Margin = new Padding(0, 0, 0, 18);
            root.Controls.Add(description, 0, 1);

            TableLayoutPanel printerRow = new TableLayoutPanel();
            printerRow.Dock = DockStyle.Fill;
            printerRow.AutoSize = true;
            printerRow.ColumnCount = 3;
            printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            printerRow.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(printerRow, 0, 2);
            Label printerLabel = new Label();
            printerLabel.Text = "PRINTER";
            printerLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            printerLabel.AutoSize = true;
            printerLabel.Anchor = AnchorStyles.Left;
            printerLabel.ForeColor = StudioTheme.Muted;
            printerLabel.Margin = new Padding(0, 0, 14, 0);
            printerRow.Controls.Add(printerLabel, 0, 0);
            printers.Dock = DockStyle.Fill;
            printers.Margin = new Padding(0, 3, 12, 0);
            printers.AccessibleName = "Printer for this snip";
            printers.SelectedIndexChanged += delegate
            {
                if (updatingPrinters || printing || printers.SelectedItem == null) return;
                SelectPrinter(printers.SelectedItem.ToString());
            };
            printerRow.Controls.Add(printers, 1, 0);
            ConfigureButton(refresh, "Refresh", "redo", 110);
            refresh.Margin = new Padding(0);
            refresh.Click += delegate { LoadPrinters(); };
            printerRow.Controls.Add(refresh, 2, 0);

            details.AutoSize = true;
            details.ForeColor = StudioTheme.Muted;
            details.Margin = new Padding(0, 0, 0, 12);
            details.MaximumSize = new Size(900, 0);
            root.Controls.Add(details, 0, 3);

            Panel previewBorder = new Panel();
            previewBorder.Dock = DockStyle.Fill;
            previewBorder.Padding = new Padding(1);
            previewBorder.BackColor = StudioTheme.Border;
            previewBorder.Margin = new Padding(0, 0, 0, 14);
            root.Controls.Add(previewBorder, 0, 4);
            preview.Dock = DockStyle.Fill;
            preview.BackColor = StudioTheme.Background;
            preview.ForeColor = StudioTheme.Text;
            preview.AutoZoom = true;
            preview.UseAntiAlias = true;
            preview.Rows = 1;
            preview.Columns = 1;
            preview.AccessibleName = "Single page print preview";
            preview.PreviewFailed += delegate(object sender, PreviewFailureEventArgs e)
            {
                previewFailed = true;
                SetStatus("The preview could not be prepared: " + e.Message + " Your snip is unchanged.", true);
                UpdateButtons();
            };
            previewBorder.Controls.Add(preview);
            unavailable.Dock = DockStyle.Fill;
            unavailable.TextAlign = ContentAlignment.MiddleCenter;
            unavailable.Font = new Font("Segoe UI", 12f);
            unavailable.BackColor = StudioTheme.Background;
            unavailable.ForeColor = StudioTheme.Muted;
            unavailable.Padding = new Padding(30);
            unavailable.Text = "Loading printers…";
            unavailable.AccessibleName = "Print preview availability";
            previewBorder.Controls.Add(unavailable);
            unavailable.BringToFront();

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.ColumnCount = 1;
            footer.RowCount = 2;
            footer.AutoSize = true;
            footer.Margin = new Padding(0);
            root.Controls.Add(footer, 0, 5);
            status.AutoSize = true;
            status.ForeColor = StudioTheme.Muted;
            status.MaximumSize = new Size(900, 0);
            status.Margin = new Padding(0, 0, 0, 12);
            status.Text = "Preparing your preview…";
            footer.Controls.Add(status, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.Margin = new Padding(0);
            footer.Controls.Add(actions, 0, 1);
            ConfigureButton(print, "Print…", "print", 120);
            print.Primary = true;
            print.Click += PrintImage;
            actions.Controls.Add(print);
            ConfigureButton(pageSetup, "Page setup…", "window", 156);
            pageSetup.Click += delegate { ChoosePageSetup(); };
            actions.Controls.Add(pageSetup);
            ConfigureButton(fit, "Fit page", "fit", 114);
            fit.Click += delegate { preview.AutoZoom = true; preview.Invalidate(); };
            actions.Controls.Add(fit);
            ConfigureButton(close, "Close", "", 88);
            close.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(close);
            CancelButton = close;
            Resize += delegate
            {
                int wrapWidth = Math.Max(300, ClientSize.Width - 60);
                description.MaximumSize = new Size(wrapWidth, 0);
                details.MaximumSize = new Size(wrapWidth, 0);
                status.MaximumSize = new Size(wrapWidth, 0);
            };
            UpdateButtons();
        }

        private static void ConfigureButton(StudioButton button, string label, string glyph, int width)
        {
            button.Text = label;
            button.Glyph = glyph;
            button.Size = new Size(width, 40);
            button.Margin = new Padding(0, 0, 8, 0);
            button.AccessibleName = label;
            button.Cursor = Cursors.Hand;
        }

        private void LoadPrinters()
        {
            if (printing) return;
            string selected = printers.SelectedItem == null ? null : printers.SelectedItem.ToString();
            updatingPrinters = true;
            try
            {
                printers.Items.Clear();
                foreach (string name in PrinterSettings.InstalledPrinters) printers.Items.Add(name);
                if (printers.Items.Count == 0)
                {
                    ShowUnavailable("No printer is available.\r\nConnect a printer or enable a PDF printer, then select Refresh.");
                    details.Text = snapshot.Width + " × " + snapshot.Height + " px  ·  1 page";
                    SetStatus("No installed printer was found. Your snip is unchanged.", true);
                    return;
                }
                if (String.IsNullOrEmpty(selected)) selected = document.PrinterSettings.PrinterName;
                int index = printers.Items.IndexOf(selected);
                printers.SelectedIndex = index < 0 ? 0 : index;
            }
            catch (Exception error)
            {
                ShowUnavailable("Windows could not list the printers.\r\nCheck the printer connection, then select Refresh.");
                SetStatus("Printer information is unavailable: " + error.Message, true);
                return;
            }
            finally { updatingPrinters = false; UpdateButtons(); }
            SelectPrinter(printers.SelectedItem.ToString());
        }

        private void SelectPrinter(string name)
        {
            try
            {
                PrinterSettings settings = new PrinterSettings();
                settings.PrinterName = name;
                if (!settings.IsValid) throw new InvalidPrinterException(settings);
                document.PrinterSettings = settings;
                document.DefaultPageSettings = new PageSettings(settings);
                document.DefaultPageSettings.Landscape = landscape;
                document.DefaultPageSettings.Margins = (Margins)margins.Clone();
                RefreshPreview();
            }
            catch (Exception error)
            {
                ShowUnavailable("This printer could not prepare a preview.\r\nChoose another printer or select Refresh.");
                SetStatus("Printer unavailable: " + error.Message, true);
            }
            UpdateButtons();
        }

        private void RefreshPreview()
        {
            previewFailed = false;
            try
            {
                if (!document.PrinterSettings.IsValid) throw new InvalidPrinterException(document.PrinterSettings);
                PageSettings page = document.DefaultPageSettings;
                // Force driver-dependent settings to be read before attaching the preview.
                RectangleF printable = page.PrintableArea;
                if (printable.Width <= 0 || printable.Height <= 0) throw new InvalidOperationException("The selected printer reports no printable area.");
                details.Text = snapshot.Width + " × " + snapshot.Height + " px  ·  1 page  ·  " +
                    (page.Landscape ? "Landscape" : "Portrait") + "  ·  " + page.PaperSize.PaperName;
                unavailable.Visible = false;
                preview.Visible = true;
                preview.AutoZoom = true;
                preview.ResetFailure();
                preview.Document = document;
                preview.InvalidatePreview();
                SetStatus("Preview uses " + document.PrinterSettings.PrinterName + ". Printing starts only after you confirm in the Print dialog.", false);
            }
            catch (Exception error)
            {
                ShowUnavailable("A preview is unavailable for this printer.\r\nChoose another printer or check Page setup.");
                SetStatus("The preview could not be prepared: " + error.Message, true);
            }
            UpdateButtons();
        }

        private void ShowUnavailable(string message)
        {
            previewFailed = true;
            preview.Document = null;
            preview.Visible = false;
            unavailable.Text = message;
            unavailable.Visible = true;
            unavailable.BringToFront();
        }

        private void ChoosePageSetup()
        {
            if (printing || !HasPrinter()) return;
            try
            {
                using (PrintDocument settingsDocument = CloneSettings(document))
                using (PageSetupDialog dialog = new PageSetupDialog())
                {
                    dialog.Document = settingsDocument;
                    dialog.AllowMargins = true;
                    dialog.AllowOrientation = true;
                    dialog.AllowPaper = true;
                    dialog.AllowPrinter = true;
                    dialog.EnableMetric = true;
                    dialog.MinMargins = new Margins(0, 0, 0, 0);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    ApplySettings(settingsDocument);
                }
                RefreshPreview();
            }
            catch (Exception error) { SetStatus("Page setup could not be opened: " + error.Message + " Your snip is unchanged.", true); }
        }

        private void ApplySettings(PrintDocument source)
        {
            document.PrinterSettings = (PrinterSettings)source.PrinterSettings.Clone();
            document.DefaultPageSettings = (PageSettings)source.DefaultPageSettings.Clone();
            document.DefaultPageSettings.PrinterSettings = document.PrinterSettings;
            landscape = document.DefaultPageSettings.Landscape;
            margins = (Margins)document.DefaultPageSettings.Margins.Clone();
            updatingPrinters = true;
            try
            {
                int index = printers.Items.IndexOf(document.PrinterSettings.PrinterName);
                if (index < 0) index = printers.Items.Add(document.PrinterSettings.PrinterName);
                printers.SelectedIndex = index;
            }
            finally { updatingPrinters = false; }
        }

        private async void PrintImage(object sender, EventArgs e)
        {
            if (printing || !HasPrinter()) return;
            try
            {
                using (PrintDocument selection = CloneSettings(document))
                using (PrintDialog dialog = new PrintDialog())
                {
                    dialog.Document = selection;
                    dialog.UseEXDialog = true;
                    dialog.AllowSelection = false;
                    dialog.AllowSomePages = false;
                    dialog.AllowCurrentPage = false;
                    dialog.AllowPrintToFile = true;
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        SetStatus("Printing canceled. Your snip is unchanged.", false);
                        return;
                    }
                    if (!selection.PrinterSettings.IsValid) throw new InvalidPrinterException(selection.PrinterSettings);
                    ApplySettings(selection);
                }
                string printerName = document.PrinterSettings.PrinterName;
                printing = true;
                UpdateButtons();
                SetStatus("Sending one page to Windows for " + printerName + "…", false);
                using (Bitmap jobImage = new Bitmap(snapshot))
                using (PrintDocument job = CloneSettings(document))
                {
                    job.PrintController = new StandardPrintController();
                    job.PrintPage += delegate(object printSender, PrintPageEventArgs page) { RenderImagePage(jobImage, page); };
                    // Printer drivers can take time or show their own PDF file dialog. Keep
                    // the workspace responsive and use a separate STA for that driver UI.
                    await RunOnSta(delegate { job.Print(); });
                }
                SetStatus("Sent to Windows for " + printerName + ". Check the printer or Windows print queue for completion.", false);
            }
            catch (Exception error)
            {
                SetStatus("Printing could not finish: " + error.Message + " Check the Windows print queue before trying again. Your snip is unchanged.", true);
            }
            finally
            {
                printing = false;
                if (!IsDisposed && !Disposing) UpdateButtons();
            }
        }

        private static Task RunOnSta(Action action)
        {
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            Thread worker = new Thread(delegate()
            {
                try { action(); completion.SetResult(true); }
                catch (Exception error) { completion.SetException(error); }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return completion.Task;
        }

        private static PrintDocument CloneSettings(PrintDocument source)
        {
            PrintDocument clone = new PrintDocument();
            clone.DocumentName = source.DocumentName;
            clone.OriginAtMargins = true;
            clone.PrinterSettings = (PrinterSettings)source.PrinterSettings.Clone();
            clone.DefaultPageSettings = (PageSettings)source.DefaultPageSettings.Clone();
            clone.DefaultPageSettings.PrinterSettings = clone.PrinterSettings;
            return clone;
        }

        private void OnPreviewPage(object sender, PrintPageEventArgs e)
        {
            RenderImagePage(snapshot, e);
        }

        private static void RenderImagePage(Bitmap image, PrintPageEventArgs e)
        {
            RectangleF area = RectangleF.Intersect(e.MarginBounds, e.PageSettings.PrintableArea);
            if (area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("The page margins leave no printable space. Change the margins in Page setup.");
            // OriginAtMargins places graphics at the user margins. PrintableArea is in
            // physical page coordinates, so convert the safe intersection to that origin.
            area.Offset(-e.MarginBounds.Left, -e.MarginBounds.Top);
            RectangleF destination = CalculateFitBounds(image.Size, area);
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                e.Graphics.FillRectangle(Brushes.White, destination);
                e.Graphics.DrawImage(image, destination, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
            }
            finally { e.Graphics.Restore(state); }
            e.HasMorePages = false;
        }

        public static RectangleF CalculateFitBounds(Size imageSize, RectangleF available)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0) throw new ArgumentOutOfRangeException("imageSize");
            if (available.Width <= 0 || available.Height <= 0 || Single.IsNaN(available.Width) || Single.IsNaN(available.Height) || Single.IsInfinity(available.Width) || Single.IsInfinity(available.Height))
                throw new ArgumentOutOfRangeException("available");
            float scale = Math.Min(available.Width / imageSize.Width, available.Height / imageSize.Height);
            float width = imageSize.Width * scale;
            float height = imageSize.Height * scale;
            return new RectangleF(available.X + (available.Width - width) / 2, available.Y + (available.Height - height) / 2, width, height);
        }

        private bool HasPrinter()
        {
            try { return printers.Items.Count > 0 && document.PrinterSettings.IsValid; }
            catch (Exception) { return false; }
        }

        private void UpdateButtons()
        {
            bool valid = HasPrinter();
            // A failing driver must not prevent switching to another listed printer.
            printers.Enabled = printers.Items.Count > 0 && !printing;
            print.Enabled = valid && !previewFailed && !printing;
            pageSetup.Enabled = valid && !printing;
            fit.Enabled = valid && !previewFailed && !printing;
            refresh.Enabled = !printing;
            close.Enabled = !printing;
        }

        private void SetStatus(string message, bool problem)
        {
            if (IsDisposed || Disposing) return;
            status.Text = message;
            status.ForeColor = problem ? StudioTheme.Accent : StudioTheme.Muted;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                preview.Document = null;
                document.Dispose();
                if (snapshot != null) { snapshot.Dispose(); snapshot = null; }
            }
            base.Dispose(disposing);
        }

        private sealed class PreviewFailureEventArgs : EventArgs
        {
            internal readonly string Message;
            internal PreviewFailureEventArgs(string message) { Message = message; }
        }

        private sealed class SafePrintPreviewControl : PrintPreviewControl
        {
            internal event EventHandler<PreviewFailureEventArgs> PreviewFailed;
            private bool reported;
            internal void ResetFailure() { reported = false; }

            protected override void OnPaint(PaintEventArgs e)
            {
                try { base.OnPaint(e); }
                catch (Exception error)
                {
                    e.Graphics.Clear(StudioTheme.Background);
                    TextRenderer.DrawText(e.Graphics, "The print preview could not be prepared.", Font,
                        ClientRectangle, StudioTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    if (!reported)
                    {
                        reported = true;
                        EventHandler<PreviewFailureEventArgs> handler = PreviewFailed;
                        if (handler != null) handler(this, new PreviewFailureEventArgs(error.Message));
                    }
                }
            }
        }
    }
}
