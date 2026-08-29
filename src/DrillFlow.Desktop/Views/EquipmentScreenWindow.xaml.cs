using DrillFlow.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.Views;

public partial class EquipmentScreenWindow : FluentWindow
{
    public EquipmentScreenWindow(EquipmentCommunicationMonitorViewModel monitor)
    {
        InitializeComponent();
        DataContext = monitor;
    }
}
