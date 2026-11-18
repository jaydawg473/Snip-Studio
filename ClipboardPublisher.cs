using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    internal enum ClipboardPublishResult { Copied, Superseded, Unavailable }

    // All delegates execute on the caller's STA synchronization context. The
    // adapter is instance-scoped so checks never need to touch the live clipboard.
    internal sealed class ClipboardPublisher
    {
        private readonly Func<uint> sequence;
        private readonly Func<DataObject, bool> write;
        private readonly Func<int, Task> delay;
        private static readonly int[] RetryDelays = { 40, 80, 140, 220, 320 };

        internal ClipboardPublisher() : this(GetClipboardSequenceNumber, WriteOnce, Task.Delay) { }

        internal ClipboardPublisher(Func<uint> sequence, Func<DataObject, bool> write, Func<int, Task> delay)
        {
            if (sequence == null) throw new ArgumentNullException("sequence");
            if (write == null) throw new ArgumentNullException("write");
            if (delay == null) throw new ArgumentNullException("delay");
            this.sequence = sequence;
            this.write = write;
            this.delay = delay;
        }

        internal uint CaptureSequence() { return sequence(); }

        internal async Task<ClipboardPublishResult> PublishAsync(Bitmap image, byte[] png, uint initialSequence, Func<bool> isCurrent)
        {
            if (isCurrent == null) throw new ArgumentNullException("isCurrent");
            ClipboardTransfer transfer = null;
            bool submitted = false, persisted = false;
            try
            {
                for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
                {
                    // A different application's Copy wins even if the delayed image
                    // still matches the document and the app's own intent counter.
                    if (!isCurrent() || sequence() != initialSequence) return ClipboardPublishResult.Superseded;
                    // A failed persistent copy can still leave OLE holding its
                    // DataObject. Its bitmap and stream must be independent of
                    // the caller's snapshot and its eventual Dispose.
                    if (transfer == null) transfer = new ClipboardTransfer(image, png);
                    transfer.Png.Position = 0;
                    submitted = true;
                    if (write(transfer.Data))
                    {
                        persisted = true;
                        return ClipboardPublishResult.Copied;
                    }
                    if (attempt == RetryDelays.Length) return ClipboardPublishResult.Unavailable;
                    // No Thread.Sleep and no WinForms internal retry loop: the UI
                    // can paint and a newer Copy can invalidate this request.
                    await delay(RetryDelays[attempt]);
                }
            }
            finally
            {
                // Persistent success releases OLE's IDataObject reference. On
                // partial/unknown failure, the COM-rooted DataObject keeps its
                // independent resources alive until clipboard replacement; its
                // bitmap is then finalized normally. Never retain a static root
                // or dispose resources that OLE might still render later.
                if (transfer != null && (persisted || !submitted)) transfer.Dispose();
            }
            return ClipboardPublishResult.Unavailable;
        }

        private sealed class ClipboardTransfer : IDisposable
        {
            internal readonly Bitmap Image;
            internal readonly MemoryStream Png;
            internal readonly DataObject Data;

            internal ClipboardTransfer(Bitmap image, byte[] png)
            {
                Image = image.Clone(new Rectangle(Point.Empty, image.Size), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    Png = new MemoryStream(png, false);
                    Data = new DataObject();
                    Data.SetData(DataFormats.Bitmap, true, Image);
                    Data.SetData("PNG", false, Png);
                }
                catch
                {
                    Image.Dispose();
                    if (Png != null) Png.Dispose();
                    throw;
                }
            }

            public void Dispose() { Image.Dispose(); Png.Dispose(); }
        }

        private static bool WriteOnce(DataObject data)
        {
            try
            {
                Clipboard.SetDataObject(data, true, 0, 0);
                return true;
            }
            catch (ExternalException)
            {
                // This catch covers only the clipboard API invocation. WinForms
                // preserves the OLE HRESULT; its generic message does not tell
                // us which step failed. Other exception types stay visible.
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
