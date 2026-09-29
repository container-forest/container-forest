using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Infrastructure.Agents;
using ContainerForest.Aggregator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Aggregator";

    /// <param name="migrateDatabase">Apply pending migrations when the host starts.</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, bool migrateDatabase = true)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<AggregatorDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AggregatorDbContext>());
        services.AddScoped<IEnvironmentRepository, EnvironmentRepository>();
        services.AddScoped<IStackRepository, StackRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleAssignmentRepository, RoleAssignmentRepository>();
        if (migrateDatabase)
        {
            services.AddHostedService<DatabaseMigrator>();
        }

        services.AddOptions<AgentGatewayOptions>()
            .Bind(configuration.GetSection(AgentGatewayOptions.SectionName))
            .Validate(o => o.IsValid(), "AgentGateway timeouts must be positive.")
            .ValidateOnStart();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<IAgentChannel, GrpcAgentChannel>();

        return services;
    }
}
