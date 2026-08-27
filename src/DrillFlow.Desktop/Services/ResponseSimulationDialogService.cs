using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.ViewModels;
using DrillFlow.Desktop.Views;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.Services;

public sealed class ResponseSimulationDialogService : IResponseSimulationDialogService
{
    private readonly IEquipmentResponseSimulator _simulator;
    private readonly ITemporaryResponseImageService _temporaryImages;
    private readonly ILocalizationService _localization;
    private readonly IContentDialogGate _dialogGate;
    private readonly ILogger<ResponseSimulationDialogService> _logger;

    public ResponseSimulationDialogService(
        IEquipmentResponseSimulator simulator,
        ITemporaryResponseImageService temporaryImages,
        ILocalizationService localization,
        IContentDialogGate dialogGate,
        ILogger<ResponseSimulationDialogService> logger)
    {
        _simulator = simulator;
        _temporaryImages = temporaryImages;
        _localization = localization;
        _dialogGate = dialogGate;
        _logger = logger;
    }

    public async Task<bool> ShowAsync(WorkflowActionViewModel action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        using (await _dialogGate.EnterAsync().ConfigureAwait(true))
        {
            return await ShowCoreAsync(action);
        }
    }

    private async Task<bool> ShowCoreAsync(WorkflowActionViewModel action)
    {
        var host = ContentDialogHost.GetForWindow(System.Windows.Application.Current.MainWindow)
                   ?? throw new InvalidOperationException("The main ContentDialog host is unavailable.");
        var lastCorrelation = action.Results.LastOrDefault()?.CorrelationId;
        var supportsImageResponse = action.Kind is WorkflowNodeKind.Integration or WorkflowNodeKind.Live;
        TemporaryResponseImage? generatedImage = null;
        try
        {
            if (supportsImageResponse)
            {
                generatedImage = await Task.Run(
                        () => _temporaryImages.CreateTemporaryImage())
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            // A test response remains useful without an image. The optional image_path field is
            // omitted by the simulator when generation is unavailable.
            _logger.LogWarning(
                exception,
                "Could not create a temporary response image for action {ActionKey}.",
                action.Alias);
        }

        var draft = await _simulator.CreateDraftAsync(
            action.Model,
            lastCorrelation,
            CancellationToken.None,
            generatedImage?.Path);
        var activeRequestSummary = draft.ActiveRequest == null
            ? _localization["ResponseTestNoActiveRequest"]
            : string.Format(
                _localization["ResponseTestActiveRequestValue"],
                draft.ActiveRequest.CorrelationId,
                draft.ActiveRequest.Action);
        var initialPreview = generatedImage == null
            ? null
            : new ResponseSimulationPreview(
                generatedImage.ImageSource,
                generatedImage.Path,
                draft.Payload);
        var viewModel = new ResponseSimulationDialogViewModel(
            action.Alias + " (" + action.Title + ")",
            _simulator.PayloadFormat,
            draft.ResponsePath,
            activeRequestSummary,
            draft.Payload,
            initialPreview,
            async currentPayload =>
            {
                if (!supportsImageResponse)
                {
                    return null;
                }

                try
                {
                    var nextImage = await Task.Run(
                            () => _temporaryImages.CreateTemporaryImage())
                        .ConfigureAwait(true);
                    string nextPayload;
                    if (string.Equals(_simulator.PayloadFormat, "JSON", StringComparison.OrdinalIgnoreCase))
                    {
                        // Preserve every user-edited logical response field. Only the generated
                        // image pathname belongs to the preview button.
                        nextPayload = SynchronizeJsonImagePath(currentPayload, nextImage.Path);
                    }
                    else
                    {
                        // A future non-JSON simulator remains usable even though its format owns
                        // how an image path is represented.
                        var nextDraft = await _simulator.CreateDraftAsync(
                                action.Model,
                                lastCorrelation,
                                CancellationToken.None,
                                nextImage.Path)
                            .ConfigureAwait(true);
                        nextPayload = nextDraft.Payload;
                    }

                    return new ResponseSimulationPreview(
                        nextImage.ImageSource,
                        nextImage.Path,
                        nextPayload);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not regenerate a temporary response image for action {ActionKey}.",
                        action.Alias);
                    throw;
                }
            },
            _localization["ResponseTestImageGenerationFailed"],
            supportsImageResponse);

        while (true)
        {
            var content = new ResponseSimulationDialogContent { DataContext = viewModel };
            var dialog = new ContentDialog(host)
            {
                Title = _localization["ResponseTestTitle"],
                Content = content,
                PrimaryButtonText = _localization["ResponseTestPublish"],
                CloseButtonText = _localization["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                DialogWidth = 920,
                DialogHeight = 660
            };

            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.Primary)
                {
                    return;
                }

                if (viewModel.RegenerateImageCommand.IsRunning)
                {
                    viewModel.ValidationMessage = _localization["ResponseTestImageGenerationInProgress"];
                    args.Cancel = true;
                    return;
                }

                SynchronizePreviewImagePath(viewModel);
                var validation = _simulator.ValidatePayload(viewModel.Payload);
                if (validation.IsValid)
                {
                    viewModel.ValidationMessage = string.Empty;
                    return;
                }

                viewModel.ValidationMessage = string.Join(Environment.NewLine, validation.Errors);
                args.Cancel = true;
            };

            var result = await dialog.ShowAsync(CancellationToken.None);
            if (result != ContentDialogResult.Primary)
            {
                return false;
            }

            try
            {
                await _simulator.PublishAsync(viewModel.Payload, CancellationToken.None);
                _logger.LogInformation(
                    "Published a simulated equipment response to {ResponsePath} for action {ActionKey}.",
                    draft.ResponsePath,
                    action.Alias);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Could not publish a simulated response for action {ActionKey}.",
                    action.Alias);
                viewModel.ValidationMessage = exception.Message;
            }
        }
    }

    private static void SynchronizePreviewImagePath(ResponseSimulationDialogViewModel viewModel)
    {
        if (!viewModel.HasGeneratedImage
            || !string.Equals(viewModel.PayloadFormat, "JSON", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            viewModel.Payload = SynchronizeJsonImagePath(
                viewModel.Payload,
                viewModel.GeneratedImagePath);
        }
        catch (JsonException)
        {
            // Keep invalid user input untouched so the simulator can show its precise validation
            // error instead of hiding it behind preview synchronization.
        }
    }

    internal static string SynchronizeJsonImagePath(string payload, string imagePath)
    {
        var root = JObject.Parse(
            payload,
            new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
        root["image_path"] = imagePath;
        return root.ToString(Formatting.Indented);
    }
}
