using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnipStudio
{
    // Opens Windows' own Share panel. A selected share target receives a local
    // PNG only after the user chooses that target in the Windows panel.
    public static class ShareService
    {
        private static readonly Dictionary<IntPtr, ShareSession> Sessions = new Dictionary<IntPtr, ShareSession>();
        private static readonly Guid ManagerId = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
        private static readonly Guid InteropId = new Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");

        [ComImport]
        [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataTransferManagerInterop
        {
            [PreserveSig] int GetForWindow(IntPtr window, ref Guid iid, out IntPtr manager);
            [PreserveSig] int ShowShareUIForWindow(IntPtr window);
        }

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string source, int length, out IntPtr value);
        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object factory);

        public static Task ShowImageAsync(IWin32Window owner, Bitmap image)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (image == null) throw new ArgumentNullException("image");
            Control control = owner as Control;
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
                throw new ArgumentException("Share needs an open Snip Studio window.", "owner");
            if (control.InvokeRequired || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Open Share from the Snip Studio window's UI thread.");
            // Clone before returning the Task; the caller can dispose its render
            // immediately without racing the PNG export or the native share UI.
            Bitmap snapshot = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
            return ShowImageCoreAsync(control, snapshot);
        }

        private static async Task ShowImageCoreAsync(Control owner, Bitmap snapshot)
        {
            string path = null;
            try
            {
                using (snapshot)
                {
                    path = await Task.Run<string>(delegate
                    {
                        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipStudio", "Share");
                        // The shared working-copy writer commits a unique PNG
                        // only after encoding and closing it. It does not launch
                        // an editor. Keep the PNG for delayed target readers.
                        return ExternalEditorService.SaveWorkingCopy(snapshot, folder);
                    });
                }
                StorageFile fileForShare = await AwaitFile(StorageFile.GetFileFromPathAsync(path));
                if (owner.IsDisposed || !owner.IsHandleCreated)
                    throw new InvalidOperationException("The snip window closed before Share could open. The local share copy was kept at " + path);
                // Post through the HWND rather than relying on a captured task
                // context. Share is also invoked from a closing overflow menu;
                // the posted call lets that menu finish releasing foreground
                // activation before Windows displays its Share window.
                await OpenOnOwnerThreadAsync(owner, fileForShare, Path.GetFileName(path));
            }
            catch (Exception error)
            {
                string copy = String.IsNullOrEmpty(path) ? "" : "\n\nA local copy was kept at:\n" + path;
                throw new InvalidOperationException("Windows Share could not be opened. Your snip is unchanged.\n\n" + error.Message + copy, error);
            }
        }

        private static async Task OpenOnOwnerThreadAsync(Control owner, StorageFile file, string fileName)
        {
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            int abandoned = 0;
            owner.BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    if (Interlocked.CompareExchange(ref abandoned, 0, 0) != 0) return;
                    if (owner.IsDisposed || !owner.IsHandleCreated || !owner.Visible)
                        throw new InvalidOperationException("The snip window is no longer visible. Reopen it before sharing.");
                    Form window = owner.FindForm();
                    if (window != null) window.Activate();
                    ShareSession session = GetSession(owner);
                    session.File = file;
                    session.FileName = fileName;
                    session.Open();
                    completion.TrySetResult(true);
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            if (await Task.WhenAny(completion.Task, Task.Delay(10000)) != completion.Task)
            {
                Interlocked.Exchange(ref abandoned, 1);
                throw new TimeoutException("The snip window did not respond to the Share request. Reopen the window and try again.");
            }
            await completion.Task;
        }

        private static ShareSession GetSession(Control owner)
        {
            IntPtr handle = owner.Handle;
            ShareSession session;
            lock (Sessions)
            {
                if (Sessions.TryGetValue(handle, out session)) return session;
                session = new ShareSession(owner, CreateInterop());
                Sessions.Add(handle, session);
            }
            return session;
        }

        private static IDataTransferManagerInterop CreateInterop()
        {
            const string className = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
            IntPtr name = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out name));
                Guid iid = InteropId;
                object factory;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out factory));
                return (IDataTransferManagerInterop)factory;
            }
            finally { if (name != IntPtr.Zero) WindowsDeleteString(name); }
        }

        private static async Task<StorageFile> AwaitFile(IAsyncOperation<StorageFile> operation)
        {
            if (operation == null) throw new IOException("Windows could not begin reading the share copy.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            try
            {
                // Poll the native status rather than leaving a WinRT completion
                // delegate to hold this operation open indefinitely. The delay
                // yields the UI thread while Windows resolves the local file.
                while (operation.Status == AsyncStatus.Started)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        try { operation.Cancel(); } catch (Exception) { }
                        throw new TimeoutException("Windows did not finish reading the local share copy. Try Share again.");
                    }
                    await Task.Delay(25).ConfigureAwait(false);
                }
                if (operation.Status == AsyncStatus.Completed) return operation.GetResults();
                if (operation.Status == AsyncStatus.Canceled) throw new OperationCanceledException("Reading the share copy was canceled.");
                throw operation.ErrorCode ?? new IOException("The share copy could not be read.");
            }
            finally { try { operation.Close(); } catch (Exception) { } }
        }

        private sealed class ShareSession : IDisposable
        {
            private Control owner;
            private readonly IntPtr handle;
            private IDataTransferManagerInterop interop;
            private DataTransferManager manager;
            internal StorageFile File;
            internal string FileName;
            private bool disposed;

            internal ShareSession(Control owner, IDataTransferManagerInterop interop)
            {
                this.owner = owner;
                this.handle = owner.Handle;
                this.interop = interop;
                IntPtr nativeManager = IntPtr.Zero;
                try
                {
                    Guid iid = ManagerId;
                    Marshal.ThrowExceptionForHR(interop.GetForWindow(handle, ref iid, out nativeManager));
                    manager = (DataTransferManager)Marshal.GetObjectForIUnknown(nativeManager);
                    manager.DataRequested += OnDataRequested;
                    owner.HandleDestroyed += OnOwnerClosed;
                    owner.Disposed += OnOwnerClosed;
                }
                finally { if (nativeManager != IntPtr.Zero) Marshal.Release(nativeManager); }
            }

            internal void Open()
            {
                if (disposed || File == null) throw new InvalidOperationException("The share copy is not ready.");
                Marshal.ThrowExceptionForHR(interop.ShowShareUIForWindow(handle));
            }

            private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
            {
                try
                {
                    StorageFile file = File;
                    if (file == null)
                    {
                        args.Request.FailWithDisplayText("The snip is not ready. Return to Snip Studio and choose Share again.");
                        return;
                    }
                    args.Request.Data.Properties.Title = String.IsNullOrEmpty(FileName) ? "Snip Studio screenshot" : FileName;
                    args.Request.Data.Properties.Description = "Screenshot from Snip Studio";
                    args.Request.Data.RequestedOperation = DataPackageOperation.Copy;
                    args.Request.Data.SetStorageItems(new IStorageItem[] { file });
                    args.Request.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    args.Request.Data.Properties.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                }
                catch (Exception)
                {
                    try { args.Request.FailWithDisplayText("The local snip could not be shared. Return to Snip Studio and try Share again."); }
                    catch (Exception) { }
                }
            }

            private void OnOwnerClosed(object sender, EventArgs args) { Dispose(); }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                lock (Sessions) Sessions.Remove(handle);
                if (owner != null)
                {
                    owner.HandleDestroyed -= OnOwnerClosed;
                    owner.Disposed -= OnOwnerClosed;
                    owner = null;
                }
                if (manager != null)
                {
                    try { manager.DataRequested -= OnDataRequested; } catch (Exception) { }
                    manager = null;
                }
                File = null;
                interop = null;
            }
        }
    }
}
