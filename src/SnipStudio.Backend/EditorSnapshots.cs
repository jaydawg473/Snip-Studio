#nullable enable

namespace SnipStudio.WinUI.Backend;

/// <summary>A point in source-image pixels, independent of display zoom or DPI.</summary>
public readonly record struct ImagePoint(double X, double Y);

/// <summary>A detached PNG of a particular committed document revision.</summary>
public sealed record PreviewSnapshot(long Revision, byte[] PngBytes);

/// <summary>Identifies the revision published to disk and whether it is still current.</summary>
public sealed record SaveSnapshotResult(long Revision, bool SavedCurrentRevision, string Path);
