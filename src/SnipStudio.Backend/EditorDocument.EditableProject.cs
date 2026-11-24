#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;

namespace SnipStudio;

public sealed partial class EditorDocument
{
    internal const int MaximumEditableAnnotations = 16_384;
    internal const int MaximumEditableMetadataBytes = 24 * 1024 * 1024;
    private const int MaximumPointsPerRecord = 100_000;
    private const float MaximumStoredCoordinate = 1_048_576;
    private static readonly Encoding ProjectTextEncoding = new UTF8Encoding(false, true);

    internal sealed class EditableSnapshot(Bitmap image, List<Annotation> annotations) : IDisposable
    {
        private bool ownsImage = true;
        internal Bitmap Image { get; } = image;
        internal List<Annotation> Annotations { get; } = annotations;

        internal void TransferImageOwnership() => ownsImage = false;

        public void Dispose()
        {
            if (!ownsImage) return;
            ownsImage = false;
            Image.Dispose();
        }
    }

    internal EditableSnapshot CaptureEditableSnapshot()
    {
        RequireImage();
        if (AnnotationCount > MaximumEditableAnnotations)
            throw new InvalidOperationException("This snip contains too many objects for an editable file. You can still export an image.");
        // History owns these immutable objects. A detached bitmap lets the
        // writer run off the owning thread while the document remains usable.
        var annotations = new List<Annotation>(Current.Annotations);
        Bitmap image = Current.Frame.Bitmap.Clone(new Rectangle(Point.Empty, ImageSize), PixelFormat.Format32bppArgb);
        return new EditableSnapshot(image, annotations);
    }

    internal void OpenEditableSnapshot(EditableSnapshot snapshot)
    {
        SetOwnedImage(snapshot.Image, snapshot.Annotations);
        snapshot.TransferImageOwnership();
        MarkSaved();
    }

    private enum StoredAnnotation : byte
    {
        Stroke = 1, Rectangle = 2, Ellipse = 3, Line = 4, Arrow = 5,
        Text = 6, Emoji = 7, HighlightBands = 8, SourceMasks = 9
    }

    internal enum EditableFormatVersion { Original = 1, ShapeRotation = 2, AnnotationStyles = 3 }

    internal static EditableFormatVersion GetEditableFormatVersion(List<Annotation> annotations)
    {
        var version = EditableFormatVersion.Original;
        foreach (Annotation annotation in annotations)
        {
            if (annotation is IStyledShape styled && styled.Style.OutlinePattern != ShapeOutlinePattern.Solid ||
                annotation is StrokeAnnotation { IsHighlighter: true } stroke && stroke.Ink.A != DefaultHighlighterAlpha)
                return EditableFormatVersion.AnnotationStyles;
            if (annotation is ClosedShapeAnnotation { RotationDegrees: not 0 })
                version = EditableFormatVersion.ShapeRotation;
        }
        return version;
    }

