using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Environments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContainerForest.Aggregator.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<EnvironmentService>();
        services.AddScoped<AgentEnrollmentService>();
        services.AddScoped<ContainerCommandService>();
        return services;
    }
}
