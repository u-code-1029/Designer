using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>Builds canonical live equipment actions on the correlated file transport.</summary>
public sealed class LiveInteractionSession : ILiveInteractionSession, IDisposable
{
    private readonly IEquipmentFileTransport _transport;
    private readonly ICorrelationIdProvider _correlationIds;
    private readonly EquipmentCommunicationOptions _communicationOptions;
    private readonly ILogger<LiveInteractionSession> _logger;
    private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
    private readonly object _stateSync = new object();
    private bool _isBusy;
    private bool _disposed;

    public LiveInteractionSession(
        IEquipmentFileTransport transport,
        ICorrelationIdProvider correlationIds,
        IOptions<EquipmentCommunicationOptions> communicationOptions,
        ILogger<LiveInteractionSession> logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _correlationIds = correlationIds ?? throw new ArgumentNullException(nameof(correlationIds));
        _communicationOptions = communicationOptions?.Value
            ?? throw new ArgumentNullException(nameof(communicationOptions));
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

    public Task<LiveImageExchangeResult> RequestFrameAsync(
        double horizontalFieldWidthMetres,
        CancellationToken cancellationToken = default)
    {
        ValidateHorizontalFieldWidth(horizontalFieldWidthMetres);
        return ExchangeImageAsync(
            LiveInteractionProtocol.LiveAction,
            horizontalFieldWidthMetres,
            LiveInteractionProtocol.LiveFrameCount,
            cancellationToken);
    }

    public Task<EquipmentResponseMessage> MoveStageAsync(
        string moveMode,
        double stageXMetres,
        double stageYMetres,
        CancellationToken cancellationToken = default)
    {
        ValidateMove(moveMode, stageXMetres, stageYMetres, "stage");
        return ExchangeAsync(
            LiveInteractionProtocol.StageAction,
            _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [LiveInteractionProtocol.MoveModeParameter] = moveMode,
                [LiveInteractionProtocol.StageXParameter] = stageXMetres,
                [LiveInteractionProtocol.StageYParameter] = stageYMetres,
            },
            requireImagePath: false,
            cancellationToken);
    }

