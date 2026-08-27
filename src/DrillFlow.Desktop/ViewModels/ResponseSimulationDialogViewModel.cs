using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrillFlow.Desktop.ViewModels;

public sealed class ResponseSimulationPreview
{
    public ResponseSimulationPreview(ImageSource imageSource, string imagePath, string payload)
    {
        ImageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
        ImagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public ImageSource ImageSource { get; }

    public string ImagePath { get; }

    public string Payload { get; }
}

public sealed class ResponseSimulationDialogViewModel : ObservableObject
{
    private readonly Func<string, Task<ResponseSimulationPreview?>> _regeneratePreviewAsync;
    private readonly string _imageGenerationFailedMessage;
    private string _payload;
    private string _validationMessage = string.Empty;
    private ImageSource? _previewImage;
    private string _generatedImagePath = string.Empty;

    public ResponseSimulationDialogViewModel(
        string actionSummary,
        string payloadFormat,
        string responsePath,
        string activeRequestSummary,
        string payload,
        ResponseSimulationPreview? initialPreview,
        Func<string, Task<ResponseSimulationPreview?>> regeneratePreviewAsync,
        string imageGenerationFailedMessage,
        bool canGenerateImage = true)
    {
        ActionSummary = actionSummary;
        PayloadFormat = payloadFormat;
        ResponsePath = responsePath;
        ActiveRequestSummary = activeRequestSummary;
        _payload = payload;
        _regeneratePreviewAsync = regeneratePreviewAsync
                                  ?? throw new ArgumentNullException(nameof(regeneratePreviewAsync));
        _imageGenerationFailedMessage = imageGenerationFailedMessage ?? string.Empty;
        CanGenerateImage = canGenerateImage;
        RegenerateImageCommand = new AsyncRelayCommand(RegenerateImageAsync, () => CanGenerateImage);

        if (initialPreview != null)
        {
            _previewImage = initialPreview.ImageSource;
            _generatedImagePath = initialPreview.ImagePath;
            _payload = initialPreview.Payload;
        }
    }

    public string ActionSummary { get; }

    public string PayloadFormat { get; }

    public string ResponsePath { get; }

    public string ActiveRequestSummary { get; }

    public ImageSource? PreviewImage => _previewImage;

    public string GeneratedImagePath => _generatedImagePath;

    public bool HasGeneratedImage => PreviewImage != null
                                     && !string.IsNullOrWhiteSpace(GeneratedImagePath);

    public bool CanGenerateImage { get; }

    public IAsyncRelayCommand RegenerateImageCommand { get; }

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

    private async Task RegenerateImageAsync()
    {
        try
        {
            var preview = await _regeneratePreviewAsync(Payload).ConfigureAwait(true);
            if (preview == null)
            {
                return;
            }

            _previewImage = preview.ImageSource;
            _generatedImagePath = preview.ImagePath;
            OnPropertyChanged(nameof(PreviewImage));
            OnPropertyChanged(nameof(GeneratedImagePath));
            OnPropertyChanged(nameof(HasGeneratedImage));
            Payload = preview.Payload;
            ValidationMessage = string.Empty;
        }
        catch (Exception exception)
        {
            ValidationMessage = string.IsNullOrWhiteSpace(_imageGenerationFailedMessage)
                ? exception.Message
                : _imageGenerationFailedMessage + " " + exception.Message;
        }
    }
}
