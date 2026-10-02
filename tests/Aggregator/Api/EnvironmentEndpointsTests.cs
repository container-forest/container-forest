using System.Net;
using System.Net.Http.Json;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class EnvironmentEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Environments_are_listed_with_their_agent_status()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await app.CreateEnvironmentAsync("archive");

        var environments = (await app.CreateClient().GetFromJsonAsync<EnvironmentView[]>("/api/environments", AggregatorApp.Json, Ct))!;

        Assert.Equal(["archive", "test"], environments.Select(e => e.Name));
        Assert.Equal((false, true), (environments[0].IsOnline, environments[1].IsOnline));
    }

    [Fact]
    public async Task Containers_come_from_the_agent_with_what_the_user_may_do()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClientAs("alice").GetFromJsonAsync<ContainerView[]>(
            $"/api/environments/{agent.EnvironmentId}/containers", AggregatorApp.Json, Ct);
        await AnswerListAsync(agent);

        // Public environments give an implicit Viewer role: containers are visible, not controllable.
        var container = Assert.Single((await request)!);
        Assert.Equal(("web-app-1", "Running", "web", false), (container.Name, container.State, container.StackName, container.CanControl));
    }

    [Fact]
    public async Task System_container_is_marked_and_cannot_be_stopped()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var list = app.CreateClient().GetFromJsonAsync<ContainerView[]>(
            $"/api/environments/{agent.EnvironmentId}/containers", AggregatorApp.Json, Ct);
        await AnswerListAsync(agent, system: true);
        Assert.True(Assert.Single((await list)!).IsSystem);

        var stop = app.CreateClient().PostAsync($"/api/environments/{agent.EnvironmentId}/containers/web-app-1/stop", content: null, Ct);
        await AnswerListAsync(agent, system: true);

        // Refused before anything is sent to the agent.
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, stop);
    }

    [Fact]
    public async Task Stop_reaches_the_agent_with_the_timeout()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var stop = app.CreateClient().PostAsync(
            $"/api/environments/{agent.EnvironmentId}/containers/web-app-1/stop?timeoutSeconds=0", content: null, Ct);
        await AnswerListAsync(agent);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.StopContainer, command.BodyCase);
        Assert.Equal(("c1", 0), (command.StopContainer.ContainerId, command.StopContainer.TimeoutSeconds));
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, stop);
    }

    [Fact]
    public async Task Start_of_a_running_container_is_a_conflict()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var start = app.CreateClient().PostAsync(
            $"/api/environments/{agent.EnvironmentId}/containers/web-app-1/start", content: null, Ct);
        await AnswerListAsync(agent);

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, start);
    }

    [Fact]
    public async Task Viewer_may_not_restart_and_unknown_environments_are_not_found()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        var alice = app.CreateClientAs("alice");

        var restart = alice.PostAsync($"/api/environments/{agent.EnvironmentId}/containers/web-app-1/restart", content: null, Ct);
        await AnswerListAsync(agent);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, restart);

        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, alice.GetAsync($"/api/environments/{Guid.NewGuid()}/containers", Ct));
    }

    [Fact]
    public async Task Logs_come_from_the_agent_in_batches()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().GetFromJsonAsync<LogsView>(
            $"/api/environments/{agent.EnvironmentId}/containers/web-app-1/logs?tail=2", AggregatorApp.Json, Ct);
        await AnswerListAsync(agent);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.StreamContainerLogs, command.BodyCase);
        Assert.Equal(("c1", 2, false), (command.StreamContainerLogs.ContainerId, command.StreamContainerLogs.TailLines, command.StreamContainerLogs.Follow));

        await agent.SendAsync(new AgentMessage
        {
            LogBatch = new LogBatch { RequestId = command.RequestId, Lines = { new LogLine { Stream = LogStream.Stdout, TimestampUnixMs = 1_000, Text = "ready" } } },
        });
        await agent.SendAsync(new AgentMessage
        {
            LogBatch = new LogBatch { RequestId = command.RequestId, Lines = { new LogLine { Stream = LogStream.Stderr, Text = "warning" } } },
        });
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        var logs = (await request)!;
        Assert.Equal([("Stdout", "ready"), ("Stderr", "warning")], logs.Lines.Select(l => (l.Stream, l.Text)));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000), logs.Lines[0].At);
    }

    [Fact]
    public async Task Anonymous_requests_are_unauthorized()
    {
        await using var app = new AggregatorApp();

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, app.CreateClientAs(null).GetAsync("/api/environments", Ct));
    }

    private static async Task AnswerListAsync(FakeAgent agent, bool system = false)
    {
        var list = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.ListContainers, list.BodyCase);
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = list.RequestId,
                Containers = new ContainerList
                {
                    Items =
                    {
                        new ContainerInfo
                        {
                            Id = "c1", Name = "web-app-1", State = ContainerState.Running, Image = "nginx:1.27", StackName = "web", ServiceName = "app",
                            System = system,
                        },
                    },
                },
            },
        });
    }

    private sealed record LogsView(LogLineView[] Lines);

    private sealed record LogLineView(DateTimeOffset? At, string Stream, string Text);

    private sealed record EnvironmentView(Guid Id, string Name, bool IsOnline);

    private sealed record ContainerView(string Id, string Name, string State, string? StackName, bool CanControl, bool IsSystem);
}
