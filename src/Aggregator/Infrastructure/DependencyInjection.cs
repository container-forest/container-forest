using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Application.Repositories;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Infrastructure.Agents;
using ContainerForest.Aggregator.Infrastructure.Deployments;
using ContainerForest.Aggregator.Infrastructure.Persistence;
using ContainerForest.Aggregator.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Aggregator";

    /// <param name="runHostedServices">
    /// Apply pending migrations when the host starts and run the background workers. Off when the host is
    /// started only to generate the OpenAPI document.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, bool runHostedServices = true)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<AggregatorDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AggregatorDbContext>());
        services.AddScoped<IEnvironmentRepository, EnvironmentRepository>();
        services.AddScoped<IStackRepository, StackRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IDiscordLinkRepository, DiscordLinkRepository>();
        services.AddScoped<INotificationChannelRepository, NotificationChannelRepository>();
        services.AddScoped<INotificationRuleRepository, NotificationRuleRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IGitRepositoryRepository, GitRepositoryRepository>();
        services.AddScoped<IDeploymentRepository, DeploymentRepository>();
        services.AddScoped<IRegistryCredentialRepository, RegistryCredentialRepository>();

        // The key ring must survive restarts: with an in-memory one every stored secret is unreadable after the next start.
        // Generating the OpenAPI document starts the host too; it gets throwaway keys instead of creating a key ring at build time.
        if (runHostedServices)
        {
            var protection = configuration.GetSection(SecretProtectionOptions.SectionName).Get<SecretProtectionOptions>() ?? new();
            services.AddDataProtection()
                .SetApplicationName("container-forest")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(protection.KeyDirectory)));
            services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        }
        else
        {
            // Covers the whole app, not only ISecretProtector: authentication adds Data Protection too, and its default
            // key ring in ~/.aspnet/DataProtection-Keys would be created on every build.
            services.AddDataProtection();
            services.Configure<KeyManagementOptions>(options =>
            {
                options.XmlRepository = new InMemoryXmlRepository();
                // The keys never leave memory and die with the build, so there is nothing to encrypt them against.
                options.XmlEncryptor = new NullXmlEncryptor();
            });
            services.AddSingleton<ISecretProtector>(new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
        }
        if (runHostedServices)
        {
            // Registration order is start order: the worker reads the database the migrator prepares.
            services.AddHostedService<DatabaseMigrator>();
            services.AddHostedService<DeploymentWorker>();
        }

        services.AddOptions<AgentGatewayOptions>()
            .Bind(configuration.GetSection(AgentGatewayOptions.SectionName))
            .Validate(o => o.IsValid(), "AgentGateway timeouts must be positive.")
            .ValidateOnStart();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<IAgentChannel, GrpcAgentChannel>();

        services.AddOptions<DeploymentOptions>()
            .Bind(configuration.GetSection(DeploymentOptions.SectionName))
            .Validate(o => o.IsValid(), "Deployments limits and timeouts must be positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DeploymentOptions>>().Value);
        services.AddOptions<GitOptions>().Bind(configuration.GetSection(GitOptions.SectionName));
        services.AddSingleton<IGitSource, LibGit2SharpGitSource>();
        services.AddSingleton<IComposeParser, YamlComposeParser>();
        services.AddSingleton<DeploymentQueue>();
        services.AddSingleton<IDeploymentQueue>(sp => sp.GetRequiredService<DeploymentQueue>());

        return services;
    }
}
