using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    // This window reads Windows' own per-application list. It never records a
    // second history, and preview selection never invokes the editor callback.
    internal sealed class RecentSnipsForm : Form
    {
        private readonly Func<CancellationToken, Task<RecentSnipsResult>> readRecent;
        private readonly Func<string, int, CancellationToken, Task<Bitmap>> readPreview;
        private readonly Action<string> openImage;
        private readonly List<RecentSnipRow> rows = new List<RecentSnipRow>();
        private readonly Label help = new Label();
        private readonly Label status = new Label();
        private readonly ListBox recentList = new ListBox();
        private readonly RecentSnipPreview preview = new RecentSnipPreview();
        private readonly Label filename = new Label();
        private readonly TextBox path = new TextBox();
        private readonly StudioButton refresh = new StudioButton();
        private readonly StudioButton open = new StudioButton();
        private readonly StudioButton close = new StudioButton();
        private readonly ToolTip tips = new ToolTip();
        private readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer();
        private CancellationTokenSource recentCancellation, previewCancellation;
        private int recentGeneration, previewGeneration, hoveredIndex = -1, previewIndex = -1;
        private bool disposed, opened, openingImage;
        internal bool RestoreWithoutActivation { get; set; }
        protected override bool ShowWithoutActivation { get { return RestoreWithoutActivation; } }

        internal RecentSnipsForm(Action<string> openImage)
            : this(delegate(CancellationToken token) { return RecentSnipService.GetRecentAsync(32, token); },
                RecentSnipService.LoadThumbnailAsync, openImage) { }

        // Injected providers also let signed, unshown checks use synthetic data
        // without reading Windows history, the shell, or any user's image files.
        internal RecentSnipsForm(Func<CancellationToken, Task<RecentSnipsResult>> readRecent,
            Func<string, int, CancellationToken, Task<Bitmap>> readPreview, Action<string> openImage)
        {
            if (readRecent == null) throw new ArgumentNullException("readRecent");
            if (readPreview == null) throw new ArgumentNullException("readPreview");
            if (openImage == null) throw new ArgumentNullException("openImage");
            this.readRecent = readRecent;
            this.readPreview = readPreview;
            this.openImage = openImage;
            Text = "Recent snips — Snip Studio";
            Font = new Font("Segoe UI", 10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(940, 650);
            MinimumSize = new Size(640, 450);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;

            help.Text = "Hover or use arrow keys to preview. Choose Open snip or double-click to open in this editor. Windows manages this list and privacy settings; unsaved snips are not included.";
            help.ForeColor = StudioTheme.Muted;
            help.UseMnemonic = false;
            status.Text = "Choose Refresh to read recent saved or opened snips from Windows.";
            status.ForeColor = StudioTheme.Muted;
            status.UseMnemonic = false;
            recentList.DrawMode = DrawMode.OwnerDrawFixed;
            recentList.IntegralHeight = false;
            recentList.BorderStyle = BorderStyle.FixedSingle;
            recentList.DisplayMember = "DisplayName";
            recentList.AccessibleName = "Recent snips";
            recentList.AccessibleDescription = "Arrow keys select a preview. Double-click or choose Open snip to open the selected image.";
            recentList.DrawItem += DrawRecentItem;
            recentList.SelectedIndexChanged += async delegate { await PreviewItemAsync(recentList.SelectedIndex); };
            recentList.MouseMove += delegate(object sender, MouseEventArgs e) { PreviewHoveredIndex(recentList.IndexFromPoint(e.Location)); };
            recentList.MouseLeave += delegate { hoveredIndex = -1; hoverTimer.Stop(); };
            recentList.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                int index = recentList.IndexFromPoint(e.Location);
                if (index >= 0 && index < rows.Count) { recentList.SelectedIndex = index; OpenSelectedSnip(); }
            };
            recentList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                hoveredIndex = -1;
                hoverTimer.Stop();
                if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; OpenSelectedSnip(); }
            };
            filename.Text = "Preview";
            filename.AutoEllipsis = true;
            filename.UseMnemonic = false;
            filename.ForeColor = StudioTheme.Text;
            path.ReadOnly = true;
            path.Multiline = true;
            path.ScrollBars = ScrollBars.Vertical;
            path.WordWrap = true;
            path.BorderStyle = BorderStyle.FixedSingle;
            path.AccessibleName = "Selected snip location";
            preview.AccessibleName = "Snip preview";
            preview.Message = "Select or hover over a recent snip to see its preview.";

            refresh.Text = "Refresh";
            refresh.Click += async delegate { await RefreshRecentSnipsAsync(); };
            open.Text = "Open snip";
            open.Primary = true;
            open.Enabled = false;
            open.Click += delegate { OpenSelectedSnip(); };
            close.Text = "Close";
            close.Click += delegate { Close(); };
            CancelButton = close;
            Controls.AddRange(new Control[] { help, status, recentList, preview, filename, path, refresh, open, close });
            hoverTimer.Interval = 140;
            hoverTimer.Tick += delegate
            {
                hoverTimer.Stop();
                if (hoveredIndex >= 0 && hoveredIndex < rows.Count && recentList.SelectedIndex != hoveredIndex)
                    recentList.SelectedIndex = hoveredIndex;
            };
            TooltipService.RegisterProvider(tips);
            tips.SetToolTip(refresh, "Read the current Windows recent list again. This does not scan folders or create an extra history.");
            tips.SetToolTip(open, "Open the previewed snip in the editor that opened this gallery. You can save current edits first.");
            tips.SetToolTip(recentList, "Hover to preview. Arrow keys change the selected preview; Enter or double-click opens the snip.");
            tips.SetToolTip(path, "The location supplied by Windows. A moved, deleted or offline file may no longer open.");
            StudioTheme.ApplyTo(this);
            StudioTheme.ThemeChanged += OnThemeChanged;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (opened) return;
            opened = true;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            if (Width > work.Width - 24 || Height > work.Height - 24)
            {
                MinimumSize = new Size(Math.Min(MinimumSize.Width, Math.Max(320, work.Width - 24)), Math.Min(MinimumSize.Height, Math.Max(320, work.Height - 24)));
                Size = new Size(Math.Min(Width, work.Width - 24), Math.Min(Height, work.Height - 24));
                Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
            }
            await RefreshRecentSnipsAsync();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (help == null) return;
            float scale = Math.Max(1f, DeviceDpi / 96f);
            RecentSnipsLayout layout = RecentSnipsLayout.Measure(ClientSize, scale);
            help.Bounds = layout.Help;
            status.Bounds = layout.Status;
            recentList.Bounds = layout.List;
            recentList.ItemHeight = Math.Max(64, (int)Math.Round(82 * scale));
            filename.Bounds = layout.Filename;
            path.Bounds = layout.Path;
            preview.Bounds = layout.Preview;
            refresh.Bounds = layout.Refresh;
            open.Bounds = layout.Open;
            close.Bounds = layout.Close;
        }

        internal async Task RefreshRecentSnipsAsync()
        {
            if (disposed) return;
            CancelWork(ref recentCancellation);
            CancelWork(ref previewCancellation);
            int generation = ++recentGeneration;
            ++previewGeneration;
            recentCancellation = new CancellationTokenSource();
            CancellationToken token = recentCancellation.Token;
            refresh.Enabled = false;
            hoverTimer.Stop();
            hoveredIndex = -1;
            status.Text = "Loading recent snips from Windows…";
            ClearRows();
            SetPreview(null, "Loading the Windows recent list…");
            filename.Text = "Preview";
            path.Text = "";
            open.Enabled = false;
            try
            {
                RecentSnipsResult result = await readRecent(token);
                if (disposed || token.IsCancellationRequested || generation != recentGeneration) return;
                if (result == null) throw new InvalidOperationException("Windows did not return a recent list.");
                if (result.Items != null)
                {
                    recentList.BeginUpdate();
                    try
                    {
                        foreach (RecentSnipInfo item in result.Items)
                        {
                            if (rows.Count >= 32) break;
                            if (item == null || String.IsNullOrWhiteSpace(item.Path)) continue;
                            RecentSnipRow row = new RecentSnipRow(item);
                            rows.Add(row);
                            recentList.Items.Add(row);
                        }
                    }
                    finally { recentList.EndUpdate(); }
                }
                if (rows.Count == 0)
                {
                    status.Text = !String.IsNullOrWhiteSpace(result.Message) ? result.Message : result.IsAvailable ? "No recent saved or opened snips are available." : "The Windows recent list is unavailable.";
                    SetPreview(null, result.IsAvailable ? "No recent snips. Save or open an image in Snip Studio, then choose Refresh. Windows recent-items privacy settings can also leave this list empty." : "Windows recent snips are unavailable. Check Windows recent-items privacy settings, then choose Refresh.");
                }
                else
                {
                    status.Text = rows.Count + (rows.Count == 1 ? " recent snip. " : " recent snips. ") + (!String.IsNullOrWhiteSpace(result.Message) ? result.Message : "Hover or select to preview; Open snip opens it.");
                    recentList.SelectedIndex = 0;
                    LoadRowThumbnailsAsync(generation, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!disposed && generation == recentGeneration)
                {
                    status.Text = "The Windows recent list could not be read. Choose Refresh to try again.";
                    SetPreview(null, "Recent snips are unavailable. Your current editor image is unchanged.");
                }
            }
            finally
            {
                if (!disposed && generation == recentGeneration) refresh.Enabled = true;
            }
        }

        private async void LoadRowThumbnailsAsync(int generation, CancellationToken token)
        {
            // One request at a time leaves room for a newly hovered large preview
            // between rows. The shell service additionally bounds its worker.
            for (int index = 0; index < rows.Count; index++)
            {
                if (disposed || token.IsCancellationRequested || generation != recentGeneration) return;
                RecentSnipRow row = rows[index];
                Bitmap image = null;
                try { image = await readPreview(row.Info.Path, 128, token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
                if (disposed || token.IsCancellationRequested || generation != recentGeneration)
                {
                    if (image != null) image.Dispose();
                    return;
                }
                row.Thumbnail = image;
                row.ThumbnailLoaded = true;
                recentList.Invalidate();
            }
        }

        internal void PreviewHoveredIndex(int index)
        {
            if (disposed || index < 0 || index >= rows.Count) { hoveredIndex = -1; hoverTimer.Stop(); return; }
            if (hoveredIndex == index) return;
            hoveredIndex = index;
            hoverTimer.Stop();
            hoverTimer.Start();
        }

        internal async Task PreviewItemAsync(int index)
        {
            if (disposed || index < 0 || index >= rows.Count) return;
            if (previewIndex == index && previewCancellation != null) return;
            CancelWork(ref previewCancellation);
            int generation = ++previewGeneration;
            previewCancellation = new CancellationTokenSource();
            CancellationToken token = previewCancellation.Token;
            previewIndex = index;
            RecentSnipInfo item = rows[index].Info;
            filename.Text = item.DisplayName;
            path.Text = item.Path;
            tips.SetToolTip(filename, item.DisplayName);
            open.Enabled = !openingImage;
            SetPreview(null, "Loading preview…");
            Bitmap image = null;
            try { image = await readPreview(item.Path, 640, token); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            if (disposed || token.IsCancellationRequested || generation != previewGeneration || previewIndex != index)
            {
                if (image != null) image.Dispose();
                return;
            }
            SetPreview(image, image == null ? "Preview unavailable. The file may have moved, be offline, or have no Windows thumbnail. Choose Open snip to try opening it." : "");
        }

        private void OpenSelectedSnip()
        {
            if (disposed || openingImage || previewIndex < 0 || previewIndex >= rows.Count) return;
            openingImage = true;
            open.Enabled = false;
            try { openImage(rows[previewIndex].Info.Path); }
            finally { openingImage = false; if (!disposed) open.Enabled = previewIndex >= 0 && previewIndex < rows.Count; }
        }

        private void DrawRecentItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= rows.Count) return;
            RecentSnipRow row = rows[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush fill = new SolidBrush(selected ? StudioTheme.Selection : StudioTheme.Input)) e.Graphics.FillRectangle(fill, e.Bounds);
            int gap = Math.Max(8, e.Bounds.Height / 10);
            int edge = Math.Max(32, e.Bounds.Height - gap * 2);
            Rectangle tile = new Rectangle(e.Bounds.Left + gap, e.Bounds.Top + gap, edge, edge);
            using (Brush fill = new SolidBrush(StudioTheme.Raised)) e.Graphics.FillRectangle(fill, tile);
            if (row.Thumbnail != null) RecentSnipPreview.DrawFitted(e.Graphics, row.Thumbnail, tile);
            else TextRenderer.DrawText(e.Graphics, row.ThumbnailLoaded ? "—" : "…", Font, tile, StudioTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            Rectangle text = new Rectangle(tile.Right + gap, e.Bounds.Top + gap, Math.Max(1, e.Bounds.Right - tile.Right - gap * 2), e.Bounds.Height - gap * 2);
            TextRenderer.DrawText(e.Graphics, row.DisplayName, Font, text, StudioTheme.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        private void SetPreview(Bitmap image, string message)
        {
            Bitmap previous = preview.Image;
            preview.Image = image;
            preview.Message = message;
            if (previous != null && !Object.ReferenceEquals(previous, image)) previous.Dispose();
            preview.Invalidate();
        }

        private void ClearRows()
        {
            recentList.Items.Clear();
            foreach (RecentSnipRow row in rows) if (row.Thumbnail != null) row.Thumbnail.Dispose();
            rows.Clear();
            previewIndex = -1;
        }

        private static void CancelWork(ref CancellationTokenSource source)
        {
            CancellationTokenSource previous = source;
            source = null;
            if (previous == null) return;
            previous.Cancel();
            previous.Dispose();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (disposed) return;
            StudioTheme.ApplyTo(this);
            recentList.Invalidate();
            preview.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                ++recentGeneration;
                ++previewGeneration;
                StudioTheme.ThemeChanged -= OnThemeChanged;
                hoverTimer.Stop();
                hoverTimer.Dispose();
                CancelWork(ref recentCancellation);
                CancelWork(ref previewCancellation);
                SetPreview(null, "");
                ClearRows();
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class RecentSnipRow
        {
            internal readonly RecentSnipInfo Info;
            internal Bitmap Thumbnail;
            internal bool ThumbnailLoaded;
            internal RecentSnipRow(RecentSnipInfo info) { Info = info; }
            public string DisplayName { get { return String.IsNullOrWhiteSpace(Info.DisplayName) ? Info.Path : Info.DisplayName; } }
        }
    }

    internal sealed class RecentSnipPreview : Control
    {
        internal Bitmap Image;
        internal string Message;
        internal RecentSnipPreview() { DoubleBuffered = true; TabStop = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(StudioTheme.Raised);
            Rectangle area = Rectangle.Inflate(ClientRectangle, -12, -12);
            if (Image != null) DrawFitted(e.Graphics, Image, area);
            else TextRenderer.DrawText(e.Graphics, Message ?? "", Font, area, StudioTheme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (Pen border = new Pen(StudioTheme.Border)) e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
        internal static void DrawFitted(Graphics graphics, Bitmap image, Rectangle area)
        {
            if (area.Width < 1 || area.Height < 1) return;
            double fit = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
            int width = Math.Max(1, (int)Math.Round(image.Width * fit));
            int height = Math.Max(1, (int)Math.Round(image.Height * fit));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height));
        }
    }

    internal sealed class RecentSnipsLayout
    {
        internal Rectangle Help, Status, List, Filename, Path, Preview, Refresh, Open, Close;
        internal static RecentSnipsLayout Measure(Size client, float scale)
        {
            Func<int, int> ui = delegate(int value) { return Math.Max(1, (int)Math.Round(value * scale)); };
            int gap = ui(12), width = Math.Max(1, client.Width - gap * 2), buttonHeight = ui(36), buttonWidth = ui(106);
            int footer = Math.Max(gap, client.Height - gap - buttonHeight);
            bool compact = client.Height < ui(440);
            int helpHeight = client.Width < ui(740) ? ui(compact ? 50 : 62) : ui(46);
            int statusHeight = ui(compact ? 32 : 42);
            int contentTop = gap + helpHeight + gap + statusHeight + gap;
            int contentHeight = Math.Max(1, footer - gap - contentTop);
            RecentSnipsLayout result = new RecentSnipsLayout();
            result.Help = new Rectangle(gap, gap, width, helpHeight);
            result.Status = new Rectangle(gap, gap + helpHeight + gap, width, statusHeight);
            result.Refresh = new Rectangle(gap, footer, buttonWidth, buttonHeight);
            result.Close = new Rectangle(Math.Max(gap, client.Width - gap - buttonWidth), footer, buttonWidth, buttonHeight);
            result.Open = new Rectangle(Math.Max(gap, result.Close.Left - gap - buttonWidth), footer, buttonWidth, buttonHeight);
            int rightLeft, rightTop, rightWidth, rightHeight;
            if (client.Width >= ui(380))
            {
                int leftWidth = Math.Min(ui(340), Math.Max(ui(150), (int)(width * .36)));
                leftWidth = Math.Min(leftWidth, Math.Max(1, (width - gap) / 2));
                result.List = new Rectangle(gap, contentTop, leftWidth, contentHeight);
                rightLeft = result.List.Right + gap; rightTop = contentTop; rightWidth = Math.Max(1, client.Width - gap - rightLeft); rightHeight = contentHeight;
            }
            else
            {
                int listHeight = Math.Max(1, contentHeight / 3);
                result.List = new Rectangle(gap, contentTop, width, listHeight);
                rightLeft = gap; rightTop = result.List.Bottom + gap; rightWidth = width; rightHeight = Math.Max(1, footer - gap - rightTop);
            }
            int nameHeight = Math.Min(ui(26), Math.Max(1, rightHeight / 5));
            int pathHeight = Math.Min(ui(48), Math.Max(1, rightHeight / 4));
            result.Filename = new Rectangle(rightLeft, rightTop, rightWidth, nameHeight);
            result.Path = new Rectangle(rightLeft, rightTop + rightHeight - pathHeight, rightWidth, pathHeight);
            int previewTop = result.Filename.Bottom + Math.Min(ui(4), Math.Max(0, rightHeight / 20));
            result.Preview = new Rectangle(rightLeft, previewTop, rightWidth, Math.Max(1, result.Path.Top - Math.Min(gap, Math.Max(0, rightHeight / 20)) - previewTop));
            return result;
        }
    }
}
