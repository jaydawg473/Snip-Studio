using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace SnipStudio.WinUI;

internal sealed class DesktopWindowSizing
{
    private readonly Window window;
    private readonly FrameworkElement content;
    private readonly int minimumWidth;
    private readonly int minimumHeight;
    private XamlRoot? root;

    internal DesktopWindowSizing(Window window, WindowId placementOwner, int suggestedWidth, int suggestedHeight,
        int minimumWidth, int minimumHeight)
    {
        this.window = window;
        content = (FrameworkElement)window.Content;
        this.minimumWidth = minimumWidth;
        this.minimumHeight = minimumHeight;
        WindowId placement = placementOwner.Value == 0 ? window.AppWindow.Id : placementOwner;
        DisplayArea? display = DisplayArea.GetFromWindowId(placement, DisplayAreaFallback.Nearest);
        if (display is not null && display.WorkArea is { Width: > 0, Height: > 0 } work)
        {
            double scale = WindowScale(placement);
            int width = Math.Min(work.Width, Scale(suggestedWidth, scale));
            int height = Math.Min(work.Height, Scale(suggestedHeight, scale));
            // DisplayArea.WorkArea may be offset from this display's origin.
            RectInt32 bounds = display.OuterBounds;
            window.AppWindow.MoveAndResize(new RectInt32(
                bounds.X + work.X + (work.Width - width) / 2,
                bounds.Y + work.Y + (work.Height - height) / 2, width, height));
        }
        UpdateMinimums();
        content.Loaded += ContentLoaded;
        window.AppWindow.Changed += WindowChanged;
        window.Closed += WindowClosed;
    }

    private void ContentLoaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        root = content.XamlRoot;
        if (root is not null) root.Changed += RootChanged;
        UpdateMinimums();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateMinimums();

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidPresenterChange) UpdateMinimums();
    }

    private void UpdateMinimums()
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        double scale = WindowScale(window.AppWindow.Id);
        int width = Scale(minimumWidth, scale), height = Scale(minimumHeight, scale);
        if (DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea is { Width: > 0, Height: > 0 } work)
        {
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
        }
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        content.Loaded -= ContentLoaded;
        window.AppWindow.Changed -= WindowChanged;
        window.Closed -= WindowClosed;
    }

    private static int Scale(int size, double scale) => (int)Math.Ceiling(size * scale);

    private static double WindowScale(WindowId window) =>
        Math.Max(96u, GetDpiForWindow(Win32Interop.GetWindowFromWindowId(window))) / 96d;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
