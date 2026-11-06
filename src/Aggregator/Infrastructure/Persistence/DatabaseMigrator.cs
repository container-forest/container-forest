using ContainerForest.Aggregator.Infrastructure.Accounts;
using ContainerForest.Aggregator.Infrastructure.Backups;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>Applies pending migrations when the host starts.</summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopeFactory, ILogger<DatabaseMigrator> logger) : IHostedService
{
    public const string BeforeMigrationSuffix = ".before-migration";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AggregatorDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await KeepCopyBeforeMigratingAsync(db, cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);

        // WAL lets readers work while a write is in progress; the setting is stored in the database file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// A migration cannot be undone, so an existing database gets a copy next to it, replaced at the next migration
    /// (ADR-0014). It holds nothing the database does not. No copy must not stop the aggregator from starting.
    /// </summary>
    private async Task KeepCopyBeforeMigratingAsync(AggregatorDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
            {
                return;
            }

            var source = db.Database.GetConnectionString()!;
            var databasePath = new SqliteConnectionStringBuilder(source).DataSource;
            if (string.IsNullOrEmpty(databasePath) || databasePath == ":memory:")
            {
                return;
            }

            await SqliteSnapshot.WriteAsync(source, Path.GetFullPath(databasePath) + BeforeMigrationSuffix, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not keep a copy of the database before migrating it");
        }
    }
}

/// <summary>Lets `dotnet ef` create the context without starting the Api host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AggregatorDbContext>
{
    // Identity builds its tables from the IdentityOptions of the application's services (the schema version decides
    // whether there is a passkeys table), so the migrations need the same options as the running aggregator.
    public AggregatorDbContext CreateDbContext(string[] args)
    {
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(AccountPolicy.Configure);
        return new(new DbContextOptionsBuilder<AggregatorDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .UseApplicationServiceProvider(services.BuildServiceProvider())
            .Options);
    }
}
