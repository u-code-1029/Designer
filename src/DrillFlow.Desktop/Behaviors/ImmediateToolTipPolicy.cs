using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace DrillFlow.Desktop.Behaviors;

/// <summary>
/// Applies one application-wide tooltip timing policy without repeating attached properties on
/// every icon button. The Loaded class handler also covers controls created later by templates.
/// </summary>
internal static class ImmediateToolTipPolicy
{
    private const int ToolTipShowDurationMilliseconds = 60_000;
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement element
            || ToolTipService.GetToolTip(element) is null)
        {
            return;
        }

        ToolTipService.SetInitialShowDelay(element, 0);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, ToolTipShowDurationMilliseconds);
    }
}
