using System.Diagnostics;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckIndependentMessageLoopLifecycleAsync()
    {
        int editorThread = Environment.CurrentManagedThreadId;
        using EditorSession session = new();
        session.OpenImageBytes(CreateImage(640, 240));
        const string originalText = "00000042_Aa-01.pdf\r\n00000042_Aa-01.pdf";
        string originalRtf = session.CreateTextRtf(originalText, "Segoe UI", 12,
            TextFontStyle.Bold | TextFontStyle.Underline, 0xFF175AA0);
        int firstIndex = session.AddText(originalText, new(20, 20), 0xFF175AA0, 12, 600, originalRtf, 60);
        var before = session.GetAnnotation(firstIndex)!;
        byte[] beforePixels = (await session.RenderPreviewAsync()).PngBytes;
        long revision = session.Revision;

        Stopwatch clock = Stopwatch.StartNew();
        IndependentLoopOutcome loop = await StartAndExitIndependentLoopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();
        performance.Add(new { Operation = "Separate STA hidden-control Application.Run/ApplicationContext exit",
            ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, EditorThreadId = editorThread,
            LoopThreadId = loop.ThreadId, MessageDispatched = loop.MessageDispatched,
            Scope = "Native message-loop lifecycle only; no shown form, desktop capture or input. Five-second bound is a failure timeout, not a speed target." });
        Check(loop.ThreadId != editorThread && loop.Apartment == ApartmentState.STA && loop.MessageDispatched,
            "A separate STA starts its message loop, dispatches the queued exit and completes");
        Check(Environment.CurrentManagedThreadId == editorThread, "Message-loop completion returns to the editor's owning STA");
        var after = session.GetAnnotation(firstIndex)!;
        Check(session.Revision == revision && session.AnnotationCount == 1 && after.Text!.Text == originalText &&
            after.Text.Rtf == before.Text!.Rtf && after.Text.FontStyle == before.Text.FontStyle && after.Text.Argb == before.Text.Argb,
            "Another STA's message-loop exit leaves editor content, style and history intact");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(beforePixels),
            "Rendering existing rich text after another STA exits preserves every prior pixel");

        const string addedText = "0007_Bb-02.pdf";
        string addedRtf = session.CreateTextRtf(addedText, "Segoe UI", 14, TextFontStyle.Italic, 0xFF8B008B);
        int addedIndex = session.AddText(addedText, new(20, 120), 0xFF8B008B, 14, 600, addedRtf, 40);
        var added = session.GetAnnotation(addedIndex)!;
        Check(addedIndex == 1 && session.AnnotationCount == 2 && added.Text!.Text == addedText && added.Text.Rtf == addedRtf &&
            added.Text.Argb == 0xFF8B008B && added.Text.FontSize == 14,
            "New rich-text layout and annotation creation remain available after the independent loop exits");
        byte[] addedPixels = (await session.RenderPreviewAsync()).PngBytes;
        Check(PixelHash(addedPixels) != PixelHash(beforePixels), "The new rich-text annotation is rendered after the lifecycle transition");
        session.Undo();
        Check(session.AnnotationCount == 1 && session.Revision == revision && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(beforePixels),
            "Undo after the independent loop restores the exact pre-add document");
        session.Redo();
        Check(session.AnnotationCount == 2 && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(addedPixels),
            "Redo after the independent loop restores the exact new rich-text rendering");
    }

    private sealed record IndependentLoopOutcome(int ThreadId, ApartmentState Apartment, bool MessageDispatched);

    private static Task<IndependentLoopOutcome> StartAndExitIndependentLoopAsync()
    {
        var completion = new TaskCompletionSource<IndependentLoopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                IndependentLoopOutcome outcome;
                using (var dispatcher = new System.Windows.Forms.Control())
                using (var context = new System.Windows.Forms.ApplicationContext())
                {
                    _ = dispatcher.Handle;
                    bool dispatched = false;
                    dispatcher.BeginInvoke((Action)(() => { dispatched = true; context.ExitThread(); }));
                    // This is the private capture coordinator's lifecycle shape,
                    // without any visible overlay, capture service or input call.
                    System.Windows.Forms.Application.Run(context);
                    outcome = new IndependentLoopOutcome(Environment.CurrentManagedThreadId,
                        Thread.CurrentThread.GetApartmentState(), dispatched);
                }
                // Release the owning editor only after the other STA has
                // completed its message loop and disposed its owned objects.
                completion.TrySetResult(outcome);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "Snip Studio lifecycle regression STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task CheckEndpointPropertiesAsync()
    {
        foreach (AnnotationShape shape in new[] { AnnotationShape.Line, AnnotationShape.Arrow })
        {
            using EditorSession session = new();
            session.OpenImageBytes(CreateImage(360, 240));
            session.AddShape(shape, new(30, 40), new(220, 80), 0xFF000000, 3);
            var original = session.GetAnnotation(0)!;
            byte[] originalPixels = (await session.RenderPreviewAsync()).PngBytes;
            Check(session.UpdateShapeEndpoints(original, new(250, 160), new(50, 120), new ShapeStyleEdit(null, 0xFF8B008B, 5)),
                "Combined endpoint and style edit commits: " + shape);
            var changed = session.GetAnnotation(0)!;
            Check(changed.Shape!.Endpoints.SequenceEqual(new[] { new ImagePoint(250, 160), new ImagePoint(50, 120) }) &&
                changed.Shape.OutlineArgb == 0xFF8B008B && changed.Shape.OutlineWidth == 5,
                "Endpoint edit preserves chosen direction and styling: " + shape);
            byte[] changedPixels = (await session.RenderPreviewAsync()).PngBytes;
            Check(PixelHash(changedPixels) != PixelHash(originalPixels), "Endpoint edit changes the rendered object: " + shape);
            session.Undo();
            var undone = session.GetAnnotation(0)!;
            Check(undone.Shape!.Endpoints.SequenceEqual(original.Shape!.Endpoints) && undone.Shape.OutlineArgb == original.Shape.OutlineArgb &&
                undone.Shape.OutlineWidth == original.Shape.OutlineWidth && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(originalPixels),
                "One Undo restores both endpoints and styling: " + shape);
            session.Redo();
            Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(changedPixels),
                "Redo restores the complete endpoint/style rendering: " + shape);
            long revision = session.Revision;
            Check(!session.UpdateShapeEndpoints(session.GetAnnotation(0)!, changed.Shape.Endpoints[0], changed.Shape.Endpoints[1]) && session.Revision == revision,
                "Unchanged endpoints do not add history: " + shape);
            Expect<InvalidOperationException>(() => session.UpdateShapeEndpoints(original, new(40, 50), new(230, 90)),
                "Stale endpoint edit cannot target a later revision: " + shape);
            Check(session.Revision == revision, "Rejected stale endpoint edit preserves current revision: " + shape);
        }
    }
}
