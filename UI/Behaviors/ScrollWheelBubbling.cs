using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetScopeDiagnosticCenter.UI.Behaviors;

/// <summary>
/// Fixes the classic WPF "page stops scrolling over a DataGrid" trap: a DataGrid's
/// internal ScrollViewer marks the mouse wheel handled even when it has nothing left
/// to scroll, so a long page freezes whenever the cursor crosses a results grid.
///
/// <para>
/// With <c>Enabled="True"</c> (set once in the implicit DataGrid style) the wheel is
/// re-raised to the parent scroll chain ONLY when the inner ScrollViewer cannot move
/// further in the wheel's direction — grids with long content keep their own scrolling
/// untouched.
/// </para>
/// </summary>
public static class ScrollWheelBubbling
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ScrollWheelBubbling),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element)
        {
            return;
        }

        var inner = FindDescendantScrollViewer(element);
        if (inner is not null && CanScrollFurther(inner, e.Delta))
        {
            // The grid genuinely has more content in this direction — let it scroll.
            return;
        }

        // Re-raise on the parent chain so the page ScrollViewer receives the wheel.
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };

        if (VisualTreeHelper.GetParent(element) is UIElement parent)
        {
            parent.RaiseEvent(args);
        }
    }

    private static bool CanScrollFurther(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        return delta < 0
            ? scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight
            : scrollViewer.VerticalOffset > 0;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
