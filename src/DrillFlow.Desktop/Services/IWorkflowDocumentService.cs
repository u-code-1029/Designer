using System.Threading.Tasks;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Desktop.Services;

public interface IWorkflowDocumentService
{
    string Serialize(WorkflowDocument document);

    WorkflowDocument Deserialize(string json);

    Task SaveAsync(string filePath, WorkflowDocument document);

    Task<WorkflowDocument> LoadAsync(string filePath);
}
