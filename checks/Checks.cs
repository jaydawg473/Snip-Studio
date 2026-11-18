using System;
using System.Drawing;
using System.Collections.Generic;
using SnipStudio;

internal static class Checks
{
    private static int count;

    [STAThread]
    private static int Main()
    {
        try
        {
            SelectionGeometry();
            OwnershipAndHistory();
            CropAndErase();
            HighlightAndHistoryLimit();
            IndexedImageInput();
            AnnotationMovement();
            OtherAnnotationMovement();
            MovementAfterCrop();
            SelectionStackingAndClamp();
            Console.WriteLine("PASS: " + count + " checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL after " + count + " checks: " + ex);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        count++;
    }

    private static Bitmap Fixture()
    {
        Bitmap image = new Bitmap(60, 40);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
        image.SetPixel(2, 3, Color.FromArgb(255, 12, 34, 56));
        image.SetPixel(20, 20, Color.FromArgb(255, 65, 87, 109));
        return image;
    }

    private static void SelectionGeometry()
    {
        Rectangle limits = new Rectangle(-300, -100, 1200, 800);
        Assert(CaptureService.NormalizeSelection(new Point(80, 90), new Point(-20, 10), limits) == new Rectangle(-20, 10, 100, 80), "Reversed drag must normalize");
        Assert(CaptureService.NormalizeSelection(new Point(-500, -200), new Point(-200, 50), limits) == new Rectangle(-300, -100, 100, 150), "Selection must clip against negative virtual origin");
        Rectangle empty = CaptureService.NormalizeSelection(new Point(0, 10), new Point(0, 90), limits);
        Assert(empty.Width == 0 || empty.Height == 0, "Zero-width selection must stay empty");
        Rectangle outside = CaptureService.NormalizeSelection(new Point(1000, 900), new Point(1100, 1000), limits);
        Assert(outside.Width <= 0 || outside.Height <= 0, "Entirely outside selection must be empty");
    }

    private static void OwnershipAndHistory()
    {
        using (EditorDocument document = new EditorDocument())
        {
            Assert(!document.HasImage, "Empty document must have no image");
            using (Bitmap source = Fixture())
            {
                document.SetImage(source);
                source.SetPixel(2, 3, Color.Magenta);
            }
            Assert(document.HasImage && document.ImageSize == new Size(60, 40), "SetImage must own a usable clone after caller disposes source");
            Assert(document.IsDirty, "New capture must require a save");
            using (Bitmap rendered = document.RenderImage())
            {
                Assert(rendered.GetPixel(2, 3).ToArgb() == Color.FromArgb(255, 12, 34, 56).ToArgb(), "Source mutation must not alter document");
                rendered.SetPixel(2, 3, Color.Black);
            }
            using (Bitmap rendered = document.RenderImage())
                Assert(rendered.GetPixel(2, 3).ToArgb() == Color.FromArgb(255, 12, 34, 56).ToArgb(), "Rendered bitmap must not alias document");
            document.MarkSaved();
            Assert(!document.IsDirty, "MarkSaved must mark the current revision clean");
            document.AddStroke(new PointF[] { new PointF(5, 8), new PointF(40, 8) }, Color.Red, 4, false);
            Assert(document.IsDirty && document.CanUndo, "Annotation must create undoable dirty revision");
            using (Bitmap rendered = document.RenderImage())
                Assert(rendered.GetPixel(20, 8).R > 220 && rendered.GetPixel(20, 8).G < 50, "Pen must render into exported image");
            document.Undo();
            Assert(!document.IsDirty && document.CanRedo, "Undo to saved revision must clear dirty state");
            using (Bitmap rendered = document.RenderImage())
                Assert(rendered.GetPixel(20, 8).ToArgb() == Color.White.ToArgb(), "Undo must remove annotation from export");
            document.Redo();
            Assert(document.IsDirty, "Redo away from saved revision must mark dirty");
            document.MarkSaved();
            document.Undo();
            Assert(document.IsDirty, "Undo away from newly saved revision must be dirty");
            document.Redo();
            Assert(!document.IsDirty, "Redo back to saved revision must be clean");
            document.Undo();
            document.AddRectangle(new RectangleF(10, 10, 20, 15), Color.Blue, 2);
            Assert(document.IsDirty && !document.CanRedo, "A branch must discard redo without reusing saved identity");
        }
    }

    private static void CropAndErase()
    {
        using (EditorDocument document = new EditorDocument())
        using (Bitmap image = Fixture())
        {
            document.SetImage(image);
            document.MarkSaved();
            document.Crop(new Rectangle(-10, -10, 40, 35));
            Assert(document.ImageSize == new Size(30, 25), "Crop must clip to source image bounds");
            using (Bitmap rendered = document.RenderImage())
                Assert(rendered.GetPixel(20, 20).ToArgb() == image.GetPixel(20, 20).ToArgb(), "Crop must preserve source pixel values");
            document.Undo();
            Assert(document.ImageSize == image.Size && !document.IsDirty, "Undo crop must restore original size and saved state");
            document.AddArrow(new PointF(6, 7), new PointF(45, 7), Color.Red, 3);
            Assert(document.Erase(new PointF(25, 7), 5), "Eraser must hit an annotation");
            using (Bitmap rendered = document.RenderImage())
                Assert(rendered.GetPixel(25, 7).ToArgb() == Color.White.ToArgb(), "Eraser must reveal original pixels");
            document.Undo();
            using (Bitmap rendered = document.RenderImage())
                Assert(rendered.GetPixel(25, 7).R > 200 && rendered.GetPixel(25, 7).G < 100, "Undo eraser must restore annotation");
            Size prior = document.ImageSize;
            try { document.Crop(new Rectangle(3, 3, 1, 20)); } catch (ArgumentException) { }
            Assert(document.ImageSize == prior, "Invalid narrow crop must preserve image");
        }
    }

    private static void HighlightAndHistoryLimit()
    {
        using (EditorDocument document = new EditorDocument())
        using (Bitmap image = Fixture())
        {
            document.SetImage(image);
            document.AddStroke(new PointF[] { new PointF(5, 12), new PointF(45, 12) }, Color.Yellow, 8, true);
            using (Bitmap rendered = document.RenderImage())
            {
                Color pixel = rendered.GetPixel(25, 12);
                Assert(pixel.R > 200 && pixel.G > 200 && pixel.B > 0 && pixel.B < 250, "Highlighter must preserve transparency over white");
            }
            for (int i = 0; i < 40; i++)
                document.AddRectangle(new RectangleF(3, 3, 15 + (i % 20), 15), Color.Blue, 1);
            int undos = 0;
            while (document.CanUndo && undos < 100)
            {
                document.Undo();
                undos++;
            }
            Assert(undos > 0 && undos <= 25, "Undo history must stay bounded at 25 edits");
        }
    }

    private static void IndexedImageInput()
    {
        using (Bitmap indexed = new Bitmap(12, 12, System.Drawing.Imaging.PixelFormat.Format8bppIndexed))
        using (EditorDocument document = new EditorDocument())
        {
            System.Drawing.Imaging.ColorPalette palette = indexed.Palette;
            palette.Entries[0] = Color.Lime;
            indexed.Palette = palette;
            document.SetImage(indexed);
            document.AddStroke(new PointF[] { new PointF(2, 6), new PointF(10, 6) }, Color.Red, 2, false);
            using (Bitmap rendered = document.RenderImage())
            {
                Assert(rendered.GetPixel(0, 0).ToArgb() == Color.Lime.ToArgb(), "Indexed input must retain palette colors");
                Assert(rendered.GetPixel(6, 6).R > 200 && rendered.GetPixel(6, 6).G < 60, "Indexed input must accept annotations");
            }
        }
    }

    private static Bitmap WhiteImage()
    {
        Bitmap image = new Bitmap(360, 180);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
        return image;
    }

    private static bool ImagesEqual(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static Bitmap ShiftOnWhite(Bitmap source, int dx, int dy)
    {
        Bitmap image = new Bitmap(source.Width, source.Height);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(source, dx, dy);
        }
        return image;
    }

    private static Rectangle InkBounds(Bitmap image)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).ToArgb() != Color.White.ToArgb())
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static void AnnotationMovement()
    {
        using (EditorDocument document = new EditorDocument())
        using (Bitmap image = WhiteImage())
        {
            document.SetImage(image);
            document.AddText("Move me", new PointF(12, 10), Color.Blue, 20);
            document.MarkSaved();
            Assert(document.AnnotationCount == 1, "Added text must remain an editable annotation");
            RectangleF originalBounds = document.GetAnnotationBounds(0);
            Assert(document.HitTestAnnotation(new PointF(originalBounds.Left + 3, originalBounds.Top + 3), 1) == 0, "Added text must be selectable");
            using (Bitmap original = document.RenderImage())
            using (Bitmap expected = ShiftOnWhite(original, 140, 60))
            {
                Assert(!InkBounds(original).IsEmpty, "Text movement fixture must contain rendered text");
                Assert(document.MoveAnnotation(0, new PointF(140, 60)), "Moving text must report a change");
                RectangleF movedBounds = document.GetAnnotationBounds(0);
                Assert(Math.Abs(movedBounds.Left - originalBounds.Left - 140) < .01f && Math.Abs(movedBounds.Top - originalBounds.Top - 60) < .01f, "Text bounds must follow requested image-pixel movement");
                Assert(document.HitTestAnnotation(new PointF(movedBounds.Left + 3, movedBounds.Top + 3), 1) == 0, "Moved text must remain selectable at its new position");
                Assert(document.HitTestAnnotation(new PointF(originalBounds.Left + 3, originalBounds.Top + 3), 1) == -1, "Old text position must stop being selectable");
                using (Bitmap moved = document.RenderImage())
                    Assert(ImagesEqual(expected, moved), "Moving text must shift its exported pixels and restore the original area");
                Assert(document.IsDirty && document.CanUndo, "Moving text must be undoable and dirty");
                document.Undo();
                using (Bitmap undone = document.RenderImage())
                    Assert(ImagesEqual(original, undone) && !document.IsDirty, "Undo movement must restore exact pixels and saved revision");
                long priorRevision = document.Revision;
                Assert(!document.MoveAnnotation(0, PointF.Empty), "Zero movement must be a no-op");
                Assert(document.Revision == priorRevision && document.CanRedo && !document.IsDirty, "Zero movement must preserve revision, redo and clean state");
                document.Redo();
                using (Bitmap redone = document.RenderImage())
                    Assert(ImagesEqual(expected, redone) && document.IsDirty, "Redo movement must restore moved pixels");
                document.MarkSaved();
                document.Undo();
                document.MoveAnnotation(0, new PointF(130, 50));
                Assert(!document.CanRedo && document.IsDirty, "A different movement branch must discard redo without reusing saved state");
                Assert(document.RemoveAnnotation(0) && document.AnnotationCount == 0, "Selected added text must be removable");
                document.Undo();
                Assert(document.AnnotationCount == 1, "Removing added text must be undoable");
            }
        }
    }

    private static void OtherAnnotationMovement()
    {
        CheckShapeMovement("Pen", delegate(EditorDocument document)
        {
            document.AddStroke(new PointF[] { new PointF(12, 12), new PointF(65, 25) }, Color.Red, 4, false);
        });
        CheckShapeMovement("Highlighter", delegate(EditorDocument document)
        {
            document.AddStroke(new PointF[] { new PointF(12, 12), new PointF(65, 25) }, Color.Yellow, 12, true);
        });
        CheckShapeMovement("Arrow", delegate(EditorDocument document)
        {
            document.AddArrow(new PointF(12, 12), new PointF(65, 25), Color.Blue, 3);
        });
        CheckShapeMovement("Box", delegate(EditorDocument document)
        {
            document.AddRectangle(new RectangleF(12, 12, 65, 28), Color.Green, 3);
        });
        CheckShapeMovement("Text highlight bands", delegate(EditorDocument document)
        {
            document.AddHighlightBands(new RectangleF[] { new RectangleF(12, 12, 78, 14), new RectangleF(12, 34, 55, 14) }, Color.Yellow);
        });
    }

    private static void CheckShapeMovement(string name, Action<EditorDocument> add)
    {
        using (EditorDocument document = new EditorDocument())
        using (Bitmap image = WhiteImage())
        {
            document.SetImage(image);
            add(document);
            Assert(document.AnnotationCount == 1, name + " must be one editable annotation");
            using (Bitmap original = document.RenderImage())
            using (Bitmap expected = ShiftOnWhite(original, 140, 60))
            {
                Assert(document.MoveAnnotation(0, new PointF(140, 60)), name + " must be movable");
                using (Bitmap moved = document.RenderImage())
                    Assert(ImagesEqual(expected, moved), name + " movement must preserve exact exported appearance");
                document.Undo();
                using (Bitmap undone = document.RenderImage())
                    Assert(ImagesEqual(original, undone), name + " movement undo must not mutate prior annotation state");
            }
        }
    }

    private static void MovementAfterCrop()
    {
        using (EditorDocument document = new EditorDocument())
        using (Bitmap image = WhiteImage())
        {
            document.SetImage(image);
            document.AddText("Editable", new PointF(15, 15), Color.Blue, 18);
            document.MoveAnnotation(0, new PointF(130, 50));
            RectangleF beforeBounds = document.GetAnnotationBounds(0);
            Rectangle crop = new Rectangle(120, 40, 220, 120);
            using (Bitmap before = document.RenderImage())
            using (Bitmap expected = before.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                document.Crop(crop);
                Assert(document.AnnotationCount == 1, "Crop must retain added text as an editable annotation");
                RectangleF afterBounds = document.GetAnnotationBounds(0);
                Assert(Math.Abs(afterBounds.Left - beforeBounds.Left + crop.Left) < .01f && Math.Abs(afterBounds.Top - beforeBounds.Top + crop.Top) < .01f, "Crop must translate annotation coordinates into cropped image space");
                using (Bitmap cropped = document.RenderImage())
                    Assert(ImagesEqual(expected, cropped), "Preserving editable annotations must not alter cropped exported pixels");
                Assert(document.MoveAnnotation(0, new PointF(10, 10)), "Added text must remain movable after crop");
                document.Undo();
                document.Undo();
                using (Bitmap restored = document.RenderImage())
                    Assert(ImagesEqual(before, restored), "Undo movement and crop must restore the complete previous composition");
            }
        }
    }

    private static void SelectionStackingAndClamp()
    {
        using (EditorDocument document = new EditorDocument())
        using (Bitmap image = WhiteImage())
        {
            document.SetImage(image);
            document.AddText("Lower", new PointF(15, 15), Color.Blue, 18);
            document.AddText("Upper", new PointF(15, 15), Color.Red, 18);
            Assert(document.HitTestAnnotation(new PointF(20, 20), 1) == 1, "Overlapping annotation selection must prefer the topmost item");
            document.RemoveAnnotation(1);
            Assert(document.HitTestAnnotation(new PointF(20, 20), 1) == 0, "Removing topmost text must reveal the lower selectable item");
            document.Undo();
            Assert(document.MoveAnnotation(1, new PointF(10000, 10000)), "Large drag must move text within usable limits");
            RectangleF visible = RectangleF.Intersect(document.GetAnnotationBounds(1), new RectangleF(0, 0, image.Width, image.Height));
            Assert(visible.Width >= 15.9f && visible.Height >= 15.9f, "Movement must retain a usable visible part of added text");
        }
    }
}
