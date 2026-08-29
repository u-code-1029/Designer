using System;

namespace DrillFlow.Desktop.Services;

public interface IWorkflowValidationPolicy
{
    event EventHandler? Changed;

    bool ValidateOnEveryChange { get; }

    void Apply(bool validateOnEveryChange);
}
