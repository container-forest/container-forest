using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
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
        services.AddScoped<CurrentActor>();
        services.AddScoped<DiscordLinkService>();
        services.AddScoped<UserManagementService>();
        services.AddScoped<ContainerCommandService>();
        services.AddScoped<ContainerQueryService>();
        services.AddScoped<ContainerLogService>();
        services.AddScoped<NotificationSettingsService>();
        services.AddScoped<RepositoryService>();
        services.AddScoped<RegistryCredentialService>();
        services.AddScoped<StackService>();
        services.AddScoped<StackCommandService>();
        services.AddScoped<DeploymentService>();
        services.AddScoped<DeploymentRunner>();

        // Steps run in this order.
        services.AddScoped<IDeployStep, GitFetchStep>();
        services.AddScoped<IDeployStep, ValidateComposeStep>();
        services.AddScoped<IDeployStep, BuildBundleStep>();
        services.AddScoped<IDeployStep, ResolveRegistryAuthStep>();
        services.AddScoped<IDeployStep, SendToAgentStep>();
        services.AddScoped<DeployPipeline>();

        services.AddSingleton<NotificationFeed>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<NotificationDeliveryService>();
        services.AddScoped<IDeploymentObserver, DeploymentNotifier>();
        return services;
    }
}
