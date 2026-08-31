using System.Threading;

namespace DrillFlow.Desktop.ViewModels;

internal static class LiveInteractionCancellation
{
    public static CancellationTokenSource CreatePostResponseSource(
        CancellationToken streamLifecycle,
        CancellationToken applicationShutdown)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            streamLifecycle,
            applicationShutdown);
    }
}
