using System;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>
/// Performs operator-driven camera and stage exchanges outside a persisted workflow. Every call
/// owns one complete request/response exchange and is serialized with other calls made through
/// this session. The shared equipment transport also excludes workflow exchanges while one of
/// these calls is in flight.
/// </summary>
public interface ILiveInteractionSession
{
    /// <summary>Whether a live request currently owns the session exchange gate.</summary>
    bool IsBusy { get; }

    /// <summary>Raised whenever <see cref="IsBusy"/> changes.</summary>
    event EventHandler? BusyChanged;

    /// <summary>Requests one low-latency camera frame.</summary>
    Task<EquipmentResponseMessage> RequestFrameAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Moves the stage by the supplied offsets in metres.</summary>
    Task<EquipmentResponseMessage> MoveRelativeAsync(
        double moveXMetres,
        double moveYMetres,
        CancellationToken cancellationToken = default);

    /// <summary>Requests a high-quality still capture.</summary>
    Task<EquipmentResponseMessage> CaptureAsync(
        CancellationToken cancellationToken = default);
}
