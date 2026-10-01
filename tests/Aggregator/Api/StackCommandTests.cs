using System.Net;
using System.Net.Http.Json;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class StackCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_is_restarted_through_the_agent_with_its_output()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var stackId = await CreateStackAsync(app, agent);

        var request = app.CreateClient().PostAsync($"/api/dev/stacks/{stackId}/restart?timeoutSeconds=30", content: null, Ct);

        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.RestartStack, command.BodyCase);
        Assert.Equal("web", command.RestartStack.StackName);
        Assert.Equal(30, command.RestartStack.TimeoutSeconds);
        await agent.SendAsync(new AgentMessage { Progress = new CommandProgress { RequestId = command.RequestId, Line = "Container web-app-1 Started" } });
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        var response = await request;
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var result = await response.Content.ReadFromJsonAsync<ResultView>(AggregatorApp.Json, Ct);
        Assert.True(result!.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(["Container web-app-1 Started"], result.Output);
    }

    [Fact]
    public async Task Failed_down_answers_with_the_compose_error_and_output()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var stackId = await CreateStackAsync(app, agent);

        var request = app.CreateClient().PostAsync($"/api/dev/stacks/{stackId}/down", content: null, Ct);

        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.DownStack, command.BodyCase);
        Assert.Equal(10, command.DownStack.TimeoutSeconds);
        await agent.SendAsync(new AgentMessage { Progress = new CommandProgress { RequestId = command.RequestId, Line = "network web_default has active endpoints" } });
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Error = new CommandError { Code = ErrorCode.CommandFailed, Message = "docker compose down exited with code 1; see the command output." },
            },
        });

        var response = await request;
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var result = await response.Content.ReadFromJsonAsync<ResultView>(AggregatorApp.Json, Ct);
        Assert.False(result!.Succeeded);
        Assert.Equal("docker compose down exited with code 1; see the command output.", result.Error);
        Assert.Equal(["network web_default has active endpoints"], result.Output);
    }

    [Fact]
    public async Task Stack_the_agent_has_not_deployed_is_not_found()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var stackId = await CreateStackAsync(app, agent);

        var request = app.CreateClient().PostAsync($"/api/dev/stacks/{stackId}/restart", content: null, Ct);

        var command = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Error = new CommandError { Code = ErrorCode.NotFound, Message = "Stack 'web' is not deployed on this host." },
            },
        });

        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, request);
    }

    [Fact]
    public async Task Viewer_may_not_restart_and_unknown_actions_are_rejected()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        var stackId = await CreateStackAsync(app, agent);

        // Public environments give an implicit Viewer role.
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden, app.CreateClientAs("alice").PostAsync($"/api/dev/stacks/{stackId}/restart", content: null, Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest, app.CreateClient().PostAsync($"/api/dev/stacks/{stackId}/remove", content: null, Ct));
    }

    private static async Task<Guid> CreateStackAsync(AggregatorApp app, FakeAgent agent)
    {
        var client = app.CreateClient();
        var repository = await client.PostAsJsonAsync(
            "/api/dev/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;

        var stack = await client.PostAsJsonAsync(
            $"/api/dev/environments/{agent.EnvironmentId}/stacks",
            new { name = "web", repositoryId, branch = "main", composePath = "compose.yaml" },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        return (await stack.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
    }

    private sealed record IdView(Guid Id);

    private sealed record ResultView(bool Succeeded, string? Error, string[] Output);
}
