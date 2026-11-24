#nullable enable
using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

internal sealed class DesktopPrintPreview : Form
{
    private readonly byte[] png;
    private readonly Size imageSize;
    private readonly ComboBox printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Printer" };
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button setup = new() { Text = "Page setup…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button print = new() { Text = "Print…", AutoSize = true };
    private readonly Button close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
    private readonly PreviewSurface preview;
    private PrinterSettings? printer;
    private PageSettings? page;
    private bool working, submitting;

    public DesktopPrintPreview(byte[] png, Size imageSize, Bitmap thumbnail)
    {
        this.png = png;
        this.imageSize = imageSize;
        preview = new PreviewSurface(thumbnail);
        Text = "Snip Studio — Print preview";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(626, 568);
        MinimumSize = new Size(370, 365);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;

        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Padding = new Padding(12), Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var printerRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        printerRow.Controls.Add(new Label { Text = "&Printer", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        printerRow.Controls.Add(printers, 1, 0);
        printerRow.Controls.Add(refresh, 2, 0);
        layout.Controls.Add(printerRow, 0, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.Controls.Add(status, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            WrapContents = true
        };
        setup.TabIndex = 0;
        print.TabIndex = 1;
        close.TabIndex = 2;
        buttons.Controls.AddRange([close, print, setup]);
        layout.Controls.Add(buttons, 0, 3);
        AcceptButton = print;
        CancelButton = close;

        printers.SelectedIndexChanged += async (_, _) => await LoadPrinterAsync(refreshList: false);
        refresh.Click += async (_, _) => await LoadPrinterAsync(refreshList: true);
        setup.Click += async (_, _) => await ChangePageSetupAsync();
        print.Click += async (_, _) => await SendPrintJobAsync();
        layout.SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        SetWorking(false);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToWorkArea();
        await LoadPrinterAsync(refreshList: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToWorkArea();
    }

    private void FitToWorkArea()
    {
        Rectangle area = Screen.FromControl(this).WorkingArea;
        float scale = DeviceDpi / 96f;
        MinimumSize = new Size(Math.Min((int)Math.Ceiling(370 * scale), area.Width), Math.Min((int)Math.Ceiling(365 * scale), area.Height));
        var size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Bounds = new Rectangle(Math.Clamp(Left, area.Left, area.Right - size.Width),
            Math.Clamp(Top, area.Top, area.Bottom - size.Height), size.Width, size.Height);
    }

    private async Task LoadPrinterAsync(bool refreshList)
    {
        if (working || IsDisposed) return;
        SetWorking(true);
        status.Text = refreshList ? "Looking for printers…" : "Preparing the preview…";
        try
        {
            if (refreshList)
            {
                string? previousName = printers.SelectedItem as string;
                var printServices = await OnSta(() => (Names: PrinterSettings.InstalledPrinters.Cast<string>().ToArray(),
                    Default: new PrinterSettings().PrinterName));
                if (IsDisposed) return;
                printers.Items.Clear();
                printers.Items.AddRange(printServices.Names);
                string? preferredPrinter = previousName ?? printServices.Default;
                int selection = preferredPrinter is null ? -1 : printers.Items.IndexOf(preferredPrinter);
                if (printers.Items.Count > 0) printers.SelectedIndex = Math.Max(0, selection);
            }

            if (printers.SelectedItem is not string name)
            {
                printer = null;
                page = null;
                preview.SetPage(null);
                status.Text = "No printers are available. Connect a printer or enable a PDF printer, then select Refresh.";
                return;
            }

            bool landscape = page?.Landscape ?? imageSize.Width > imageSize.Height;
            Margins margins = page is null ? new Margins(36, 36, 36, 36) : (Margins)page.Margins.Clone();
            var selectedPrinter = new PrinterSettings { PrinterName = name };
            PageSettings selectedPage = printer?.PrinterName == name && page is not null
                ? (PageSettings)page.Clone()
                : new PageSettings(selectedPrinter) { Landscape = landscape, Margins = margins };
            await ApplyPageAsync(selectedPrinter, selectedPage);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (IsDisposed) return;
            printer = null;
            page = null;
            preview.SetPage(null);
            status.Text = "Printer unavailable. Choose another printer or select Refresh. " + error.Message;
        }
        finally { SetWorking(false); }
    }

    private async Task ApplyPageAsync(PrinterSettings selectedPrinter, PageSettings selectedPage)
    {
        selectedPage.PrinterSettings = selectedPrinter;
        PreviewPage next = await OnSta(() => ReadPage(selectedPrinter, selectedPage, imageSize));
        if (IsDisposed) return;
        printer = selectedPrinter;
        page = selectedPage;
        preview.SetPage(next);
        int index = printers.Items.IndexOf(selectedPrinter.PrinterName);
        if (index >= 0) printers.SelectedIndex = index;
        status.Text = $"1 page · {next.PaperName} · {(next.Landscape ? "Landscape" : "Portrait")}";
    }

    private async Task ChangePageSetupAsync()
    {
        if (working || printer is null || page is null) return;
        SetWorking(true);
        try
        {
            using PrintDocument document = DesktopPrintingService.PrepareDocument(printer, page);
            using var options = new PageSetupDialog { Document = document, AllowPrinter = false, EnableMetric = true };
            if (options.ShowDialog(this) == DialogResult.OK)
                await ApplyPageAsync(document.PrinterSettings, document.DefaultPageSettings);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!IsDisposed) status.Text = "Page setup could not be applied. The previous preview is still available. " + error.Message;
        }
        finally { SetWorking(false); }
    }

    private async Task SendPrintJobAsync()
    {
        if (working || printer is null || page is null) return;
        SetWorking(true);
        try
        {
            using PrintDocument document = DesktopPrintingService.PrepareDocument(printer, page);
            using var options = new PrintDialog
            {
                Document = document,
                UseEXDialog = true,
                AllowCurrentPage = false,
                AllowSelection = false,
                AllowSomePages = false,
                ShowNetwork = true
            };
            if (options.ShowDialog(this) != DialogResult.OK)
            {
                status.Text = "Printing canceled.";
                return;
            }
            submitting = true;
            close.Enabled = false;
            status.Text = "Sending one page to Windows…";
            await OnSta(() => DesktopPrintingService.Submit(document, png));
            if (IsDisposed) return;
            submitting = false;
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!IsDisposed) status.Text = "Printing could not finish. Check the Windows print queue before retrying. " + error.Message;
        }
        finally
        {
            submitting = false;
            SetWorking(false);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (submitting && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    private void SetWorking(bool active)
    {
        working = active;
        if (IsDisposed) return;
        printers.Enabled = !active && printers.Items.Count > 0;
        refresh.Enabled = !active;
        setup.Enabled = !active && page is not null;
        print.Enabled = !active && page is not null;
        close.Enabled = !submitting;
    }

    private static PreviewPage ReadPage(PrinterSettings printer, PageSettings page, Size imageSize)
    {
        if (!printer.IsValid) throw new InvalidPrinterException(printer);
        Rectangle bounds = page.Bounds;
        Margins margins = page.Margins;
        RectangleF available = RectangleF.FromLTRB(margins.Left, margins.Top, bounds.Width - margins.Right, bounds.Height - margins.Bottom);
        RectangleF destination = DesktopPrintingService.CalculateDestination(imageSize, available, page.PrintableArea);
        return new PreviewPage(bounds.Size, destination, page.PaperSize.PaperName, page.Landscape);
    }

    private static Task<TResult> OnSta<TResult>(Func<TResult> run)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.SetResult(run()); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Snip Studio printing" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private sealed record PreviewPage(Size Size, RectangleF Image, string PaperName, bool Landscape);

    private sealed class PreviewSurface : Control
    {
        private readonly Bitmap thumbnail;
        private PreviewPage? page;

        public PreviewSurface(Bitmap thumbnail)
        {
            this.thumbnail = thumbnail;
            Dock = DockStyle.Fill;
            BackColor = SystemColors.ControlDark;
            AccessibleName = "Single-page print preview";
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = false;
        }

        public void SetPage(PreviewPage? next)
        {
            page = next;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            const int inset = 12;
            if (page is null || ClientSize.Width <= 2 * inset || ClientSize.Height <= 2 * inset) return;
            RectangleF paper = DesktopPrintingService.FitInside(page.Size,
                new RectangleF(inset, inset, ClientSize.Width - 2 * inset, ClientSize.Height - 2 * inset));
            float scale = paper.Width / page.Size.Width;
            RectangleF destination = new(paper.Left + page.Image.X * scale, paper.Top + page.Image.Y * scale,
                page.Image.Width * scale, page.Image.Height * scale);
            e.Graphics.FillRectangle(Brushes.White, paper);
            DesktopPrintingService.DrawImage(e.Graphics, thumbnail, destination);
        }
    }
}
