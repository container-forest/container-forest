using System.Net;
using System.Net.Http.Json;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class UnmanagedStackEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UnmanagedStack Caddy = new()
    {
        Name = "caddy", Directory = "/srv/docker/caddy", ConfigFiles = { "/srv/docker/caddy/compose.yaml" }, HasContainers = true,
    };

    [Fact]
    public async Task Admin_lists_unmanaged_stacks_from_the_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().GetFromJsonAsync<List<UnmanagedStackView>>(
            $"/api/environments/{agent.EnvironmentId}/unmanaged-stacks", AggregatorApp.Json, Ct);
        await agent.AnswerUnmanagedStacksAsync(Caddy);

        var stack = Assert.Single((await request)!);
        Assert.Equal(("caddy", "/srv/docker/caddy", false, true, true, true), (stack.Name, stack.Directory, stack.IsSystem, stack.HasContainers, stack.CanRestart, stack.CanDeploy));
        Assert.Equal(["/srv/docker/caddy/compose.yaml"], stack.ConfigFiles);
    }

    [Fact]
    public async Task Admin_takes_an_unmanaged_stack_down_and_gets_the_output()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().PostAsync(
            $"/api/environments/{agent.EnvironmentId}/unmanaged-stacks/caddy/down?timeoutSeconds=3", content: null, Ct);
        await agent.AnswerUnmanagedStacksAsync(Caddy);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.ControlUnmanagedStack, command.BodyCase);
        Assert.Equal(
            ("caddy", UnmanagedStackOperation.Down, 3),
            (command.ControlUnmanagedStack.StackName, command.ControlUnmanagedStack.Operation, command.ControlUnmanagedStack.TimeoutSeconds));
        await agent.SendAsync(new AgentMessage { Progress = new CommandProgress { RequestId = command.RequestId, Line = "Container caddy Removed" } });
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<StackCommandView>(AggregatorApp.Json, Ct))!;
        Assert.True(result.Succeeded);
        Assert.Equal(["Container caddy Removed"], result.Output);
    }

    [Fact]
    public async Task A_system_stack_is_refused_before_compose_runs()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().PostAsync($"/api/environments/{agent.EnvironmentId}/unmanaged-stacks/forest/restart", content: null, Ct);
        await agent.AnswerUnmanagedStacksAsync(new UnmanagedStack { Name = "forest", Directory = "/srv/docker/forest", System = true });

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, request);
    }

    [Fact]
    public async Task Admin_removes_a_stopped_container()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().PostAsync($"/api/environments/{agent.EnvironmentId}/containers/c1/remove", content: null, Ct);
        var list = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = list.RequestId,
                Containers = new ContainerList { Items = { new ContainerInfo { Id = "c1", Name = "old-1", State = ContainerState.Exited } } },
            },
        });
        var command = await agent.ReceiveAsync();
        Assert.Equal(("c1", ControllerMessage.BodyOneofCase.RemoveContainer), (command.RemoveContainer.ContainerId, command.BodyCase));
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, request);
    }

    private sealed record UnmanagedStackView(
        string Name, string Directory, IReadOnlyList<string> ConfigFiles, bool IsSystem, bool HasContainers, bool CanRestart, bool CanDeploy);

    private sealed record StackCommandView(bool Succeeded, string? Error, IReadOnlyList<string> Output);
}
