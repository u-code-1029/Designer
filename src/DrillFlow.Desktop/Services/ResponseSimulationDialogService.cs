using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DrillFlow.Application.Communication;
using DrillFlow.Desktop.ViewModels;
using DrillFlow.Desktop.Views;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.Services;

public sealed class ResponseSimulationDialogService : IResponseSimulationDialogService
{
    private readonly IEquipmentResponseSimulator _simulator;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ResponseSimulationDialogService> _logger;

    public ResponseSimulationDialogService(
        IEquipmentResponseSimulator simulator,
        ILocalizationService localization,
        ILogger<ResponseSimulationDialogService> logger)
    {
        _simulator = simulator;
        _localization = localization;
        _logger = logger;
    }

    public async Task<bool> ShowAsync(WorkflowActionViewModel action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var host = ContentDialogHost.GetForWindow(System.Windows.Application.Current.MainWindow)
                   ?? throw new InvalidOperationException("The main ContentDialog host is unavailable.");
        var lastCorrelation = action.Results.LastOrDefault()?.CorrelationId;
        var draft = await _simulator.CreateDraftAsync(
            action.Model,
            lastCorrelation,
            CancellationToken.None);
        var activeRequestSummary = draft.ActiveRequest == null
            ? _localization["ResponseTestNoActiveRequest"]
            : string.Format(
                _localization["ResponseTestActiveRequestValue"],
                draft.ActiveRequest.Index,
                draft.ActiveRequest.Command);
        var viewModel = new ResponseSimulationDialogViewModel(
            action.Alias + " (" + action.Title + ")",
            _simulator.PayloadFormat,
            draft.ResponsePath,
            activeRequestSummary,
            draft.Payload);

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
                DialogWidth = 700,
                DialogHeight = 620
            };

            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.Primary)
                {
                    return;
                }

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
}
