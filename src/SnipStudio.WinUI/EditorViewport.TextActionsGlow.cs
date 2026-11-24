using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    private const double TextActionsGlowSpread = 12;
    private bool textActionsGlowActive;
    private Storyboard? textActionsGlowSweep;
    private LinearGradientBrush? sweptTextActionsBrush;
    private Transform? originalTextActionsBrushTransform;

    internal void StartTextActionsGlow()
    {
        if (textActionsGlowActive) return;
        textActionsGlowActive = true;
        UpdateTextActionsGlow();

        if (!Host.IsLoaded || !new UISettings().AnimationsEnabled ||
            TextActionsGlowRim.BorderBrush is not LinearGradientBrush gradient) return;

        var rotation = new RotateTransform { CenterX = .5, CenterY = .5 };
        sweptTextActionsBrush = gradient;
        originalTextActionsBrushTransform = gradient.RelativeTransform;
        gradient.RelativeTransform = rotation;
        var sweep = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(sweep, rotation);
        Storyboard.SetTargetProperty(sweep, nameof(RotateTransform.Angle));
        textActionsGlowSweep = new Storyboard();
        textActionsGlowSweep.Children.Add(sweep);
        textActionsGlowSweep.Begin();
    }

    private void UpdateTextActionsGlow()
    {
        if (!textActionsGlowActive) return;
        if (ImageFrame.Visibility != Visibility.Visible ||
            view.TextActionsGlowBounds(TextActionsGlowSpread) is not { } bounds)
        {
            TextActionsGlow.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(TextActionsGlow, bounds.X);
        Canvas.SetTop(TextActionsGlow, bounds.Y);
        TextActionsGlow.Width = bounds.Width;
        TextActionsGlow.Height = bounds.Height;
        TextActionsGlow.Visibility = Visibility.Visible;
    }

    internal void EndTextActionsGlow()
    {
        textActionsGlowActive = false;
        TextActionsGlow.Visibility = Visibility.Collapsed;
        textActionsGlowSweep?.Stop();
        textActionsGlowSweep = null;
        if (sweptTextActionsBrush is not null)
            sweptTextActionsBrush.RelativeTransform = originalTextActionsBrushTransform;
        sweptTextActionsBrush = null;
        originalTextActionsBrushTransform = null;
    }
}
