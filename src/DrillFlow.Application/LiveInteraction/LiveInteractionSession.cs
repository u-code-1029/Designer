using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Validation;
using Microsoft.Extensions.Logging;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>
/// Builds strongly defined live commands on top of the same correlated file transport used by
/// workflow execution. The session gate prevents two UI gestures (for example a frame poll and a
/// double-click move) from competing before they reach the shared transport gate.
/// </summary>
public sealed class LiveInteractionSession : ILiveInteractionSession, IDisposable
{
    private readonly IEquipmentFileTransport _transport;
    private readonly ICorrelationIdProvider _correlationIds;
    private readonly ILogger<LiveInteractionSession> _logger;
    private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
    private readonly object _stateSync = new object();
    private bool _isBusy;
    private bool _disposed;

    public LiveInteractionSession(
        IEquipmentFileTransport transport,
        ICorrelationIdProvider correlationIds,
        ILogger<LiveInteractionSession> logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _correlationIds = correlationIds ?? throw new ArgumentNullException(nameof(correlationIds));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsBusy
    {
        get
        {
            lock (_stateSync)
            {
                return _isBusy;
            }
        }
    }

    public event EventHandler? BusyChanged;

    public Task<EquipmentResponseMessage> RequestFrameAsync(
        CancellationToken cancellationToken = default)
    {
        return ExchangeAsync(
            LiveInteractionProtocol.FrameCommand,
            null,
            requireImagePath: true,
            cancellationToken);
    }

    public Task<EquipmentResponseMessage> MoveRelativeAsync(
        double moveXMetres,
        double moveYMetres,
        CancellationToken cancellationToken = default)
    {
        var moveX = ParameterValueValidator.GetMoveCoordinate(
            moveXMetres,
            LiveInteractionProtocol.MoveXParameter);
        var moveY = ParameterValueValidator.GetMoveCoordinate(
            moveYMetres,
            LiveInteractionProtocol.MoveYParameter);

        return ExchangeAsync(
            LiveInteractionProtocol.MoveCommand,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [LiveInteractionProtocol.MoveModeParameter] =
                    LiveInteractionProtocol.RelativeMoveMode,
                [LiveInteractionProtocol.MoveXParameter] = moveX,
                [LiveInteractionProtocol.MoveYParameter] = moveY,
            },
            requireImagePath: false,
            cancellationToken);
    }

    public Task<EquipmentResponseMessage> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        return ExchangeAsync(
            LiveInteractionProtocol.CaptureCommand,
            null,
            requireImagePath: true,
            cancellationToken);
    }

    public void Dispose()
    {
        lock (_stateSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Do not dispose the gate here. Host shutdown can race an in-flight file exchange, whose
        // finally block must still release it. SemaphoreSlim does not allocate an OS handle unless
        // AvailableWaitHandle is requested, which this class never does.
    }

    private async Task<EquipmentResponseMessage> ExchangeAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        bool requireImagePath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SetBusy(true);
            var index = await _correlationIds.NextAsync(cancellationToken).ConfigureAwait(false);
            var request = new EquipmentRequestMessage(index, command, parameters);

            if (string.Equals(command, LiveInteractionProtocol.FrameCommand, StringComparison.Ordinal))
            {
                _logger.LogTrace(
                    "Starting live frame with correlation ID {CorrelationId}.",
                    index);
            }
            else
            {
                _logger.LogDebug(
                    "Starting live equipment command {Command} with correlation ID {CorrelationId}.",
                    command,
                    index);
            }

            var response = await _transport.ExchangeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.Index != index)
            {
                throw new InvalidOperationException(
                    $"The live equipment response index {response.Index} does not match request "
                    + $"index {index}.");
            }

            if (requireImagePath && response.ImagePath is null)
            {
                throw new InvalidOperationException(
                    $"The '{command}' response must contain an absolute 'image_path'.");
            }

            if (string.Equals(command, LiveInteractionProtocol.FrameCommand, StringComparison.Ordinal))
            {
                _logger.LogTrace(
                    "Completed live frame with correlation ID {CorrelationId}.",
                    index);
            }
            else
            {
                _logger.LogDebug(
                    "Completed live equipment command {Command} with correlation ID {CorrelationId}.",
                    command,
                    index);
            }
            return response;
        }
        finally
        {
            try
            {
                SetBusy(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private void SetBusy(bool value)
    {
        lock (_stateSync)
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
        }

        var handlers = BusyChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // UI observers must never be able to strand the equipment gate or turn a valid
                // controller response into a failed exchange.
                try
                {
                    _logger.LogWarning(
                        exception,
                        "A live interaction BusyChanged observer failed.");
                }
                catch (Exception)
                {
                    // Logging providers can already be disposed during host shutdown.
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateSync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LiveInteractionSession));
            }
        }
    }
}
