using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

internal sealed class InMemoryNotificationChannels : InMemoryRepository<NotificationChannel, NotificationChannelId>, INotificationChannelRepository
{
    public Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NotificationChannel>>(Items.OrderBy(c => c.Name).ToList());
}

internal sealed class InMemoryNotificationRules(InMemoryNotificationChannels channels)
    : InMemoryRepository<NotificationRule, NotificationRuleId>, INotificationRuleRepository
{
    public Exception? Failure { get; set; }

    public async Task<IReadOnlyList<NotificationRule>> ListActiveAsync(CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        var enabled = (await channels.ListAsync(cancellationToken)).Where(c => c.IsEnabled).Select(c => c.Id).ToHashSet();
        return Items.Where(r => r.IsEnabled && enabled.Contains(r.ChannelId)).ToList();
    }

    public Task<IReadOnlyList<NotificationRule>> ListByChannelAsync(NotificationChannelId channelId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NotificationRule>>(Items.Where(r => r.ChannelId == channelId).ToList());
}

internal sealed class InMemoryNotifications : InMemoryRepository<Notification, NotificationId>, INotificationRepository
{
    public IReadOnlyList<Notification> All => Items;

    public Task<IReadOnlyList<Notification>> ListPendingAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Notification>>(Items.Where(n => n.Status == NotificationStatus.Pending).OrderBy(n => n.CreatedAt).ToList());

    public Task<IReadOnlyList<Notification>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Notification>>(Items.OrderByDescending(n => n.CreatedAt).Take(limit).ToList());
}

/// <summary>An environment "prod" with a stack "web", and the notification services over in-memory repositories.</summary>
internal sealed class NotificationFixture
{
    public static readonly DateTimeOffset Now = DeployFixtures.Now;

    public NotificationFixture()
    {
        Rules = new InMemoryNotificationRules(Channels);
        Environment = DockerEnvironment.Create("prod", isPrivate: false, Now);
        Environments.Add(Environment);
        Stack = Stack.Create(
            Environment.Id,
            StackName.Parse("web"),
            RepositoryId.New(),
            BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"),
            isSystem: false,
            Now);
        Stacks.Add(Stack);
    }

    public DockerEnvironment Environment { get; }

    public Stack Stack { get; }

    public InMemoryEnvironments Environments { get; } = new();

    public InMemoryStacks Stacks { get; } = new();

    public InMemoryNotificationChannels Channels { get; } = new();

    public InMemoryNotificationRules Rules { get; }

    public InMemoryNotifications Notifications { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public NotificationFeed Feed { get; } = new();

    public NotificationChannel AddChannel(string name, ulong discordChannelId = 1000)
    {
        var channel = NotificationChannel.CreateDiscord(name, discordChannelId, Now);
        Channels.Add(channel);
        return channel;
    }

    public NotificationRule AddRule(NotificationRule rule)
    {
        Rules.Add(rule);
        return rule;
    }

    public NotificationDispatcher Dispatcher(DateTimeOffset? now = null) =>
        new(Rules, Notifications, UnitOfWork, Feed, new FixedTime(now ?? Now));

    public NotificationDeliveryService Delivery(DateTimeOffset? now = null) =>
        new(Notifications, Channels, Environments, UnitOfWork, new FixedTime(now ?? Now), NullLogger<NotificationDeliveryService>.Instance);

    public NotificationEvent FailedDeployment(string error = "Branch 'main' does not exist.") =>
        new(
            NotificationEventKinds.DeploymentFailed,
            Environment.Id,
            Stack.Id,
            Now,
            new DeploymentNotificationDetails(DeploymentId.New(), Stack.Id, Stack.Name, null, false, error));

    /// <summary>A pending notification for the channel, as the dispatcher would have created it.</summary>
    public Notification AddPending(NotificationChannel channel, DateTimeOffset? createdAt = null)
    {
        var rule = AddRule(NotificationRule.ForEverything(channel, NotificationEventKinds.All, Now));
        var notification = Notification.Create(rule, FailedDeployment(), "Deployment of web failed", createdAt ?? Now);
        Notifications.Add(notification);
        return notification;
    }
}

/// <summary>What the notification watches need from DI: a scope with their notifiers over the fixture's repositories.</summary>
internal sealed class NotifierScopes(NotificationFixture fixture) : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    public IServiceProvider ServiceProvider => this;

    public IServiceScope CreateScope() => this;

    public object? GetService(Type serviceType) =>
        serviceType == typeof(AgentConnectivityNotifier)
            ? new AgentConnectivityNotifier(fixture.Dispatcher(), fixture.Environments, NullLogger<AgentConnectivityNotifier>.Instance)
            : serviceType == typeof(ContainerIncidentNotifier)
                ? new ContainerIncidentNotifier(
                    fixture.Dispatcher(), fixture.Environments, fixture.Stacks, NullLogger<ContainerIncidentNotifier>.Instance)
                : null;

    public void Dispose() { }
}
