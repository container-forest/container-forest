using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Connection;
using ContainerForest.Agent.Docker;
using ContainerForest.Agent.Enrollment;
using ContainerForest.Agent.Platform;
using ContainerForest.Agent.Stacks;

namespace ContainerForest.Agent;

public static class DependencyInjection
{
    public static IServiceCollection AddAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .Validate(
                o => o.IsValid(),
                "CF_AGENT__CONTROLLERURL must be an absolute http(s) URL, the data, stacks and secrets directories absolute paths, " +
                "the docker executable set and the compose timeout and heartbeat interval positive.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBootIdProvider>(sp => OperatingSystem.IsLinux()
            ? new LinuxBootIdProvider()
            : new UptimeBootIdProvider(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDockerGateway, DockerDotNetGateway>(_ => new DockerDotNetGateway());

        services.AddSingleton<IAgentCommandHandler, ListContainersHandler>();
        services.AddSingleton<IAgentCommandHandler, StartContainerHandler>();
        services.AddSingleton<IAgentCommandHandler, StopContainerHandler>();
        services.AddSingleton<IAgentCommandHandler, RestartContainerHandler>();

        services.AddSingleton<StackLocks>();
        services.AddSingleton<IStackFileStore, StackFileStore>();
        services.AddSingleton<IComposeRunner, DockerComposeCli>();
        services.AddSingleton<RegistryConfigWriter>();
        services.AddSingleton<IAgentCommandHandler, DeployStackHandler>();
        services.AddSingleton<IAgentCommandHandler, RestartStackHandler>();
        services.AddSingleton<IAgentCommandHandler, DownStackHandler>();
        services.AddSingleton<CommandDispatcher>();

        services.AddSingleton<CredentialStore>();
        services.AddSingleton<AgentEnroller>();
        services.AddSingleton<AgentSessionRunner>();
        services.AddHostedService<AgentWorker>();
        return services;
    }
}
