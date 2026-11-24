using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio.WinUI.Backend;
using OutlinePattern = SnipStudio.ShapeOutlinePattern;

internal static partial class Program
{
    private static async Task CheckEditableProjectsAsync(string output)
    {
        using EditorSession original = new();
        original.OpenImageBytes(CreateImage(transparent: true));
        original.AddStroke(new[] { new ImagePoint(1, 2), new ImagePoint(35, 9) }, 0xFFFF1800, 3, false);
        original.AddStroke(new[] { new ImagePoint(8, 12), new ImagePoint(50, 17) }, 0xFF00A0FF, 4, true);
        original.AddShape(AnnotationShape.Rectangle, new(18, 15), new(52, 32), 0xFF221100, 2, 0x8088CCFF);
        original.AddShape(AnnotationShape.Ellipse, new(38, 21), new(59, 41), 0xFFAA0000, 3);
        original.AddShape(AnnotationShape.Line, new(2, 2), new(8, 23), 0xFF4455AA, 2);
        original.AddShape(AnnotationShape.Arrow, new(16, 21), new(35, 42), 0xFF79CC44, 2);
        string note = "0012-AbC\nLine 2";
        string rtf = original.CreateTextRtf(note, "Segoe UI", 9, TextFontStyle.Bold | TextFontStyle.Italic, 0xFF1A1444);
        int textIndex = original.AddText(note, new(4, 18), 0xFF1A1444, 9, 38, rtf, minimumHeight: 28);
        Check(textIndex >= 0 && original.AddEmoji(original.EmojiCatalog[0].Key, new(46, 3), new(62, 19), -23),
            "Project fixture includes an editable note and a rotated sticker");
        Check(original.Crop(new(4, 3), new(59, 43)), "Project fixture crops across several annotation bounds");
        string expectedPreview = PixelHash((await original.RenderPreviewAsync()).PngBytes);
        string projectPath = Path.Combine(output, "editable-roundtrip" + EditorSession.EditableProjectExtension);

        using (CancellationTokenSource alreadyCanceled = new())
        {
            alreadyCanceled.Cancel();
            string canceledPath = Path.Combine(output, "editable-canceled.snipstudio");
            await ExpectAsync<OperationCanceledException>(() =>
                original.SaveEditableProjectAsync(original.PrepareEditableProjectSaveTarget(canceledPath), alreadyCanceled.Token),
                "A canceled editable save publishes no project");
            Check(!File.Exists(canceledPath) && original.IsDirty, "Cancel preserves the unstored revision");
        }

        SaveSnapshotResult saved = await original.SaveEditableProjectAsync(original.PrepareEditableProjectSaveTarget(projectPath));
        Check(saved.SavedCurrentRevision && !original.IsDirty && original.CanUndo && File.Exists(projectPath),
            "Editable save publishes exactly the current revision and keeps live undo");
        byte[] project = File.ReadAllBytes(projectPath);
        Check(BinaryPrimitives.ReadInt32LittleEndian(project.AsSpan(8, 4)) == 1,
            "Unrotated shapes and rotated stickers retain the original project format");
        saved = await original.SaveEditableProjectAsync(original.PrepareEditableProjectSaveTarget(projectPath));
        Check(saved.SavedCurrentRevision && File.ReadAllBytes(projectPath).SequenceEqual(project),
            "An unchanged project can resave to its approved path without changing stored content");
        using EditorSession reopened = new();
        reopened.OpenImageBytes(CreateImage());
        await reopened.OpenEditableProjectAsync(projectPath);
        Check(!reopened.IsDirty && !reopened.CanUndo && reopened.ImageWidth == 55 && reopened.ImageHeight == 40,
            "Opening an editable project starts a clean cropped document");
        Check(reopened.GetAnnotations().Select(item => item.Kind).SequenceEqual(original.GetAnnotations().Select(item => item.Kind)),
            "Editable format preserves annotation order and duplicate kinds");
        Check(PixelHash((await reopened.RenderPreviewAsync()).PngBytes) == expectedPreview,
            "Rendered project roundtrip reproduces cropped pixels and all annotations");
        Check(reopened.GetAnnotation(textIndex)?.Text is { } restored && restored.Text == note && restored.Rtf == rtf &&
            restored.FontStyle == (TextFontStyle.Bold | TextFontStyle.Italic) && restored.MinimumHeight == 28,
            "Rich text preserves newlines, identifier case, leading zeros, styling and the saved frame");
        Check(reopened.GetAnnotations().Any(item => item.Emoji is { RotationDegrees: -23 }),
            "Editable file preserves sticker position and rotation");
        Check(reopened.GetAnnotations().Count(item => item.Shape is
            { Shape: AnnotationShape.Rectangle or AnnotationShape.Ellipse, RotationDegrees: 0, OutlinePattern: OutlinePattern.Solid }) == 2,
            "Both closed shapes in a version 1 editable file open at zero rotation with solid outlines");
        Check(reopened.UpdateText(textIndex, "Edited after reopening", 0xFF000000, 9, 48),
            "Text remains directly editable after reopening");
        Check(reopened.IsDirty && reopened.CanUndo, "Editing a reopened project starts new undo history");
        reopened.Undo();
        Check(!reopened.IsDirty && PixelHash((await reopened.RenderPreviewAsync()).PngBytes) == expectedPreview,
            "Undo returns to the reopened editable baseline");

        byte[] damaged = (byte[])project.Clone();
        damaged[24] ^= 0x80;
        string damagedPath = Path.Combine(output, "editable-damaged.snipstudio");
        File.WriteAllBytes(damagedPath, damaged);
        long unchangedRevision = reopened.Revision;
        await ExpectAsync<InvalidDataException>(() => reopened.OpenEditableProjectAsync(damagedPath),
            "Editable format detects corrupted metadata before replacing the document");
        string truncatedPath = Path.Combine(output, "editable-truncated.snipstudio");
        int pixelOffset = 80 + BinaryPrimitives.ReadInt32LittleEndian(project.AsSpan(12, 4));
        int middleOfPixels = pixelOffset + (project.Length - pixelOffset) / 2;
        File.WriteAllBytes(truncatedPath, project[..middleOfPixels]);
        await ExpectAsync<InvalidDataException>(() => reopened.OpenEditableProjectAsync(truncatedPath),
            "Editable format rejects a truncated pixel payload");
        Check(reopened.Revision == unchangedRevision && PixelHash((await reopened.RenderPreviewAsync()).PngBytes) == expectedPreview,
            "Invalid project files preserve the existing editor state");

        var protectedTarget = reopened.PrepareEditableProjectSaveTarget(projectPath);
        byte[] externalRevision = project.Concat(new byte[] { 0x20 }).ToArray();
        File.WriteAllBytes(projectPath, externalRevision);
        await ExpectAsync<IOException>(() => reopened.SaveEditableProjectAsync(protectedTarget),
            "Save refuses to replace a changed on-disk project");
        Check(File.ReadAllBytes(projectPath).SequenceEqual(externalRevision), "External edits remain intact after save conflict");
        reopened.OpenImageBytes(CreateImage());
        Expect<IOException>(() => reopened.PrepareEditableProjectSaveTarget(projectPath),
            "Replacing the current snip clears the old project's replacement baseline");

        await CheckEditableProjectShapeRotationAsync(output);
        await CheckEditableProjectOutlinePatternsAsync(output);
    }