    internal static void WriteEditableAnnotations(BinaryWriter output, List<Annotation> annotations, EditableFormatVersion version,
        CancellationToken cancellationToken)
    {
        output.Write(annotations.Count);
        foreach (Annotation annotation in annotations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (annotation)
            {
                case StrokeAnnotation stroke:
                    output.Write((byte)StoredAnnotation.Stroke);
                    output.Write(stroke.Ink.ToArgb());
                    output.Write(stroke.Width);
                    output.Write(stroke.IsHighlighter);
                    WriteElementCount(output, stroke.PointCount);
                    for (int index = 0; index < stroke.PointCount; index++)
                        WritePoint(output, stroke.GetPoint(index));
                    break;
                case RectangleAnnotation rectangle:
                    output.Write((byte)StoredAnnotation.Rectangle);
                    WriteRectangle(output, rectangle.GeometryBounds);
                    if (version >= EditableFormatVersion.ShapeRotation) output.Write(rectangle.RotationDegrees);
                    WriteStyle(output, rectangle.Style, version);
                    break;
                case EllipseAnnotation ellipse:
                    output.Write((byte)StoredAnnotation.Ellipse);
                    WriteRectangle(output, ellipse.GeometryBounds);
                    if (version >= EditableFormatVersion.ShapeRotation) output.Write(ellipse.RotationDegrees);
                    WriteStyle(output, ellipse.Style, version);
                    break;
                case LineAnnotation line:
                    output.Write((byte)StoredAnnotation.Line);
                    WritePoint(output, line.Start);
                    WritePoint(output, line.End);
                    WriteStyle(output, line.Style, version);
                    break;
                case ArrowAnnotation arrow:
                    output.Write((byte)StoredAnnotation.Arrow);
                    WritePoint(output, arrow.Start);
                    WritePoint(output, arrow.End);
                    WriteStyle(output, arrow.Style, version);
                    break;
                case TextAnnotation text:
                    output.Write((byte)StoredAnnotation.Text);
                    WritePoint(output, text.Location);
                    output.Write(text.LayoutWidth);
                    output.Write(text.MinimumHeight);
                    ValidateProjectRtf(text.Rtf);
                    WriteText(output, text.Text, 16_000);
                    WriteText(output, text.Rtf, 1_048_576);
                    break;
                case EmojiAnnotation emoji:
                    output.Write((byte)StoredAnnotation.Emoji);
                    WriteText(output, emoji.Key, 256);
                    WriteRectangle(output, emoji.Frame);
                    output.Write(emoji.RotationDegrees);
                    break;
                case HighlightBandsAnnotation highlight:
                    output.Write((byte)StoredAnnotation.HighlightBands);
                    output.Write(highlight.Ink.ToArgb());
                    WriteElementCount(output, highlight.BandCount);
                    for (int index = 0; index < highlight.BandCount; index++)
                        WriteRectangle(output, highlight.GetBand(index));
                    break;
                case SourceTextMasksAnnotation masks:
                    output.Write((byte)StoredAnnotation.SourceMasks);
                    WriteElementCount(output, masks.RegionCount);
                    for (int index = 0; index < masks.RegionCount; index++)
                    {
                        WriteRectangle(output, masks.GetRegion(index));
                        output.Write(masks.GetBackground(index).ToArgb());
                    }
                    break;
                default:
                    throw new InvalidOperationException("An object in this snip cannot be stored in an editable file. You can still export an image.");
            }
            if (output.BaseStream.Position > MaximumEditableMetadataBytes)
                throw new InvalidOperationException("The snip's editable objects are too large. You can still export an image.");
        }
    }

