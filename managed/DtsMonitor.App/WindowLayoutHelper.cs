using System.Windows;

namespace DtsMonitor.App;

internal static class WindowLayoutHelper
{
    public static void FitAndCenter(
        Window window,
        double designWidth,
        double designHeight,
        double margin = 24,
        bool allowUpscale = false,
        double maxScale = 1.0)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            window.ClearValue(Window.MaxWidthProperty);
            window.ClearValue(Window.MaxHeightProperty);
            return;
        }

        Rect workArea = SystemParameters.WorkArea;
        Rect hostArea = GetHostArea(window, workArea);

        double maxWidth = Math.Max(window.MinWidth, hostArea.Width - margin * 2);
        double maxHeight = Math.Max(window.MinHeight, hostArea.Height - margin * 2);
        double scaleLimit = allowUpscale ? Math.Max(1.0, maxScale) : 1.0;
        double scale = Math.Min(scaleLimit, Math.Min(maxWidth / designWidth, maxHeight / designHeight));

        window.ClearValue(Window.MaxWidthProperty);
        window.ClearValue(Window.MaxHeightProperty);
        window.Width = Math.Max(window.MinWidth, designWidth * scale);
        window.Height = Math.Max(window.MinHeight, designHeight * scale);

        double left = hostArea.Left + Math.Max(0, (hostArea.Width - window.Width) / 2);
        double top = hostArea.Top + Math.Max(0, (hostArea.Height - window.Height) / 2);

        window.Left = left;
        window.Top = top;
    }

    public static void CenterCurrentSize(Window window, double margin = 24)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            window.ClearValue(Window.MaxWidthProperty);
            window.ClearValue(Window.MaxHeightProperty);
            return;
        }

        Rect workArea = SystemParameters.WorkArea;
        Rect hostArea = GetHostArea(window, workArea);

        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        width = Math.Min(width, Math.Max(window.MinWidth, hostArea.Width - margin * 2));
        height = Math.Min(height, Math.Max(window.MinHeight, hostArea.Height - margin * 2));

        window.ClearValue(Window.MaxWidthProperty);
        window.ClearValue(Window.MaxHeightProperty);
        window.Width = width;
        window.Height = height;

        window.Left = hostArea.Left + Math.Max(0, (hostArea.Width - width) / 2);
        window.Top = hostArea.Top + Math.Max(0, (hostArea.Height - height) / 2);
    }

    private static Rect GetHostArea(Window window, Rect workArea)
    {
        if (window.Owner is not Window owner || !owner.IsLoaded)
        {
            return workArea;
        }

        Rect ownerBounds = owner.WindowState == WindowState.Normal
            ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
            : workArea;

        double left = Math.Max(workArea.Left, ownerBounds.Left);
        double top = Math.Max(workArea.Top, ownerBounds.Top);
        double right = Math.Min(workArea.Right, ownerBounds.Right);
        double bottom = Math.Min(workArea.Bottom, ownerBounds.Bottom);

        if (right <= left || bottom <= top)
        {
            return workArea;
        }

        return new Rect(left, top, right - left, bottom - top);
    }
}
