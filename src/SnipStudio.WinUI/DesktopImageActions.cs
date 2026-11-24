using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT;

namespace SnipStudio.WinUI;

/// <summary>Local clipboard and per-window Windows Share actions for a frozen image.</summary>
public static class DesktopImageActions
{
    private const int MaximumBytes = 128 * 1024 * 1024;
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);
    private const int ClipboardCannotClose = unchecked((int)0x800401D4);
    private const int WindowsBusy = unchecked((int)0x800700AA);
    private static readonly int[] ClipboardRetryDelays = { 40, 80, 140, 220, 320 };
    private static readonly Dictionary<nint, SharePayload> shares = new();
    private static readonly Guid ManagerInterface = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private static readonly object clipboardIntentGate = new();
    private static ClipboardPayload? pendingClipboard;
    private static ClipboardWriteRequest? currentClipboardWrite;

    internal static long BeginClipboardWrite()
    {
        lock (clipboardIntentGate)
        {
            long intent = (currentClipboardWrite?.Intent ?? 0) + 1;
            Volatile.Write(ref currentClipboardWrite, new ClipboardWriteRequest(intent, ObserveClipboard()));
            return intent;
        }
    }

    internal static bool IsCurrentClipboardWrite(long intent) => Volatile.Read(ref currentClipboardWrite)?.Intent == intent;

    internal static async Task<bool> CopyPngAsync(byte[] png, long intent, Func<bool>? isCurrent = null)
    {
        Validate(png);
        ClipboardWriteRequest? request = Volatile.Read(ref currentClipboardWrite);
        if (request is null || request.Intent != intent) return false;
        ClipboardObservation observed = request.Clipboard;
        bool CanCopy() => IsCurrentClipboardWrite(intent) && isCurrent?.Invoke() != false && ObserveClipboard() == observed;
        if (!CanCopy()) return false;

        InMemoryRandomAccessStream stream = await CreateStreamAsync(png);
        bool submitted = false, accepted = false, persisted = false;
        ClipboardPayload? payload = null;
        try
        {
            if (!CanCopy()) return false;
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.Properties.Title = "Snip Studio image";
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            payload = new ClipboardPayload(stream, data);
            // Keep clipboard history and cross-device roaming disabled for the
            // image, including after a temporarily busy clipboard is retried.
            var options = new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false };
            ClipboardWriteResult result = await RetryClipboardWriteAsync(CanCopy, () =>
            {
                if (!accepted)
                {
                    submitted = true;
                    if (!Clipboard.SetContentWithOptions(data, options)) return false;
                    accepted = true;
                    observed = ObserveClipboard();
                    // Windows can still own a delayed image after a Flush
                    // failure. Keep one accepted payload alive until it is
                    // persisted or superseded by another accepted image.
                    ClipboardPayload? previous = pendingClipboard;
                    pendingClipboard = payload;
                    previous?.Dispose();
                }

                // Flush the accepted payload in place, never submit it again.
                Clipboard.Flush();
                persisted = true;
                return true;
            }, Task.Delay);
            if (result == ClipboardWriteResult.Busy)
                throw new InvalidOperationException(accepted
                    ? "Windows accepted the image but could not finish saving it to the clipboard."
                    : "The Windows clipboard stayed busy. Try Copy image again.");
            return result == ClipboardWriteResult.Copied;
        }
        finally
        {
            if (persisted && ReferenceEquals(pendingClipboard, payload)) pendingClipboard = null;
            // An uncertain native failure can leave a delayed reference.
            // Dispose only before submission or after confirmed persistence.
            if (!submitted || persisted)
            {
                if (payload is null) stream.Dispose();
                else payload.Dispose();
            }
        }
    }

    private static async Task<ClipboardWriteResult> RetryClipboardWriteAsync(
        Func<bool> canCopy, Func<bool> tryWrite, Func<int, Task> delay)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (!canCopy()) return ClipboardWriteResult.Superseded;
            try
            {
                if (tryWrite()) return ClipboardWriteResult.Copied;
            }
            catch (Exception exception) when (exception.HResult is ClipboardCannotOpen or ClipboardCannotClose or WindowsBusy)
            {
                // The Windows clipboard or OLE is temporarily unavailable.
            }
            if (!canCopy()) return ClipboardWriteResult.Superseded;
            if (attempt == ClipboardRetryDelays.Length) return ClipboardWriteResult.Busy;
            // The foreground editor's STA can process input between attempts.
            await delay(ClipboardRetryDelays[attempt]);
        }
    }

    private enum ClipboardWriteResult { Copied, Superseded, Busy }
    private sealed record ClipboardWriteRequest(long Intent, ClipboardObservation Clipboard);
    private readonly record struct ClipboardObservation(uint Sequence, nint Owner);
    private static ClipboardObservation ObserveClipboard() => new(GetClipboardSequenceNumber(), GetClipboardOwner());

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetClipboardOwner();

    public static async Task SharePngAsync(byte[] png, nint hwnd)
    {
        Validate(png);
        if (hwnd == 0) throw new ArgumentException("A live editor window is required for Share.", nameof(hwnd));
        if (shares.ContainsKey(hwnd)) throw new InvalidOperationException("The previous Windows Share operation is still open. Finish or cancel it before sharing another image.");
        InMemoryRandomAccessStream stream = await CreateStreamAsync(png);
        Guid iid = ManagerInterface;
        nint managerPointer = 0;
        SharePayload? payload = null;
        try
        {
            IDataTransferManagerInterop interop = DataTransferManager.As<IDataTransferManagerInterop>();
            managerPointer = interop.GetForWindow(hwnd, ref iid);
            var manager = MarshalInterface<DataTransferManager>.FromAbi(managerPointer);
            payload = new SharePayload(hwnd, manager, stream);
            shares.Add(hwnd, payload);
            manager.DataRequested += payload.OnDataRequested;
            interop.ShowShareUIForWindow(hwnd);
        }
        catch
        {
            if (payload is not null) payload.Dispose();
            else stream.Dispose();
            throw;
        }
        finally { if (managerPointer != 0) Marshal.Release(managerPointer); }
    }

    /// <summary>Call when the owning window closes, releasing any abandoned share lease.</summary>
    public static void ReleaseWindow(nint hwnd)
    {
        if (shares.TryGetValue(hwnd, out SharePayload? payload)) payload.Dispose();
    }

    private static async Task<InMemoryRandomAccessStream> CreateStreamAsync(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream);
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void Validate(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length is <= 0 or > MaximumBytes) throw new ArgumentException("The image is empty or exceeds the 128 MB clipboard/share limit.", nameof(png));
        ReadOnlySpan<byte> signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature)) throw new ArgumentException("Clipboard and Share require a PNG image snapshot.", nameof(png));
    }

    private sealed class SharePayload : IDisposable
    {
        private readonly nint hwnd;
        private readonly DataTransferManager manager;
        private readonly InMemoryRandomAccessStream stream;
        private DataPackage? data;
        private bool disposed;
        internal SharePayload(nint hwnd, DataTransferManager manager, InMemoryRandomAccessStream stream)
        { this.hwnd = hwnd; this.manager = manager; this.stream = stream; }

        internal void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            if (disposed) { args.Request.FailWithDisplayText("This image is no longer available. Return to Snip Studio and Share again."); return; }
            data = args.Request.Data;
            data.Properties.Title = "Snip Studio image";
            data.Properties.Description = "An image shared from Snip Studio.";
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            data.ShareCompleted += OnCompleted;
            data.ShareCanceled += OnCanceled;
        }

        private void OnCompleted(DataPackage sender, ShareCompletedEventArgs args) => Dispose();
        private void OnCanceled(DataPackage sender, object args) => Dispose();
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            manager.DataRequested -= OnDataRequested;
            if (data is not null)
            {
                data.ShareCompleted -= OnCompleted;
                data.ShareCanceled -= OnCanceled;
            }
            shares.Remove(hwnd);
            stream.Dispose();
        }
    }

    private sealed class ClipboardPayload : IDisposable
    {
        private InMemoryRandomAccessStream? stream;
        private readonly DataPackage data;
        internal ClipboardPayload(InMemoryRandomAccessStream stream, DataPackage data) { this.stream = stream; this.data = data; }
        public void Dispose() { Interlocked.Exchange(ref stream, null)?.Dispose(); GC.KeepAlive(data); }
    }


    // Microsoft desktop Share contract, valid for packaged and unpackaged apps:
    // https://learn.microsoft.com/windows/apps/develop/windows-integration/integrate-sharesheet-send
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(nint appWindow);
    }
}
