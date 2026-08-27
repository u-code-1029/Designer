namespace DrillFlow.Application.Communication;

/// <summary>
/// Defines what the application does with a successfully completed request file.
/// This is independent from <see cref="EquipmentRequestFileLifecycle"/>, which
/// describes whether the equipment removes the request while processing it.
/// </summary>
public enum ApplicationRequestFileLifecycle
{
    DeleteAfterResponse = 0,
    RetainUntilOverwritten = 1,
}
