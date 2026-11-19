using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT;

namespace SnipStudio.WinUI;

/// <summary>Local clipboard and per-window Windows Share actions for a frozen image.</summary>
public static class DesktopImageActions
{
    private const int MaximumBytes = 128 * 1024 * 1024;
    private static readonly Dictionary<nint, SharePayload> shares = new();
    private static readonly Guid ManagerInterface = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private static ClipboardPayload? pendingClipboard;

    public static async Task CopyPngAsync(byte[] png)
    {
        Validate(png);
        InMemoryRandomAccessStream stream = await CreateStreamAsync(png);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.Properties.Title = "Snip Studio image";
        data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        // An explicit copy remains available to paste, but is not offered for
        // Windows clipboard history or cross-device roaming by this application.
        var payload = new ClipboardPayload(stream, data);
        bool attempted = false;
        try
        {
            attempted = true;
            if (!Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
                throw new InvalidOperationException("Windows could not copy the image. Your document is kept; try Copy again.");
            ClipboardPayload? previous = pendingClipboard;
            pendingClipboard = payload;
            previous?.Dispose();
            Clipboard.Flush();
            // Persistent success has copied the data. On a Flush failure the
            // clipboard can still own this delayed payload, so retain its
            // independent bytes until our next successful replacement instead
            // of leaving a closed stream behind for the next paste. This is a
            // bounded one-payload lease; other clipboard providers may keep OLE
            // references even after an uncertain persistence failure.
            if (ReferenceEquals(pendingClipboard, payload)) pendingClipboard = null;
            payload.Dispose();
        }
        finally
        {
            // A SetContent failure can leave OLE owning the package. Without a
            // successful persistence result, let native references release the
            // independent stream naturally; do not force-close it for a late
            // consumer. Failures before submission own no external references.
            if (!attempted) payload.Dispose();
        }
    }

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
