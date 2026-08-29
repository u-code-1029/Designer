using System;
using System.Windows;
using DrillFlow.Desktop.ViewModels;
using DrillFlow.Desktop.Views;

namespace DrillFlow.Desktop.Services;

public sealed class EquipmentScreenPopOutService : IEquipmentScreenPopOutService
{
    private EquipmentScreenWindow? _window;

    public void Show(EquipmentCommunicationMonitorViewModel monitor)
    {
        if (monitor is null)
        {
            throw new ArgumentNullException(nameof(monitor));
        }

        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
            return;
        }

        monitor.EnterPopOutMode();
        var window = new EquipmentScreenWindow(monitor)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        _window = window;
        window.Closed += (_, _) =>
        {
            _window = null;
            monitor.ExitPopOutMode();
        };
        window.Show();
        window.Activate();
    }
}
