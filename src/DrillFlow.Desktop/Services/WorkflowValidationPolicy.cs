using System;

namespace DrillFlow.Desktop.Services;

public sealed class WorkflowValidationPolicy : IWorkflowValidationPolicy
{
    private bool _validateOnEveryChange;

    public WorkflowValidationPolicy(IUserSettingsStore settingsStore)
    {
        if (settingsStore is null)
        {
            throw new ArgumentNullException(nameof(settingsStore));
        }

        _validateOnEveryChange = settingsStore.Load().ValidateWorkflowOnEveryChange;
    }

    public event EventHandler? Changed;

    public bool ValidateOnEveryChange => _validateOnEveryChange;

    public void Apply(bool validateOnEveryChange)
    {
        if (_validateOnEveryChange == validateOnEveryChange)
        {
            return;
        }

        _validateOnEveryChange = validateOnEveryChange;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
