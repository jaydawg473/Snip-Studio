using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using SnipStudio.WinUI.Backend;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using DrawingRectangle = System.Drawing.RectangleF;
using DrawingSize = System.Drawing.Size;

namespace SnipStudio.WinUI;

internal sealed class WindowsPrintSession
{
    private const float DefaultInset = 0.36f * 96f;
    private readonly Grid owner;
    private readonly Canvas host = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Canvas page = new() { Background = new SolidColorBrush(Colors.White) };
    private readonly Image image;
    private readonly DrawingSize pixels;
    private readonly PrintDocument document = new();
    private readonly IPrintDocumentSource source;
    private readonly TaskCompletionSource<PrintTaskCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object taskGate = new();
    private PrintTask? activeTask;
    private Exception? pageFailure;
    private bool ended;

    private WindowsPrintSession(Grid owner, BitmapImage bitmap)
    {
        this.owner = owner;
        pixels = new DrawingSize(bitmap.PixelWidth, bitmap.PixelHeight);
        image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        page.Children.Add(image);
        host.Children.Add(page);
        source = document.DocumentSource;
    }

    public static async Task<bool> PrintAsync(Grid owner, nint window, byte[] png, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PrintManager.IsSupported()) return DesktopPrintingService.PrintImage(png, window);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();

        PrintTaskCompletion result = await new WindowsPrintSession(owner, bitmap).ShowAsync(window, cancellationToken);
        if (result == PrintTaskCompletion.Failed)
            throw new InvalidOperationException("Windows could not submit the print job. Check the selected printer or choose another printer.");
        return result == PrintTaskCompletion.Submitted;
    }

    private async Task<PrintTaskCompletion> ShowAsync(nint window, CancellationToken cancellationToken)
    {
        PrintManager manager = PrintManagerInterop.GetForWindow(window);
        owner.Children.Add(host);
        bool registered = false;
        try
        {
            document.Paginate += OnPaginate;
            document.GetPreviewPage += OnGetPreviewPage;
            document.AddPages += OnAddPages;
            manager.PrintTaskRequested += OnPrintTaskRequested;
            registered = true;
            if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(window).AsTask(cancellationToken))
            {
                if (completion.Task.IsFaulted) return await completion.Task;
                throw new InvalidOperationException("Windows could not open Print. Check the Windows print service and try again.");
            }

            // Opening the panel and submitting a print job are different operations.
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            PrintTask? task;
            lock (taskGate)
            {
                ended = true;
                task = activeTask;
            }
            try
            {
                if (task is not null) task.Completed -= OnCompleted;
            }
            finally
            {
                try
                {
                    if (registered) manager.PrintTaskRequested -= OnPrintTaskRequested;
                }
                finally
                {
                    document.Paginate -= OnPaginate;
                    document.GetPreviewPage -= OnGetPreviewPage;
                    document.AddPages -= OnAddPages;
                    owner.Children.Remove(host);
                }
            }
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        try
        {
            lock (taskGate)
            {
                if (ended || activeTask is not null) return;
                activeTask = args.Request.CreatePrintTask("Snip Studio image", requested =>
                {
                    try { requested.SetSource(source); }
                    catch (Exception error) when (IsRecoverable(error)) { completion.TrySetException(error); }
                });
                activeTask.Completed += OnCompleted;
                activeTask.Options.Orientation = pixels.Width > pixels.Height ? PrintOrientation.Landscape : PrintOrientation.Portrait;
            }
        }
        catch (Exception error) when (IsRecoverable(error)) { completion.TrySetException(error); }
    }

    private void OnCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        if (args.Completion == PrintTaskCompletion.Failed && Volatile.Read(ref pageFailure) is Exception error)
            completion.TrySetException(error);
        else
            completion.TrySetResult(args.Completion);
    }

    private void OnPaginate(object sender, PaginateEventArgs args)
    {
        int count = 0;
        try
        {
            SetPageLayout((PrintTaskOptions)args.PrintTaskOptions);
            Volatile.Write(ref pageFailure, null);
            count = 1;
        }
        catch (Exception error) when (IsRecoverable(error)) { Volatile.Write(ref pageFailure, error); }

        // Windows may repaginate after another printer or paper size is selected.
        try { document.SetPreviewPageCount(count, PreviewPageCountType.Final); }
        catch (Exception error) when (IsRecoverable(error)) { completion.TrySetException(error); }
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs args)
    {
        if (args.PageNumber != 1 || Volatile.Read(ref pageFailure) is not null) return;
        try { document.SetPreviewPage(1, page); }
        catch (Exception error) when (IsRecoverable(error)) { completion.TrySetException(error); }
    }

    private void OnAddPages(object sender, AddPagesEventArgs args)
    {
        try
        {
            SetPageLayout((PrintTaskOptions)args.PrintTaskOptions);
            document.AddPage(page);
        }
        catch (Exception error) when (IsRecoverable(error)) { completion.TrySetException(error); }
        finally
        {
            try { document.AddPagesComplete(); }
            catch (Exception error) when (IsRecoverable(error)) { completion.TrySetException(error); }
        }
    }

    private void SetPageLayout(PrintTaskOptions options)
    {
        PrintPageDescription description = options.GetPageDescription(0);
        float width = (float)description.PageSize.Width;
        float height = (float)description.PageSize.Height;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("The selected printer did not report a valid page size. Choose a different paper size or printer.");
        var printable = description.ImageableRect;
        // Preserve the normal paper inset while leaving more room on small media.
        float inset = Math.Min(DefaultInset, Math.Min(width, height) / 4);
        DrawingRectangle destination = DesktopPrintingService.CalculateDestination(pixels,
            new DrawingRectangle(inset, inset, width - 2 * inset, height - 2 * inset),
            new DrawingRectangle((float)printable.X, (float)printable.Y, (float)printable.Width, (float)printable.Height));
        page.Width = width;
        page.Height = height;
        image.Width = destination.Width;
        image.Height = destination.Height;
        Canvas.SetLeft(image, destination.Left);
        Canvas.SetTop(image, destination.Top);
        host.InvalidateMeasure();
        host.UpdateLayout();
    }

    private static bool IsRecoverable(Exception error) => error is not OutOfMemoryException and not AccessViolationException;
}