    public Task<EquipmentResponseMessage> MoveCameraAsync(
        string moveMode,
        double cameraXMetres,
        double cameraYMetres,
        CancellationToken cancellationToken = default)
    {
        ValidateMove(moveMode, cameraXMetres, cameraYMetres, "camera");
        return ExchangeAsync(
            LiveInteractionProtocol.CameraAction,
            _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [LiveInteractionProtocol.MoveModeParameter] = moveMode,
                [LiveInteractionProtocol.CameraXParameter] = cameraXMetres,
                [LiveInteractionProtocol.CameraYParameter] = cameraYMetres,
            },
            requireImagePath: false,
            cancellationToken);
    }

    public Task<EquipmentResponseMessage> FocusAsync(
        double horizontalFieldWidthMetres,
        double rangeMetres,
        int steps,
        CancellationToken cancellationToken = default)
    {
        ValidateHorizontalFieldWidth(horizontalFieldWidthMetres);
        if (!LiveInteractionProtocol.IsFinite(rangeMetres) || rangeMetres <= 0d)
        {
            throw new ParameterValidationException(
                "range must be a finite number greater than zero metres.");
        }

        if (steps <= 3)
        {
            throw new ParameterValidationException("steps must be an integer greater than 3.");
        }

        return ExchangeAsync(
            LiveInteractionProtocol.FocusAction,
            _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [LiveInteractionProtocol.HorizontalFieldWidthParameter] = horizontalFieldWidthMetres,
                [LiveInteractionProtocol.FocusRangeParameter] = rangeMetres,
                [LiveInteractionProtocol.FocusStepsParameter] = steps,
            },
            requireImagePath: false,
            cancellationToken);
    }

    public Task<LiveImageExchangeResult> IntegrateAsync(
        double horizontalFieldWidthMetres,
        int frameCount,
        CancellationToken cancellationToken = default)
    {
        ValidateHorizontalFieldWidth(horizontalFieldWidthMetres);
        if (!LiveInteractionProtocol.IsValidIntegrationFrameCount(frameCount))
        {
            throw new ParameterValidationException(
                "frame_count must be a power of two between 1 and 64.");
        }

        return ExchangeImageAsync(
            LiveInteractionProtocol.IntegrationAction,
            horizontalFieldWidthMetres,
            frameCount,
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

        // Do not dispose the gate: host shutdown can race an in-flight exchange whose finally
        // block still has to release it.
    }

    private async Task<LiveImageExchangeResult> ExchangeImageAsync(
        string action,
        double horizontalFieldWidthMetres,
        int frameCount,
        CancellationToken cancellationToken)
    {
        string? requestedImagePath = null;
        try
        {
            var response = await ExchangeAsync(
                    action,
                    correlationId =>
                    {
                        requestedImagePath = CreateOwnedImagePath(action, correlationId);
                        return new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            [LiveInteractionProtocol.HorizontalFieldWidthParameter] =
                                horizontalFieldWidthMetres,
                            [LiveInteractionProtocol.FrameCountParameter] = frameCount,
                            [LiveInteractionProtocol.ImagePathParameter] = requestedImagePath,
                        };
                    },
                    requireImagePath: true,
                    cancellationToken,
                    prepareOwnedImageDirectory: true)
                .ConfigureAwait(false);

            return new LiveImageExchangeResult(response, requestedImagePath!);
        }
        catch
        {
            // Only the correlation-specific path generated above belongs to this application.
            // A controller-owned alternate response image is never considered for deletion here.
            TryDeleteOwnedImagePath(requestedImagePath);
            throw;
        }
    }

    private async Task<EquipmentResponseMessage> ExchangeAsync(
        string action,
        Func<int, IReadOnlyDictionary<string, object?>?> parameterFactory,
        bool requireImagePath,
        CancellationToken cancellationToken,
        bool prepareOwnedImageDirectory = false)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SetBusy(true);
            var correlationId = await _correlationIds.NextAsync(cancellationToken)
                .ConfigureAwait(false);
            if (prepareOwnedImageDirectory)
            {
                await EnsureOwnedImageDirectoryAsync(action, cancellationToken)
                    .ConfigureAwait(false);
            }

            var request = new EquipmentRequestMessage(
                correlationId,
                action,
                parameterFactory(correlationId));

            if (string.Equals(action, LiveInteractionProtocol.LiveAction, StringComparison.Ordinal))
            {
                _logger.LogTrace(
                    "Starting live image request with correlation ID {CorrelationId}.",
                    correlationId);
            }
            else
            {
                _logger.LogDebug(
                    "Starting live equipment action {Action} with correlation ID {CorrelationId}.",
                    action,
                    correlationId);
            }

            var response = await _transport.ExchangeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.CorrelationId != correlationId)
            {
                throw new InvalidOperationException(
                    $"The response correlation ID {response.CorrelationId} does not match "
                    + $"request correlation ID {correlationId}.");
            }

            if (!string.Equals(response.Action, action, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The response action '{response.Action}' does not match request action "
                    + $"'{action}'.");
            }

            if (response.Result != 0)
            {
                throw new LiveEquipmentActionFailedException(response);
            }

            if (requireImagePath && response.ImagePath is null)
            {
                throw new InvalidOperationException(
                    $"The '{action}' response must contain an absolute 'image_path'.");
            }

            if (string.Equals(action, LiveInteractionProtocol.LiveAction, StringComparison.Ordinal))
            {
                _logger.LogTrace(
                    "Completed live image request with correlation ID {CorrelationId}.",
                    correlationId);
            }
            else
            {
                _logger.LogDebug(
                    "Completed live equipment action {Action} with correlation ID {CorrelationId}.",
                    action,
                    correlationId);
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

    private string CreateOwnedImagePath(string action, int correlationId)
    {
        var directory = GetOwnedImageDirectory(action);
        return Path.Combine(directory, action + "-" + correlationId + ".bmp");
    }

    private string GetOwnedImageDirectory(string action)
    {
        if (string.Equals(action, LiveInteractionProtocol.LiveAction, StringComparison.Ordinal))
        {
            return _communicationOptions.LiveImageDirectory;
        }

        return Path.Combine(
            _communicationOptions.ExchangeDirectory,
            EquipmentCommunicationOptions.DefaultLiveImageDirectoryName);
    }

    private void TryDeleteOwnedImagePath(string? requestedImagePath)
    {
        if (string.IsNullOrWhiteSpace(requestedImagePath))
        {
            return;
        }

        try
        {
            File.Delete(requestedImagePath!);
        }
        catch (Exception exception) when (
            exception is IOException
            || exception is UnauthorizedAccessException
            || exception is NotSupportedException
            || exception is ArgumentException
            || exception is System.Security.SecurityException)
        {
            try
            {
                _logger.LogWarning(
                    exception,
                    "Could not remove the app-owned image path {ImagePath} after a failed "
                    + "Live Interaction exchange.",
                    requestedImagePath);
            }
            catch (Exception)
            {
                // Logging providers can already be disposed while cancellation cleanup runs.
            }
        }
    }

    private Task EnsureOwnedImageDirectoryAsync(
        string action,
        CancellationToken cancellationToken)
    {
        var directory = GetOwnedImageDirectory(action);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                cancellationToken.ThrowIfCancellationRequested();
            },
            CancellationToken.None);
    }

    private static void ValidateHorizontalFieldWidth(double value)
    {
        if (!LiveInteractionProtocol.IsValidHorizontalFieldWidth(value))
        {
            throw new ParameterValidationException(
                "hfw must be finite, greater than zero, and less than 2.4E-3 metres.");
        }
    }

    private static void ValidateMove(string moveMode, double x, double y, string action)
    {
        if (!LiveInteractionProtocol.IsMoveMode(moveMode))
        {
            throw new ParameterValidationException(
                "move_mode must be exactly 'relative' or 'absolute'.");
        }

        if (!LiveInteractionProtocol.IsFinite(x) || !LiveInteractionProtocol.IsFinite(y))
        {
            throw new ParameterValidationException(
                action + " coordinates must be finite numbers in metres.");
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
                try
                {
                    _logger.LogWarning(exception, "A live BusyChanged observer failed.");
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
