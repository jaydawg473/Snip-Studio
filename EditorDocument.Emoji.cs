using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnipStudio
{
    // A detached snapshot: callers cannot mutate the object retained by undo history.
    public sealed class EmojiAnnotationInfo
    {
        public string Key { get; private set; }
        public string Glyph { get; private set; }
        public string DisplayName { get; private set; }
        public RectangleF Frame { get; private set; }
        public float RotationDegrees { get; private set; }
        public RectangleF Bounds { get; private set; }

        internal EmojiAnnotationInfo(string key, RectangleF frame, float rotationDegrees, RectangleF bounds)
        {
            Key = key;
            Glyph = EmojiArtwork.GetGlyph(key);
            DisplayName = EmojiArtwork.GetDisplayName(key);
            Frame = frame;
            RotationDegrees = rotationDegrees;
            Bounds = bounds;
        }
    }

    public sealed partial class EditorDocument
    {
        public bool AddEmoji(string glyphOrKey, RectangleF frame, float rotationDegrees = 0)
        {
            ThrowIfDisposed();
            if (!HasImage) return false;
            string key = EmojiArtwork.ResolveKey(glyphOrKey);
            float rotation = NormalizeEmojiRotation(rotationDegrees);
            if (!IsUsableEmojiFrame(frame)) return false;
            Add(new EmojiAnnotation(key, frame, rotation));
            return true;
        }

        public EmojiAnnotationInfo GetEmojiAnnotation(int index)
        {
            ThrowIfDisposed();
            EmojiAnnotation emoji = GetEmojiAnnotationObject(index);
            return emoji == null ? null : new EmojiAnnotationInfo(emoji.Key, emoji.Frame, emoji.RotationDegrees, emoji.Bounds);
        }

        internal EmojiAnnotation CreateEmojiAnnotationPreview(int index, RectangleF frame, float rotationDegrees)
        {
            RequireImage();
            float rotation = NormalizeEmojiRotation(rotationDegrees);
            if (!IsUsableEmojiFrame(frame)) return null;
            EmojiAnnotation original = GetEmojiAnnotationObject(index);
            return original == null ? null : new EmojiAnnotation(original.Key, frame, rotation);
        }

        public bool UpdateEmojiAnnotationBounds(int index, RectangleF frame, float rotationDegrees)
        {
            EmojiAnnotation replacement = CreateEmojiAnnotationPreview(index, frame, rotationDegrees);
            if (replacement == null) return false;
            EmojiAnnotation original = GetEmojiAnnotationObject(index);
            if (original.Frame == replacement.Frame && original.RotationDegrees == replacement.RotationDegrees) return false;
            return ReplaceAnnotation(index, replacement);
        }

        private EmojiAnnotation GetEmojiAnnotationObject(int index)
        {
            return HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] as EmojiAnnotation : null;
        }

        private static bool IsUsableEmojiFrame(RectangleF frame)
        {
            ValidatePoint(frame.Location);
            ValidatePoint(new PointF(frame.Right, frame.Bottom));
            return frame.Width >= 1 && frame.Height >= 1;
        }

        private static float NormalizeEmojiRotation(float rotationDegrees)
        {
            if (Single.IsNaN(rotationDegrees) || Single.IsInfinity(rotationDegrees))
                throw new ArgumentOutOfRangeException("rotationDegrees");
            float rotation = rotationDegrees % 360;
            if (rotation >= 180) rotation -= 360;
            if (rotation < -180) rotation += 360;
            return rotation == 0 ? 0 : rotation;
        }

        // Sticker artwork is shared by the catalog. History contains only its
        // stable catalog key and immutable geometry, without copied bitmaps.
        internal sealed class EmojiAnnotation : Annotation
        {
            private readonly string key;
            private readonly RectangleF frame;
            private readonly float rotationDegrees;
            private readonly RectangleF bounds;
            private readonly double cosine, sine;

            internal EmojiAnnotation(string key, RectangleF frame, float rotationDegrees)
            {
                this.key = key;
                this.frame = frame;
                this.rotationDegrees = rotationDegrees;
                double radians = rotationDegrees * Math.PI / 180;
                cosine = Math.Cos(radians);
                sine = Math.Sin(radians);
                double halfWidth = (Math.Abs(cosine) * frame.Width + Math.Abs(sine) * frame.Height) / 2;
                double halfHeight = (Math.Abs(sine) * frame.Width + Math.Abs(cosine) * frame.Height) / 2;
                double centerX = (double)frame.X + frame.Width / 2.0;
                double centerY = (double)frame.Y + frame.Height / 2.0;
                bounds = RectangleF.FromLTRB((float)(centerX - halfWidth), (float)(centerY - halfHeight),
                    (float)(centerX + halfWidth), (float)(centerY + halfHeight));
                ValidatePoint(bounds.Location);
                ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
            }

            internal string Key { get { return key; } }
            internal RectangleF Frame { get { return frame; } }
            internal float RotationDegrees { get { return rotationDegrees; } }
            public override RectangleF Bounds { get { return bounds; } }

            public override Annotation Translate(PointF delta)
            {
                RectangleF moved = frame;
                moved.Offset(delta);
                return new EmojiAnnotation(key, moved, rotationDegrees);
            }

            public override void Draw(Graphics graphics)
            {
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
                    graphics.RotateTransform(rotationDegrees);
                    EmojiArtwork.Draw(graphics, key, new RectangleF(-frame.Width / 2, -frame.Height / 2, frame.Width, frame.Height));
                }
                finally { graphics.Restore(state); }
            }

            public override bool HitTest(PointF point, float radius)
            {
                // Inverse rotation keeps selection and erasing aligned with the
                // actual object frame instead of its larger axis-aligned bounds.
                double x = point.X - ((double)frame.X + frame.Width / 2.0);
                double y = point.Y - ((double)frame.Y + frame.Height / 2.0);
                double localX = x * cosine + y * sine;
                double localY = -x * sine + y * cosine;
                double outsideX = Math.Max(0, Math.Abs(localX) - frame.Width / 2.0);
                double outsideY = Math.Max(0, Math.Abs(localY) - frame.Height / 2.0);
                return outsideX * outsideX + outsideY * outsideY <= (double)radius * radius;
            }
        }
    }
}
