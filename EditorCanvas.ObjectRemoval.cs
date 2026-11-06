using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private Rectangle removalArea;
        private object removalImageIdentity;
        private long removalRevision;
        private Color removalFill = Color.White;
        private bool removalDragging;
        private bool removalMoving;
        private Rectangle removalMoveOriginalArea;
        private PointF removalMoveStart;
        private Point removalDragClientStart;
        private PointF removalDragStart, removalDragEnd;
        private CancellationTokenSource removalCancellation;
        private ContextMenuStrip removalPopup;
        private string removalStatus = "Click a flat-color object or drag an area, then review and press Delete.";

        public event EventHandler SourceRemovalSelectionChanged;
        public bool HasSelectedRemovalArea { get { return !removalArea.IsEmpty && RemovalSelectionIsCurrent; } }
        public Rectangle SelectedRemovalArea { get { return HasSelectedRemovalArea ? removalArea : Rectangle.Empty; } }
        public string SourceRemovalStatus { get { return removalStatus; } }
        public Color SourceRemovalFillColor { get { return removalFill; } }
        private bool RemovalSelectionIsCurrent { get { return HasImage && Object.ReferenceEquals(removalImageIdentity, ImageIdentity) && removalRevision == DocumentRevision; } }
        private bool CanEditSourceRemovalSelection { get { return !textActionsMode && (tool == EditorTool.RemoveArea || tool == EditorTool.Select); } }

        private bool IsOnSourceRemovalSelection(Point location)
        {
            return HasSelectedRemovalArea && IsOnImage(location) && ((RectangleF)removalArea).Contains(ToImage(location, false));
        }

        // Moving this review box changes only the future removal coordinates.
        // The source pixels, existing annotations and Undo history are untouched.
        public bool MoveSourceRemovalSelection(Point delta)
        {
            return MoveSourceRemovalSelectionTo((long)removalArea.X + delta.X, (long)removalArea.Y + delta.Y, true);
        }

        private bool MoveSourceRemovalSelectionTo(long x, long y, bool notify)
        {
            if (!HasSelectedRemovalArea || !CanEditSourceRemovalSelection) return false;
            Point next = new Point((int)Math.Max(0L, Math.Min((long)ImageSize.Width - removalArea.Width, x)),
                (int)Math.Max(0L, Math.Min((long)ImageSize.Height - removalArea.Height, y)));
            if (next == removalArea.Location) return false;
            removalArea.Location = next;
            if (notify) ReportSourceRemovalMoved();
            Invalidate();
            return true;
        }

        private void ReportSourceRemovalMoved()
        {
            SetSourceRemovalStatus("Removal selection moved. Delete fills the selected " + removalArea.Width + " × " + removalArea.Height +
                " pixels with " + RemovalColorText + "; Escape cancels. Undo restores deleted pixels.");
        }

        private bool HandleSourceRemovalKey(Keys keyData)
        {
            if (textActionsMode || !CanEditSourceRemovalSelection) return false;
            if (keyData == Keys.Escape && (HasSelectedRemovalArea || tool == EditorTool.RemoveArea)) { CancelGesture(); return true; }
            if (!HasSelectedRemovalArea) return false;
            if (keyData == Keys.Delete) { RemoveSelectedSourceArea(); return true; }
            if (tool != EditorTool.Select || (keyData & (Keys.Control | Keys.Alt)) != 0) return false;
            int amount = (keyData & Keys.Shift) != 0 ? 10 : 1;
            Point delta = Point.Empty;
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Left) delta.X = -amount; else if (code == Keys.Right) delta.X = amount;
            else if (code == Keys.Up) delta.Y = -amount; else if (code == Keys.Down) delta.Y = amount;
            else return false;
            MoveSourceRemovalSelection(delta);
            return true;
        }

        public void ChooseSourceRemovalFillColor(IWin32Window owner)
        {
            if (!HasSelectedRemovalArea || !CanEditSourceRemovalSelection) return;
            using (ColorPaletteDialog picker = new ColorPaletteDialog(removalFill, new Color[0]))
            {
                if (picker.ShowDialog(owner ?? FindForm()) == DialogResult.OK && HasSelectedRemovalArea)
                {
                    removalFill = picker.SelectedColor;
                    SetSourceRemovalStatus("Selected area will be filled with " + RemovalColorText + ". Press Delete to remove; Undo restores the pixels.");
                }
            }
        }

        public void SelectSourceRemovalArea(Rectangle area)
        {
            CancelSourceRemoval();
            if (!HasImage || textActionsMode || tool != EditorTool.RemoveArea) return;
            area.Intersect(new Rectangle(Point.Empty, ImageSize));
            if (area.Width <= 0 || area.Height <= 0) return;
            Color foreground;
            SampleTextColors(area, out removalFill, out foreground);
            removalArea = area;
            removalImageIdentity = ImageIdentity;
            removalRevision = DocumentRevision;
            SetSourceRemovalStatus("Selected " + area.Width + " × " + area.Height + " source pixels. Drag this box or switch to Move to reposition it. Delete fills with " + RemovalColorText + "; right-click to change the fill. Undo restores the pixels.");
            Invalidate();
        }

        public bool RemoveSelectedSourceArea()
        {
            if (!HasSelectedRemovalArea || !CanEditSourceRemovalSelection) return false;
            // Deleting during a box drag must release its mouse capture without
            // canceling the reviewed selection before the document commit.
            if (removalMoving) { removalMoving = false; Capture = false; UpdateCursor(); }
            Rectangle area = removalArea;
            Color fillColor = removalFill;
            try
            {
                if (!document.EraseImageRegions(new[] { area }, fillColor)) return false;
            }
            catch (Exception)
            {
                SetSourceRemovalStatus("The area could not be removed. Your snip is unchanged; try a smaller area.");
                return false;
            }
            CancelSourceRemoval();
            selectedIndex = -1;
            SetSourceRemovalStatus("Area removed with solid fill " + ColorText(fillColor) + ". Undo restores the original pixels.");
            RaiseChanged();
            return true;
        }

        private string RemovalColorText { get { return ColorText(removalFill); } }
        private static string ColorText(Color color) { return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2"); }

        private void SetSourceRemovalStatus(string value)
        {
            removalStatus = value;
            if (disposingCanvas) return;
            EventHandler changed = SourceRemovalSelectionChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private void CancelSourceRemoval()
        {
            CancelSourceRemoval(false);
        }

        private void CancelSourceRemoval(bool preserveSelection)
        {
            if (removalCancellation != null) removalCancellation.Cancel();
            bool keep = preserveSelection && HasSelectedRemovalArea;
            bool hadSelection = !removalArea.IsEmpty || removalDragging || removalMoving || removalCancellation != null;
            if (!keep)
            {
                removalArea = Rectangle.Empty;
                removalImageIdentity = null;
            }
            removalDragging = false;
            removalMoving = false;
            if (removalPopup != null && removalPopup.Visible) removalPopup.Close();
            if (hadSelection) SetSourceRemovalStatus(keep
                ? "Drag the removal selection to reposition it. Arrow keys nudge in Move; Delete removes the selected area. Escape cancels."
                : "Click a flat-color object or drag an area, then review and press Delete.");
        }

        private bool BeginSourceRemoval(MouseEventArgs e)
        {
            if (textActionsMode || !CanEditSourceRemovalSelection || e.Button != MouseButtons.Left) return false;
            if (IsOnSourceRemovalSelection(e.Location))
            {
                selectedIndex = -1;
                removalMoving = true;
                removalMoveOriginalArea = removalArea;
                removalMoveStart = ToImage(e.Location, false);
                Capture = true;
                Cursor = Cursors.SizeAll;
                Invalidate();
                return true;
            }
            CancelSourceRemoval();
            if (tool == EditorTool.Select) return false;
            selectedIndex = -1;
            if (IsOnImage(e.Location))
            {
                removalDragging = true;
                removalDragClientStart = e.Location;
                removalDragStart = removalDragEnd = ToImage(e.Location, true);
                Capture = true;
            }
            Invalidate();
            return true;
        }

        private bool UpdateSourceRemoval(MouseEventArgs e)
        {
            if (removalMoving)
            {
                if (!HasSelectedRemovalArea) { CancelSourceRemoval(); Capture = false; return true; }
                PointF current = ToImage(e.Location, false);
                long x = (long)removalMoveOriginalArea.X + (long)Math.Round(current.X - removalMoveStart.X);
                long y = (long)removalMoveOriginalArea.Y + (long)Math.Round(current.Y - removalMoveStart.Y);
                MoveSourceRemovalSelectionTo(x, y, false);
                Cursor = Cursors.SizeAll;
                return true;
            }
            if (!removalDragging) return false;
            removalDragEnd = ToRemovalEdge(e.Location);
            Invalidate();
            return true;
        }

        private PointF ToRemovalEdge(Point point)
        {
            PointF value = ToImage(point, false);
            return new PointF(Math.Max(0, Math.Min(ImageSize.Width, value.X)), Math.Max(0, Math.Min(ImageSize.Height, value.Y)));
        }

        private bool CompleteSourceRemoval(MouseEventArgs e)
        {
            if (removalMoving && e.Button == MouseButtons.Left)
            {
                UpdateSourceRemoval(e);
                removalMoving = false;
                Capture = false;
                UpdateCursor();
                if (HasSelectedRemovalArea) ReportSourceRemovalMoved();
                Invalidate();
                return true;
            }
            if (!removalDragging || e.Button != MouseButtons.Left) return false;
            removalDragEnd = ToRemovalEdge(e.Location);
            bool dragged = Math.Abs(e.X - removalDragClientStart.X) >= 4 || Math.Abs(e.Y - removalDragClientStart.Y) >= 4;
            RectangleF drag = BoundsBetween(removalDragStart, removalDragEnd);
            Rectangle area = Rectangle.FromLTRB((int)Math.Floor(drag.Left), (int)Math.Floor(drag.Top), (int)Math.Ceiling(drag.Right), (int)Math.Ceiling(drag.Bottom));
            Point hit = new Point((int)removalDragStart.X, (int)removalDragStart.Y);
            removalDragging = false;
            Capture = false;
            if (dragged && area.Width > 0 && area.Height > 0) SelectSourceRemovalArea(area);
            else SelectFlatColorObjectAsync(hit);
            Invalidate();
            return true;
        }

        private async void SelectFlatColorObjectAsync(Point hit)
        {
            CancelSourceRemoval();
            if (!HasImage || tool != EditorTool.RemoveArea || textActionsMode) return;
            object identity = ImageIdentity;
            long revision = DocumentRevision;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            removalCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            SetSourceRemovalStatus("Finding the connected flat-color area… Escape cancels. Drag to select any region manually.");
            try
            {
                using (Bitmap image = document.CopyBaseImage())
                {
                    Rectangle area = await Task.Run(delegate { return FindFlatColorRegion(image, hit, token); });
                    if (disposingCanvas || token.IsCancellationRequested || tool != EditorTool.RemoveArea || textActionsMode ||
                        !Object.ReferenceEquals(identity, ImageIdentity) || revision != DocumentRevision) return;
                    // Retire this request before selecting; selection cancels any older request.
                    if (Object.ReferenceEquals(removalCancellation, cancellation)) removalCancellation = null;
                    if (area.IsEmpty) SetSourceRemovalStatus("No bounded flat-color object was selected. Drag a rectangle around the area to remove.");
                    else SelectSourceRemovalArea(area);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!disposingCanvas && !token.IsCancellationRequested && Object.ReferenceEquals(identity, ImageIdentity) && revision == DocumentRevision)
                    SetSourceRemovalStatus("Automatic area selection is unavailable. Drag a rectangle to select the area.");
            }
            finally
            {
                if (Object.ReferenceEquals(removalCancellation, cancellation)) removalCancellation = null;
                cancellation.Dispose();
            }
        }

        // Color connectivity is an aid for flat screenshot UI, not semantic object recognition.
        // Work, memory and elapsed time are bounded; manual selection remains available.
        internal static Rectangle FindFlatColorRegion(Bitmap image, Point hit, CancellationToken token)
        {
            if (image == null) throw new ArgumentNullException("image");
            token.ThrowIfCancellationRequested();
            int width = image.Width, height = image.Height;
            long pixelCount = (long)width * height;
            if (hit.X < 0 || hit.Y < 0 || hit.X >= width || hit.Y >= height || pixelCount > 20000000) return Rectangle.Empty;
            Stopwatch clock = Stopwatch.StartNew();
            int[] pixels = new int[(int)pixelCount];
            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    if ((y & 127) == 0) token.ThrowIfCancellationRequested();
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width, width);
                }
            }
            finally { image.UnlockBits(data); }
            int seed = hit.Y * width + hit.X, target = pixels[seed];
            byte[] visited = new byte[pixels.Length];
            int[] pending = new int[pixels.Length];
            int read = 0, write = 1, count = 0;
            pending[0] = seed;
            visited[seed] = 1;
            int left = hit.X, right = hit.X, top = hit.Y, bottom = hit.Y;
            while (read < write)
            {
                if ((read & 4095) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (clock.ElapsedMilliseconds > 2000) return Rectangle.Empty;
                }
                int index = pending[read++], x = index % width, y = index / width;
                count++;
                if (count > pixelCount * .8) return Rectangle.Empty;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                if (x > 0) QueueFlatPixel(index - 1, target, pixels, visited, pending, ref write);
                if (x + 1 < width) QueueFlatPixel(index + 1, target, pixels, visited, pending, ref write);
                if (y > 0) QueueFlatPixel(index - width, target, pixels, visited, pending, ref write);
                if (y + 1 < height) QueueFlatPixel(index + width, target, pixels, visited, pending, ref write);
            }
            int edges = (left == 0 ? 1 : 0) + (right == width - 1 ? 1 : 0) + (top == 0 ? 1 : 0) + (bottom == height - 1 ? 1 : 0);
            if (count < 12 || right - left < 3 || bottom - top < 3 || edges >= 3) return Rectangle.Empty;
            return Rectangle.FromLTRB(Math.Max(0, left - 2), Math.Max(0, top - 2), Math.Min(width, right + 3), Math.Min(height, bottom + 3));
        }

        private static void QueueFlatPixel(int index, int target, int[] pixels, byte[] visited, int[] pending, ref int write)
        {
            if (visited[index] != 0) return;
            visited[index] = 1;
            int color = pixels[index];
            if (Math.Abs((color & 255) - (target & 255)) <= 22 && Math.Abs(((color >> 8) & 255) - ((target >> 8) & 255)) <= 22 &&
                Math.Abs(((color >> 16) & 255) - ((target >> 16) & 255)) <= 22 && Math.Abs(((color >> 24) & 255) - ((target >> 24) & 255)) <= 16)
                pending[write++] = index;
        }

        private void DrawSourceRemoval(Graphics graphics)
        {
            if (textActionsMode || !CanEditSourceRemovalSelection) return;
            RectangleF bounds = removalDragging ? BoundsBetween(removalDragStart, removalDragEnd) : SelectedRemovalArea;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using (Brush tint = new SolidBrush(Color.FromArgb(42, 255, 181, 0)))
            using (Pen halo = new Pen(Color.Black, 3 / zoom))
            using (Pen line = new Pen(Color.White, 1.3f / zoom))
            {
                graphics.FillRectangle(tint, bounds);
                graphics.DrawRectangle(halo, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                line.DashStyle = DashStyle.Dash;
                graphics.DrawRectangle(line, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
        }

        private void ShowSourceRemovalMenu(Point location)
        {
            if (removalDragging || removalMoving) { removalDragging = false; removalMoving = false; Capture = false; }
            if (removalPopup != null) removalPopup.Dispose();
            removalPopup = new ContextMenuStrip();
            ToolStripMenuItem move = new ToolStripMenuItem("Move selection");
            move.Enabled = HasSelectedRemovalArea;
            move.Checked = tool == EditorTool.Select;
            move.Click += delegate { Tool = EditorTool.Select; Focus(); };
            ToolStripMenuItem remove = new ToolStripMenuItem("Remove selected area");
            remove.ShortcutKeyDisplayString = "Delete";
            remove.Enabled = HasSelectedRemovalArea;
            remove.Click += delegate { RemoveSelectedSourceArea(); Focus(); };
            ToolStripMenuItem fill = new ToolStripMenuItem("Choose fill color… (" + RemovalColorText + ")");
            fill.Enabled = HasSelectedRemovalArea;
            fill.Click += delegate { ChooseSourceRemovalFillColor(FindForm()); };
            ToolStripMenuItem cancel = new ToolStripMenuItem("Cancel selection");
            cancel.ShortcutKeyDisplayString = "Esc";
            cancel.Click += delegate { CancelSourceRemoval(); Invalidate(); Focus(); };
            removalPopup.Items.Add(move); removalPopup.Items.Add(remove); removalPopup.Items.Add(fill); removalPopup.Items.Add(cancel);
            StudioTheme.StyleMenu(removalPopup);
            removalPopup.Show(this, location);
        }

        private void DisposeSourceRemoval()
        {
            CancelSourceRemoval();
            if (removalPopup != null) { removalPopup.Dispose(); removalPopup = null; }
        }
    }
}
