using CommunityToolkit.Mvvm.ComponentModel;

namespace DrillFlow.Desktop.ViewModels;

public sealed class ResponseSimulationDialogViewModel : ObservableObject
{
    private string _payload;
    private string _validationMessage = string.Empty;

    public ResponseSimulationDialogViewModel(
        string actionSummary,
        string payloadFormat,
        string responsePath,
        string activeRequestSummary,
        string payload)
    {
        ActionSummary = actionSummary;
        PayloadFormat = payloadFormat;
        ResponsePath = responsePath;
        ActiveRequestSummary = activeRequestSummary;
        _payload = payload;
    }

    public string ActionSummary { get; }

    public string PayloadFormat { get; }

    public string ResponsePath { get; }

    public string ActiveRequestSummary { get; }

    public string Payload
    {
        get => _payload;
        set => SetProperty(ref _payload, value ?? string.Empty);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set
        {
            if (SetProperty(ref _validationMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);
}
