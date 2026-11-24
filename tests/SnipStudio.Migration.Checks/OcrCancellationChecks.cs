using System.Reflection;
using Windows.Foundation;
using Windows.Media.Ocr;

internal static partial class Program
{
    private static async Task CheckNativeOcrCancellationAsync()
    {
        using var cancel = new CancellationTokenSource();
        var operation = new CancelWaitingForCompletion();
        MethodInfo awaitRecognition = typeof(global::SnipStudio.TextRecognition)
            .GetMethod("AwaitRecognition", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pending = (Task<OcrResult>)awaitRecognition.Invoke(null, [operation, cancel.Token])!;

        await Task.Run(cancel.Cancel).WaitAsync(TimeSpan.FromSeconds(10));
        await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)),
            "A completed native OCR cancellation produces a canceled task");
        Check(operation.CallbackReturnedDuringCancel,
            "Native completion returns while Cancel waits for the completion callback");
        Check(operation.CloseCount == 1,
            "A canceled native operation is closed exactly once before OCR ownership is released");

        var preCanceledOperation = new CancelWaitingForCompletion();
        var preCanceled = Task.Run(() => (Task<OcrResult>)awaitRecognition.Invoke(null,
            [preCanceledOperation, cancel.Token])!);
        await ExpectAsync<OperationCanceledException>(() => preCanceled.WaitAsync(TimeSpan.FromSeconds(10)),
            "A token canceled before native registration can still complete");
        Check(preCanceledOperation.CallbackReturnedDuringCancel && preCanceledOperation.CloseCount == 1,
            "Pre-cancellation attaches the native completion handler before invoking Cancel");
    }

    private sealed class CancelWaitingForCompletion : IAsyncOperation<OcrResult>
    {
        public AsyncOperationCompletedHandler<OcrResult> Completed { get; set; } = null!;
        public uint Id => 1;
        public AsyncStatus Status { get; private set; } = AsyncStatus.Started;
        public Exception ErrorCode => null!;
        internal bool CallbackReturnedDuringCancel { get; private set; }
        internal int CloseCount { get; private set; }

        public void Cancel()
        {
            Status = AsyncStatus.Canceled;
            Task callback = Task.Factory.StartNew(() => Completed(this, Status), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            CallbackReturnedDuringCancel = callback.Wait(TimeSpan.FromSeconds(5));
        }

        public OcrResult GetResults() => throw new InvalidOperationException("Canceled OCR has no result.");
        public void Close() => CloseCount++;
    }
}
