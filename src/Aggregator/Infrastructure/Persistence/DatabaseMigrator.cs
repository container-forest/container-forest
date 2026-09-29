using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>Applies pending migrations when the host starts.</summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopeFactory, ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AggregatorDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        }

        await db.Database.MigrateAsync(cancellationToken);

        // WAL lets readers work while a write is in progress; the setting is stored in the database file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Lets `dotnet ef` create the context without starting the Api host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AggregatorDbContext>
{
    public AggregatorDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AggregatorDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
