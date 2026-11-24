using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private (uint Pen, uint Marker, bool Dark)? renderedInkIcons;
    private bool updatingInkIcons;

    private async void RefreshInkToolIcons()
    {
        if (closed || updatingInkIcons || PenButton is null || HighlighterButton is null) return;
        updatingInkIcons = true;
        try
        {
            while (!closed)
            {
                var current = (Pen: penColor & 0xFFFFFFu, Marker: highlighterColor & 0xFFFFFFu,
                    Dark: Root.ActualTheme == ElementTheme.Dark);
                if (renderedInkIcons == current) break;
                renderedInkIcons = current;
                var pen = await MakeInkIconAsync(current.Pen, marker: false, current.Dark);
                var marker = await MakeInkIconAsync(current.Marker, marker: true, current.Dark);
                if (closed) break;
                if (pen is not null) PenButton.Icon = new ImageIcon { Source = pen };
                if (marker is not null) HighlighterButton.Icon = new ImageIcon { Source = marker };
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine("Stroke icon could not be rendered: " + error.Message);
        }
        finally { updatingInkIcons = false; }
    }

    private static async Task<SvgImageSource?> MakeInkIconAsync(uint rgb, bool marker, bool dark)
    {
        string ink = $"#{rgb:X6}";
        string rim = dark ? "#F1F5FC" : "#17253A";
        string body = dark ? "#273344" : "#F8FAFC";
        string outline = marker ? "M4 2H16V10L14.5 12V15.5L5.5 18V12L4 10Z" : "M3.4 2H16.6L14.4 10.7L10 18L5.6 10.7Z";
        string band = marker ? "M4.9 3H15.1V8.5H4.9Z" : "M4.55 3H15.45L14.05 8.5H5.95Z";
        string tip = marker ? "M6.4 13L13.6 11.1V14.8L6.4 16.85Z" : "M7.6 13.1H12.4L10 17Z";
        string artwork = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 20 20">
              <path d="{outline}" fill="{body}" stroke="{rim}" stroke-width="1.5" stroke-linejoin="round"/>
              <path d="{band} {tip}" fill="{ink}"/>
            </svg>
            """;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteString(artwork);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var source = new SvgImageSource { RasterizePixelWidth = 48, RasterizePixelHeight = 48 };
        return await source.SetSourceAsync(stream) == SvgImageSourceLoadStatus.Success ? source : null;
    }
}
