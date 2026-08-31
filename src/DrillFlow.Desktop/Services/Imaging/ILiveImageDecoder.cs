using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Desktop.Services;

public interface ILiveImageDecoder
{
    Task<LiveImageDecodeResult> DecodeAsync(
        byte[] encodedImage,
        CancellationToken cancellationToken);
}
