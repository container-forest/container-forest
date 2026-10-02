using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Contracts.Agent;
using Grpc.Core;

namespace ContainerForest.Aggregator.Api.Tests;

public class AgentGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_containers_through_a_connected_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().GetAsync($"/api/environments/{agent.EnvironmentId}/containers", Ct);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.ListContainers, command.BodyCase);

        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Containers = new ContainerList
                {
                    Items =
                    {
                        new ContainerInfo
                        {
                            Id = "abc123", Name = "web-app-1", State = ContainerState.Running, Image = "nginx:1.27",
                            StackName = "web", ServiceName = "app", StatusText = "Up 3 minutes",
                        },
                    },
                },
            },
        });

        var response = await request;
        response.EnsureSuccessStatusCode();
        var containers = await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        var container = Assert.Single(containers.EnumerateArray());
        Assert.Equal("web-app-1", container.GetProperty("name").GetString());
        Assert.Equal("Running", container.GetProperty("state").GetString());
        Assert.Equal("web", container.GetProperty("stackName").GetString());
    }

    [Fact]
    public async Task Connection_is_recorded_on_the_environment()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var environments = await app.CreateClient().GetFromJsonAsync<AggregatorApp.EnvironmentView[]>(
            "/api/environments", AggregatorApp.Json, Ct);

        var environment = Assert.Single(environments!);
        Assert.True(environment.IsEnrolled);
        Assert.Equal("test-host", environment.AgentHostname);
        Assert.Equal("0.0.1", environment.AgentVersion);
    }

    [Fact]
    public async Task Enrollment_token_works_only_once()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();
        var request = new EnrollRequest { EnrollmentToken = environment.EnrollmentToken, Hostname = "host" };

        await app.Gateway.EnrollAsync(request, cancellationToken: Ct);
        var error = await Assert.ThrowsAsync<RpcException>(async () => await app.Gateway.EnrollAsync(request, cancellationToken: Ct));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task Revoked_agent_is_disconnected_and_cannot_connect_again()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();
        var credentials = await app.Gateway.EnrollAsync(new EnrollRequest { EnrollmentToken = environment.EnrollmentToken }, cancellationToken: Ct);
        await using (await FakeAgent.ConnectAsync(app.Gateway, credentials, "boot-1"))
        {
            await app.WaitUntilOnlineAsync(environment.Id);

            await HttpAssert.StatusAsync(
                HttpStatusCode.NoContent, app.CreateClient().PostAsync($"/api/environments/{environment.Id}/revoke-agent", content: null, Ct));
            await app.WaitUntilOnlineAsync(environment.Id, online: false);
        }

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await using var agent = await FakeAgent.ConnectAsync(app.Gateway, credentials, "boot-1");
            await agent.ReceiveAsync();
        });
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        var listed = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/environments", AggregatorApp.Json, Ct);
        Assert.False(listed[0].GetProperty("isEnrolled").GetBoolean());
    }

    [Fact]
    public async Task Wrong_secret_is_rejected()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();
        var credentials = await app.Gateway.EnrollAsync(new EnrollRequest { EnrollmentToken = environment.EnrollmentToken }, cancellationToken: Ct);

        // The aggregator can reject the stream before the Hello is written, so the error comes from
        // either the write or the read.
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await using var agent = await FakeAgent.ConnectAsync(
                app.Gateway, new EnrollResponse { AgentId = credentials.AgentId, AgentSecret = "wrong" }, "boot-1");
            await agent.ReceiveAsync();
        });
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task First_message_must_be_hello()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();
        var credentials = await app.Gateway.EnrollAsync(new EnrollRequest { EnrollmentToken = environment.EnrollmentToken }, cancellationToken: Ct);

        using var call = app.Gateway.Connect(FakeAgent.Credentials(credentials.AgentId, credentials.AgentSecret), cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, Ct);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext(Ct));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Command_to_an_offline_agent_fails_at_once()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();

        var response = await app.CreateClient().GetAsync(
            $"/api/environments/{environment.Id}/containers", Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.ServiceUnavailable, response);
    }

    [Fact]
    public async Task Agent_error_becomes_a_problem_response()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().GetAsync($"/api/environments/{agent.EnvironmentId}/containers", Ct);
        var command = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Error = new CommandError { Code = ErrorCode.DockerUnavailable, Message = "Docker socket unreachable" },
            },
        });

        var response = await request;
        await HttpAssert.StatusAsync(HttpStatusCode.BadGateway, response);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.Equal("Docker socket unreachable", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Command_the_agent_does_not_support_is_not_implemented()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().GetAsync($"/api/environments/{agent.EnvironmentId}/containers", Ct);
        var command = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Error = new CommandError { Code = ErrorCode.Unsupported, Message = "This agent version does not support the command." },
            },
        });

        var response = await request;
        await HttpAssert.StatusAsync(HttpStatusCode.NotImplemented, response);
    }

    [Fact]
    public async Task Disconnect_fails_the_waiting_command()
    {
        await using var app = new AggregatorApp();
        var agent = await app.EnrollAndConnectAsync();

        var request = app.CreateClient().GetAsync($"/api/environments/{agent.EnvironmentId}/containers", Ct);
        await agent.ReceiveAsync();
        await agent.DisposeAsync();

        var response = await request;
        await HttpAssert.StatusAsync(HttpStatusCode.ServiceUnavailable, response);
        await app.WaitUntilOnlineAsync(agent.EnvironmentId, online: false);
    }
}
