using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DrillFlow.Application.RealtimeVideo;

public static class RealtimeVideoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the strongly typed real-time video settings and their startup validator.
    /// A host may bind <see cref="RealtimeVideoOptions.SectionName"/> before calling this method.
    /// </summary>
    public static IServiceCollection AddRealtimeVideoOptions(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions<RealtimeVideoOptions>();
        services.AddSingleton<IValidateOptions<RealtimeVideoOptions>, RealtimeVideoOptionsValidator>();
        return services;
    }
}
