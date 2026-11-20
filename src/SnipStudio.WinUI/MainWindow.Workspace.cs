using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<DependencyObject, object> suppressedWorkspaceTooltips = [];
    private ICommandBarElement[] horizontalAnnotationCommands = [];
    private ICommandBarElement[] annotationOverflowCommands = [];
    private bool annotationToolbarOnSide;

    private void ApplyWorkspacePreferences()
    {
        string edge = preferences.AnnotationToolbarPosition;
        bool bottom = edge == "Bottom";
        bool side = edge is "Left" or "Right";

        Grid.SetRow(AnnotationToolbarHost, bottom ? 6 : 1);
        Grid.SetRow(ContextRow, bottom ? 5 : 2);
        Grid.SetColumn(SideAnnotationHost, edge == "Right" ? 2 : 0);
        if (annotationToolbarOnSide != side) MoveAnnotationCommands(side);
        UpdateAnnotationToolbarState(!busy && !applicationExitPending);
        ApplyWorkspaceTooltips();
    }

    private void MoveAnnotationCommands(bool toSide)
    {
        if (toSide)
        {
            // Move command instances so selections, enabled state and attached flyouts
            // remain the same when the toolbar changes edge.
            ToolBar.IsOpen = false;
            ToolBar.IsDynamicOverflowEnabled = false;
            horizontalAnnotationCommands = ToolBar.PrimaryCommands.ToArray();
            annotationOverflowCommands = ToolBar.SecondaryCommands.ToArray();
            ToolBar.PrimaryCommands.Clear();
            ToolBar.SecondaryCommands.Clear();
            SideAnnotationCommands.Children.Clear();
            foreach (ICommandBarElement command in horizontalAnnotationCommands.Concat(annotationOverflowCommands))
            {
                switch (command)
                {
                    case AppBarButton button:
                        button.IsCompact = true;
                        SideAnnotationCommands.Children.Add(button);
                        break;
                    case AppBarToggleButton toggle:
                        toggle.IsCompact = true;
                        SideAnnotationCommands.Children.Add(toggle);
                        break;
                    case AppBarSeparator:
                        SideAnnotationCommands.Children.Add(new Border { Height = 5 });
                        break;
                    case AppBarElementContainer:
                        SideAnnotationCommands.Children.Add(SideExternalEditorButton);
                        break;
                }
            }
        }
        else
        {
            SideAnnotationCommands.Children.Clear();
            foreach (ICommandBarElement command in horizontalAnnotationCommands)
                ToolBar.PrimaryCommands.Add(command);
            foreach (ICommandBarElement command in annotationOverflowCommands)
            {
                if (command is AppBarButton button) button.ClearValue(AppBarButton.IsCompactProperty);
                if (command is AppBarToggleButton toggle) toggle.ClearValue(AppBarToggleButton.IsCompactProperty);
                ToolBar.SecondaryCommands.Add(command);
            }
            ToolBar.IsDynamicOverflowEnabled = true;
            horizontalAnnotationCommands = annotationOverflowCommands = [];
        }
        annotationToolbarOnSide = toSide;
    }

    private void UpdateAnnotationToolbarState(bool enabled)
    {
        ToolBar.IsEnabled = SideAnnotationHost.IsEnabled = enabled;
        AnnotationToolbarHost.Visibility = session.HasImage && !annotationToolbarOnSide ? Visibility.Visible : Visibility.Collapsed;
        SideAnnotationHost.Visibility = session.HasImage && annotationToolbarOnSide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWorkspaceToolTip(DependencyObject owner, string text)
    {
        if (preferences.ShowTooltips)
        {
            ToolTipService.SetToolTip(owner, text);
            return;
        }
        suppressedWorkspaceTooltips[owner] = text;
        ToolTipService.SetToolTip(owner, null);
    }

    private void ApplyWorkspaceTooltips()
    {
        if (preferences.ShowTooltips)
        {
            foreach ((DependencyObject owner, object tooltip) in suppressedWorkspaceTooltips)
                ToolTipService.SetToolTip(owner, tooltip);
            suppressedWorkspaceTooltips.Clear();
            return;
        }

        foreach (DependencyObject owner in WorkspaceElements(Root))
        {
            if (ToolTipService.GetToolTip(owner) is not { } tooltip) continue;
            suppressedWorkspaceTooltips[owner] = tooltip;
            ToolTipService.SetToolTip(owner, null);
        }
    }

    private static IEnumerable<DependencyObject> WorkspaceElements(DependencyObject element)
    {
        // Include markup in CommandBar overflow and show/hide panels before it is loaded.
        yield return element;
        IEnumerable<DependencyObject> children = element switch
        {
            Panel panel => panel.Children.OfType<DependencyObject>(),
            CommandBar commandBar => commandBar.PrimaryCommands.Concat(commandBar.SecondaryCommands).OfType<DependencyObject>(),
            Border { Child: { } child } => [child],
            ContentControl { Content: DependencyObject content } => [content],
            _ => []
        };
        foreach (DependencyObject child in children)
        {
            foreach (DependencyObject descendant in WorkspaceElements(child))
                yield return descendant;
        }
    }
}
