#nullable enable

using System;
using System.Collections.Generic;

namespace SnipStudio.WinUI.Backend;

/// <summary>A point in source-image pixels, independent of display zoom or DPI.</summary>
public readonly record struct ImagePoint(double X, double Y);

/// <summary>A detached PNG of a particular committed document revision.</summary>
public sealed record PreviewSnapshot(long Revision, byte[] PngBytes);

/// <summary>Identifies the revision published to disk and whether it is still current.</summary>
public sealed record SaveSnapshotResult(long Revision, bool SavedCurrentRevision, string Path);

/// <summary>A detached rectangle in source-image pixels.</summary>
public readonly record struct ImageRectangle(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public enum AnnotationShape { Line, Arrow, Rectangle, Ellipse }
public enum AnnotationKind { Ink, Text, Shape, Emoji }

[Flags]
public enum TextFontStyle { Regular = 0, Bold = 1, Italic = 2, Underline = 4, Strikeout = 8 }

public sealed record TextAnnotationSnapshot(string Text, string Rtf, string FontFamily,
    TextFontStyle FontStyle, ImagePoint Location, uint Argb, float FontSize,
    float LayoutWidth, float MinimumHeight, ImageRectangle InkBounds);

public sealed record ShapeAnnotationSnapshot(AnnotationShape Shape, uint? FillArgb, uint? OutlineArgb,
    float OutlineWidth, int FillOpacity, int OutlineOpacity, ImageRectangle GeometryBounds,
    IReadOnlyList<ImagePoint> Endpoints);

public sealed record ShapeStyleEdit(uint? FillArgb, uint? OutlineArgb, float OutlineWidth,
    int FillOpacity = 100, int OutlineOpacity = 100);

public sealed record EmojiAnnotationSnapshot(string Key, string Glyph, string DisplayName,
    ImageRectangle Frame, float RotationDegrees);

/// <summary>Revision is part of the identity: an index alone must not survive Undo or replacement.</summary>
public sealed record AnnotationSnapshot(int Index, long Revision, AnnotationKind Kind,
    ImageRectangle Bounds, TextAnnotationSnapshot? Text, ShapeAnnotationSnapshot? Shape,
    EmojiAnnotationSnapshot? Emoji)
{
    internal object? Owner { get; init; }
}

public sealed record EmojiStickerSnapshot(string Key, string Glyph, string DisplayName);
