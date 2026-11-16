using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class RecentSnipInfo
    {
        internal string Path { get; private set; }
        internal string DisplayName { get; private set; }
        internal RecentSnipInfo(string path) { Path = path; DisplayName = System.IO.Path.GetFileName(path); }
    }

    internal sealed class RecentSnipsResult
    {
        internal IList<RecentSnipInfo> Items { get; private set; }
        internal string Message { get; private set; }
        internal bool IsAvailable { get; private set; }
        internal RecentSnipsResult(IList<RecentSnipInfo> items, string message, bool available)
        {
            Items = new List<RecentSnipInfo>(items).AsReadOnly();
            Message = message;
            IsAvailable = available;
        }
    }

    // Read-only, on-demand access to Windows' list for this AppID only. No
    // custom history, registry access, directory enumeration or startup work.
    // One STA worker owns all Shell objects; at most 40 requests can wait.
    internal static class RecentSnipService
    {
        internal const int MaximumItems = 32;
        internal const int MaximumThumbnailEdge = 640;
        private const int MaximumPendingRequests = 40;
        private static readonly object sync = new object();
        private static readonly Queue<WorkItem> pending = new Queue<WorkItem>();
        private static bool workerRunning;
        private const uint FileSystemPath = 0x80058000;
        private const uint ThumbnailOnly = 0x00000008;

        internal static Task<RecentSnipsResult> GetRecentAsync(int maximumItems, CancellationToken token)
        {
            int limit = LimitItems(maximumItems);
            return Enqueue(delegate { return ReadRecentCore(limit, Application.ExecutablePath, token); },
                Unavailable("Recent snips are busy. Please try Refresh again."), token);
        }

        // Read-only signed-library probe seam: a host loading this assembly has
        // a different Application.ExecutablePath. Its caller must supply the
        // exact Snip Studio executable being verified. AppID and link-target
        // checks remain identical, and native work still uses the STA queue.
        internal static Task<RecentSnipsResult> GetRecentForExecutableAsync(int maximumItems, string executable, CancellationToken token)
        {
            int limit = LimitItems(maximumItems);
            return Enqueue(delegate { return ReadRecentCore(limit, executable, token); },
                Unavailable("Recent snips are busy. Please try Refresh again."), token);
        }

        // A successful result is detached from Shell and file handles. The
        // caller owns it. Null means Windows could not provide a thumbnail;
        // it does not prove that the file was deleted. A bitmap is discarded
        // if cancellation wins completion. Callers must also reject and
        // dispose successful results belonging to a stale UI generation.
        internal static Task<Bitmap> LoadThumbnailAsync(string path, int maximumEdge, CancellationToken token)
        {
            int edge = LimitThumbnailEdge(maximumEdge);
            return Enqueue(delegate { return ReadThumbnailCore(path, edge, token); }, (Bitmap)null, token);
        }

        internal static int LimitItems(int value) { return Math.Max(1, Math.Min(MaximumItems, value)); }
        internal static int LimitThumbnailEdge(int value) { return Math.Max(16, Math.Min(MaximumThumbnailEdge, value)); }

        // Pure policy seam: bounded input, exact order, case-insensitive
        // deduplication and the same accepted-path rules used by the recorder.
        // Neither this method nor constructing the DTOs touches any file.
        internal static RecentSnipsResult CreateRecentResult(IEnumerable<string> paths, int maximumItems, CancellationToken token)
        {
            List<RecentSnipInfo> items = new List<RecentSnipInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;
            int limit = LimitItems(maximumItems);
            foreach (string candidate in paths)
            {
                token.ThrowIfCancellationRequested();
                if (scanned++ == MaximumItems) break;
                string path;
                if (TaskbarJumpList.TryNormalizeRecentFile(candidate, out path) && seen.Add(path))
                    items.Add(new RecentSnipInfo(path));
                if (items.Count == limit) break;
            }
            token.ThrowIfCancellationRequested();
            return new RecentSnipsResult(items, items.Count == 0 ?
                "No recent snips are available. Open or save an image in Snip Studio. Windows recent-history settings control this list." :
                "Recent images from Snip Studio's Windows history. Pinned items may also appear.", true);
        }

        // The recorder uses links to this executable with exactly one quoted
        // image argument. Do not resolve links, launch targets, or accept a
        // different portable installation or extra command-line arguments.
        internal static bool TryGetOwnedLinkImagePath(string target, string arguments, string executable, out string path)
        {
            path = null;
            if (String.IsNullOrWhiteSpace(target) || String.IsNullOrWhiteSpace(executable) ||
                String.IsNullOrEmpty(arguments) || arguments.Length > 32702 || arguments.Length < 3 ||
                arguments[0] != '"' || arguments[arguments.Length - 1] != '"') return false;
            try
            {
                if (!String.Equals(TaskbarJumpList.BuildOpenCommand(target), TaskbarJumpList.BuildOpenCommand(executable), StringComparison.OrdinalIgnoreCase)) return false;
                string candidate = arguments.Substring(1, arguments.Length - 2);
                string normalized;
                if (!TaskbarJumpList.TryNormalizeRecentFile(candidate, out normalized) ||
                    !String.Equals(TaskbarJumpList.QuoteArgument(normalized), arguments, StringComparison.Ordinal)) return false;
                path = normalized;
                return true;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            catch (System.Security.SecurityException) { }
            return false;
        }

        private static RecentSnipsResult Unavailable(string message)
        {
            return new RecentSnipsResult(new RecentSnipInfo[0], message, false);
        }

        private static Task<T> Enqueue<T>(Func<T> operation, T unavailable, CancellationToken token)
        {
            Request<T> request = new Request<T>(operation, unavailable, token);
            if (token.IsCancellationRequested) { request.Dispose(); return request.Task; }
            List<WorkItem> discarded = new List<WorkItem>();
            bool accepted = false;
            try
            {
                lock (sync)
                {
                    int count = pending.Count;
                    for (int index = 0; index < count; index++)
                    {
                        WorkItem item = pending.Dequeue();
                        if (item.IsCanceled) discarded.Add(item); else pending.Enqueue(item);
                    }
                    if (pending.Count < MaximumPendingRequests)
                    {
                        pending.Enqueue(request);
                        accepted = true;
                        if (!workerRunning)
                        {
                            try
                            {
                                Thread worker = new Thread(Work);
                                worker.IsBackground = true;
                                worker.Name = "Snip Studio recent previews";
                                worker.SetApartmentState(ApartmentState.STA);
                                workerRunning = true;
                                worker.Start();
                            }
                            catch
                            {
                                workerRunning = false;
                                while (pending.Count > 0) discarded.Add(pending.Dequeue());
                                throw;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                if (IsFatal(exception)) throw;
                accepted = false;
            }
            finally
            {
                foreach (WorkItem item in discarded) { item.Fail(); item.Dispose(); }
            }
            if (!accepted) { request.Fail(); request.Dispose(); }
            return request.Task;
        }

        private static void Work()
        {
            // Native calls are not abortable. A hung Shell provider occupies
            // this single background worker, never the UI or more workers.
            // Canceled tasks finish immediately; their native result is later
            // released here, and canceled queued work is never executed.
            for (;;)
            {
                WorkItem item;
                lock (sync)
                {
                    if (pending.Count == 0) { workerRunning = false; return; }
                    item = pending.Dequeue();
                }
                try { item.Execute(); }
                finally { item.Dispose(); }
            }
        }

        private abstract class WorkItem : IDisposable
        {
            internal abstract bool IsCanceled { get; }
            internal abstract void Execute();
            internal abstract void Fail();
            public abstract void Dispose();
        }

        private sealed class Request<T> : WorkItem
        {
            private readonly Func<T> operation;
            private readonly T unavailable;
            private readonly CancellationToken token;
            private readonly TaskCompletionSource<T> completion;
            private CancellationTokenRegistration registration;
            internal Task<T> Task { get { return completion.Task; } }
            internal override bool IsCanceled { get { return token.IsCancellationRequested; } }
            internal Request(Func<T> operation, T unavailable, CancellationToken token)
            {
                this.operation = operation;
                this.unavailable = unavailable;
                this.token = token;
                completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                registration = token.Register(delegate { completion.TrySetCanceled(); });
            }
            internal override void Execute()
            {
                if (IsCanceled) { completion.TrySetCanceled(); return; }
                T value = default(T);
                try
                {
                    value = operation();
                    if (IsCanceled) completion.TrySetCanceled();
                    else if (completion.TrySetResult(value)) { value = default(T); return; }
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception exception)
                {
                    if (IsFatal(exception)) throw;
                    Fail();
                }
                finally
                {
                    IDisposable owned = value as IDisposable;
                    if (owned != null) owned.Dispose();
                }
            }
            internal override void Fail()
            {
                if (IsCanceled) completion.TrySetCanceled(); else completion.TrySetResult(unavailable);
            }
            public override void Dispose() { registration.Dispose(); }
        }

        private static RecentSnipsResult ReadRecentCore(int limit, string executable, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || Environment.OSVersion.Version < new Version(6, 1))
                return Unavailable("Windows recent snips are unavailable on this version of Windows.");
            int initialized = CoInitializeEx(IntPtr.Zero, 2);
            if (initialized < 0) return Unavailable("Windows recent snips are unavailable right now. Please try Refresh again.");
            object listsObject = null;
            object arrayObject = null;
            try
            {
                listsObject = new ApplicationDocumentListsComObject();
                IApplicationDocumentLists lists = (IApplicationDocumentLists)listsObject;
                Check(lists.SetAppID(TaskbarJumpList.ApplicationId));
                token.ThrowIfCancellationRequested();
                Guid arrayId = typeof(IObjectArray).GUID;
                // Never pass zero: that requests the complete history.
                Check(lists.GetList(0, (uint)MaximumItems, ref arrayId, out arrayObject));
                IObjectArray array = (IObjectArray)arrayObject;
                uint count;
                Check(array.GetCount(out count));
                List<string> paths = new List<string>();
                bool skipped = false;
                Guid unknownId = new Guid("00000000-0000-0000-C000-000000000046");
                for (uint index = 0; index < Math.Min(count, (uint)MaximumItems); index++)
                {
                    token.ThrowIfCancellationRequested();
                    object itemObject = null;
                    try
                    {
                        if (array.GetAt(index, ref unknownId, out itemObject) < 0 || itemObject == null) { skipped = true; continue; }
                        string path = ReadItemPath(itemObject, executable);
                        if (path == null) skipped = true; else paths.Add(path);
                    }
                    catch (Exception exception) { if (IsFatal(exception)) throw; skipped = true; }
                    finally { ReleaseComObject(itemObject); }
                }
                RecentSnipsResult result = CreateRecentResult(paths, limit, token);
                if (skipped) return new RecentSnipsResult(result.Items,
                    result.Items.Count == 0 ? "Windows returned no usable recent images for this installation. Some history entries are unavailable. Windows recent-history settings also control this list." :
                    "Recent images from Snip Studio's Windows history. Some entries are unavailable; pinned items may also appear.", true);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                if (IsFatal(exception)) throw;
                return Unavailable("Windows could not read Snip Studio's recent history. Recent items may be disabled by Windows privacy settings, or Windows may be unavailable. Please try Refresh again.");
            }
            finally
            {
                ReleaseComObject(arrayObject);
                ReleaseComObject(listsObject);
                CoUninitialize();
            }
        }

        private static string ReadItemPath(object value, string executable)
        {
            IShellItem item = value as IShellItem;
            if (item != null)
            {
                IntPtr displayName = IntPtr.Zero;
                try
                {
                    string normalized;
                    if (item.GetDisplayName(FileSystemPath, out displayName) >= 0 && displayName != IntPtr.Zero &&
                        TaskbarJumpList.TryNormalizeRecentFile(Marshal.PtrToStringUni(displayName), out normalized)) return normalized;
                }
                finally { if (displayName != IntPtr.Zero) Marshal.FreeCoTaskMem(displayName); }
            }
            IShellLinkW link = value as IShellLinkW;
            if (link == null) return null;
            StringBuilder target = new StringBuilder(32768);
            StringBuilder arguments = new StringBuilder(32768);
            if (link.GetPath(target, target.Capacity, IntPtr.Zero, 4) < 0 || link.GetArguments(arguments, arguments.Capacity) < 0 ||
                target.Length >= target.Capacity - 1 || arguments.Length >= arguments.Capacity - 1) return null;
            string imagePath;
            return TryGetOwnedLinkImagePath(target.ToString(), arguments.ToString(), executable, out imagePath) ? imagePath : null;
        }

        private static Bitmap ReadThumbnailCore(string candidate, int edge, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path;
            if (!TaskbarJumpList.TryNormalizeRecentFile(candidate, out path)) return null;
            int initialized = CoInitializeEx(IntPtr.Zero, 2);
            if (initialized < 0) return null;
            object factoryObject = null;
            IntPtr nativeBitmap = IntPtr.Zero;
            try
            {
                Guid factoryId = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref factoryId, out factoryObject) < 0 || factoryObject == null) return null;
                token.ThrowIfCancellationRequested();
                // Resize-to-fit (0) + thumbnail-only: never enlarge, return an
                // icon as if it were a preview, or load a full image ourselves.
                if (((IShellItemImageFactory)factoryObject).GetImage(new NativeSize(edge, edge), ThumbnailOnly, out nativeBitmap) < 0 ||
                    nativeBitmap == IntPtr.Zero) return null;
                token.ThrowIfCancellationRequested();
                return CopyNativeThumbnail(nativeBitmap, edge);
            }
            finally
            {
                if (nativeBitmap != IntPtr.Zero) DeleteObject(nativeBitmap);
                ReleaseComObject(factoryObject);
                CoUninitialize();
            }
        }

        private static Bitmap CopyNativeThumbnail(IntPtr source, int edge)
        {
            NativeBitmap bitmap;
            if (GetObject(source, Marshal.SizeOf(typeof(NativeBitmap)), out bitmap) == 0 ||
                bitmap.Width < 1 || bitmap.Height < 1 || bitmap.Width > edge || bitmap.Height > edge) return null;
            int bytesPerRow = checked(bitmap.Width * 4);
            byte[] pixels = new byte[checked(bytesPerRow * bitmap.Height)];
            BitmapInfo info = new BitmapInfo();
            info.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
            info.Header.Width = bitmap.Width;
            info.Header.Height = -bitmap.Height;
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                if (GetDIBits(dc, source, 0, (uint)bitmap.Height, pixels, ref info, 0) != bitmap.Height) return null;
            }
            finally { DeleteDC(dc); }
            return CreateThumbnailBitmap(bitmap.Width, bitmap.Height, pixels);
        }

        // Detached pixel-copy seam. GetImage has no alpha-type output. Preserve
        // premultiplied alpha; if a channel exceeds alpha, convert straight
        // alpha to premultiplied. Entirely zero alpha is treated as legacy RGB.
        // This bounded compatibility rule avoids FromHbitmap's alpha loss; it
        // is a Windows preview, not a guarantee of original-image pixel parity.
        internal static Bitmap CreateThumbnailBitmap(int width, int height, byte[] pixels)
        {
            if (width < 1 || height < 1 || width > MaximumThumbnailEdge || height > MaximumThumbnailEdge ||
                pixels == null || pixels.Length != checked(width * height * 4)) throw new ArgumentException("Invalid thumbnail pixels.");
            byte[] copy = (byte[])pixels.Clone();
            bool anyAlpha = false;
            bool straightAlpha = false;
            for (int index = 0; index < copy.Length; index += 4)
            {
                int alpha = copy[index + 3];
                anyAlpha |= alpha != 0;
                straightAlpha |= copy[index] > alpha || copy[index + 1] > alpha || copy[index + 2] > alpha;
            }
            for (int index = 0; index < copy.Length; index += 4)
            {
                if (!anyAlpha) copy[index + 3] = 255;
                else if (straightAlpha)
                    for (int channel = 0; channel < 3; channel++) copy[index + channel] = (byte)((copy[index + channel] * copy[index + 3] + 127) / 255);
            }
            Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            try
            {
                result.SetResolution(96, 96);
                BitmapData data = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    for (int row = 0; row < height; row++) Marshal.Copy(copy, row * width * 4, IntPtr.Add(data.Scan0, row * data.Stride), width * 4);
                }
                finally { result.UnlockBits(data); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        private static bool IsFatal(Exception exception)
        {
            return exception is OutOfMemoryException || exception is StackOverflowException || exception is ThreadAbortException || exception is AccessViolationException;
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeSize
        {
            internal int Width, Height;
            internal NativeSize(int width, int height) { Width = width; Height = height; }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
        {
            internal int Type, Width, Height, WidthBytes;
            internal ushort Planes, BitsPixel;
            internal IntPtr Bits;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, ImageSize;
            internal int XPixelsPerMeter, YPixelsPerMeter;
            internal uint ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Color; }

        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object item);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true)] private static extern int GetObject(IntPtr value, int size, out NativeBitmap bitmap);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint count, [Out] byte[] pixels, ref BitmapInfo info, uint usage);

        // GUIDs and vtable order checked against Microsoft's current
        // win32metadata ShObjIdl_core.h on 2026-11-13. IObjectArray's contract
        // is also used by the existing TaskbarJumpList implementation.
        [ComImport, Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")] private class ApplicationDocumentListsComObject { }
        [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationDocumentLists
        {
            [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int GetList(uint listType, uint desired, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object items);
        }
        [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectArray
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
            [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out object parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr value);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem item, uint hint, out int order);
        }
        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
        }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
            [PreserveSig] int GetIDList(out IntPtr idList);
            [PreserveSig] int SetIDList(IntPtr idList);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
            [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
            [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        }
    }
}
