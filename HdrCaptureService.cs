using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace SnipStudio
{
    // HDR capture reads compositor scRGB floating-point pixels. It never applies
    // an after-the-fact gamma filter to an already-clipped GDI screenshot.
    public static class HdrCaptureService
    {
        public static Bitmap CaptureDesktop(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException("bounds");
            return Task.Run(delegate { return CaptureCore(bounds); }).GetAwaiter().GetResult();
        }

        private static Bitmap CaptureCore(Rectangle bounds)
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("Windows Graphics Capture is unavailable on this computer. HDR color correction needs Windows 10 version 1903 or later and a compatible graphics driver.");
            List<MonitorCapture> monitors = EnumerateMonitors(bounds);
            if (monitors.Count == 0) throw new InvalidOperationException("No display intersects the requested capture.");
            using (D3dDevice device = new D3dDevice())
            {
                Bitmap result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                try
                {
                    using (Graphics graphics = Graphics.FromImage(result))
                    {
                        graphics.Clear(Color.Black);
                        foreach (MonitorCapture monitor in monitors)
                        {
                            using (Bitmap screen = CaptureMonitor(device, monitor))
                            {
                                Rectangle area = Rectangle.Intersect(bounds, monitor.Bounds);
                                Rectangle source = new Rectangle(area.X - monitor.Bounds.X, area.Y - monitor.Bounds.Y, area.Width, area.Height);
                                Rectangle destination = new Rectangle(area.X - bounds.X, area.Y - bounds.Y, area.Width, area.Height);
                                graphics.DrawImage(screen, destination, source, GraphicsUnit.Pixel);
                            }
                        }
                    }
                    return result;
                }
                catch { result.Dispose(); throw; }
            }
        }

        private static Bitmap CaptureMonitor(D3dDevice device, MonitorCapture monitor)
        {
            GraphicsCaptureItem item = CreateItem(monitor.Handle);
            if (item.Size.Width <= 0 || item.Size.Height <= 0)
                throw new InvalidOperationException("The display returned an empty capture surface.");
            using (Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.RuntimeDevice,
                DirectXPixelFormat.R16G16B16A16Float, 2, item.Size))
            using (GraphicsCaptureSession session = pool.CreateCaptureSession(item))
            {
                try { session.IsCursorCaptureEnabled = false; }
                catch (MissingMethodException) { }
                catch (NotImplementedException) { }
                session.StartCapture();
                Stopwatch timer = Stopwatch.StartNew();
                Direct3D11CaptureFrame frame = null;
                while (frame == null && timer.ElapsedMilliseconds < 4000)
                {
                    frame = pool.TryGetNextFrame();
                    if (frame == null) Thread.Sleep(12);
                }
                if (frame == null)
                    throw new TimeoutException("The HDR capture did not receive a display frame. The screen may be locked, protected, or unavailable.");
                using (frame)
                {
                    if (frame.ContentSize.Width != monitor.Bounds.Width || frame.ContentSize.Height != monitor.Bounds.Height)
                        throw new InvalidOperationException("The display size changed while capturing. Please try the capture again.");
                    return device.ReadSurface(frame.Surface, monitor.Bounds.Size, monitor.SdrWhiteNits);
                }
            }
        }

        private static GraphicsCaptureItem CreateItem(IntPtr monitor)
        {
            object factory = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            IGraphicsCaptureItemInterop interop = (IGraphicsCaptureItemInterop)factory;
            Guid iid = typeof(GraphicsCaptureItem).GUID;
            IntPtr item;
            int hr = interop.CreateForMonitor(monitor, ref iid, out item);
            Marshal.ThrowExceptionForHR(hr);
            try { return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(item); }
            finally { Marshal.Release(item); }
        }

        private static List<MonitorCapture> EnumerateMonitors(Rectangle bounds)
        {
            List<MonitorCapture> monitors = new List<MonitorCapture>();
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data)
            {
                Rectangle monitorBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (monitorBounds.IntersectsWith(bounds))
                    monitors.Add(new MonitorCapture { Handle = monitor, Bounds = monitorBounds, SdrWhiteNits = 80 });
                return true;
            }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Display enumeration failed.");
            return monitors;
        }

        // Public for deterministic testing. scRGB is linear light with 1.0 = 80
        // nits. Values above the display's SDR white are compressed through a
        // luminance-preserving shoulder before conversion to sRGB.
        public static Color ToneMapScRgb(float red, float green, float blue, float sdrWhiteNits)
        {
            float scale = 80f / Math.Max(80f, sdrWhiteNits);
            double r = Clean(red) * scale, g = Clean(green) * scale, b = Clean(blue) * scale;
            double luminance = .2126 * r + .7152 * g + .0722 * b;
            if (luminance > 1)
            {
                double mapped = 1 + (luminance - 1) / (1 + (luminance - 1));
                double multiplier = mapped / luminance;
                r *= multiplier; g *= multiplier; b *= multiplier;
            }
            double peak = Math.Max(r, Math.Max(g, b));
            if (peak > 1) { r /= peak; g /= peak; b /= peak; }
            return Color.FromArgb(255, Encode(r), Encode(g), Encode(b));
        }

        private static double Clean(float value) { return Single.IsNaN(value) || value < 0 ? 0 : Single.IsInfinity(value) ? 65504 : value; }
        private static int Encode(double linear)
        {
            linear = Math.Max(0, Math.Min(1, linear));
            double encoded = linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
            return Math.Max(0, Math.Min(255, (int)Math.Round(encoded * 255)));
        }

        public static float HalfToSingle(ushort bits)
        {
            int exponent = (bits >> 10) & 31, mantissa = bits & 1023;
            double value;
            if (exponent == 0) value = mantissa * Math.Pow(2, -24);
            else if (exponent == 31) value = mantissa == 0 ? Double.PositiveInfinity : Double.NaN;
            else value = (1 + mantissa / 1024.0) * Math.Pow(2, exponent - 15);
            return (float)(((bits & 32768) != 0) ? -value : value);
        }

        private sealed class MonitorCapture
        {
            internal IntPtr Handle;
            internal Rectangle Bounds;
            internal float SdrWhiteNits;
        }

        private sealed class D3dDevice : IDisposable
        {
            private IntPtr device, context;
            internal IDirect3DDevice RuntimeDevice;

            internal D3dDevice()
            {
                int level;
                int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out level, out context);
                Marshal.ThrowExceptionForHR(hr);
                Guid dxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                IntPtr dxgiDevice = IntPtr.Zero, runtime = IntPtr.Zero;
                try
                {
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref dxgi, out dxgiDevice));
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out runtime));
                    RuntimeDevice = (IDirect3DDevice)Marshal.GetObjectForIUnknown(runtime);
                }
                catch { Dispose(); throw; }
                finally
                {
                    if (runtime != IntPtr.Zero) Marshal.Release(runtime);
                    if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
                }
            }

            internal Bitmap ReadSurface(IDirect3DSurface surface, Size size, float whiteNits)
            {
                IDirect3DDxgiInterfaceAccess access = (IDirect3DDxgiInterfaceAccess)surface;
                Guid textureIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                IntPtr texture, staging = IntPtr.Zero;
                Marshal.ThrowExceptionForHR(access.GetInterface(ref textureIid, out texture));
                try
                {
                    TextureDescription description;
                    Method<GetDescription>(texture, 10)(texture, out description);
                    if (description.Format != 10) throw new InvalidOperationException("The capture surface is not HDR floating-point RGBA.");
                    description.Usage = 3;
                    description.BindFlags = 0;
                    description.CpuAccessFlags = 0x20000;
                    description.MiscFlags = 0;
                    Marshal.ThrowExceptionForHR(Method<CreateTexture2D>(device, 5)(device, ref description, IntPtr.Zero, out staging));
                    Method<CopyResource>(context, 47)(context, staging, texture);
                    MappedResource mapped;
                    Marshal.ThrowExceptionForHR(Method<MapResource>(context, 14)(context, staging, 0, 1, 0, out mapped));
                    try { return ConvertPixels(mapped, size, whiteNits); }
                    finally { Method<UnmapResource>(context, 15)(context, staging, 0); }
                }
                finally
                {
                    if (staging != IntPtr.Zero) Marshal.Release(staging);
                    Marshal.Release(texture);
                }
            }

            private static Bitmap ConvertPixels(MappedResource mapped, Size size, float whiteNits)
            {
                Bitmap result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
                BitmapData output = null;
                try
                {
                    output = result.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    byte[] sourceRow = new byte[checked(size.Width * 8)];
                    byte[] outputRow = new byte[checked(size.Width * 4)];
                    for (int y = 0; y < size.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(mapped.Data, checked((int)mapped.RowPitch * y)), sourceRow, 0, sourceRow.Length);
                        for (int x = 0; x < size.Width; x++)
                        {
                            int offset = x * 8;
                            Color color = ToneMapScRgb(HalfToSingle(BitConverter.ToUInt16(sourceRow, offset)),
                                HalfToSingle(BitConverter.ToUInt16(sourceRow, offset + 2)), HalfToSingle(BitConverter.ToUInt16(sourceRow, offset + 4)), whiteNits);
                            int destination = x * 4;
                            outputRow[destination] = color.B;
                            outputRow[destination + 1] = color.G;
                            outputRow[destination + 2] = color.R;
                            outputRow[destination + 3] = 255;
                        }
                        Marshal.Copy(outputRow, 0, IntPtr.Add(output.Scan0, output.Stride * y), outputRow.Length);
                    }
                    result.UnlockBits(output);
                    output = null;
                    return result;
                }
                catch
                {
                    if (output != null) result.UnlockBits(output);
                    result.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (RuntimeDevice != null)
                {
                    IDisposable disposable = RuntimeDevice as IDisposable;
                    if (disposable != null) disposable.Dispose();
                    RuntimeDevice = null;
                }
                if (context != IntPtr.Zero) { Marshal.Release(context); context = IntPtr.Zero; }
                if (device != IntPtr.Zero) { Marshal.Release(device); device = IntPtr.Zero; }
            }
        }

        private static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T)) as T;
        }

        [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
            [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
        }
        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess { [PreserveSig] int GetInterface(ref Guid iid, out IntPtr result); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct SampleDescription { internal uint Count, Quality; }
        [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
        {
            internal uint Width, Height, MipLevels, ArraySize, Format;
            internal SampleDescription Sample;
            internal uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MappedResource { internal IntPtr Data; internal uint RowPitch, DepthPitch; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDescription(IntPtr self, out TextureDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2D(IntPtr self, ref TextureDescription description, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapResource(IntPtr self, IntPtr resource, uint subresource, uint type, uint flags, out MappedResource mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapResource(IntPtr self, IntPtr resource, uint subresource);
        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out int selectedLevel, out IntPtr context);
        [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }
}
