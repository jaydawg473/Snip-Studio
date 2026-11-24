using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private void OnStrokeSizeStepClicked(object sender, RoutedEventArgs args) =>
        StepSize(StrokeWidthBox, sender, Viewport.InkWidth);

    private void OnTextSizeStepClicked(object sender, RoutedEventArgs args) =>
        StepSize(TextSizeBox, sender, textSizeFormatter.ExactValue);

    private void StepSize(NumberBox input, object sender, double fallback)
    {
        if (busy || closed || sender is not FrameworkElement { Tag: string direction }) return;
        double baseValue = double.IsFinite(input.Value) ? input.Value : fallback;
        double step = direction == "Up" ? input.SmallChange : -input.SmallChange;
        input.Value = Math.Clamp(baseValue + step, input.Minimum, input.Maximum);
    }

    private void RestoreSizeValue(NumberBox input, double fallback) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && !double.IsFinite(input.Value)) input.Value = fallback;
        });
}
