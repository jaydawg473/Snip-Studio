using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckShapeTransformsAsync()
    {
        foreach (AnnotationShape kind in new[] { AnnotationShape.Rectangle, AnnotationShape.Ellipse })
            await CheckClosedShapeTransformAsync(kind);

        foreach (AnnotationShape kind in new[] { AnnotationShape.Line, AnnotationShape.Arrow })
            CheckEndpointShapeRotation(kind);

        CheckStickerRotation();
        CheckPlacedShapeOutlineWidth();
        CheckShapeMovePicking();
        CheckDrawingOutlinePicking();
        CheckMoveAfterCrop();
    }

    private static void CheckPlacedShapeOutlineWidth()
    {
        using var session = new EditorSession();
        foreach (AnnotationShape kind in Enum.GetValues<AnnotationShape>())
        {
            session.OpenImageBytes(CreateImage());
            uint? fill = kind is AnnotationShape.Rectangle or AnnotationShape.Ellipse ? 0xFF008000 : null;
            Check(session.AddShape(kind, new(12, 14), new(40, 26), 0xFF303060, 2, fill,
                    SnipStudio.ShapeOutlinePattern.Dashed, outlineOpacity: 65) && session.RotateAnnotation(0, 37),
                $"Create a styled and rotated {kind} for width editing");
            AnnotationSnapshot original = session.GetAnnotation(0)!;
            ShapeAnnotationSnapshot originalShape = original.Shape!;
            Check(session.UpdateShapeOutlineWidth(original, 7.5f), $"Change the {kind} width after drawing");
            AnnotationSnapshot changed = session.GetAnnotation(0)!;
            ShapeAnnotationSnapshot changedShape = changed.Shape!;
            Check(changedShape == (originalShape with { OutlineWidth = 7.5f, Endpoints = changedShape.Endpoints }) &&
                originalShape.Endpoints.SequenceEqual(changedShape.Endpoints),
                $"Width editing preserves {kind} rotation, geometry, colors, opacity and line style");
            Expect<InvalidOperationException>(() => session.UpdateShapeOutlineWidth(original, 12), $"Reject a stale {kind} width edit");
            Check(!session.UpdateShapeOutlineWidth(changed, 7.5f) && session.Revision == changed.Revision,
                $"Applying the same {kind} width keeps history unchanged");
            session.Undo();
            Check(session.GetAnnotation(0)!.Shape!.OutlineWidth == originalShape.OutlineWidth, $"Undo restores the {kind} width");
            session.Redo();
            Check(session.GetAnnotation(0)!.Shape!.OutlineWidth == 7.5f, $"Redo restores the edited {kind} width");
        }
    }

    private static void CheckDrawingOutlinePicking()
    {
        using var session = new EditorSession();
        foreach (AnnotationShape kind in new[] { AnnotationShape.Rectangle, AnnotationShape.Ellipse })
        {
            session.OpenImageBytes(CreateImage());
            session.AddShape(AnnotationShape.Line, new(1, 20), new(60, 20), 0xFF101010, 2);
            session.AddShape(kind, new(8, 8), new(48, 32), 0xFF101010, 2, 0xFF008000);
            long revision = session.Revision;
            Check(session.HitTestAnnotationOutline(new(28, 8), 1)?.Index == 1, $"Drawing tools can grab the {kind} outline");
            Check(session.HitTestAnnotationOutline(new(28, 20), 1) is null, $"An opaque {kind} interior keeps drawing available");
            Check(session.HitTestAnnotationOutline(new(4, 20), 1)?.Index == 0, "The uncovered portion of a line stays grabbable");
            Check(session.Revision == revision, "Hovering over artwork leaves history unchanged");
            session.RotateAnnotation(1, 90);
            Check(session.HitTestAnnotationOutline(new(16, 20), 1)?.Index == 1, $"Drawing hit testing follows the rotated {kind} outline");
        }
        session.OpenImageBytes(CreateImage());
        session.AddShape(AnnotationShape.Arrow, new(6, 24), new(52, 24), 0xFF101010, 2);
        Check(session.HitTestAnnotationOutline(new(22, 24), 1)?.Index == 0 &&
            session.HitTestAnnotationOutline(new(44, 27), 1)?.Index == 0, "The arrow shaft and its head are grabbable");
        Check(session.HitTestAnnotationOutline(new(12, 30), 1) is null, "Drawing near an arrow shaft can still start a new stroke");
        session.AddStroke(new[] { new ImagePoint(10, 34), new ImagePoint(48, 34) }, 0xFF0000FF, 3, highlighter: false);
        Check(session.HitTestAnnotationOutline(new(30, 34), 1)?.Index == 1, "Placed pen ink is grabbable from a drawing tool");
    }

    private static async Task CheckClosedShapeTransformAsync(AnnotationShape kind)
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage());
        Check(session.AddShape(kind, new(12, 14), new(36, 24), 0, 1, 0xFF008000), $"Create filled {kind}");
        AnnotationSnapshot original = session.GetAnnotation(0)!;
        PreviewSnapshot before = await session.RenderPreviewAsync();
        Check(session.RotateAnnotation(original, 90), $"Rotate selected {kind} clockwise");

        AnnotationSnapshot rotated = session.GetAnnotation(0)!;
        ImageRectangle frame = new(12, 14, 24, 10);
        Check(rotated.Shape!.GeometryBounds == frame && rotated.Shape.RotationDegrees == 90,
            $"Rotating {kind} retains its unrotated frame and records the angle");
        Check(NearTransform(rotated.Bounds.X, 19) && NearTransform(rotated.Bounds.Y, 7) &&
            NearTransform(rotated.Bounds.Width, 10) && NearTransform(rotated.Bounds.Height, 24),
            $"Rotated {kind} footprint swaps dimensions about its center");
        PreviewSnapshot after = await session.RenderPreviewAsync();
        Check(ReadPixel(before.PngBytes, 15, 19).ToArgb() == Color.Green.ToArgb() &&
            ReadPixel(after.PngBytes, 15, 19).ToArgb() == Color.White.ToArgb() &&
            ReadPixel(after.PngBytes, 24, 9).ToArgb() == Color.Green.ToArgb(),
            $"Rotated {kind} renders at the transformed geometry");
        Check(session.HitTest(new(24, 9), 0.25f) == 0 && session.HitTest(new(15, 19), 0.25f) == -1,
            $"Hit testing follows the {kind} rotation");
        Check(!session.RotateAnnotation(rotated, 450) && session.Revision == rotated.Revision,
            $"A full turn adds no {kind} history entry");
        Expect<InvalidOperationException>(() => session.RotateAnnotation(original, 45), $"Reject stale {kind} transform");
        Expect<ArgumentOutOfRangeException>(() => session.RotateAnnotation(0, float.NaN), $"Reject nonfinite {kind} angle");
        Check(session.Revision == rotated.Revision, $"Rejected {kind} transforms preserve the revision");

        session.Undo();
        Check(session.Revision == original.Revision &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(before.PngBytes),
            $"Undo restores the exact original {kind} pixels");
        session.Redo();
        Check(session.Revision == rotated.Revision &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(after.PngBytes),
            $"Redo restores the exact rotated {kind} pixels");

        Check(session.UpdateShapeStyle(0, 0xFF0000FF, 0xFF202020, 2) &&
            session.GetAnnotation(0)!.Shape!.RotationDegrees == 90, $"Restyling {kind} preserves its rotation");
        var resized = new ImageRectangle(10, 12, 22, 13);
        Check(session.ResizeAnnotation(0, resized), $"Resize rotated {kind}");
        AnnotationSnapshot selection = session.GetAnnotation(0)!;
        Check(selection.Shape!.RotationDegrees == 90 && selection.Shape.GeometryBounds == resized,
            $"Resizing {kind} retains rotation and updates the local frame");
        Check(session.UpdateAnnotationProperties(selection, resized, rotationDegrees: -270) == false &&
            session.Revision == selection.Revision, $"Equivalent {kind} properties leave the revision unchanged");
    }

    private static void CheckEndpointShapeRotation(AnnotationShape kind)
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage());
        Check(session.AddShape(kind, new(14, 22), new(38, 22), 0xFF101010, 2), $"Create {kind} endpoints");
        AnnotationSnapshot before = session.GetAnnotation(0)!;
        Check(session.RotateAnnotation(before, 90), $"Rotate {kind} endpoints around their midpoint");
        AnnotationSnapshot after = session.GetAnnotation(0)!;
        var ends = after.Shape!.Endpoints;
        Check(NearTransform(ends[0].X, 26) && NearTransform(ends[0].Y, 10) &&
            NearTransform(ends[1].X, 26) && NearTransform(ends[1].Y, 34) &&
            NearTransform(after.Shape.RotationDegrees, 90), $"Rotating {kind} preserves length, midpoint, and endpoint order");
        Check(!session.RotateAnnotation(after, 450) && session.Revision == after.Revision, $"A full turn adds no {kind} history");
        session.Undo();
        Check(session.GetAnnotation(0)!.Shape!.Endpoints[1] == new ImagePoint(38, 22), $"Undo restores {kind} tip");
    }

    private static void CheckShapeMovePicking()
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage());
        session.AddStroke(new ImagePoint[] { new(12, 22), new(42, 22) }, 0xFFFF0000, 1, false);
        Check(session.AddShape(AnnotationShape.Ellipse, new(12, 10), new(42, 32), 0xFF202020, 1), "Create hollow ellipse");
        Check(session.HitTestMovableAnnotation(new(27, 22), 0.25f)?.Index == 0,
            "Moving prefers visible ink under a hollow shape");
        Check(session.HitTest(new(27, 27), 0.25f) == -1 && session.HitTestMovableAnnotation(new(27, 27), 0.25f)?.Index == 1,
            "Hollow interiors are movable without changing ordinary hit testing");
        Check(session.RotateAnnotation(1, 90) && session.HitTestMovableAnnotation(new(17, 7), 0.25f) is null,
            "A rotated ellipse does not capture a point merely inside its bounding box");
    }

    private static void CheckStickerRotation()
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage());
        Check(session.AddEmoji(session.EmojiCatalog[0].Key, new(12, 14), new(34, 33), -20), "Create rotated sticker");
        AnnotationSnapshot original = session.GetAnnotation(0)!;
        Check(session.RotateAnnotation(original, 400), "The selection rotation command also works for stickers");
        AnnotationSnapshot rotated = session.GetAnnotation(0)!;
        Check(rotated.Emoji!.Frame == original.Emoji!.Frame && rotated.Emoji.RotationDegrees == 40 &&
            !session.RotateAnnotation(0, -320) && session.Revision == rotated.Revision,
            "Sticker rotation retains its frame and normalizes equivalent absolute angles");
    }

    private static void CheckMoveAfterCrop()
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage());
        Check(session.AddShape(AnnotationShape.Rectangle, new(12, 14), new(36, 24), 0, 1, 0xFF008000) &&
            session.Crop(new(31, 12), new(63, 47)), "Create an object partly outside a cropped image");
        AnnotationSnapshot original = session.GetAnnotation(0)!;
        ImagePoint delta = session.ConstrainAnnotationMove(original, new(0, 6));
        Check(delta == new ImagePoint(0, 6) && session.Revision == original.Revision,
            "Previewing a vertical drag never snaps an already clipped object sideways");
        Check(session.MoveAnnotation(original, delta), "Commit the previewed drag");
        ImageRectangle actual = session.GetAnnotation(0)!.Shape!.GeometryBounds;
        Check(actual == new ImageRectangle(-19, 8, 24, 10), "Committed geometry matches the previewed move exactly");

        AnnotationSnapshot shifted = session.GetAnnotation(0)!;
        Check(session.ConstrainAnnotationMove(shifted, new(-20, 0)) == new ImagePoint(0, 0),
            "A drag cannot push the clipped object entirely out of reach");
        session.Undo();
        Check(session.GetAnnotation(0)!.Shape!.GeometryBounds == original.Shape!.GeometryBounds, "Undo restores the clipped position");
    }

    private static bool NearTransform(double actual, double expected) => Math.Abs(actual - expected) < 0.01;
}
