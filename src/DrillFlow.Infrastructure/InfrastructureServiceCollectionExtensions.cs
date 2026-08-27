using System;
using System.Net.Http;
using System.Threading;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Http;
using DrillFlow.Application.Persistence;
using DrillFlow.Infrastructure.Communication;
using DrillFlow.Infrastructure.Http;
using DrillFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DrillFlow.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDrillFlowInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        services
            .AddOptions<EquipmentCommunicationOptions>()
            .Bind(configuration.GetSection(EquipmentCommunicationOptions.SectionName))
            .ValidateOnStart();
        services
            .AddOptions<CorrelationIdStoreOptions>()
            .Bind(configuration.GetSection(CorrelationIdStoreOptions.SectionName))
            .ValidateOnStart();

        return AddInfrastructureServices(services);
    }

    public static IServiceCollection AddDrillFlowInfrastructure(
        this IServiceCollection services,
        Action<EquipmentCommunicationOptions> configureCommunication,
        Action<CorrelationIdStoreOptions> configureCorrelationStore)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configureCommunication is null)
        {
            throw new ArgumentNullException(nameof(configureCommunication));
        }

        if (configureCorrelationStore is null)
        {
            throw new ArgumentNullException(nameof(configureCorrelationStore));
        }

        services.AddOptions<EquipmentCommunicationOptions>()
            .Configure(configureCommunication)
            .ValidateOnStart();
        services.AddOptions<CorrelationIdStoreOptions>()
            .Configure(configureCorrelationStore)
            .ValidateOnStart();

        return AddInfrastructureServices(services);
    }

    private static IServiceCollection AddInfrastructureServices(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<EquipmentCommunicationOptions>,
            EquipmentCommunicationOptionsValidator>();
        services.AddSingleton<IValidateOptions<CorrelationIdStoreOptions>,
            CorrelationIdStoreOptionsValidator>();
        services.AddSingleton<ICorrelationIdProvider, PersistentCorrelationIdProvider>();
        services.AddSingleton<IEquipmentMessageCodec, XmlTemplateEquipmentMessageCodec>();
        services.AddSingleton<IEquipmentFileTransport, FileEquipmentTransport>();
        services.AddSingleton<IEquipmentResponseSimulator, JsonEquipmentResponseSimulator>();
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<IHttpActionExecutor, HttpActionExecutor>();
        services.AddSingleton<IWorkflowDocumentSerializer, JsonWorkflowDocumentSerializer>();
        return services;
    }
}
