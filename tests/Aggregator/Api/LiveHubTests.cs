using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ContainerForest.Contracts.Agent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ContainerForest.Aggregator.Api.Tests;

public class LiveHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Panel_hears_that_containers_of_its_environment_changed()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await using var live = await LiveClient.ConnectAsync(app, AggregatorApp.AdminUserName);

        var request = app.CreateClient().PostAsync($"/api/environments/{agent.EnvironmentId}/containers/c1/restart", null, Ct);
        await AnswerListAsync(agent);
        var command = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, request);

        await live.WaitForAsync($"ContainersChanged {agent.EnvironmentId}");
    }

    [Fact]
    public async Task Burst_of_container_events_from_the_agent_is_one_update()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await using var live = await LiveClient.ConnectAsync(app, AggregatorApp.AdminUserName);

        // A compose up: every container reports create and start.
        foreach (var (id, action) in new[] { ("c1", "create"), ("c2", "create"), ("c1", "start"), ("c2", "start") })
        {
            await agent.SendAsync(new AgentMessage { ContainerEvent = new ContainerEvent { ContainerId = id, Action = action } });
        }

        var expected = $"ContainersChanged {agent.EnvironmentId}";
        await live.WaitForAsync(expected);
        await Task.Delay(ContainerForest.Aggregator.Infrastructure.Agents.ContainerEventCoalescer.Window * 2, Ct);
        Assert.Single(live.Events, e => e == expected);
    }

    [Fact]
    public async Task Panel_follows_a_deployment_from_queue_to_finish()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var repository = await (await client.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct))
            .Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct);
        var stack = await CreateStackAsync(client, agent.EnvironmentId, repository!.Id, "web");
        app.Git.Snapshot = new Application.Deployments.GitSnapshot(
            Domain.Stacks.CommitSha.Parse("0123456789abcdef0123456789abcdef01234567"),
            [new(Domain.Stacks.StackFilePath.Parse("compose.yaml"), "services:\n  web:\n    image: nginx:1.27\n"u8.ToArray(), IsExecutable: false)],
            []);
        await using var live = await LiveClient.ConnectAsync(app, AggregatorApp.AdminUserName);

        var queued = await (await client.PostAsJsonAsync($"/api/stacks/{stack}/deployments", new { pull = false }, Ct))
            .Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct);
        var expected = $"DeploymentChanged {agent.EnvironmentId} {stack} {queued!.Id}";
        await live.WaitForAsync(expected);
        var command = await agent.ReceiveAsync();
        var beforeFinish = live.Events.Count(e => e == expected);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        // The run reports every step; the last report comes with the finished deployment.
        for (var attempt = 0; attempt < 100 && live.Events.Count(e => e == expected) <= beforeFinish; attempt++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(live.Events.Count(e => e == expected) > beforeFinish);
        await live.WaitForAsync($"ContainersChanged {agent.EnvironmentId}");
    }

    [Fact]
    public async Task Updates_of_a_private_environment_reach_only_those_who_see_it()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var admin = app.CreateClient();
        var environment = await (await admin.PostAsJsonAsync("/api/environments", new { name = "prod", isPrivate = true }, Ct))
            .Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct);
        var repository = await (await admin.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct))
            .Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct);
        await using var adminLive = await LiveClient.ConnectAsync(app, AggregatorApp.AdminUserName);
        await using var aliceLive = await LiveClient.ConnectAsync(app, "alice");

        var web = await CreateStackAsync(admin, environment!.Id, repository!.Id, "web");
        await adminLive.WaitForAsync($"StackChanged {environment.Id} {web}");
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(aliceLive.Events, e => e.StartsWith("StackChanged", StringComparison.Ordinal));

        // A new role changes what alice sees: everyone hears it, and her panel subscribes again.
        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            admin.PostAsJsonAsync($"/api/users/{aliceId}/role-assignments", new { environmentId = environment.Id, stackId = (Guid?)null, role = "Viewer" }, Ct));
        await aliceLive.WaitForAsync("EnvironmentsChanged");
        await aliceLive.Connection.InvokeAsync("Resubscribe", Ct);

        var api = await CreateStackAsync(admin, environment.Id, repository.Id, "api");
        await aliceLive.WaitForAsync($"StackChanged {environment.Id} {api}");
    }

    [Fact]
    public async Task Unknown_user_cannot_connect()
    {
        await using var app = new AggregatorApp();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => LiveClient.ConnectAsync(app, "nobody"));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private static async Task<Guid> CreateStackAsync(HttpClient client, Guid environmentId, Guid repositoryId, string name)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/environments/{environmentId}/stacks", new { name, repositoryId, branch = "main", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
    }

    private static async Task AnswerListAsync(FakeAgent agent)
    {
        var list = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = list.RequestId,
                Containers = new ContainerList
                {
                    Items = { new ContainerInfo { Id = "c1", Name = "web-app-1", State = ContainerState.Running, Image = "nginx:1.27" } },
                },
            },
        });
    }

    private sealed record IdView(Guid Id);

    /// <summary>A panel's connection to /hubs/live, recording every event as "Name id…".</summary>
    private sealed class LiveClient : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> events = new();

        private LiveClient(HubConnection connection)
        {
            Connection = connection;
            connection.Closed += ex => { closedWith = ex; return Task.CompletedTask; };
            connection.On("EnvironmentsChanged", () => events.Enqueue("EnvironmentsChanged"));
            connection.On<Guid>("ContainersChanged", e => events.Enqueue($"ContainersChanged {e}"));
            connection.On<Guid, Guid>("StackChanged", (e, s) => events.Enqueue($"StackChanged {e} {s}"));
            connection.On<Guid, Guid, Guid>("DeploymentChanged", (e, s, d) => events.Enqueue($"DeploymentChanged {e} {s} {d}"));
        }

        private Exception? closedWith;

        public HubConnection Connection { get; }

        public IReadOnlyCollection<string> Events => events;

        // Long polling: the test server has no WebSockets for this client, and the hub behaves the same.
        public static async Task<LiveClient> ConnectAsync(AggregatorApp app, string userName)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/live"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    options.Headers[AggregatorApp.DevUserHeader] = userName;
                })
                .Build();
            var client = new LiveClient(connection);
            await connection.StartAsync(TestContext.Current.CancellationToken);

            // StartAsync returns once the handshake is answered, before the hub's OnConnectedAsync has joined the
            // environment groups; an event published in between would be lost. Invocations run after OnConnectedAsync,
            // so this one returns only when the connection is subscribed.
            await connection.InvokeAsync("Resubscribe", TestContext.Current.CancellationToken);
            return client;
        }

        public async Task WaitForAsync(string expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (events.Contains(expected))
                {
                    return;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.Fail($"No '{expected}' among: {string.Join("; ", events)}; state {Connection.State}, closed: {closedWith}");
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
