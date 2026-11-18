using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private bool imageSaveInProgress;
        internal bool IsSavingImage { get { return imageSaveInProgress; } }

        // The caller retains its bitmap until this method returns. Even forced
        // owner disposal waits for the pure worker before that bitmap can die.
        private void SaveRenderedImageWithProgress(Bitmap image, string path, ImageFormat format)
        {
            if (image == null) throw new ArgumentNullException("image");
            RunImageSaveWithProgress(delegate { SaveRenderedImageAtomic(image, path, format); });
        }

        private void RunImageSaveWithProgress(Action operation)
        {
            if (imageSaveInProgress) throw new InvalidOperationException("A snip save is already in progress.");
            if (operation == null) throw new ArgumentNullException("operation");
            imageSaveInProgress = true;
            try
            {
                using (SaveProgressForm progress = new SaveProgressForm(operation))
                {
                    try { progress.ShowDialog(this); }
                    finally
                    {
                        // Task completion is independent of the UI continuation.
                        // This rare teardown path may wait for IO; ordinary saves
                        // await it in the responsive modal message loop instead.
                        Task task = progress.OperationTask;
                        if (task != null) task.GetAwaiter().GetResult();
                    }
                    if (!progress.Started) throw new InvalidOperationException("The save could not be started. Your snip is still open.");
                    if (progress.Failure != null) ExceptionDispatchInfo.Capture(progress.Failure).Throw();
                }
            }
            finally { imageSaveInProgress = false; }
        }

        internal static void SaveRenderedImageAtomic(Bitmap image, string path, ImageFormat format)
        {
            if (image == null) throw new ArgumentNullException("image");
            path = Path.GetFullPath(path);
            string extension = Path.GetExtension(path);
            if (String.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                PdfExportService.Save(image, path);
                return;
            }
            if (format == null || (format.Guid != ImageFormat.Png.Guid && format.Guid != ImageFormat.Jpeg.Guid && format.Guid != ImageFormat.Bmp.Guid))
                throw new ArgumentException("Choose PNG, JPEG, Bitmap or PDF.", "format");
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".snip-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (format.Guid == ImageFormat.Jpeg.Guid)
                    {
                        using (Bitmap flattened = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics graphics = Graphics.FromImage(flattened))
                            { graphics.Clear(Color.White); graphics.DrawImageUnscaled(image, 0, 0); }
                            flattened.Save(output, format);
                        }
                    }
                    else image.Save(output, format);
                    output.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                temporary = null;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private bool CompleteImageSave(string path, object identity, long revision)
        {
            if (IsDisposed || Disposing) return false;
            preferences.SaveFolder = Path.GetDirectoryName(path);
            if (!canvas.HasImage || !Object.ReferenceEquals(canvas.ImageIdentity, identity) || canvas.DocumentRevision != revision)
            {
                SetStatus("Saved the earlier image to " + Path.GetFileName(path) + ". Your current changes remain unsaved.");
                return false;
            }
            currentPath = path;
            canvas.MarkSaved();
            UpdateDocumentState();
            SetStatus("Saved " + Path.GetFileName(path));
            if (!layoutTestMode && !String.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                TaskbarJumpList.RecordRecentFile(path);
            return true;
        }
    }
}