    internal static List<Annotation> ReadEditableAnnotations(BinaryReader input, EditableFormatVersion version,
        CancellationToken cancellationToken)
    {
        int count = input.ReadInt32();
        if (count is < 0 or > MaximumEditableAnnotations ||
            count > input.BaseStream.Length - input.BaseStream.Position)
            throw new InvalidDataException("The snip's object count is invalid.");
        var annotations = new List<Annotation>(count);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            annotations.Add((StoredAnnotation)input.ReadByte() switch
            {
                StoredAnnotation.Stroke => ReadStroke(input, version),
                StoredAnnotation.Rectangle => ReadClosedShape(input, version, ellipse: false),
                StoredAnnotation.Ellipse => ReadClosedShape(input, version, ellipse: true),
                StoredAnnotation.Line => new LineAnnotation(ReadPoint(input), ReadPoint(input), ReadStyle(input, version)),
                StoredAnnotation.Arrow => new ArrowAnnotation(ReadPoint(input), ReadPoint(input), ReadStyle(input, version)),
                StoredAnnotation.Text => ReadTextAnnotation(input),
                StoredAnnotation.Emoji => ReadEmoji(input),
                StoredAnnotation.HighlightBands => ReadHighlights(input),
                StoredAnnotation.SourceMasks => ReadMasks(input),
                _ => throw new InvalidDataException("The snip contains an unsupported object type.")
            });
        }
        if (input.BaseStream.Position != input.BaseStream.Length)
            throw new InvalidDataException("The snip contains unexpected object data.");
        return annotations;
    }

    private static Annotation ReadClosedShape(BinaryReader input, EditableFormatVersion version, bool ellipse)
    {
        RectangleF frame = ReadRectangle(input);
        float rotation = version >= EditableFormatVersion.ShapeRotation ? ReadNumber(input, -180, 180) : 0;
        ShapeStyle style = ReadStyle(input, version);
        if (ellipse) return new EllipseAnnotation(frame, style, rotation);
        return new RectangleAnnotation(frame, style, rotation);
    }

    private static StrokeAnnotation ReadStroke(BinaryReader input, EditableFormatVersion version)
    {
        Color color = Color.FromArgb(input.ReadInt32());
        float width = ReadNumber(input, 0.001f, 1024);
        bool highlighter = input.ReadBoolean();
        if (highlighter && version < EditableFormatVersion.AnnotationStyles)
            color = Color.FromArgb(DefaultHighlighterAlpha, color);
        var points = new PointF[ReadElementCount(input, 8)];
        for (int index = 0; index < points.Length; index++) points[index] = ReadPoint(input);
        return new StrokeAnnotation(points, color, width, highlighter);
    }

    private static TextAnnotation ReadTextAnnotation(BinaryReader input)
    {
        PointF location = ReadPoint(input);
        float width = ReadNumber(input, 0, 32768);
        float minimumHeight = ReadNumber(input, 0, 32768);
        string plain = ReadText(input, 16_000);
        string rtf = ReadText(input, 1_048_576);
        ValidateProjectRtf(rtf);
        // The constructor reads the actual font, color and weight from RTF.
        return new TextAnnotation(plain, rtf, location, Color.Black, 12, width,
            minimumHeight, "Segoe UI", FontStyle.Regular);
    }

    private static EmojiAnnotation ReadEmoji(BinaryReader input)
    {
        string key = EmojiArtwork.ResolveKey(ReadText(input, 256));
        RectangleF frame = ReadRectangle(input);
        if (!IsUsableEmojiFrame(frame)) throw new InvalidDataException("The snip contains invalid sticker geometry.");
        return new EmojiAnnotation(key, frame, NormalizeEmojiRotation(ReadNumber(input, -180, 180)));
    }

    private static HighlightBandsAnnotation ReadHighlights(BinaryReader input)
    {
        Color color = Color.FromArgb(input.ReadInt32());
        var bands = new RectangleF[ReadElementCount(input, 16)];
        for (int index = 0; index < bands.Length; index++) bands[index] = ReadRectangle(input);
        return new HighlightBandsAnnotation(bands, color);
    }

    private static SourceTextMasksAnnotation ReadMasks(BinaryReader input)
    {
        int count = ReadElementCount(input, 20);
        var regions = new RectangleF[count];
        var colors = new Color[count];
        RectangleF union = RectangleF.Empty;
        for (int index = 0; index < count; index++)
        {
            regions[index] = ReadRectangle(input);
            colors[index] = Color.FromArgb(input.ReadInt32());
            union = union.IsEmpty ? regions[index] : RectangleF.Union(union, regions[index]);
        }
        return new SourceTextMasksAnnotation(regions, colors, union);
    }

    private static void WritePoint(BinaryWriter output, PointF point)
    {
        ValidateStoredNumber(point.X, -MaximumStoredCoordinate, MaximumStoredCoordinate);
        ValidateStoredNumber(point.Y, -MaximumStoredCoordinate, MaximumStoredCoordinate);
        output.Write(point.X);
        output.Write(point.Y);
    }

    private static PointF ReadPoint(BinaryReader input) => new(
        ReadNumber(input, -MaximumStoredCoordinate, MaximumStoredCoordinate),
        ReadNumber(input, -MaximumStoredCoordinate, MaximumStoredCoordinate));

    private static void WriteRectangle(BinaryWriter output, RectangleF rectangle)
    {
        WritePoint(output, rectangle.Location);
        output.Write(ValidateStoredNumber(rectangle.Width, float.Epsilon, MaximumStoredCoordinate));
        output.Write(ValidateStoredNumber(rectangle.Height, float.Epsilon, MaximumStoredCoordinate));
    }

    private static RectangleF ReadRectangle(BinaryReader input) => new(ReadPoint(input),
        new SizeF(ReadNumber(input, float.Epsilon, MaximumStoredCoordinate),
            ReadNumber(input, float.Epsilon, MaximumStoredCoordinate)));

    private static float ReadNumber(BinaryReader input, float minimum, float maximum) =>
        ValidateStoredNumber(input.ReadSingle(), minimum, maximum);

    private static float ValidateStoredNumber(float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException("The snip contains invalid object dimensions or coordinates.");
        return value;
    }

    private static void WriteStyle(BinaryWriter output, ShapeStyle style, EditableFormatVersion version)
    {
        WriteOptionalColor(output, style.FillColor);
        WriteOptionalColor(output, style.OutlineColor);
        output.Write(style.OutlineWidth);
        output.Write(style.FillOpacity);
        output.Write(style.OutlineOpacity);
        if (version >= EditableFormatVersion.AnnotationStyles) output.Write((byte)style.OutlinePattern);
    }

    private static ShapeStyle ReadStyle(BinaryReader input, EditableFormatVersion version)
    {
        Color fill = ReadOptionalColor(input);
        Color outline = ReadOptionalColor(input);
        float width = ReadNumber(input, float.Epsilon, 1024);
        int fillOpacity = input.ReadInt32();
        int outlineOpacity = input.ReadInt32();
        ShapeOutlinePattern pattern = version >= EditableFormatVersion.AnnotationStyles
            ? input.ReadByte() switch
            {
                (byte)ShapeOutlinePattern.Solid => ShapeOutlinePattern.Solid,
                (byte)ShapeOutlinePattern.Dashed => ShapeOutlinePattern.Dashed,
                (byte)ShapeOutlinePattern.Dotted => ShapeOutlinePattern.Dotted,
                _ => throw new InvalidDataException("The snip contains an unsupported shape outline pattern.")
            }
            : ShapeOutlinePattern.Solid;
        return new ShapeStyle(fill, outline, width, fillOpacity, outlineOpacity, pattern);
    }

    private static void WriteOptionalColor(BinaryWriter output, Color color)
    {
        output.Write(!color.IsEmpty);
        if (!color.IsEmpty) output.Write(color.ToArgb());
    }

    private static Color ReadOptionalColor(BinaryReader input) =>
        input.ReadBoolean() ? Color.FromArgb(input.ReadInt32()) : Color.Empty;

    private static void WriteElementCount(BinaryWriter output, int count)
    {
        if (count is < 1 or > MaximumPointsPerRecord)
            throw new InvalidOperationException("One object contains too many points or regions for an editable file.");
        output.Write(count);
    }

    private static int ReadElementCount(BinaryReader input, int itemBytes)
    {
        int count = input.ReadInt32();
        if (count is < 1 or > MaximumPointsPerRecord ||
            (long)count * itemBytes > input.BaseStream.Length - input.BaseStream.Position)
            throw new InvalidDataException("The snip contains an invalid object data count.");
        return count;
    }

    private static void WriteText(BinaryWriter output, string value, int maximumCharacters)
    {
        if (value.Length > maximumCharacters) throw new InvalidDataException("A text field in the snip is too long.");
        byte[] bytes = ProjectTextEncoding.GetBytes(value);
        output.Write(bytes.Length);
        output.Write(bytes);
    }

    private static string ReadText(BinaryReader input, int maximumCharacters)
    {
        int byteCount = input.ReadInt32();
        if (byteCount < 0 || byteCount > 4 * maximumCharacters ||
            byteCount > input.BaseStream.Length - input.BaseStream.Position)
            throw new InvalidDataException("A text field in the snip has an invalid length.");
        string value = ProjectTextEncoding.GetString(input.ReadBytes(byteCount));
        if (value.Length > maximumCharacters) throw new InvalidDataException("A text field in the snip is too long.");
        return value;
    }

    private static void ValidateProjectRtf(string rtf)
    {
        RichTextLayout.ValidateRtf(rtf);
        int depth = 0;
        for (int index = 0; index < rtf.Length; index++)
        {
            char value = rtf[index];
            if (depth == 0 && index > 0 && !char.IsWhiteSpace(value))
                throw new InvalidDataException("A formatted note has unexpected data after its closing group.");
            if (value == '\\') index++;
            else if (value == '{' && ++depth > 256)
                throw new InvalidDataException("A formatted note has too many nested groups.");
            else if (value == '}' && --depth < 0)
                throw new InvalidDataException("A formatted note has an unmatched closing group.");
        }
        if (depth != 0) throw new InvalidDataException("A formatted note contains an incomplete group.");
    }
}

internal static partial class RichTextLayout
{
    // Editable projects prepare formatted annotations on a private STA. Its
    // native control must be retired on that thread while shared layout caches
    // can still be in use by the live document.
    internal static void ReleaseProjectThreadEditor()
    {
        editor?.Dispose();
        editor = null;
        editorFont?.Dispose();
        editorFont = null;
    }
}
