using System.Threading.Tasks;
using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Services;

public interface IResponseSimulationDialogService
{
    /// <returns><see langword="true"/> when a response file was published.</returns>
    Task<bool> ShowAsync(WorkflowActionViewModel action);
}
