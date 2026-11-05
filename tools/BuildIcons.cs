using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using SnipStudio;

internal static class BuildIcons
{
    private static readonly Color Ink = Color.FromArgb(225, 237, 250);

    private static void Main(string[] args)
    {
        string assets = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("assets");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(Path.Combine(assets, "svg"));
        Directory.CreateDirectory(Path.Combine(assets, "png-32"));
        Directory.CreateDirectory(Path.Combine(assets, "png-64"));
        foreach (string key in IconArtwork.Keys)
        {
            File.WriteAllText(Path.Combine(assets, "svg", key + ".svg"), IconArtwork.GetToolSvg(key), new UTF8Encoding(false));
            using (Bitmap small = IconArtwork.CreateToolIcon(key, 32, Ink)) small.Save(Path.Combine(assets, "png-32", key + ".png"), ImageFormat.Png);
            using (Bitmap large = IconArtwork.CreateToolIcon(key, 64, Ink)) large.Save(Path.Combine(assets, "png-64", key + ".png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(assets, "SnipStudio.svg"), IconArtwork.GetAppSvg(), new UTF8Encoding(false));
        foreach (int size in new int[] { 256, 512 })
            using (Bitmap app = IconArtwork.CreateAppIconBitmap(size)) app.Save(Path.Combine(assets, "app-" + size + ".png"), ImageFormat.Png);
        WriteIcon(Path.Combine(assets, "SnipStudio.ico"));
        WritePreview(Path.Combine(assets, "Icon-set-preview.png"));
        WriteAppSizes(Path.Combine(assets, "App-icon-sizes.png"));
        File.WriteAllText(Path.Combine(assets, "README.txt"), "SNIP STUDIO ICON SET\r\n\r\n" + IconArtwork.Keys.Length + " original toolbar symbols, designed on a 24-unit grid with rounded 1.8-unit strokes.\r\nSVG files use currentColor so an app or stylesheet can choose the ink color.\r\nPNG files use pale blue-white ink for the dark Snip Studio workspace.\r\nSnipStudio.ico contains 16/24/32/48/64/128/256-pixel PNG-compressed frames.\r\nThe app mark is also supplied as vector SVG and transparent 256/512-pixel PNG.\r\n\r\nAll live toolbar images and exported symbols share IconArtwork.cs geometry.\r\nRun tools\\BuildIcons.exe assets to regenerate these files after compiling\r\ntools\\BuildIcons.cs together with IconArtwork.cs and System.Drawing.\r\n", new UTF8Encoding(false));
        Validate(assets);
        Console.WriteLine("Generated and validated " + IconArtwork.Keys.Length + " toolbar icons, app artwork, multi-resolution ICO, and previews in " + assets);
    }

    private static void WriteIcon(string path)
    {
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        foreach (int size in sizes)
            using (Bitmap bitmap = IconArtwork.CreateAppIconBitmap(size))
            using (MemoryStream png = new MemoryStream())
            {
                bitmap.Save(png, ImageFormat.Png);
                frames.Add(png.ToArray());
            }
        using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }

    private static void WritePreview(string path)
    {
        using (Bitmap preview = new Bitmap(1440, 1370, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(preview))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(15, 21, 32));
            using (LinearGradientBrush gradient = new LinearGradientBrush(new Point(0, 0), new Point(1440, 280), Color.FromArgb(25, 42, 65), Color.FromArgb(15, 21, 32))) g.FillRectangle(gradient, 0, 0, 1440, 280);
            using (Font eyebrow = new Font("Segoe UI", 11, FontStyle.Bold))
            using (Font title = new Font("Segoe UI", 40, FontStyle.Bold))
            using (Font subtitle = new Font("Segoe UI", 16))
            using (Brush pale = new SolidBrush(Ink))
            using (Brush muted = new SolidBrush(Color.FromArgb(149, 169, 193)))
            using (Brush cyan = new SolidBrush(Color.FromArgb(86, 214, 218)))
            {
                g.DrawString("THE SNIP STUDIO COLLECTION", eyebrow, cyan, 64, 48);
                g.DrawString("Every detail, considered.", title, pale, 59, 82);
                g.DrawString(IconArtwork.Keys.Length + " original symbols. One clear visual language.", subtitle, muted, 64, 160);
                using (Bitmap app = IconArtwork.CreateAppIconBitmap(176)) g.DrawImageUnscaled(app, 1180, 38);
                using (Pen rule = new Pen(Color.FromArgb(43, 58, 79))) g.DrawLine(rule, 64, 258, 1376, 258);
                DrawPanel(g, 64, "CAPTURE", "Start with the right frame.", new string[] { "new", "rectangle", "freeform", "window", "fullscreen", "crop" });
                DrawPanel(g, 512, "MARKUP", "Make the important part stand out.", new string[] { "pen", "highlighter", "arrow", "box", "text", "eraser", "color", "move", "pan" });
                DrawPanel(g, 960, "WORKSPACE", "A smoother path from image to action.", new string[] { "open", "paste", "copy", "save", "undo", "redo", "zoom-in", "zoom-out", "fit", "text-scan", "print", "help" });
                DrawRecordingPanel(g);
                g.DrawString("24 × 24 vector grid  /  rounded 1.8-unit stroke  /  scalable SVG + PNG  /  Windows app icon", eyebrow, muted, 64, 1315);
            }
            preview.Save(path, ImageFormat.Png);
        }
    }

    private static void DrawRecordingPanel(Graphics g)
    {
        using (GraphicsPath panel = Round(new Rectangle(64, 1040, 1312, 220), 22))
        using (Brush fill = new SolidBrush(Color.FromArgb(26, 35, 49)))
        using (Pen border = new Pen(Color.FromArgb(43, 58, 79)))
        {
            g.FillPath(fill, panel); g.DrawPath(border, panel);
        }
        using (Font heading = new Font("Segoe UI", 12, FontStyle.Bold))
        using (Font sub = new Font("Segoe UI", 10.5f))
        using (Font label = new Font("Segoe UI", 10.5f))
        using (Brush pale = new SolidBrush(Ink))
        using (Brush muted = new SolidBrush(Color.FromArgb(149, 169, 193)))
        using (Brush cyan = new SolidBrush(Color.FromArgb(86, 214, 218)))
        {
            g.DrawString("SETTINGS & RECORDING", heading, cyan, 92, 1077);
            g.DrawString("Your preferences. Your recording.", sub, muted, 92, 1110);
            string[] recordingKeys = new string[] { "settings", "record", "stop", "microphone", "volume" };
            for (int i = 0; i < recordingKeys.Length; i++)
            {
                int x = 475 + i * 175;
                using (GraphicsPath tile = Round(new Rectangle(x, 1081, 72, 72), 17))
                using (Brush fill = new SolidBrush(Color.FromArgb(33, 46, 64))) g.FillPath(fill, tile);
                using (Bitmap icon = IconArtwork.CreateToolIcon(recordingKeys[i], 36, Ink)) g.DrawImageUnscaled(icon, x + 18, 1099);
                string name = DisplayName(recordingKeys[i]);
                SizeF measure = g.MeasureString(name, label);
                g.DrawString(name, label, pale, x + 36 - measure.Width / 2, 1172);
            }
        }
    }

    private static void WriteAppSizes(string path)
    {
        using (Bitmap preview = new Bitmap(900, 260, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(preview))
        using (Font title = new Font("Segoe UI", 18, FontStyle.Bold))
        using (Font label = new Font("Segoe UI", 11))
        using (Brush white = new SolidBrush(Ink))
        using (Brush muted = new SolidBrush(Color.FromArgb(149, 169, 193)))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(15, 21, 32));
            g.DrawString("Made to stay clear at every size.", title, white, 28, 22);
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128 };
            int[] centers = new int[] { 75, 205, 335, 465, 605, 770 };
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap icon = IconArtwork.CreateAppIconBitmap(sizes[i])) g.DrawImageUnscaled(icon, centers[i] - sizes[i] / 2, 150 - sizes[i] / 2);
                string text = sizes[i] + " px";
                SizeF measurement = g.MeasureString(text, label);
                g.DrawString(text, label, muted, centers[i] - measurement.Width / 2, 226);
            }
            preview.Save(path, ImageFormat.Png);
        }
    }

    private static void Validate(string assets)
    {
        using (Icon icon = new Icon(Path.Combine(assets, "SnipStudio.ico"), new Size(32, 32)))
        using (Bitmap raster = icon.ToBitmap())
            if (raster.Width != 32 || raster.Height != 32) throw new InvalidOperationException("ICO frame could not be loaded.");
        foreach (string key in IconArtwork.Keys)
        {
            using (Bitmap icon = IconArtwork.CreateToolIcon(key, 24, Color.White))
            {
                bool any = false;
                for (int y = 0; y < icon.Height; y++)
                    for (int x = 0; x < icon.Width; x++)
                        if (icon.GetPixel(x, y).A > 100) any = true;
                if (!any) throw new InvalidOperationException("Icon rendered empty: " + key);
                if (icon.GetPixel(0, 0).A != 0) throw new InvalidOperationException("Icon background lost transparency: " + key);
            }
        }
    }

    private static void DrawPanel(Graphics g, int x, string title, string subtitle, string[] keys)
    {
        Rectangle panel = new Rectangle(x, 294, 416, 718);
        using (GraphicsPath rounded = Round(panel, 22))
        using (Brush fill = new SolidBrush(Color.FromArgb(26, 35, 49)))
        using (Pen border = new Pen(Color.FromArgb(43, 58, 79)))
        {
            g.FillPath(fill, rounded); g.DrawPath(border, rounded);
        }
        using (Font heading = new Font("Segoe UI", 12, FontStyle.Bold))
        using (Font sub = new Font("Segoe UI", 10.5f))
        using (Font label = new Font("Segoe UI", 10.5f))
        using (Brush pale = new SolidBrush(Ink))
        using (Brush muted = new SolidBrush(Color.FromArgb(149, 169, 193)))
        using (Brush cyan = new SolidBrush(Color.FromArgb(86, 214, 218)))
        {
            g.DrawString(title, heading, cyan, x + 28, 325);
            g.DrawString(subtitle, sub, muted, x + 28, 354);
            for (int i = 0; i < keys.Length; i++)
            {
                int cellX = x + 18 + (i % 3) * 128;
                int cellY = 418 + (i / 3) * 140;
                using (GraphicsPath tile = Round(new Rectangle(cellX + 27, cellY, 72, 72), 17))
                using (Brush tileFill = new SolidBrush(Color.FromArgb(33, 46, 64))) g.FillPath(tileFill, tile);
                using (Bitmap icon = IconArtwork.CreateToolIcon(keys[i], 36, Ink)) g.DrawImageUnscaled(icon, cellX + 45, cellY + 18);
                string name = DisplayName(keys[i]);
                SizeF measure = g.MeasureString(name, label);
                g.DrawString(name, label, pale, cellX + 63 - measure.Width / 2, cellY + 85);
            }
        }
    }

    private static string DisplayName(string key)
    {
        switch (key)
        {
            case "new": return "New capture";
            case "rectangle": return "Rectangle";
            case "freeform": return "Freeform";
            case "window": return "Window";
            case "fullscreen": return "All screens";
            case "crop": return "Crop";
            case "highlighter": return "Highlighter";
            case "box": return "Outline box";
            case "color": return "Ink color";
            case "zoom-in": return "Zoom in";
            case "zoom-out": return "Zoom out";
            case "fit": return "Fit to view";
            case "text-scan": return "Copy text";
            case "print": return "Print";
            case "record": return "Record";
            case "stop": return "Stop";
            case "microphone": return "Microphone";
            case "volume": return "System audio";
            case "settings": return "Settings";
            default: return Char.ToUpperInvariant(key[0]) + key.Substring(1);
        }
    }

    private static GraphicsPath Round(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
