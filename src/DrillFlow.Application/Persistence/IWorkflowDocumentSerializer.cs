using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Application.Persistence;

public interface IWorkflowDocumentSerializer
{
    string Serialize(WorkflowDocument document);

    WorkflowDocument Deserialize(string json);

    Task SaveAsync(string filePath, WorkflowDocument document, CancellationToken cancellationToken);

    Task<WorkflowDocument> LoadAsync(string filePath, CancellationToken cancellationToken);
}
