using DrillFlow.Application.Execution;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace DrillFlow.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddDrillFlowApplication(this IServiceCollection services)
    {
        services.AddSingleton<ExpressionEngine>();
        services.AddSingleton<WorkflowValidator>();
        services.AddSingleton<RunResultStore>();
        services.AddSingleton<IWorkflowRunner, WorkflowRunner>();
        return services;
    }
}
