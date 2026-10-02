using System.Net;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class ContainerCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_restarts_a_container_through_the_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = Send(app, agent, "c1", "restart", AggregatorApp.AdminUserName);
        await AnswerListAsync(agent);

        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.RestartContainer, command.BodyCase);
        Assert.Equal("c1", command.RestartContainer.ContainerId);
        Assert.Equal(10, command.RestartContainer.TimeoutSeconds);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, request);
    }

    [Fact]
    public async Task User_without_a_role_on_a_public_environment_is_forbidden()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();

        var request = Send(app, agent, "c1", "stop", "alice");
        await AnswerListAsync(agent);

        // Public environments give an implicit Viewer role, which may see containers but not stop them.
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, request);
    }

    [Fact]
    public async Task Unknown_dev_user_is_unauthorized()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var response = await Send(app, agent, "c1", "restart", "nobody");

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, response);
    }

    private static Task<HttpResponseMessage> Send(AggregatorApp app, FakeAgent agent, string containerId, string action, string user)
    {
        return app.CreateClientAs(user).PostAsync(
            $"/api/environments/{agent.EnvironmentId}/containers/{containerId}/{action}", content: null, Ct);
    }

    private static async Task AnswerListAsync(FakeAgent agent)
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
                    Items = { new ContainerInfo { Id = "c1", Name = "web-app-1", State = ContainerState.Running, Image = "nginx:1.27" } },
                },
            },
        });
    }
}
