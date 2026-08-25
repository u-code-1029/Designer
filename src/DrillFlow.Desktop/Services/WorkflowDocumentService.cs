using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Persistence;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Desktop.Services;

public sealed class WorkflowDocumentService : IWorkflowDocumentService
{
    private readonly IWorkflowDocumentSerializer _serializer;

    public WorkflowDocumentService(IWorkflowDocumentSerializer serializer)
    {
        _serializer = serializer;
    }

    public string Serialize(WorkflowDocument document) => _serializer.Serialize(document);

    public WorkflowDocument Deserialize(string json) => _serializer.Deserialize(json);

    public Task SaveAsync(string filePath, WorkflowDocument document) =>
        _serializer.SaveAsync(filePath, document, CancellationToken.None);

    public Task<WorkflowDocument> LoadAsync(string filePath) =>
        _serializer.LoadAsync(filePath, CancellationToken.None);
}
