using System;
using System.Drawing;
using System.Runtime.InteropServices;
using SnipStudio;

internal static class HdrChecks
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    private static int count;
    private static void Assert(bool pass, string message) { if (!pass) throw new Exception(message); count++; }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Assert(HdrCaptureService.HalfToSingle(0x3c00) == 1, "half1");
            Assert(HdrCaptureService.HalfToSingle(0xc000) == -2, "half negative");
            Assert(HdrCaptureService.HalfToSingle(0x3800) == .5f, "half fraction");
            Assert(HdrCaptureService.ToneMapScRgb(0, 0, 0, 80).ToArgb() == Color.Black.ToArgb(), "black");
            Assert(HdrCaptureService.ToneMapScRgb(1, 1, 1, 80).R == 255, "SDR white");
            Assert(HdrCaptureService.ToneMapScRgb(.21586f, .21586f, .21586f, 80).R == 128, "linear to sRGB midpoint");
            Assert(HdrCaptureService.ToneMapScRgb(3, 3, 3, 240).R == 255, "HDR SDR-white scaling");
            Assert(HdrCaptureService.ToneMapScRgb(float.NaN, -2, 0, 80).R == 0, "invalid channels safe");
            Console.WriteLine("PASS: " + count + " HDR math checks");
            if (args.Length > 0 && args[0] == "--live")
            {
                try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
                using (Bitmap image = HdrCaptureService.CaptureDesktop(new Rectangle(0, 0, 800, 600)))
                    Console.WriteLine("LIVE float capture: " + image.Width + "x" + image.Height + "; center=" + image.GetPixel(400, 300));
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
