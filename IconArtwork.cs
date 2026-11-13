using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SnipStudio
{
    // One set of vector geometry drives both the live toolbar and the exported SVGs.
    public static class IconArtwork
    {
        public static readonly string[] Keys = new string[] {
            "new", "rectangle", "freeform", "window", "fullscreen", "scrolling", "crop",
            "pen", "highlighter", "shapes", "ellipse", "line", "emoji", "arrow", "box", "text", "eraser", "remove-area", "color", "move", "pan",
            "open", "paste", "copy", "save", "share", "undo", "redo", "zoom-in", "zoom-out", "fit", "help", "text-scan", "print",
            "record", "stop", "microphone", "volume", "settings", "more", "camera", "clock", "video"
        };

        private static readonly Dictionary<string, RectangleF> toolInkBounds = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        private static readonly RectangleF appInkBounds = new RectangleF(.725f, .725f, 22.55f, 22.55f);

        public static Bitmap CreateToolIcon(string key, int size, Color color)
        {
            if (size < 1 || size > 2048) throw new ArgumentOutOfRangeException("size");
            RectangleF bounds = GetToolInkBounds(key);
            float fit = size / Math.Max(bounds.Width, bounds.Height);
            int width = Math.Max(1, Math.Min(size, (int)Math.Ceiling(bounds.Width * fit)));
            int height = Math.Max(1, Math.Min(size, (int)Math.Ceiling(bounds.Height * fit)));
            int samples = size <= 128 ? 4 : 1;
            using (Bitmap master = new Bitmap(width * samples, height * samples, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(master))
                {
                    Prepare(graphics);
                    graphics.Clear(Color.Transparent);
                    float uniform = Math.Min(width / bounds.Width, height / bounds.Height) * samples;
                    graphics.TranslateTransform((master.Width - bounds.Width * uniform) / 2f, (master.Height - bounds.Height * uniform) / 2f);
                    graphics.ScaleTransform(uniform, uniform);
                    graphics.TranslateTransform(-bounds.X, -bounds.Y);
                    using (VectorPainter painter = new VectorPainter(graphics, color)) DrawTool(key, painter);
                }
                using (Bitmap raster = Resize(master, width, height)) return TrimTransparent(raster);
            }
        }

        public static string GetToolSvg(string key)
        {
            using (VectorPainter painter = new VectorPainter(null, Color.Black))
            {
                DrawTool(key, painter);
                RectangleF bounds = painter.InkBounds;
                float fit = 24f / Math.Max(bounds.Width, bounds.Height);
                return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"" + Number(bounds.Width * fit) + "\" height=\"" + Number(bounds.Height * fit) +
                    "\" viewBox=\"" + Number(bounds.X) + " " + Number(bounds.Y) + " " + Number(bounds.Width) + " " + Number(bounds.Height) +
                    "\" preserveAspectRatio=\"xMidYMid meet\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n" + painter.Svg + "</svg>\n";
            }
        }

        public static Bitmap CreateAppIconBitmap(int size)
        {
            if (size < 1 || size > 2048) throw new ArgumentOutOfRangeException("size");
            int renderSize = size <= 256 ? size * 4 : size;
            using (Bitmap master = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(master))
                {
                    Prepare(graphics);
                    graphics.Clear(Color.Transparent);
                    graphics.ScaleTransform(renderSize / appInkBounds.Width, renderSize / appInkBounds.Height);
                    graphics.TranslateTransform(-appInkBounds.X, -appInkBounds.Y);
                    using (GraphicsPath tile = RoundedRectangle(1, 1, 22, 22, 5.2f))
                    using (LinearGradientBrush blue = new LinearGradientBrush(new PointF(3, 1), new PointF(21, 24), Color.FromArgb(40, 145, 255), Color.FromArgb(0, 66, 167)))
                    {
                        graphics.FillPath(blue, tile);
                        using (Pen rim = new Pen(Color.FromArgb(90, 155, 221, 255), .55f)) graphics.DrawPath(rim, tile);
                    }
                    using (GraphicsPath shine = new GraphicsPath())
                    {
                        shine.AddBezier(3.0f, 6.5f, 4.0f, 2.5f, 13.5f, .8f, 19.2f, 3.1f);
                        shine.AddBezier(19.2f, 3.1f, 13.2f, 3.6f, 6.8f, 7.7f, 3.0f, 14.5f);
                        shine.CloseFigure();
                        using (Brush glow = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) graphics.FillPath(glow, shine);
                    }
                    // Balanced capture brackets remain recognizable at 16 px.
                    using (Pen shadow = new Pen(Color.FromArgb(28, 0, 30, 90), 2.4f))
                    {
                        shadow.StartCap = shadow.EndCap = LineCap.Round;
                        shadow.LineJoin = LineJoin.Round;
                        graphics.TranslateTransform(0, .35f);
                        DrawFrame(graphics, shadow);
                        graphics.TranslateTransform(0, -.35f);
                    }
                    using (Pen frame = new Pen(Color.FromArgb(247, 253, 255), 1.95f))
                    {
                        frame.StartCap = frame.EndCap = LineCap.Round;
                        frame.LineJoin = LineJoin.Round;
                        DrawFrame(graphics, frame);
                    }
                    // A deliberate, bright editing cut inside the capture frame.
                    using (GraphicsPath nib = new GraphicsPath())
                    {
                        nib.AddPolygon(new PointF[] { new PointF(8.1f, 15.9f), new PointF(9.0f, 12.8f), new PointF(14.0f, 7.8f), new PointF(16.2f, 10.0f), new PointF(11.2f, 15.0f) });
                        using (LinearGradientBrush cyan = new LinearGradientBrush(new PointF(10, 8), new PointF(14, 16), Color.FromArgb(182, 255, 255), Color.FromArgb(63, 223, 226))) graphics.FillPath(cyan, nib);
                    }
                    using (Pen seam = new Pen(Color.FromArgb(0, 103, 174), .65f)) graphics.DrawLine(seam, 13.25f, 8.55f, 15.45f, 10.75f);
                }
                return Resize(master, size, size);
            }
        }

        public static string GetAppSvg()
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"512\" height=\"512\" viewBox=\".725 .725 22.55 22.55\" preserveAspectRatio=\"xMidYMid meet\" fill=\"none\">\n" +
                "<defs><linearGradient id=\"blue\" x1=\"3\" y1=\"1\" x2=\"21\" y2=\"24\" gradientUnits=\"userSpaceOnUse\"><stop stop-color=\"#2891FF\"/><stop offset=\"1\" stop-color=\"#0042A7\"/></linearGradient><linearGradient id=\"cyan\" x1=\"10\" y1=\"8\" x2=\"14\" y2=\"16\" gradientUnits=\"userSpaceOnUse\"><stop stop-color=\"#B6FFFF\"/><stop offset=\"1\" stop-color=\"#3FDFE2\"/></linearGradient></defs>\n" +
                "<rect x=\"1\" y=\"1\" width=\"22\" height=\"22\" rx=\"5.2\" fill=\"url(#blue)\" stroke=\"#9BDDFF\" stroke-opacity=\".35\" stroke-width=\".55\"/>\n" +
                "<path d=\"M3 6.5C4 2.5 13.5 .8 19.2 3.1C13.2 3.6 6.8 7.7 3 14.5Z\" fill=\"white\" opacity=\".08\"/>\n" +
                "<path d=\"M10 6.6H6.6V10M14 6.6H17.4V10M6.6 14V17.4H10M14 17.4H17.4V14\" stroke=\"#F7FDFF\" stroke-width=\"1.95\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n" +
                "<path d=\"M8.1 15.9L9 12.8L14 7.8L16.2 10L11.2 15Z\" fill=\"url(#cyan)\"/>\n" +
                "<path d=\"M13.25 8.55L15.45 10.75\" stroke=\"#0067AE\" stroke-width=\".65\"/>\n</svg>\n";
        }

        private static void DrawFrame(Graphics g, Pen pen)
        {
            g.DrawLines(pen, new PointF[] { new PointF(10, 6.6f), new PointF(6.6f, 6.6f), new PointF(6.6f, 10) });
            g.DrawLines(pen, new PointF[] { new PointF(14, 6.6f), new PointF(17.4f, 6.6f), new PointF(17.4f, 10) });
            g.DrawLines(pen, new PointF[] { new PointF(6.6f, 14), new PointF(6.6f, 17.4f), new PointF(10, 17.4f) });
            g.DrawLines(pen, new PointF[] { new PointF(14, 17.4f), new PointF(17.4f, 17.4f), new PointF(17.4f, 14) });
        }

        private static void DrawTool(string key, VectorPainter p)
        {
            string normalized = NormalizeKey(key);
            switch (normalized)
            {
                case "more": p.Dot(5, 12, 1.1f); p.Dot(12, 12, 1.1f); p.Dot(19, 12, 1.1f); break;
                case "camera": p.RoundRect(3, 7, 18, 13, 2); p.Path("M7 7 L9 4 L15 4 L17 7"); p.Circle(12, 13.5f, 3.3f); break;
                case "clock": p.Circle(12, 12, 8); p.Path("M12 7 L12 12 L16 14"); break;
                case "video": p.RoundRect(3, 6, 12, 12, 2); p.Path("M15 10 L21 7 L21 17 L15 14 Z"); break;
                case "new":
                    p.RoundRect(3.5f, 4.5f, 17, 15, 2.3f);
                    p.Path("M12 8.5 L12 15.5 M8.5 12 L15.5 12");
                    break;
                case "rectangle":
                    p.Path("M8 4 L4 4 L4 8 M16 4 L20 4 L20 8 M4 16 L4 20 L8 20 M16 20 L20 20 L20 16");
                    p.Path("M11 4 L13 4 M11 20 L13 20 M4 11 L4 13 M20 11 L20 13");
                    break;
                case "freeform":
                    p.Path("M7.2 4.6 C9.7 2.1 12.5 4.7 14.4 5.1 C16.8 5.6 20.4 4.4 20.2 8.1 C20.1 11.3 17.1 11.2 17.8 14.2 C18.4 17.1 17.8 20.4 14.1 20.1 C11.5 19.8 11.5 17.8 8.8 18.5 C5.8 19.3 3 17.7 3.8 14.7 C4.4 12.5 6.5 12 5.1 9.6 C3.9 7.6 5.1 5.4 7.2 4.6 Z");
                    break;
                case "window":
                    p.RoundRect(3.5f, 4.5f, 17, 15, 2.2f);
                    p.Path("M3.8 9 L20.2 9");
                    p.Dot(6.7f, 6.8f, .45f);
                    p.Dot(9, 6.8f, .45f);
                    break;
                case "fullscreen":
                    p.Path("M8 3.5 L3.5 3.5 L3.5 8 M16 3.5 L20.5 3.5 L20.5 8 M3.5 16 L3.5 20.5 L8 20.5 M16 20.5 L20.5 20.5 L20.5 16");
                    p.RoundRect(7.2f, 7.2f, 9.6f, 9.6f, 1.3f);
                    break;
                case "scrolling":
                    p.Path("M8 3.5 L4.5 3.5 L4.5 8 M16 3.5 L19.5 3.5 L19.5 8 M4.5 16 L4.5 20.5 L8 20.5 M16 20.5 L19.5 20.5 L19.5 16");
                    p.Path("M12 6 L12 18 M8.5 14.5 L12 18 L15.5 14.5");
                    break;
                case "open":
                    p.Path("M3.5 10.5 L3.5 6.2 C3.5 5.3 4.1 4.8 5 4.8 L9 4.8 L11.3 7 L18.5 7 C19.5 7 20 7.6 20 8.5 L20 10.5");
                    p.Path("M3.5 10.5 L20.5 10.5 L18.5 19 L5.5 19 Z");
                    break;
                case "paste":
                    p.Path("M8 5.5 L6 5.5 C4.9 5.5 4.5 6.2 4.5 7.3 L4.5 19 C4.5 20 5.1 20.5 6.1 20.5 L17.9 20.5 C18.9 20.5 19.5 20 19.5 19 L19.5 7.3 C19.5 6.2 19.1 5.5 18 5.5 L16 5.5");
                    p.RoundRect(8, 3.5f, 8, 4, 1.3f);
                    p.Path("M8 12 L16 12 M8 16 L14 16");
                    break;
                case "copy":
                    p.Path("M7.8 16.3 L5.5 16.3 C4.2 16.3 3.5 15.6 3.5 14.3 L3.5 5.5 C3.5 4.2 4.2 3.5 5.5 3.5 L14.3 3.5 C15.6 3.5 16.3 4.2 16.3 5.5 L16.3 7.8");
                    p.RoundRect(7.8f, 7.8f, 12.7f, 12.7f, 2.1f);
                    break;
                case "share":
                    p.Path("M20 13L20 19C20 20.1 19.1 21 18 21L5 21C3.9 21 3 20.1 3 19L3 8C3 6.9 3.9 6 5 6L9 6 M13 3L21 3L21 11 M21 3L10 14"); break;
                case "save":
                    p.Path("M5 3.5 L17 3.5 L20.5 7 L20.5 18.8 C20.5 19.9 19.9 20.5 18.8 20.5 L5.2 20.5 C4.1 20.5 3.5 19.9 3.5 18.8 L3.5 5.2 C3.5 4.1 4.1 3.5 5 3.5 Z");
                    p.Path("M7.5 3.8 L7.5 9 L16.5 9 L16.5 3.8 M7.5 20.1 L7.5 13.5 L16.5 13.5 L16.5 20.1");
                    break;
                case "help":
                    p.Circle(12, 12, 8.5f);
                    p.Path("M9.4 9 C9.6 7.6 10.7 6.8 12.2 6.8 C14 6.8 15.1 7.8 15.1 9.2 C15.1 11.3 12 11.5 12 13.5");
                    p.Dot(12, 17, .85f);
                    break;
                case "pan":
                    p.Path("M8 11 L8 5.5 C8 3.7 10.8 3.7 10.8 5.5 L10.8 10 M10.8 5.5 L10.8 4.5 C10.8 2.7 13.6 2.7 13.6 4.5 L13.6 10 M13.6 5.5 C13.6 3.7 16.4 3.7 16.4 5.5 L16.4 11 M16.4 8 C16.4 6.2 19.2 6.2 19.2 8 L19.2 13.3 C19.2 18.5 16.7 21 12.7 21 C9.8 21 8.6 19.3 7.2 17.3 L4.6 13.6 C3.5 12 5.4 10.3 6.7 11.6 L8 13 Z");
                    break;
                case "move":
                    p.Path("M12 3 L12 21 M3 12 L21 12 M9 6 L12 3 L15 6 M9 18 L12 21 L15 18 M6 9 L3 12 L6 15 M18 9 L21 12 L18 15");
                    break;
                case "pen":
                    p.Path("M4 20 L5.4 15.5 L16.8 4.1 C17.3 3.6 17.9 3.6 18.4 4.1 L19.9 5.6 C20.4 6.1 20.4 6.7 19.9 7.2 L8.5 18.6 Z");
                    p.Path("M15.3 5.6 L18.4 8.7 M5.4 15.5 L8.5 18.6");
                    break;
                case "highlighter":
                    p.Path("M7.5 3.5 L16.5 3.5 L16.5 13 L13.5 16 L10.5 16 L7.5 13 Z");
                    p.Path("M7.5 7 L16.5 7 M10.5 16 L9 19 L13.5 19 L13.5 16 M4 21 L20 21");
                    break;
                case "arrow":
                    p.Path("M4.5 19.5 L19 5 M10.5 5 L19 5 L19 13.5");
                    break;
                case "box":
                    p.RoundRect(4, 5.5f, 16, 13, 1.4f);
                    break;
                case "shapes":
                    p.RoundRect(3.5f, 3.5f, 11, 11, 1.2f);
                    p.Circle(15.2f, 15.2f, 5.6f);
                    break;
                case "ellipse":
                    p.Circle(12, 12, 7.8f);
                    break;
                case "line":
                    p.Path("M4.5 19.5 L19.5 4.5");
                    break;
                case "emoji":
                    p.Circle(12, 12, 8.5f);
                    p.Dot(9, 9.5f, .75f); p.Dot(15, 9.5f, .75f);
                    p.Path("M7.8 13.2 C8.5 18.1 15.5 18.1 16.2 13.2");
                    break;
                case "text":
                    p.Path("M4.5 7 L4.5 4.5 L19.5 4.5 L19.5 7 M12 4.5 L12 20 M8.5 20 L15.5 20");
                    break;
                case "text-scan":
                    p.Path("M7 3.5 L3.5 3.5 L3.5 7 M17 3.5 L20.5 3.5 L20.5 7 M3.5 17 L3.5 20.5 L7 20.5 M17 20.5 L20.5 20.5 L20.5 17");
                    p.Path("M7.5 8 L16.5 8 M7.5 12 L16.5 12 M7.5 16 L13.5 16");
                    break;
                case "print":
                    p.Path("M7 8 L7 3.5 L17 3.5 L17 8 M7 17 L5.5 17 C4.2 17 3.5 16.3 3.5 15 L3.5 10 C3.5 8.7 4.2 8 5.5 8 L18.5 8 C19.8 8 20.5 8.7 20.5 10 L20.5 15 C20.5 16.3 19.8 17 18.5 17 L17 17");
                    p.Path("M7 13.5 L17 13.5 L17 20.5 L7 20.5 Z M9.5 17 L14.5 17");
                    p.Dot(17.4f, 10.8f, .65f);
                    break;
                case "record":
                    p.Circle(12, 12, 8.2f);
                    p.Dot(12, 12, 4.3f);
                    break;
                case "stop":
                    p.RoundRect(5, 5, 14, 14, 1.8f);
                    break;
                case "microphone":
                    p.RoundRect(9, 3, 6, 11, 3);
                    p.Path("M6.2 10.7 L6.2 12 C6.2 15.6 8.7 17.5 12 17.5 C15.3 17.5 17.8 15.6 17.8 12 L17.8 10.7 M12 17.5 L12 21 M8.5 21 L15.5 21");
                    break;
                case "volume":
                    p.Path("M3.5 9 L7.5 9 L12 5 L12 19 L7.5 15 L3.5 15 Z M15.5 8.5 C17.5 10.4 17.5 13.6 15.5 15.5 M18 5.5 C21.8 9.1 21.8 14.9 18 18.5");
                    break;
                case "settings":
                    p.Path("M10.561 2.913 L13.439 2.913 L13.620 4.985 L15.815 5.894 L17.408 4.557 L19.443 6.592 L18.106 8.185 L19.015 10.380 L21.087 10.561 L21.087 13.439 L19.015 13.620 L18.106 15.815 L19.443 17.408 L17.408 19.443 L15.815 18.106 L13.620 19.015 L13.439 21.087 L10.561 21.087 L10.380 19.015 L8.185 18.106 L6.592 19.443 L4.557 17.408 L5.894 15.815 L4.985 13.620 L2.913 13.439 L2.913 10.561 L4.985 10.380 L5.894 8.185 L4.557 6.592 L6.592 4.557 L8.185 5.894 L10.380 4.985 Z");
                    p.Circle(12, 12, 3.1f);
                    break;
                case "remove-area":
                    p.Path("M8 4 L4 4 L4 8 M16 4 L20 4 L20 8 M4 16 L4 20 L8 20 M16 20 L20 20 L20 16");
                    p.Path("M11 4 L13 4 M11 20 L13 20 M4 11 L4 13 M20 11 L20 13 M8 12 L16 12");
                    break;
                case "eraser":
                    p.Path("M3.6 13.2 L12.8 4 C13.6 3.2 14.5 3.2 15.3 4 L20 8.7 C20.8 9.5 20.8 10.4 20 11.2 L11.7 19.5 L7.2 19.5 L3.6 15.9 C2.8 15.1 2.8 14 3.6 13.2 Z");
                    p.Path("M8.2 8.6 L15.4 15.8 M11.7 19.5 L21 19.5");
                    break;
                case "crop":
                    p.Path("M7 3 L7 15.5 C7 16.5 7.5 17 8.5 17 L21 17 M3 7 L15.5 7 C16.5 7 17 7.5 17 8.5 L17 21");
                    p.Path("M17 3 L17 4 M3 17 L4 17");
                    break;
                case "undo":
                    p.Path("M8.5 5 L4 9.5 L8.5 14 M4.4 9.5 L14.3 9.5 C18.3 9.5 20.5 11.4 20.5 14.5 C20.5 17.6 18.4 19.5 14.7 19.5 L12.5 19.5");
                    break;
                case "redo":
                    p.Path("M15.5 5 L20 9.5 L15.5 14 M19.6 9.5 L9.7 9.5 C5.7 9.5 3.5 11.4 3.5 14.5 C3.5 17.6 5.6 19.5 9.3 19.5 L11.5 19.5");
                    break;
                case "color":
                    p.Path("M20.5 11.7 C20.5 6.6 16.7 3.5 12.1 3.5 C7.2 3.5 3.5 7.2 3.5 12 C3.5 17.1 7.4 20.5 11.8 20.5 C13.5 20.5 14.1 19.4 13.5 18.1 C12.8 16.5 13.9 15.2 15.5 15.2 L17.5 15.2 C19.4 15.2 20.5 14 20.5 11.7 Z");
                    p.Dot(7.3f, 11.2f, .95f); p.Dot(10, 7.4f, .95f); p.Dot(14.6f, 7.4f, .95f); p.Dot(17.3f, 10.8f, .95f);
                    break;
                case "zoom-in":
                    p.Circle(10.4f, 10.4f, 6.9f);
                    p.Path("M15.5 15.5 L20.5 20.5 M10.4 7 L10.4 13.8 M7 10.4 L13.8 10.4");
                    break;
                case "zoom-out":
                    p.Circle(10.4f, 10.4f, 6.9f);
                    p.Path("M15.5 15.5 L20.5 20.5 M7 10.4 L13.8 10.4");
                    break;
                case "fit":
                    p.Path("M8 3.5 L3.5 3.5 L3.5 8 M16 3.5 L20.5 3.5 L20.5 8 M3.5 16 L3.5 20.5 L8 20.5 M16 20.5 L20.5 20.5 L20.5 16");
                    p.Path("M7 7 L10 10 M14 10 L17 7 M7 17 L10 14 M14 14 L17 17");
                    break;
                default:
                    throw new ArgumentException("Unknown icon key: " + key, "key");
            }
        }

        private static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        private static string NormalizeKey(string key)
        {
            string normalized = (key ?? "").ToLowerInvariant();
            if (normalized == "highlight") return "highlighter";
            if (normalized == "minus") return "zoom-out";
            if (normalized == "plus") return "zoom-in";
            return normalized;
        }

        private static RectangleF GetToolInkBounds(string key)
        {
            string normalized = NormalizeKey(key);
            lock (toolInkBounds)
            {
                RectangleF bounds;
                if (!toolInkBounds.TryGetValue(normalized, out bounds))
                {
                    using (VectorPainter painter = new VectorPainter(null, Color.Black))
                    {
                        DrawTool(normalized, painter);
                        bounds = painter.InkBounds;
                    }
                    toolInkBounds.Add(normalized, bounds);
                }
                return bounds;
            }
        }

        private static string Number(float value) { return value.ToString("0.######", CultureInfo.InvariantCulture); }

        private static Bitmap Resize(Bitmap source, int width, int height)
        {
            Bitmap output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(output))
            {
                Prepare(g);
                g.Clear(Color.Transparent);
                g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
            return output;
        }

        // One scan per rendered bitmap removes even the last subpixel empty edge.
        // Callers cache the completed images; bounds geometry is cached separately.
        private static Bitmap TrimTransparent(Bitmap source)
        {
            Rectangle whole = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData pixels = source.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int left = source.Width, top = source.Height, right = -1, bottom = -1;
            try
            {
                int[] row = new int[source.Width];
                for (int y = 0; y < source.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (((uint)row[x] >> 24) == 0) continue;
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                }
            }
            finally { source.UnlockBits(pixels); }
            if (right < left) return new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            return source.Clone(Rectangle.FromLTRB(left, top, right + 1, bottom + 1), PixelFormat.Format32bppArgb);
        }

        private static GraphicsPath RoundedRectangle(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class VectorPainter : IDisposable
        {
            private readonly Graphics graphics;
            private readonly Pen pen;
            private readonly Brush fill;
            private readonly StringBuilder svg = new StringBuilder();
            private RectangleF inkBounds;
            private bool hasInkBounds;
            internal string Svg { get { return svg.ToString(); } }
            internal RectangleF InkBounds { get { return inkBounds; } }

            internal VectorPainter(Graphics graphics, Color color)
            {
                this.graphics = graphics;
                pen = new Pen(color, 1.8f);
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                fill = new SolidBrush(color);
            }

            internal void Path(string d)
            {
                svg.Append("  <path d=\"").Append(d).Append("\"/>\n");
                using (GraphicsPath path = ParsePath(d))
                {
                    if (graphics != null) graphics.DrawPath(pen, path);
                    else IncludePath(path, true);
                }
            }

            internal void RoundRect(float x, float y, float w, float h, float r)
            {
                svg.Append("  <rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h)).Append("\" rx=\"").Append(N(r)).Append("\"/>\n");
                using (GraphicsPath path = RoundedRectangle(x, y, w, h, r))
                {
                    if (graphics != null) graphics.DrawPath(pen, path);
                    else IncludePath(path, true);
                }
            }

            internal void Circle(float x, float y, float radius)
            {
                svg.Append("  <circle cx=\"").Append(N(x)).Append("\" cy=\"").Append(N(y)).Append("\" r=\"").Append(N(radius)).Append("\"/>\n");
                if (graphics != null) graphics.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
                else using (GraphicsPath path = new GraphicsPath())
                { path.AddEllipse(x - radius, y - radius, radius * 2, radius * 2); IncludePath(path, true); }
            }

            internal void Dot(float x, float y, float radius)
            {
                svg.Append("  <circle cx=\"").Append(N(x)).Append("\" cy=\"").Append(N(y)).Append("\" r=\"").Append(N(radius)).Append("\" fill=\"currentColor\" stroke=\"none\"/>\n");
                if (graphics != null) graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
                else IncludeBounds(new RectangleF(x - radius, y - radius, radius * 2, radius * 2));
            }

            private void IncludePath(GraphicsPath path, bool stroked)
            {
                using (GraphicsPath outline = (GraphicsPath)path.Clone())
                {
                    if (stroked) outline.Widen(pen, null, .001f);
                    IncludeBounds(outline.GetBounds());
                }
            }

            private void IncludeBounds(RectangleF bounds)
            {
                inkBounds = hasInkBounds ? RectangleF.Union(inkBounds, bounds) : bounds;
                hasInkBounds = true;
            }

            private static string N(float value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }

            private static GraphicsPath ParsePath(string data)
            {
                MatchCollection tokens = Regex.Matches(data, "[MLCZ]|-?(?:[0-9]+(?:\\.[0-9]+)?|\\.[0-9]+)");
                GraphicsPath path = new GraphicsPath();
                PointF point = PointF.Empty;
                char command = 'M';
                int i = 0;
                while (i < tokens.Count)
                {
                    string token = tokens[i].Value;
                    if (token.Length == 1 && Char.IsLetter(token[0]))
                    {
                        command = token[0]; i++;
                        if (command == 'Z') { path.CloseFigure(); continue; }
                    }
                    if (command == 'M')
                    {
                        point = new PointF(Read(tokens, ref i), Read(tokens, ref i));
                        path.StartFigure(); command = 'L';
                    }
                    else if (command == 'L')
                    {
                        PointF next = new PointF(Read(tokens, ref i), Read(tokens, ref i));
                        path.AddLine(point, next); point = next;
                    }
                    else if (command == 'C')
                    {
                        PointF control1 = new PointF(Read(tokens, ref i), Read(tokens, ref i));
                        PointF control2 = new PointF(Read(tokens, ref i), Read(tokens, ref i));
                        PointF next = new PointF(Read(tokens, ref i), Read(tokens, ref i));
                        path.AddBezier(point, control1, control2, next); point = next;
                    }
                    else throw new InvalidOperationException("Unsupported vector path command.");
                }
                return path;
            }

            private static float Read(MatchCollection tokens, ref int index)
            {
                return Single.Parse(tokens[index++].Value, CultureInfo.InvariantCulture);
            }

            public void Dispose() { pen.Dispose(); fill.Dispose(); }
        }
    }
}
