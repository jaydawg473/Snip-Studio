using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SnipStudio
{
    public sealed class EmojiStickerDefinition
    {
        public string Key { get; private set; }
        public string Glyph { get; private set; }
        public string DisplayName { get; private set; }

        internal EmojiStickerDefinition(string key, string glyph, string displayName)
        {
            Key = key; Glyph = glyph; DisplayName = displayName;
        }
    }

    // Microsoft Fluent Emoji 3D artwork is embedded; no font, file, or network lookup.
    // See assets/emoji/ATTRIBUTION.txt and manifest.json for the pinned upstream sources.
    public static class EmojiArtwork
    {
        private sealed class ArtworkEntry
        {
            internal readonly EmojiStickerDefinition Definition;
            internal readonly object Sync = new object();
            internal Bitmap Image;
            internal Rectangle SourceBounds;
            internal ArtworkEntry(EmojiStickerDefinition definition) { Definition = definition; }
        }

        private static readonly ReadOnlyCollection<EmojiStickerDefinition> catalog;
        private static readonly Dictionary<string, ArtworkEntry> entries;
        private static readonly Dictionary<string, string> aliases;

        static EmojiArtwork()
        {
            EmojiStickerDefinition[] definitions = {
                new EmojiStickerDefinition("smile", "\U0001F600", "Smile"),
                new EmojiStickerDefinition("laugh", "\U0001F604", "Laugh"),
                new EmojiStickerDefinition("happy", "\U0001F642", "Happy"),
                new EmojiStickerDefinition("wink", "\U0001F609", "Wink"),
                new EmojiStickerDefinition("love", "\U0001F60D", "Love"),
                new EmojiStickerDefinition("thinking", "\U0001F914", "Thinking"),
                new EmojiStickerDefinition("thumbs-up", "\U0001F44D", "Thumbs up"),
                new EmojiStickerDefinition("thumbs-down", "\U0001F44E", "Thumbs down"),
                new EmojiStickerDefinition("applause", "\U0001F44F", "Applause"),
                new EmojiStickerDefinition("thank-you", "\U0001F64F", "Thank you"),
                new EmojiStickerDefinition("strong", "\U0001F4AA", "Strong"),
                new EmojiStickerDefinition("look", "\U0001F440", "Look"),
                new EmojiStickerDefinition("check-mark", "\u2705", "Check mark"),
                new EmojiStickerDefinition("cross", "\u274C", "Cross"),
                new EmojiStickerDefinition("warning", "\u26A0\uFE0F", "Warning"),
                new EmojiStickerDefinition("important", "\u2757", "Important"),
                new EmojiStickerDefinition("question", "\u2753", "Question"),
                new EmojiStickerDefinition("idea", "\U0001F4A1", "Idea"),
                new EmojiStickerDefinition("star", "\u2B50", "Star"),
                new EmojiStickerDefinition("heart", "\u2764\uFE0F", "Heart"),
                new EmojiStickerDefinition("fire", "\U0001F525", "Fire"),
                new EmojiStickerDefinition("celebrate", "\U0001F389", "Celebrate"),
                new EmojiStickerDefinition("pin", "\U0001F4CC", "Pin"),
                new EmojiStickerDefinition("right-arrow", "\u27A1\uFE0F", "Right arrow")
            };
            catalog = Array.AsReadOnly(definitions);
            entries = new Dictionary<string, ArtworkEntry>(StringComparer.Ordinal);
            aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EmojiStickerDefinition definition in definitions)
            {
                entries.Add(definition.Key, new ArtworkEntry(definition));
                aliases.Add(definition.Key, definition.Key);
                aliases.Add(definition.Glyph, definition.Key);
                string plainGlyph = definition.Glyph.Replace("\uFE0F", "");
                if (!aliases.ContainsKey(plainGlyph)) aliases.Add(plainGlyph, definition.Key);
                string emojiGlyph = plainGlyph + "\uFE0F";
                if (!aliases.ContainsKey(emojiGlyph)) aliases.Add(emojiGlyph, definition.Key);
            }
        }

        public static IReadOnlyList<EmojiStickerDefinition> Catalog { get { return catalog; } }

        public static string ResolveKey(string glyphOrKey)
        {
            string key;
            if (glyphOrKey == null || !aliases.TryGetValue(glyphOrKey, out key))
                throw new ArgumentException("Choose an emoji from the sticker catalog.", "glyphOrKey");
            return key;
        }

        public static string GetGlyph(string key) { return GetEntry(key).Definition.Glyph; }
        public static string GetDisplayName(string key) { return GetEntry(key).Definition.DisplayName; }

        public static float GetAspectRatio(string key)
        {
            ArtworkEntry entry = GetEntry(key);
            lock (entry.Sync)
            {
                EnsureLoaded(entry);
                return entry.SourceBounds.Width / (float)entry.SourceBounds.Height;
            }
        }

        // Caller owns the returned thumbnail. The cached source never leaves this class.
        public static Bitmap CreateThumbnail(string key, int maxSize)
        {
            if (maxSize < 1 || maxSize > 4096) throw new ArgumentOutOfRangeException("maxSize");
            float aspect = GetAspectRatio(key);
            int width = Math.Max(1, aspect >= 1f ? maxSize : (int)Math.Round(maxSize * aspect));
            int height = Math.Max(1, aspect >= 1f ? (int)Math.Round(maxSize / aspect) : maxSize);
            Bitmap thumbnail = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(thumbnail))
                {
                    graphics.Clear(Color.Transparent);
                    Draw(graphics, key, new RectangleF(0, 0, width, height));
                }
                return thumbnail;
            }
            catch { thumbnail.Dispose(); throw; }
        }

        // Destination is in the caller's coordinates; its current transform handles rotation.
        // Use the nontransparent source bounds, then fit uniformly without stretching.
        public static void Draw(Graphics graphics, string key, RectangleF destination)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            if (!IsFinite(destination.X) || !IsFinite(destination.Y) ||
                !IsFinite(destination.Width) || !IsFinite(destination.Height))
                throw new ArgumentException("Sticker destination must be finite.", "destination");
            ArtworkEntry entry = GetEntry(key);
            if (destination.Width <= 0 || destination.Height <= 0) return;
            lock (entry.Sync)
            {
                EnsureLoaded(entry);
                Rectangle source = entry.SourceBounds;
                float fit = Math.Min(destination.Width / source.Width, destination.Height / source.Height);
                float width = source.Width * fit, height = source.Height * fit;
                RectangleF fitted = new RectangleF(destination.X + (destination.Width - width) / 2f,
                    destination.Y + (destination.Height - height) / 2f, width, height);
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.DrawImage(entry.Image, fitted, new RectangleF(source.X, source.Y, source.Width, source.Height), GraphicsUnit.Pixel);
                }
                finally { graphics.Restore(state); }
            }
        }

        private static ArtworkEntry GetEntry(string key) { return entries[ResolveKey(key)]; }
        private static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        // Invoked under the entry lock. Decode and detach fully before closing the stream.
        private static void EnsureLoaded(ArtworkEntry entry)
        {
            if (entry.Image != null) return;
            string resource = "SnipStudio.Emoji." + entry.Definition.Key + ".png";
            Bitmap detached = null;
            try
            {
                using (Stream stream = typeof(EmojiArtwork).Assembly.GetManifestResourceStream(resource))
                {
                    if (stream == null) throw new InvalidOperationException("Embedded emoji artwork is missing: " + entry.Definition.Key);
                    using (Image decoded = Image.FromStream(stream, true, true))
                    {
                        if (decoded.RawFormat.Guid != ImageFormat.Png.Guid || decoded.Width < 1 || decoded.Height < 1 ||
                            decoded.Width > 4096 || decoded.Height > 4096)
                            throw new InvalidOperationException("Embedded emoji artwork is invalid: " + entry.Definition.Key);
                        detached = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
                        using (Graphics graphics = Graphics.FromImage(detached))
                        {
                            graphics.CompositingMode = CompositingMode.SourceCopy;
                            graphics.DrawImageUnscaled(decoded, 0, 0);
                        }
                    }
                }
                Rectangle bounds = FindAlphaBounds(detached);
                if (bounds.Width < 1 || bounds.Height < 1)
                    throw new InvalidOperationException("Embedded emoji artwork has no visible pixels: " + entry.Definition.Key);
                entry.SourceBounds = bounds;
                entry.Image = detached;
                detached = null;
            }
            finally { if (detached != null) detached.Dispose(); }
        }

        private static Rectangle FindAlphaBounds(Bitmap bitmap)
        {
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (row[x * 4 + 3] == 0) continue;
                        if (x < left) left = x; if (x > right) right = x;
                        if (y < top) top = y; if (y > bottom) bottom = y;
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
    }
}
