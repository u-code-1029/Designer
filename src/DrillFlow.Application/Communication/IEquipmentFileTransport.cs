using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Application.Communication;

public interface IEquipmentFileTransport
{
    /// <summary>
    /// Publishes one request and waits for its matching return response. Implementations permit
    /// only one in-flight exchange per transport instance.
    /// </summary>
    Task<EquipmentResponseMessage> ExchangeAsync(
        EquipmentRequestMessage request,
        CancellationToken cancellationToken);
}

