using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class NotificationSettingsServiceTests
{
    private static readonly DateTimeOffset Now = NotificationFixture.Now;
    private const NotificationEventKinds Deployments = NotificationEventKinds.DeploymentSucceeded | NotificationEventKinds.DeploymentFailed;

    private readonly NotificationFixture fixture = new();
    private readonly InMemoryUsers users = new();
    private readonly InMemoryStacks stacks = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly NotificationSettingsService service;

    public NotificationSettingsServiceTests()
    {
        var admin = User.CreateAdministrator("root", Now);
        users.Add(admin);
        currentUser.Id = admin.Id;
        stacks.Add(fixture.Stack);
        service = new NotificationSettingsService(
            new CurrentActor(currentUser, users),
            fixture.Channels,
            fixture.Rules,
            fixture.Notifications,
            fixture.Environments,
            stacks,
            fixture.UnitOfWork,
            new FixedTime(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Channel_keeps_the_full_discord_id()
    {
        var channel = await service.CreateChannelAsync(" alerts ", "1234567890123456789", Ct);

        Assert.Equal(("alerts", 1234567890123456789UL), (channel.Name, channel.DiscordChannelId));
        Assert.Same(channel, Assert.Single(await service.ListChannelsAsync(Ct)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12ab")]
    [InlineData("99999999999999999999")]
    public async Task Discord_id_must_be_a_positive_64_bit_number(string id)
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateChannelAsync("alerts", id, Ct));
    }

    [Fact]
    public async Task Channel_is_renamed_moved_and_disabled()
    {
        var channel = await service.CreateChannelAsync("alerts", "1", Ct);

        await service.UpdateChannelAsync(channel.Id, "deploys", "2", isEnabled: false, Ct);

        Assert.Equal(("deploys", 2UL, false), (channel.Name, channel.DiscordChannelId, channel.IsEnabled));
    }

    [Fact]
    public async Task Rule_scope_follows_what_is_given()
    {
        var channel = await service.CreateChannelAsync("alerts", "1", Ct);

        var everything = await service.CreateRuleAsync(channel.Id, Deployments, null, null, Ct);
        var environment = await service.CreateRuleAsync(channel.Id, Deployments, fixture.Environment.Id, null, Ct);
        var stack = await service.CreateRuleAsync(channel.Id, Deployments, null, fixture.Stack.Id, Ct);

        Assert.Equal((null, null), (everything.EnvironmentName, everything.StackName));
        Assert.Equal(("prod", null), (environment.EnvironmentName, environment.StackName));
        Assert.Equal(("prod", "web"), (stack.EnvironmentName, stack.StackName));
        Assert.Equal(fixture.Environment.Id, stack.Rule.EnvironmentId);
        Assert.Equal(3, (await service.ListRulesAsync(channel.Id, Ct)).Count);
    }

    [Fact]
    public async Task Stack_of_another_environment_is_rejected()
    {
        var channel = await service.CreateChannelAsync("alerts", "1", Ct);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRuleAsync(channel.Id, Deployments, EnvironmentId.New(), fixture.Stack.Id, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.CreateRuleAsync(channel.Id, Deployments, null, StackId.New(), Ct));
    }

    [Fact]
    public async Task Rule_without_events_is_rejected()
    {
        var channel = await service.CreateChannelAsync("alerts", "1", Ct);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRuleAsync(channel.Id, NotificationEventKinds.None, null, null, Ct));
    }

    [Fact]
    public async Task Rule_events_change_and_it_can_be_switched_off_and_deleted()
    {
        var channel = await service.CreateChannelAsync("alerts", "1", Ct);
        var rule = (await service.CreateRuleAsync(channel.Id, Deployments, null, null, Ct)).Rule;

        await service.UpdateRuleAsync(rule.Id, NotificationEventKinds.AgentOffline, isEnabled: false, Ct);
        Assert.Equal((NotificationEventKinds.AgentOffline, false), (rule.Events, rule.IsEnabled));

        await service.DeleteRuleAsync(rule.Id, Ct);
        Assert.Empty(await service.ListRulesAsync(channel.Id, Ct));
    }

    [Fact]
    public async Task History_names_channels_and_keeps_notifications_of_deleted_ones()
    {
        var kept = await service.CreateChannelAsync("alerts", "1", Ct);
        var deleted = await service.CreateChannelAsync("old", "2", Ct);
        foreach (var channel in new[] { kept, deleted })
        {
            var rule = (await service.CreateRuleAsync(channel.Id, Deployments, null, null, Ct)).Rule;
            fixture.Notifications.Add(Notification.Create(
                rule,
                new NotificationEvent(NotificationEventKinds.DeploymentFailed, fixture.Environment.Id, fixture.Stack.Id, Now),
                "web failed",
                Now));
        }

        await service.DeleteChannelAsync(deleted.Id, Ct);
        var history = await service.ListHistoryAsync(limit: null, Ct);

        Assert.Equal(["alerts", null], history.Select(e => e.ChannelName).OrderBy(n => n is null));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ListHistoryAsync(NotificationSettingsService.MaxHistoryLimit + 1, Ct));
    }

    [Fact]
    public async Task Only_administrators_manage_notifications()
    {
        var alice = User.Create("alice", Now);
        users.Add(alice);
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListChannelsAsync(Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateChannelAsync("alerts", "1", Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListHistoryAsync(null, Ct));
    }
}
