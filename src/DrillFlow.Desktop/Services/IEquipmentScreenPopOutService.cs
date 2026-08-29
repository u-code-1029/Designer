using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Services;

public interface IEquipmentScreenPopOutService
{
    void Show(EquipmentCommunicationMonitorViewModel monitor);
}
