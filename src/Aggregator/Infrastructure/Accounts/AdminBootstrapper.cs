using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// Creates the first administrator from <see cref="BootstrapOptions"/> when the database has no users: on a
/// fresh installation there is nobody else who could. Runs after <see cref="DatabaseMigrator"/>.
/// </summary>
internal sealed class AdminBootstrapper(
    IServiceScopeFactory scopeFactory,
    IOptions<BootstrapOptions> options,
    TimeProvider time,
    ILogger<AdminBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AggregatorDbContext>();
        if (await db.Set<User>().AnyAsync(cancellationToken))
        {
            return;
        }

        var bootstrap = options.Value;
        if (!bootstrap.IsConfigured)
        {
            logger.LogWarning(
                "There are no users and {Section}:AdminUserName / AdminPassword are not set: nobody can sign in.",
                BootstrapOptions.SectionName);
            return;
        }

        var user = User.CreateAdministrator(bootstrap.AdminUserName!, time.GetUtcNow());
        db.Set<User>().Add(user);

        // The account store saves the context, so the domain user is stored together with its account
        // and a rejected password leaves nothing behind.
        var accounts = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
        var result = await accounts.CreateAsync(new AccountUser(user), bootstrap.AdminPassword!);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cannot create the first administrator: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }

        logger.LogInformation("Created the first administrator '{UserName}'.", user.UserName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
