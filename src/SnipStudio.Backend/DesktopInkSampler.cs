#nullable enable
using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

public static partial class CaptureCoordinator
{
    public static Task<uint?> PickDesktopInkAsync(CancellationToken cancellationToken = default) =>
        RunOnCaptureThreadAsync<uint?>(async token =>
        {
            await SettleCompositorAsync(token);
            ValidateDesktopSize();
            Rectangle desktop = SystemInformation.VirtualScreen;
            if ((long)desktop.Width * desktop.Height > 64_000_000)
                throw new InvalidOperationException("The connected desktop is too large to sample a color.");
            Rectangle[] monitors = Screen.AllScreens.Select(screen => screen.Bounds).ToArray();
            using Bitmap snapshot = global::SnipStudio.CaptureService.CaptureRegion(desktop);
            using var overlay = new InkSamplingOverlay(snapshot, desktop, monitors);
            SynchronizationContext dispatcher = SynchronizationContext.Current!;
            using var registration = token.Register(() => dispatcher.Post(_ =>
            {
                if (!overlay.IsDisposed && overlay.Visible) overlay.Close();
            }, null));
            token.ThrowIfCancellationRequested();
            DialogResult result = overlay.ShowDialog();
            token.ThrowIfCancellationRequested();
            return result == DialogResult.OK ? overlay.SelectedArgb : null;
        }, cancellationToken);

    private sealed class InkSamplingOverlay(Bitmap snapshot, Rectangle desktop, Rectangle[] monitors) : Form
    {
        private Rectangle swatchBounds;
        private Color? hovered;
        private bool wasActive;
        private bool shown;
        internal uint SelectedArgb { get; private set; }

        protected override void OnLoad(EventArgs args)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = desktop;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            AccessibleName = "Pick a screen color. Enter selects. Escape cancels.";
            base.OnLoad(args);
        }

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            if (!global::SnipStudio.CaptureNativeMethods.SetWindowPos(Handle,
                global::SnipStudio.CaptureNativeMethods.HwndTopmost, desktop.X, desktop.Y, desktop.Width,
                desktop.Height, global::SnipStudio.CaptureNativeMethods.SwpShowWindow))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            shown = true;
            Activate();
            UpdateSample(System.Windows.Forms.Cursor.Position);
        }

        protected override void OnActivated(EventArgs args)
        {
            base.OnActivated(args);
            wasActive = true;
        }

        protected override void OnDeactivate(EventArgs args)
        {
            base.OnDeactivate(args);
            if (shown && wasActive && DialogResult == DialogResult.None) Close();
        }

        protected override void OnPaintBackground(PaintEventArgs args) { }

        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.DrawImageUnscaled(snapshot, 0, 0);
            if (hovered is not { } color) return;
            args.Graphics.FillRectangle(SystemBrushes.Window, swatchBounds);
            args.Graphics.DrawRectangle(SystemPens.WindowText, swatchBounds);
            using var brush = new SolidBrush(color);
            Rectangle chip = new(swatchBounds.X + 7, swatchBounds.Y + 6, 20, swatchBounds.Height - 12);
            args.Graphics.FillRectangle(brush, chip);
            args.Graphics.DrawRectangle(SystemPens.WindowText, chip);
            var label = new Rectangle(chip.Right + 8, swatchBounds.Y, swatchBounds.Width - 38, swatchBounds.Height);
            TextRenderer.DrawText(args.Graphics, $"#{color.R:X2}{color.G:X2}{color.B:X2}    Esc cancels", SystemFonts.MessageBoxFont,
                label, SystemColors.WindowText, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            UpdateSample(PointToScreen(args.Location));
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button == MouseButtons.Right) Close();
            else if (args.Button == MouseButtons.Left) SelectSample(PointToScreen(args.Location));
        }

        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            if (key == Keys.Escape) { Close(); return true; }
            if (key == Keys.Enter) { SelectSample(System.Windows.Forms.Cursor.Position); return true; }
            return base.ProcessCmdKey(ref message, key);
        }

        protected override void WndProc(ref Message message)
        {
            const int displayChanged = 0x007E, dpiChanged = 0x02E0;
            if (shown && message.Msg is displayChanged or dpiChanged) Close();
            base.WndProc(ref message);
        }

        private Color? Sample(Point screen) =>
            desktop.Contains(screen) && monitors.Any(monitor => monitor.Contains(screen))
                ? snapshot.GetPixel(screen.X - desktop.X, screen.Y - desktop.Y)
                : null;

        private void SelectSample(Point screen)
        {
            if (Sample(screen) is not { } color) return;
            SelectedArgb = (uint)(color.ToArgb() | unchecked((int)0xFF000000));
            DialogResult = DialogResult.OK;
        }

        private void UpdateSample(Point screen)
        {
            Rectangle previous = swatchBounds;
            hovered = Sample(screen);
            if (hovered.HasValue)
            {
                Rectangle monitor = monitors.First(bounds => bounds.Contains(screen));
                int width = Math.Min(178, monitor.Width);
                int height = Math.Min(34, monitor.Height);
                int x = Math.Clamp(screen.X + 16, monitor.Left, monitor.Right - width);
                int y = screen.Y + 20 + height <= monitor.Bottom ? screen.Y + 20 : Math.Max(monitor.Top, screen.Y - height - 12);
                swatchBounds = new Rectangle(x - desktop.X, y - desktop.Y, width, height);
            }
            else swatchBounds = Rectangle.Empty;
            if (!previous.IsEmpty) { previous.Inflate(2, 2); Invalidate(previous); }
            if (!swatchBounds.IsEmpty)
            {
                Rectangle redraw = swatchBounds;
                redraw.Inflate(2, 2);
                Invalidate(redraw);
            }
        }
    }
}
