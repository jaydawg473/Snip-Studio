using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckShapePatternsAsync(string output)
    {
        byte[] paper = CreateShapePatternCanvas();
        foreach (AnnotationShape kind in Enum.GetValues<AnnotationShape>())
        {
            using EditorSession session = new();
            session.OpenImageBytes(paper);
            bool endpointShape = kind is AnnotationShape.Line or AnnotationShape.Arrow;
            ImagePoint first = endpointShape ? new(14, 56) : new(14, 20);
            ImagePoint last = endpointShape ? new(146, 56) : new(146, 92);
            Check(session.AddShape(kind, first, last, 0xFF000000, 4), $"Create {kind} with the default outline");
            AnnotationSnapshot solid = session.GetAnnotation(0)!;
            Check(solid.Shape!.OutlinePattern == ShapeOutlinePattern.Solid, $"Existing {kind} creation remains solid");
            byte[] solidPixels = (await session.RenderPreviewAsync()).PngBytes;
            string solidHash = PixelHash(solidPixels);

            Check(ApplyShapePattern(session, solid, ShapeOutlinePattern.Dashed), $"Set {kind} outline to Dashed");
            AnnotationSnapshot dashed = session.GetAnnotation(0)!;
            byte[] dashedPixels = (await session.RenderPreviewAsync()).PngBytes;
            string dashedHash = PixelHash(dashedPixels);
            Check(dashed.Shape!.OutlinePattern == ShapeOutlinePattern.Dashed &&
                dashed.Shape.GeometryBounds == solid.Shape.GeometryBounds, $"Changing the {kind} outline preserves its geometry");

            Check(ApplyShapePattern(session, dashed, ShapeOutlinePattern.Dotted), $"Set {kind} outline to Dotted");
            AnnotationSnapshot dotted = session.GetAnnotation(0)!;
            byte[] dottedPixels = (await session.RenderPreviewAsync()).PngBytes;
            string dottedHash = PixelHash(dottedPixels);
            Check(dotted.Shape!.OutlinePattern == ShapeOutlinePattern.Dotted &&
                solidHash != dashedHash && solidHash != dottedHash && dashedHash != dottedHash &&
                CountShapePatternPixels(dashedPixels) > 0 && CountShapePatternPixels(dottedPixels) > 0 &&
                CountShapePatternPixels(dashedPixels) < CountShapePatternPixels(solidPixels) &&
                CountShapePatternPixels(dottedPixels) < CountShapePatternPixels(solidPixels),
                $"{kind} renders distinct solid, dashed, and dotted outlines with visible gaps");

            if (kind == AnnotationShape.Arrow)
            {
                var headBeyondTheShaft = new Rectangle(125, 46, 21, 7);
                Check(CountShapePatternPixels(dashedPixels, headBeyondTheShaft) > 0 &&
                    CountShapePatternPixels(dottedPixels, headBeyondTheShaft) > 0,
                    "Patterned arrows retain a visible head beyond the shaft");
            }

            Check(!ApplyShapePattern(session, dotted, ShapeOutlinePattern.Dotted) && session.Revision == dotted.Revision,
                $"Reapplying the same {kind} outline leaves history alone");
            Expect<ArgumentOutOfRangeException>(() => ApplyShapePattern(session, dotted, (ShapeOutlinePattern)255),
                $"Reject invalid {kind} outline pattern");
            Check(session.Revision == dotted.Revision, $"Rejected {kind} styles preserve the current revision");

            session.Undo();
            Check(session.GetAnnotation(0)!.Shape!.OutlinePattern == ShapeOutlinePattern.Dashed &&
                PixelHash((await session.RenderPreviewAsync()).PngBytes) == dashedHash,
                $"Undo returns the exact dashed {kind} pixels");
            session.Redo();
            Check(session.GetAnnotation(0)!.Shape!.OutlinePattern == ShapeOutlinePattern.Dotted &&
                PixelHash((await session.RenderPreviewAsync()).PngBytes) == dottedHash,
                $"Redo returns the exact dotted {kind} pixels");

            Check(session.MoveAnnotation(session.GetAnnotation(0)!, new(3, 4)) &&
                session.GetAnnotation(0)!.Shape!.OutlinePattern == ShapeOutlinePattern.Dotted,
                $"Moving {kind} preserves its chosen outline");
            Check(session.RotateAnnotation(session.GetAnnotation(0)!, 31) &&
                session.GetAnnotation(0)!.Shape!.OutlinePattern == ShapeOutlinePattern.Dotted,
                $"Rotating {kind} preserves its chosen outline");

            if (kind == AnnotationShape.Arrow)
            {
                string path = Path.Combine(output, "patterned-arrow.png");
                string expected = PixelHash((await session.RenderPreviewAsync()).PngBytes);
                await session.SaveAsync(path);
                Check(PixelHash(File.ReadAllBytes(path)) == expected, "PNG export reproduces the patterned arrow pixels");
            }
        }
    }

    private static bool ApplyShapePattern(EditorSession session, AnnotationSnapshot annotation, ShapeOutlinePattern pattern)
    {
        ShapeAnnotationSnapshot shape = annotation.Shape!;
        var style = new ShapeStyleEdit(shape.FillArgb, shape.OutlineArgb, shape.OutlineWidth,
            shape.FillOpacity, shape.OutlineOpacity, pattern);
        return shape.Endpoints.Count == 2
            ? session.UpdateShapeEndpoints(annotation, shape.Endpoints[0], shape.Endpoints[1], style)
            : session.UpdateAnnotationProperties(annotation, shape.GeometryBounds, style);
    }

    private static byte[] CreateShapePatternCanvas()
    {
        using Bitmap image = new(160, 112, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
        using MemoryStream png = new();
        image.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static int CountShapePatternPixels(byte[] png, Rectangle? region = null)
    {
        using MemoryStream stream = new(png, writable: false);
        using Bitmap image = new(stream);
        Rectangle area = region ?? new Rectangle(0, 0, image.Width, image.Height);
        int ink = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (image.GetPixel(x, y).R < 128) ink++;
        return ink;
    }
}
