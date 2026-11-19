#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;

namespace SnipStudio;

public sealed partial class EditorDocument
{
    // The same non-destructive source mask representation used by recognized
    // replacement, without creating empty text annotations. Every selected row
    // can retain its own sampled background in one Undo transaction.
    internal bool EraseRecognizedRegions(IReadOnlyList<Rectangle> regions, IReadOnlyList<Color> backgrounds,
        object expectedImageIdentity, long expectedRevision)
    {
        RequireImage();
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(backgrounds);
        if (regions.Count != backgrounds.Count)
            throw new ArgumentException("Every selected region needs its own source background.", nameof(backgrounds));
        if (regions.Count == 0) return false;
        if (regions.Count > 4096) throw new ArgumentException("Select fewer source text rows.", nameof(regions));
        if (!ReferenceEquals(expectedImageIdentity, BaseIdentity) || expectedRevision != Revision) return false;
        var masks = new Rectangle[regions.Count];
        var colors = new Color[regions.Count];
        for (int index = 0; index < masks.Length; index++)
        {
            Rectangle region = regions[index];
            if (region.Width < 1 || region.Height < 1 || ClipPixelRectangle(region) != region)
                throw new ArgumentException("Selected source masks must remain within the image.", nameof(regions));
            masks[index] = region;
            colors[index] = Color.FromArgb(255, backgrounds[index]);
        }
        // Allocate/validate before retaining history or advancing its revision.
        var annotations = new List<Annotation>(Current.Annotations.Count + 1)
        {
            new SourceTextMasksAnnotation(masks, colors)
        };
        annotations.AddRange(Current.Annotations);
        if (!ReferenceEquals(expectedImageIdentity, BaseIdentity) || expectedRevision != Revision) return false;
        Current.Frame.AddReference();
        Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
        return true;
    }
}
