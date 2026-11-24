using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckAnnotationDragPreviewAsync()
    {
        using var session = new EditorSession();
        byte[] source = CreateImage(200, 130);
        session.OpenImageBytes(source);
        const uint green = 0xFF00AB00, red = 0xFFB00000, blue = 0xFF0000C0;
        session.AddShape(AnnotationShape.Rectangle, new(12, 12), new(95, 65), green, 3, green);
        session.AddShape(AnnotationShape.Rectangle, new(40, 20), new(135, 86), red, 3, red);
        session.AddShape(AnnotationShape.Rectangle, new(82, 35), new(160, 111), blue, 3, blue);
        AnnotationSnapshot item = session.GetAnnotation(1)!;
        AnnotationDragPreview preview = await session.RenderAnnotationDragPreviewAsync(item, CancellationToken.None)
            ?? throw new InvalidOperationException("Current object has no drag snapshot.");
        var spriteOffset = preview.ObjectFrame;
        int objectX = 102 - (int)spriteOffset.Left, objectY = 51 - (int)spriteOffset.Top;
        Check(Pixel(preview.BelowPng, 68, 51) == green && Pixel(preview.BelowPng, 102, 51) == Pixel(source, 102, 51),
            "Base and preceding layers render without the dragged pixels or later annotation");
        Check(Pixel(preview.ObjectPng, objectX, objectY) == red && (Pixel(preview.ObjectPng, 0, 0) >> 24) == 0,
            "The selected sprite is correctly positioned and independently transparent");
        Check(preview.AbovePng is { } upper && Pixel(upper, 102, 51) == blue && (Pixel(upper, 68, 51) >> 24) == 0,
            "Later annotations remain above the dragged object");
        Check(session.Revision == item.Revision && session.AnnotationCount == 3 && session.IsAnnotationCurrent(item),
            "Preparing the object layers does not edit the document");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => session.RenderAnnotationDragPreviewAsync(item, canceled.Token),
            "Cancellation stops drag preparation without editing the document");
        session.MoveAnnotation(item, new(6, 0));
        Check(await session.RenderAnnotationDragPreviewAsync(item, CancellationToken.None) is null,
            "A stale object selection cannot replace the visible preview");
    }
}
