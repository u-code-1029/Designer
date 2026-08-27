using System;
using DrillFlow.Application.Communication;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>
/// Represents an explicit non-success result returned by equipment during a Live Interaction
/// exchange. Unlike transport and image-I/O failures, this is a terminal controller decision and
/// must not be treated as a transient frame error.
/// </summary>
public sealed class LiveEquipmentActionFailedException : InvalidOperationException
{
    public LiveEquipmentActionFailedException(EquipmentResponseMessage response)
        : base(CreateMessage(response))
    {
        Response = response;
    }

    public EquipmentResponseMessage Response { get; }

    public int CorrelationId => Response.CorrelationId;

    public string Action => Response.Action;

    public int Result => Response.Result;

    private static string CreateMessage(EquipmentResponseMessage response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        return $"Equipment action '{response.Action}' with correlation ID "
               + $"{response.CorrelationId} returned failure result {response.Result}.";
    }
}
