#nullable enable
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

// Transient native desktop HUDs belong to the capture STA only.
// The editor and options remain WinUI. No legacy MainForm dependency.
internal sealed class CaptureSessionHud : Form
{
    private readonly Rectangle captureRegion;
    private readonly CancellationTokenSource cancellation;
    private readonly Label progress = new();
    private readonly Label status = new();
    private bool closingAllowed;
    internal bool FinishRequested { get; private set; }
    internal bool IsOutsideCapture
    {
        get { Rectangle occupied = Bounds; occupied.Inflate(16, 16); return !occupied.IntersectsWith(captureRegion); }
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } // WS_EX_NOACTIVATE
    }

    internal CaptureSessionHud(Rectangle region, CancellationTokenSource cancellation)
    {
        captureRegion = region;
        this.cancellation = cancellation;
        Text = "Scrolling capture — Snip Studio";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(440, 160);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progress.AutoSize = true;
        progress.Text = "Preparing scrolling capture…";
        progress.AccessibleName = "Captured views and image height";
        progress.Margin = new Padding(0, 0, 0, 8);
        status.Dock = DockStyle.Fill;
        status.Text = "Keep the selected window in front. Finish keeps the accepted portion; Esc cancels.";
        status.AccessibleName = "Scrolling capture status";
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        var finish = new Button { Text = "Finish", AutoSize = true, MinimumSize = new Size(112, 32), AccessibleName = "Finish and review captured content" };
        var cancel = new Button { Text = "Cancel (Esc)", AutoSize = true, MinimumSize = new Size(112, 32), AccessibleName = "Cancel scrolling capture" };
        finish.MouseDown += (_, args) => { if (args.Button == MouseButtons.Left) FinishRequested = true; };
        finish.Click += (_, _) => FinishRequested = true;
        cancel.MouseDown += (_, args) => { if (args.Button == MouseButtons.Left) cancellation.Cancel(); };
        cancel.Click += (_, _) => cancellation.Cancel();
        actions.Controls.Add(finish);
        actions.Controls.Add(cancel);
        layout.Controls.Add(progress, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        PlaceOutsideRegion();
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        int disabled = 1;
        _ = DwmSetWindowAttribute(Handle, 3, ref disabled, sizeof(int));
        // Optional defense in depth. Overlapping HUDs are hidden for every
        // frame regardless of whether this Windows version accepts WDA_EXCLUDEFROMCAPTURE.
        _ = SetWindowDisplayAffinity(Handle, 0x11);
    }

    private void PlaceOutsideRegion()
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle area = screen.WorkingArea;
            Point[] locations = [new(area.Left + 12, area.Top + 12), new(area.Right - Width - 12, area.Top + 12),
                new(area.Left + 12, area.Bottom - Height - 12), new(area.Right - Width - 12, area.Bottom - Height - 12)];
            foreach (Point location in locations)
            {
                Rectangle occupied = new(location, Size);
                occupied.Inflate(16, 16);
                if (area.Contains(new Rectangle(location, Size)) && !occupied.IntersectsWith(captureRegion))
                { Location = location; return; }
            }
        }
        Rectangle fallback = Screen.FromRectangle(captureRegion).WorkingArea;
        Location = new Point(fallback.Left + 12, fallback.Top + 12);
    }

    internal void UpdateProgress(int frames, int height, string message)
    {
        if (IsDisposed) return;
        progress.Text = $"{frames:N0} view{(frames == 1 ? "" : "s")} · {height:N0} px high";
        status.Text = message + "  Finish keeps the accepted portion; Esc cancels.";
    }
    internal void FinishAndClose() { closingAllowed = true; Close(); }
    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (!closingAllowed && !cancellation.IsCancellationRequested) cancellation.Cancel();
        base.OnFormClosing(args);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

internal sealed class CaptureDelayHud : Form
{
    private readonly Label countdown = new();
    private bool closingAllowed;
    internal bool CancelRequested { get; private set; }
    internal CaptureDelayHud(int seconds)
    {
        Text = "Preparing capture — Snip Studio";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = SystemFonts.MessageBoxFont;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(350, 120);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        countdown.AutoSize = true;
        countdown.Margin = new Padding(0, 0, 0, 16);
        SetSeconds(seconds);
        var cancel = new Button { Text = "Cancel (Esc)", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(112, 32) };
        cancel.Click += (_, _) => { CancelRequested = true; Close(); };
        CancelButton = cancel;
        layout.Controls.Add(countdown);
        layout.Controls.Add(cancel);
        Controls.Add(layout);
    }
    internal void SetSeconds(int seconds) => countdown.Text = $"Capture in {seconds}… Prepare your screen.";
    internal void FinishAndClose() { closingAllowed = true; Close(); }
    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (!closingAllowed) CancelRequested = true;
        base.OnFormClosing(args);
    }
}
