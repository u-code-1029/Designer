using System;
using DrillFlow.Application.Communication;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>
/// Couples an image-producing response to the unique output pathname owned by that request. The
/// caller may remove the file after consuming it only when the controller returned this exact
/// pathname; a different response pathname remains controller-owned.
/// </summary>
public sealed class LiveImageExchangeResult
{
    public LiveImageExchangeResult(
        EquipmentResponseMessage response,
        string requestedImagePath)
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
        if (!EquipmentResponseMessage.IsSupportedAbsoluteImagePath(requestedImagePath))
        {
            throw new ArgumentException(
                "The requested image path must be an absolute local or UNC pathname.",
                nameof(requestedImagePath));
        }

        RequestedImagePath = requestedImagePath;
    }

    public EquipmentResponseMessage Response { get; }

    public string RequestedImagePath { get; }

    public bool OwnsResponseImage =>
        string.Equals(
            RequestedImagePath,
            Response.ImagePath,
            StringComparison.OrdinalIgnoreCase);
}