    private static async Task CheckEditableProjectShapeRotationAsync(string output)
    {
        using EditorSession writer = new();
        writer.OpenImageBytes(CreateImage(transparent: true));
        Check(writer.AddShape(AnnotationShape.Rectangle, new(15, 12), new(41, 24), 0xFF221100, 2, 0xFFC0CCFF) &&
            writer.AddShape(AnnotationShape.Ellipse, new(30, 21), new(51, 40), 0xFFAA0000, 3),
            "Rotation project fixture creates a filled rectangle and an outline ellipse");
        string beforeRotation = PixelHash((await writer.RenderPreviewAsync()).PngBytes);
        Check(writer.RotateAnnotation(0, 35) && writer.RotateAnnotation(1, -52),
            "Rotation project fixture contains two independently rotated closed shapes");
        string afterRotation = PixelHash((await writer.RenderPreviewAsync()).PngBytes);
        Check(beforeRotation != afterRotation, "Rotated shapes affect the rendered project pixels");

        string path = Path.Combine(output, "editable-rotated-shapes.snipstudio");
        await writer.SaveEditableProjectAsync(writer.PrepareEditableProjectSaveTarget(path));
        byte[] project = File.ReadAllBytes(path);
        Check(BinaryPrimitives.ReadInt32LittleEndian(project.AsSpan(8, 4)) == 2,
            "Closed-shape rotations require the version 2 project format");

        using EditorSession reader = new();
        await reader.OpenEditableProjectAsync(path);
        Check(reader.GetAnnotation(0)?.Shape is { Shape: AnnotationShape.Rectangle, RotationDegrees: 35, OutlinePattern: OutlinePattern.Solid } &&
            reader.GetAnnotation(1)?.Shape is { Shape: AnnotationShape.Ellipse, RotationDegrees: -52, OutlinePattern: OutlinePattern.Solid } &&
            PixelHash((await reader.RenderPreviewAsync()).PngBytes) == afterRotation,
            "Version 2 reopens both shape rotations and reproduces the exact rendered pixels");

        long reopenedRevision = reader.Revision;
        // Eight-byte magic, version, metadata length, digest, width/height/count,
        // shape kind, four frame coordinates, then the first shape's angle.
        const int versionOffset = 8;
        const int firstAngleOffset = EditableMetadataOffset + 3 * sizeof(int) + 1 + 4 * sizeof(float);
        foreach (float invalidAngle in new[] { float.NaN, 270f })
        {
            byte[] invalid = (byte[])project.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(firstAngleOffset, sizeof(float)), invalidAngle);
            RefreshEditableMetadataDigest(invalid);
            string name = float.IsNaN(invalidAngle) ? "nonfinite" : "out-of-range";
            string invalidPath = Path.Combine(output, $"editable-rotation-{name}.snipstudio");
            File.WriteAllBytes(invalidPath, invalid);
            await ExpectAsync<InvalidDataException>(() => reader.OpenEditableProjectAsync(invalidPath),
                $"Editable format rejects a {name} shape angle even with an intact object digest");
        }
        byte[] unknownVersion = (byte[])project.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(unknownVersion.AsSpan(versionOffset, sizeof(int)), int.MaxValue);
        string unknownPath = Path.Combine(output, "editable-unsupported-rotation-version.snipstudio");
        File.WriteAllBytes(unknownPath, unknownVersion);
        await ExpectAsync<InvalidDataException>(() => reader.OpenEditableProjectAsync(unknownPath),
            "Reader rejects unsupported editable format versions");
        Check(reader.Revision == reopenedRevision && !reader.IsDirty &&
            PixelHash((await reader.RenderPreviewAsync()).PngBytes) == afterRotation,
            "Invalid rotation records leave the open project unchanged");
    }

    private static async Task CheckEditableProjectOutlinePatternsAsync(string output)
    {
        using EditorSession writer = new();
        writer.OpenImageBytes(CreateImage(transparent: true));
        Check(writer.AddShape(AnnotationShape.Rectangle, new(7, 9), new(29, 18), 0xFF123456, 2,
                0x803388CC, OutlinePattern.Dashed) &&
            writer.AddShape(AnnotationShape.Ellipse, new(25, 16), new(48, 34), 0xFFAA0000, 3,
                outlinePattern: OutlinePattern.Dotted) &&
            writer.AddShape(AnnotationShape.Line, new(8, 22), new(18, 42), 0xFF4455AA, 2,
                outlinePattern: OutlinePattern.Dashed) &&
            writer.AddShape(AnnotationShape.Arrow, new(9, 40), new(50, 40), 0xFF79CC44, 2,
                outlinePattern: OutlinePattern.Dotted),
            "The outline-pattern project fixture includes all four editable shape kinds");
        Check(writer.RotateAnnotation(1, 24), "A patterned outline can share a record with a nonzero angle");

        string preview = PixelHash((await writer.RenderPreviewAsync()).PngBytes);
        string path = Path.Combine(output, "editable-outline-patterns.snipstudio");
        await writer.SaveEditableProjectAsync(writer.PrepareEditableProjectSaveTarget(path));
        byte[] project = File.ReadAllBytes(path);
        Check(BinaryPrimitives.ReadInt32LittleEndian(project.AsSpan(8, 4)) == 3,
            "Non-solid outlines use version 3 project records");

        using EditorSession reader = new();
        await reader.OpenEditableProjectAsync(path);
        Check(reader.GetAnnotations().Select(item => item.Shape!.OutlinePattern).SequenceEqual(new[]
            { OutlinePattern.Dashed, OutlinePattern.Dotted, OutlinePattern.Dashed, OutlinePattern.Dotted }) &&
            reader.GetAnnotation(1)?.Shape?.RotationDegrees == 24 &&
            PixelHash((await reader.RenderPreviewAsync()).PngBytes) == preview,
            "Version 3 preserves every outline pattern, closed-shape rotation, and exact rendered pixels");

        long revision = reader.Revision;
        byte[] invalid = (byte[])project.Clone();
        // The four shapes are the only records in this fixture. The last style
        // ends with the outline-pattern byte.
        invalid[EditableMetadataOffset + GetEditableMetadataLength(invalid) - 1] = byte.MaxValue;
        RefreshEditableMetadataDigest(invalid);
        string invalidPath = Path.Combine(output, "editable-unsupported-outline-pattern.snipstudio");
        File.WriteAllBytes(invalidPath, invalid);
        await ExpectAsync<InvalidDataException>(() => reader.OpenEditableProjectAsync(invalidPath),
            "An unknown outline pattern is rejected even with an intact object digest");
        Check(reader.Revision == revision && !reader.IsDirty &&
            PixelHash((await reader.RenderPreviewAsync()).PngBytes) == preview,
            "Unknown outline patterns preserve the open project");
    }

    private const int EditableMetadataOffset = 48;

    private static int GetEditableMetadataLength(byte[] project) =>
        BinaryPrimitives.ReadInt32LittleEndian(project.AsSpan(12, sizeof(int)));

    private static void RefreshEditableMetadataDigest(byte[] project) =>
        SHA256.HashData(project.AsSpan(EditableMetadataOffset, GetEditableMetadataLength(project)))
            .AsSpan().CopyTo(project.AsSpan(16, 32));
}
