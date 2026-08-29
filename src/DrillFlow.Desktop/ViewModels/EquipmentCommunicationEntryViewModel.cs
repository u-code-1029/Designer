using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DrillFlow.Desktop.ViewModels;

public enum EquipmentCommunicationDirection
{
    Request,
    Response
}

public enum EquipmentCommunicationEntryState
{
    Waiting,
    Retried,
    Matched,
    Failed
}

public sealed class EquipmentCommunicationEntryViewModel : ObservableObject
{
    private EquipmentCommunicationEntryState _state;
    private string _stateDetail = string.Empty;

    public EquipmentCommunicationEntryViewModel(
        DateTimeOffset timestamp,
        EquipmentCommunicationDirection direction,
        string filePath,
        string action,
        int correlationId,
        int attempt,
        string payloadJson,
        EquipmentCommunicationEntryState state)
    {
        Timestamp = timestamp;
        Direction = direction;
        FilePath = filePath ?? string.Empty;
        Action = action ?? string.Empty;
        CorrelationId = correlationId;
        Attempt = attempt;
        PayloadJson = payloadJson ?? string.Empty;
        _state = state;
    }

    public DateTimeOffset Timestamp { get; }

    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public EquipmentCommunicationDirection Direction { get; }

    public bool IsRequest => Direction == EquipmentCommunicationDirection.Request;

    public bool IsResponse => Direction == EquipmentCommunicationDirection.Response;

    public string DirectionText => IsRequest ? "REQUEST" : "RESPONSE";

    public string FilePath { get; }

    public string Action { get; }

    public int CorrelationId { get; }

    public int Attempt { get; }

    public string AttemptText => IsRequest && Attempt > 1 ? "retry " + Attempt : string.Empty;

    public string PayloadJson { get; }

    public EquipmentCommunicationEntryState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string StateText => State switch
    {
        EquipmentCommunicationEntryState.Waiting => "WAITING",
        EquipmentCommunicationEntryState.Retried => "RETRIED",
        EquipmentCommunicationEntryState.Matched => "MATCHED",
        EquipmentCommunicationEntryState.Failed => "STOPPED",
        _ => string.Empty
    };

    public string StateDetail
    {
        get => _stateDetail;
        private set => SetProperty(ref _stateDetail, value);
    }

    public void MarkRetried()
    {
        State = EquipmentCommunicationEntryState.Retried;
        StateDetail = string.Empty;
    }

    public void MarkMatched()
    {
        State = EquipmentCommunicationEntryState.Matched;
        StateDetail = string.Empty;
    }

    public void MarkFailed(string detail)
    {
        StateDetail = detail ?? string.Empty;
        State = EquipmentCommunicationEntryState.Failed;
    }
}
