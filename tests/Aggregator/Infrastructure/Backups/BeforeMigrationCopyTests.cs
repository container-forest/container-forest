using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using ContainerForest.Aggregator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Backups;

public sealed class BeforeMigrationCopyTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cf-migration-test-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(directory, "aggregator.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task A_new_database_gets_no_copy()
    {
        await using var services = BuildServices();

        await new DatabaseMigrator(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseMigrator>.Instance).StartAsync(Ct);

        Assert.False(File.Exists(DatabasePath + DatabaseMigrator.BeforeMigrationSuffix));
    }

    [Fact]
    public async Task An_existing_database_is_copied_before_a_migration()
    {
        await using var services = BuildServices();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AggregatorDbContext>();
            var migrations = db.Database.GetMigrations().ToList();
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migrations[^2], cancellationToken: Ct);
        }

        await new DatabaseMigrator(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseMigrator>.Instance).StartAsync(Ct);

        Assert.True(File.Exists(DatabasePath + DatabaseMigrator.BeforeMigrationSuffix));
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(AccountPolicy.Configure);
        services.AddSingleton<ILiveUpdates, NullLiveUpdates>();
        services.AddDbContext<AggregatorDbContext>(options => options.UseSqlite($"Data Source={DatabasePath}"));
        return services.BuildServiceProvider();
    }
}
