using System;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>
/// Performs operator-driven exchanges outside a persisted workflow. Calls are serialized for the
/// whole request/response exchange and share the transport gate with Designer workflow actions.
/// </summary>
public interface ILiveInteractionSession
{
    bool IsBusy { get; }

    event EventHandler? BusyChanged;

    /// <summary>Requests one low-latency image using the canonical <c>live</c> action.</summary>
    Task<LiveImageExchangeResult> RequestFrameAsync(
        double horizontalFieldWidthMetres,
        CancellationToken cancellationToken = default);

    Task<EquipmentResponseMessage> MoveStageAsync(
        string moveMode,
        double stageXMetres,
        double stageYMetres,
        CancellationToken cancellationToken = default);

    Task<EquipmentResponseMessage> MoveCameraAsync(
        string moveMode,
        double cameraXMetres,
        double cameraYMetres,
        CancellationToken cancellationToken = default);

    Task<EquipmentResponseMessage> FocusAsync(
        double horizontalFieldWidthMetres,
        double rangeMetres,
        int steps,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects an optical lens, or queries the current lens with <c>no_change</c>.
    /// </summary>
    Task<EquipmentResponseMessage> ChangeLensAsync(
        string lensMode,
        CancellationToken cancellationToken = default);

    /// <summary>Runs automatic contrast and brightness adjustment at the current live HFW.</summary>
    Task<EquipmentResponseMessage> AutoContrastBrightnessAsync(
        double horizontalFieldWidthMetres,
        CancellationToken cancellationToken = default);

    /// <summary>Requests a high-quality integrated image at the supplied HFW and frame count.</summary>
    Task<LiveImageExchangeResult> IntegrateAsync(
        double horizontalFieldWidthMetres,
        int frameCount,
        CancellationToken cancellationToken = default);
}
