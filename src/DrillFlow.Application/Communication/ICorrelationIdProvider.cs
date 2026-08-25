using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Application.Communication;

public interface ICorrelationIdProvider
{
    Task<int> NextAsync(CancellationToken cancellationToken);
}

