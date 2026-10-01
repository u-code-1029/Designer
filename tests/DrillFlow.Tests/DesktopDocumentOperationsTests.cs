using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Execution;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Validation;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using DrillFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopDocumentOperationsTests
{
    [Fact]
    public async Task PendingSave_LocksDocumentCommandsAndKeepsLateModelChangesDirty()
    {
        using var context = new DesktopDocumentTestContext();
        var editor = context.CreateEditor();
        var action = editor.CreateAndInsert(WorkflowNodeKind.Delay, editor.Actions, 0)!;
        var saveCompletion = new TaskCompletionSource<bool>();
        context.Documents.SaveCompletion = saveCompletion.Task;

        var save = editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.IsDocumentOperationBusy);
        Assert.False(editor.IsWorkflowEditingEnabled);
        Assert.False(editor.NewCommand.CanExecute(null));
        Assert.False(editor.OpenCommand.CanExecute(null));
        Assert.False(editor.SaveAsCommand.CanExecute(null));
        Assert.False(editor.RunCommand.CanExecute(null));
        Assert.False(editor.UndoCommand.CanExecute(null));
        Assert.False(await editor.PrepareForCloseAsync());
        action.Alias = "ignored_while_saving";
        Assert.Equal("delay_1", action.Alias);
        Assert.Null(editor.CreateAndInsert(WorkflowNodeKind.Stage, editor.Actions, 1));
        await editor.NewCommand.ExecuteAsync(null);
        Assert.Single(editor.Actions);

        // Automation can mutate the model directly, bypassing disabled WPF editors.
        // Such a change must not alter the file snapshot or be marked as saved.
        action.Model.Key = "late_edit";
        Assert.Equal("delay_1", Assert.Single(context.Documents.SavedDocument!.Nodes).Key);
        saveCompletion.SetResult(true);
        await save;

        Assert.False(editor.IsDocumentOperationBusy);
        Assert.True(editor.IsWorkflowEditingEnabled);
        Assert.True(editor.IsDirty);
        Assert.Equal(context.Dialogs.SavePath, editor.DocumentPath);
    }

    [Fact]
    public async Task FailedSaveAs_PreservesDocumentNamePathAndDirtyState()
    {
        using var context = new DesktopDocumentTestContext();
        var editor = context.CreateEditor();
        editor.CreateAndInsert(WorkflowNodeKind.Delay, editor.Actions, 0);
        await editor.SaveCommand.ExecuteAsync(null);
        var name = editor.DocumentName;
        var path = editor.DocumentPath;
        editor.SelectedAction!.Alias = "changed_delay";
        context.Dialogs.SavePath = @"C:\Workflows\replacement.drillflow.json";
        context.Documents.SaveCompletion = Task.FromException(new IOException("Save failed"));

        await editor.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(name, editor.DocumentName);
        Assert.Equal(path, editor.DocumentPath);
        Assert.True(editor.IsDirty);
        Assert.True(editor.StatusIsError);
        Assert.True(editor.IsWorkflowEditingEnabled);
        Assert.Equal("replacement", context.Documents.SavedDocument!.Name);
    }

    [Fact]
    public async Task PendingOpen_PreventsConcurrentDocumentReplacementAndEdits()
    {
        using var context = new DesktopDocumentTestContext();
        var editor = context.CreateEditor();
        var loadCompletion = new TaskCompletionSource<WorkflowDocument>();
        context.Documents.LoadCompletion = loadCompletion.Task;

        var open = editor.OpenCommand.ExecuteAsync(null);

        Assert.True(editor.IsDocumentOperationBusy);
        Assert.False(editor.NewCommand.CanExecute(null));
        Assert.Null(editor.CreateAndInsert(WorkflowNodeKind.Delay, editor.Actions, 0));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(context.Documents.SavedDocument);
        loadCompletion.SetResult(new WorkflowDocument
        {
            Name = "Loaded workflow",
            Nodes = { new DelayNode { Key = "loaded_delay" } }
        });
        await open;

        Assert.Equal("Loaded workflow", editor.DocumentName);
        Assert.Equal("loaded_delay", Assert.Single(editor.Actions).Alias);
        Assert.False(editor.IsDirty);
        Assert.True(editor.IsWorkflowEditingEnabled);
    }

    [Fact]
    public async Task PendingUnsavedChangesDecision_LocksEditingUntilCanceled()
    {
        using var context = new DesktopDocumentTestContext();
        var editor = context.CreateEditor();
        editor.CreateAndInsert(WorkflowNodeKind.Delay, editor.Actions, 0);
        var decision = new TaskCompletionSource<UnsavedChangesChoice>();
        context.Dialogs.UnsavedDecision = decision.Task;

        var createNew = editor.NewCommand.ExecuteAsync(null);

        Assert.True(editor.IsDocumentOperationBusy);
        Assert.False(editor.RunCommand.CanExecute(null));
        Assert.False(editor.SaveCommand.CanExecute(null));
        decision.SetResult(UnsavedChangesChoice.Cancel);
        await createNew;

        Assert.Single(editor.Actions);
        Assert.True(editor.IsDirty);
        Assert.True(editor.IsWorkflowEditingEnabled);
    }

    [Fact]
    public async Task SuccessfulSaveAs_CommitsNameOnlyAfterSavingAndHandlesUppercaseExtension()
    {
        using var context = new DesktopDocumentTestContext();
        var editor = context.CreateEditor();
        editor.CreateAndInsert(WorkflowNodeKind.Delay, editor.Actions, 0);
        var oldName = editor.DocumentName;
        context.Dialogs.SavePath = @"C:\Workflows\Inspection.DRILLFLOW.JSON";
        var completion = new TaskCompletionSource<bool>();
        context.Documents.SaveCompletion = completion.Task;

        var save = editor.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(oldName, editor.DocumentName);
        Assert.Null(editor.DocumentPath);
        Assert.Equal("Inspection", context.Documents.SavedDocument!.Name);
        completion.SetResult(true);
        await save;

        Assert.Equal("Inspection", editor.DocumentName);
        Assert.Equal(context.Dialogs.SavePath, editor.DocumentPath);
        Assert.False(editor.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsavedChangesSave_DoesNotDiscardLateEditsWhenCreatingNewOrClosing(
        bool close)
    {
        using var context = new DesktopDocumentTestContext();
        var editor = context.CreateEditor();
        var action = editor.CreateAndInsert(WorkflowNodeKind.Delay, editor.Actions, 0)!;
        context.Dialogs.UnsavedDecision = Task.FromResult(UnsavedChangesChoice.Save);
        var completion = new TaskCompletionSource<bool>();
        context.Documents.SaveCompletion = completion.Task;

        var operation = close
            ? editor.PrepareForCloseAsync()
            : editor.NewCommand.ExecuteAsync(null);
        action.Model.Key = "late_edit";
        completion.SetResult(true);
        await operation;

        if (close)
        {
            Assert.False(await (Task<bool>)operation);
        }
        Assert.Same(action, Assert.Single(editor.Actions));
        Assert.True(editor.IsDirty);
        Assert.Equal("late_edit", action.Alias);
        Assert.True(editor.IsWorkflowEditingEnabled);
    }
}

internal sealed class DesktopDocumentTestContext : IDisposable
{
    private readonly LiveInteractionSession _session;
    private readonly UnusedServices _services = new();
    private MainPageViewModel? _editor;

    public DesktopDocumentTestContext()
    {
        var options = Options.Create(CommunicationOptions);
        _session = new LiveInteractionSession(
            _services, _services, options, NullLogger<LiveInteractionSession>.Instance);
        LiveInteraction = new LiveInteractionPageViewModel(
            _session, Dialogs, _services, _services, _services, _services, _services,
            _services, options, Localization, Execution,
            NullLogger<LiveInteractionPageViewModel>.Instance);
    }

    public StubDocuments Documents { get; } = new();
    public StubDialogs Dialogs { get; } = new();
    public StubSettings Settings { get; } = new();
    public StubLocalization Localization { get; } = new();
    public StubExecution Execution { get; } = new();
    public EquipmentCommunicationOptions CommunicationOptions { get; } = new()
    {
        ExchangeDirectory = @"C:\Exchange"
    };
    public LiveInteractionPageViewModel LiveInteraction { get; }
    public string? LastOpenedFolder => _services.LastOpenedFolder;

    public MainPageViewModel CreateEditor() => _editor = new MainPageViewModel(
        Localization, Documents, Execution, LiveInteraction, _services, Dialogs,
        Dialogs, _services, _services, _services, new WorkflowValidationPolicy(Settings),
        new WorkflowValidator(), NullLogger<MainPageViewModel>.Instance);

    public SettingsPageViewModel CreateSettings() => new(
        Settings, Localization, new StubTheme(), new WorkflowValidationPolicy(Settings),
        Options.Create(CommunicationOptions), Execution, LiveInteraction, Dialogs,
        _services, NullLogger<SettingsPageViewModel>.Instance);

    public void Dispose()
    {
        _editor?.Dispose();
        _session.Dispose();
    }

    internal sealed class StubDocuments : IWorkflowDocumentService
    {
        private readonly JsonWorkflowDocumentSerializer _serializer = new();
        public Task SaveCompletion { get; set; } = Task.CompletedTask;
        public Task<WorkflowDocument> LoadCompletion { get; set; } =
            Task.FromResult(new WorkflowDocument());
        public WorkflowDocument? SavedDocument { get; private set; }
        public string Serialize(WorkflowDocument document) => _serializer.Serialize(document);
        public WorkflowDocument Deserialize(string json) => _serializer.Deserialize(json);
        public Task SaveAsync(string filePath, WorkflowDocument document)
        {
            SavedDocument = document;
            return SaveCompletion;
        }
        public Task<WorkflowDocument> LoadAsync(string filePath) => LoadCompletion;
    }

    internal sealed class StubDialogs : IFileDialogService, IUserDialogService
    {
        public string SavePath { get; set; } = @"C:\Workflows\Saved.drillflow.json";
        public Task<UnsavedChangesChoice> UnsavedDecision { get; set; } =
            Task.FromResult(UnsavedChangesChoice.Discard);
        public string? ShowOpenWorkflowDialog() => @"C:\Workflows\Opened.drillflow.json";
        public string? ShowSaveWorkflowDialog(string suggestedFileName) => SavePath;
        public string? ShowSaveImageDialog(string sourceImagePath, string detectedExtension) => null;
        public string? ShowSelectFolderDialog(string initialFolder) => null;
        public string? ShowSelectLiveImageFolderDialog(string initialFolder) => null;
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync() => UnsavedDecision;
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    internal sealed class StubSettings : IUserSettingsStore
    {
        public UserPreferences Preferences { get; set; } = new()
        {
            Communication = new CommunicationSettings { ExchangeFolder = @"C:\Exchange" }
        };
        public UserPreferences? SavedPreferences { get; private set; }
        public UserPreferences Load() => Preferences;
        public void Save(UserPreferences preferences) => SavedPreferences = preferences;
    }

    internal sealed class StubLocalization : ILocalizationService
    {
        public event EventHandler? LanguageChanged;
        public string SelectedLanguage => "en-US";
        public string EffectiveLanguage => "en-US";
        public string this[string key] => key;
        public void Initialize() { }
        public void ApplyLanguage(string language, bool persist = true) =>
            LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class StubExecution : IWorkflowExecutionFacade
    {
#pragma warning disable CS0067
        public event EventHandler<WorkflowRunStateChangedEventArgs>? RunStateChanged;
        public event EventHandler<WorkflowNodeStateChangedEventArgs>? NodeStateChanged;
#pragma warning restore CS0067
        public WorkflowRunState State => WorkflowRunState.Idle;
        public WorkflowNode? CurrentNode => null;
        public RunResultStore Results { get; } = new();
        public Task RunAsync(WorkflowDocument document) => throw new NotSupportedException();
        public Task RunSelectedAsync(WorkflowDocument document, Guid actionId) =>
            throw new NotSupportedException();
        public void Continue() { }
        public void Step() { }
        public void RequestStop() { }
        public void ForceStop() { }
    }

    private sealed class StubTheme : IApplicationThemeService
    {
        public string SelectedTheme => "System";
        public void Initialize() { }
        public void ApplyTheme(string selection) { }
    }

    private sealed class UnusedServices : ILiveCaptureSnapshotStore, ILiveImageDecoder,
        IDefaultFileLauncher, IEquipmentResponseSimulator, ITemporaryResponseImageService,
        IExchangeFolderLauncher, IResponseSimulationDialogService, IEquipmentFileTransport,
        ICorrelationIdProvider
    {
        public string? LastOpenedFolder { get; private set; }
        public string PayloadFormat => "XML";
        public string Open() => throw new NotSupportedException();
        public string Open(string directory) => LastOpenedFolder = directory;
        public Task<int> NextAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<LiveCaptureSnapshot> AcquireAsync(string path, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<LiveImageDecodeResult> DecodeAsync(byte[] bytes, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<EquipmentResponseSimulationDraft> CreateDraftAsync(
            WorkflowNode node, int? id, CancellationToken token, string? image = null) =>
            throw new NotSupportedException();
        public Task<EquipmentRequestSnapshot?> GetActiveRequestAsync(CancellationToken token) =>
            throw new NotSupportedException();
        public Task<FrameResponseSimulationResult> TryPublishFrameResponseAsync(
            EquipmentRequestSnapshot request, string image, CancellationToken token) =>
            throw new NotSupportedException();
        public ResponsePayloadValidationResult ValidatePayload(string payload) =>
            throw new NotSupportedException();
        public Task PublishAsync(string payload, CancellationToken token) =>
            throw new NotSupportedException();
        public TemporaryResponseImage CreateTemporaryImage() => throw new NotSupportedException();
        public bool TryReleaseTemporaryImage(string path) => false;
        public Task<bool> ShowAsync(WorkflowActionViewModel action) => throw new NotSupportedException();
        public Task<EquipmentResponseMessage> ExchangeAsync(
            EquipmentRequestMessage request, CancellationToken token) => throw new NotSupportedException();
    }
}
