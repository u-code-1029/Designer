using System;

namespace DrillFlow.Application.Communication;

/// <summary>
/// Immutable communication settings captured for one complete request/response exchange. Runtime
/// settings changes are intentionally observed only by a subsequent exchange.
/// </summary>
public sealed class EquipmentCommunicationSnapshot
{
    private EquipmentCommunicationSnapshot(
        string exchangeDirectory,
        string liveImageDirectory,
        string requestFileName,
        string responseFileName,
        EquipmentRequestFileLifecycle equipmentRequestLifecycle,
        ApplicationRequestFileLifecycle applicationRequestLifecycle,
        ApplicationResponseFileLifecycle applicationResponseLifecycle,
        TimeSpan responseTimeout,
        bool retryEnabled,
        int maximumRetryCount,
        TimeSpan retryDelay,
        TimeSpan requestPublishDelay,
        TimeSpan pollingInterval,
        TimeSpan stableReadDelay)
    {
        ExchangeDirectory = exchangeDirectory;
        LiveImageDirectory = liveImageDirectory;
        RequestFileName = requestFileName;
        ResponseFileName = responseFileName;
        EquipmentRequestLifecycle = equipmentRequestLifecycle;
        ApplicationRequestLifecycle = applicationRequestLifecycle;
        ApplicationResponseLifecycle = applicationResponseLifecycle;
        ResponseTimeout = responseTimeout;
        RetryEnabled = retryEnabled;
        MaximumRetryCount = maximumRetryCount;
        RetryDelay = retryDelay;
        RequestPublishDelay = requestPublishDelay;
        PollingInterval = pollingInterval;
        StableReadDelay = stableReadDelay;
    }

    public string ExchangeDirectory { get; }

    public string LiveImageDirectory { get; }

    public string RequestFileName { get; }

    public string ResponseFileName { get; }

    public EquipmentRequestFileLifecycle EquipmentRequestLifecycle { get; }

    public ApplicationRequestFileLifecycle ApplicationRequestLifecycle { get; }

    public ApplicationResponseFileLifecycle ApplicationResponseLifecycle { get; }

    public TimeSpan ResponseTimeout { get; }

    public bool RetryEnabled { get; }

    public int MaximumRetryCount { get; }

    public TimeSpan RetryDelay { get; }

    public TimeSpan RequestPublishDelay { get; }

    public TimeSpan PollingInterval { get; }

    public TimeSpan StableReadDelay { get; }

    public static EquipmentCommunicationSnapshot Capture(EquipmentCommunicationOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new EquipmentCommunicationSnapshot(
            options.ExchangeDirectory ?? string.Empty,
            options.LiveImageDirectory ?? string.Empty,
            options.RequestFileName ?? string.Empty,
            options.ResponseFileName ?? string.Empty,
            options.EquipmentRequestLifecycle,
            options.ApplicationRequestLifecycle,
            options.ApplicationResponseLifecycle,
            options.ResponseTimeout,
            options.RetryEnabled,
            options.MaximumRetryCount,
            options.RetryDelay,
            options.RequestPublishDelay,
            options.PollingInterval,
            options.StableReadDelay);
    }
}
