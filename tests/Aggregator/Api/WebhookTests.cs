using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class WebhookTests
{
    private const string First = "0123456789abcdef0123456789abcdef01234567";
    private const string Second = "fedcba9876543210fedcba9876543210fedcba98";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signed_push_deploys_the_stacks_that_opted_in()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var (repositoryId, secret) = await CreateRepositoryWithWebhookAsync(client);
        var stackId = await CreateStackAsync(client, agent.EnvironmentId, repositoryId, "web", deployOnPush: true);
        await CreateStackAsync(client, agent.EnvironmentId, repositoryId, "manual", deployOnPush: false);
        app.Git.Snapshot = Snapshot(First);

        var response = await SendPushAsync(app, repositoryId, secret, First);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var result = await response.Content.ReadFromJsonAsync<WebhookView>(AggregatorApp.Json, Ct);
        Assert.Equal(("GitHub", "Push"), (result!.Provider, result.Outcome));
        var stack = Assert.Single(result.Stacks);
        Assert.Equal(("web", "Queued"), (stack.Name, stack.Outcome));

        var command = await agent.ReceiveAsync();
        Assert.Equal(("web", First), (command.DeployStack.StackName, command.DeployStack.CommitSha));
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
        var history = await WaitForFinishedAsync(client, stackId);
        Assert.Equal(("Webhook", "Succeeded", stack.DeploymentId), (history[0].Trigger, history[0].Status, (Guid?)history[0].Id));
    }

    [Fact]
    public async Task Organization_webhook_of_a_connection_deploys_the_pushed_repository()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var connection = await client.PostAsJsonAsync(
            "/api/git-connections",
            new { name = "team", urlPrefix = "https://gitea.example.com/team/", allEnvironments = true },
            Ct);
        var connectionId = (await connection.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        var secret = (await (await client.PostAsync($"/api/git-connections/{connectionId}/webhook", null, Ct))
            .Content.ReadFromJsonAsync<ConnectionSecretView>(AggregatorApp.Json, Ct))!.Secret;
        var repository = await client.PostAsJsonAsync(
            "/api/repositories", new { name = "app", url = "https://gitea.example.com/team/app.git", connectionId }, Ct);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        await CreateStackAsync(client, agent.EnvironmentId, repositoryId, "app", deployOnPush: true);
        app.Git.Snapshot = Snapshot(First);

        var body = Encoding.UTF8.GetBytes(
            $$$"""{"ref":"refs/heads/main","after":"{{{First}}}","repository":{"clone_url":"https://gitea.example.com/team/app.git"}}""");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/connections/{connectionId}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Gitea-Event", "push");
        request.Headers.Add("X-Gitea-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
        var response = await app.CreateClientAs(null).SendAsync(request, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var result = await response.Content.ReadFromJsonAsync<WebhookView>(AggregatorApp.Json, Ct);
        Assert.Equal(("Gitea", "Push", "Queued"), (result!.Provider, result.Outcome, Assert.Single(result.Stacks).Outcome));
        var command = await agent.ReceiveAsync();
        Assert.Equal(("app", First), (command.DeployStack.StackName, command.DeployStack.CommitSha));
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
    }

    [Fact]
    public async Task Push_during_a_deployment_is_deployed_after_it()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var (repositoryId, secret) = await CreateRepositoryWithWebhookAsync(client);
        var stackId = await CreateStackAsync(client, agent.EnvironmentId, repositoryId, "web", deployOnPush: true);
        app.Git.Snapshot = Snapshot(First);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, SendPushAsync(app, repositoryId, secret, First));
        var first = await agent.ReceiveAsync();

        // The first deployment has fetched its commit; a second push arrives while compose runs.
        app.Git.Snapshot = Snapshot(Second);
        var during = await (await SendPushAsync(app, repositoryId, secret, Second)).Content.ReadFromJsonAsync<WebhookView>(AggregatorApp.Json, Ct);
        Assert.Equal("AfterRunning", during!.Stacks.Single().Outcome);

        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = first.RequestId } });
        var second = await agent.ReceiveAsync();
        Assert.Equal(Second, second.DeployStack.CommitSha);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = second.RequestId } });

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var history = await client.GetFromJsonAsync<DeploymentView[]>($"/api/stacks/{stackId}/deployments", AggregatorApp.Json, Ct);
            if (history is [{ Status: "Succeeded", Commit: Second }, { Status: "Succeeded", Commit: First }])
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The push that arrived during the deployment was not deployed after it.");
    }

    [Fact]
    public async Task Wrong_signature_and_unknown_repository_are_the_same_401()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();
        var (repositoryId, _) = await CreateRepositoryWithWebhookAsync(client);

        var wrong = await SendPushAsync(app, repositoryId, "guessed", First);
        var unknown = await SendPushAsync(app, Guid.NewGuid(), "guessed", First);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, wrong);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, unknown);
        Assert.Equal(
            await wrong.Content.ReadFromJsonAsync<ProblemView>(AggregatorApp.Json, Ct),
            await unknown.Content.ReadFromJsonAsync<ProblemView>(AggregatorApp.Json, Ct));
    }

    [Fact]
    public async Task New_secret_replaces_the_old_one_and_removing_it_stops_deliveries()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();
        var (repositoryId, oldSecret) = await CreateRepositoryWithWebhookAsync(client);

        var renewed = await client.PostAsync($"/api/repositories/{repositoryId}/webhook", null, Ct);
        var newSecret = (await renewed.Content.ReadFromJsonAsync<ConnectionSecretView>(AggregatorApp.Json, Ct))!.Secret;
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, SendPingAsync(app, repositoryId, oldSecret));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, SendPingAsync(app, repositoryId, newSecret));

        var removed = await client.DeleteAsync($"/api/repositories/{repositoryId}/webhook", Ct);
        Assert.False((await removed.Content.ReadFromJsonAsync<RepositoryView>(AggregatorApp.Json, Ct))!.HasWebhook);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, SendPingAsync(app, repositoryId, newSecret));
    }

    [Fact]
    public async Task Only_administrators_manage_the_secret_and_only_editors_turn_deploy_on_push_on()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var (repositoryId, _) = await CreateRepositoryWithWebhookAsync(client);
        var stackId = await CreateStackAsync(client, agent.EnvironmentId, repositoryId, "web", deployOnPush: false);
        var userId = await app.CreateUserAsync("olga", isAdmin: false);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, client.PostAsJsonAsync(
            $"/api/users/{userId}/role-assignments", new { environmentId = agent.EnvironmentId, stackId = (Guid?)null, role = "Operator" }, Ct));
        var operatorClient = app.CreateClientAs("olga");

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, operatorClient.PostAsync($"/api/repositories/{repositoryId}/webhook", null, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, operatorClient.DeleteAsync($"/api/repositories/{repositoryId}/webhook", Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden, operatorClient.PutAsJsonAsync($"/api/stacks/{stackId}/deploy-on-push", new { enabled = true }, Ct));
    }

    [Fact]
    public async Task Body_that_is_not_json_is_400_after_the_signature_check()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();
        var (repositoryId, secret) = await CreateRepositoryWithWebhookAsync(client);

        var response = await SendAsync(app, repositoryId, secret, "push", "payload=%7B%7D");

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, response);
    }

    private static Task<HttpResponseMessage> SendPushAsync(AggregatorApp app, Guid repositoryId, string secret, string after) =>
        SendAsync(app, repositoryId, secret, "push", $$"""{"ref":"refs/heads/main","after":"{{after}}"}""");

    private static Task<HttpResponseMessage> SendPingAsync(AggregatorApp app, Guid repositoryId, string secret) =>
        SendAsync(app, repositoryId, secret, "ping", """{"zen":"Keep it logically awesome."}""");

    private static Task<HttpResponseMessage> SendAsync(AggregatorApp app, Guid repositoryId, string secret, string gitHubEvent, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{repositoryId}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-GitHub-Event", gitHubEvent);
        request.Headers.Add("X-Hub-Signature-256", $"sha256={signature}");

        // Anonymous, like the git server.
        return app.CreateClientAs(null).SendAsync(request, Ct);
    }

    private static async Task<(Guid Id, string Secret)> CreateRepositoryWithWebhookAsync(HttpClient client)
    {
        var repository = await client.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var id = (await repository.Content.ReadFromJsonAsync<RepositoryView>(AggregatorApp.Json, Ct))!.Id;

        var webhook = await client.PostAsync($"/api/repositories/{id}/webhook", null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, webhook);
        var created = (await webhook.Content.ReadFromJsonAsync<SecretView>(AggregatorApp.Json, Ct))!;
        Assert.True(created.Repository.HasWebhook);
        return (id, created.Secret);
    }

    private static async Task<Guid> CreateStackAsync(HttpClient client, Guid environmentId, Guid repositoryId, string name, bool deployOnPush)
    {
        var stack = await client.PostAsJsonAsync(
            $"/api/environments/{environmentId}/stacks", new { name, repositoryId, branch = "main", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        var id = (await stack.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.Id;

        var changed = await client.PutAsJsonAsync($"/api/stacks/{id}/deploy-on-push", new { enabled = deployOnPush }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, changed);
        Assert.Equal(deployOnPush, (await changed.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.DeployOnPush);
        return id;
    }

    private static async Task<DeploymentView[]> WaitForFinishedAsync(HttpClient client, Guid stackId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var history = await client.GetFromJsonAsync<DeploymentView[]>($"/api/stacks/{stackId}/deployments", AggregatorApp.Json, Ct);
            if (history is [{ Status: "Succeeded" or "Failed" }, ..])
            {
                return history;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No finished deployment of stack {stackId}.");
    }

    private static GitSnapshot Snapshot(string sha) => new(
        CommitSha.Parse(sha),
        [new SourceFile(StackFilePath.Parse("compose.yaml"), Encoding.UTF8.GetBytes("services:\n  web:\n    image: nginx:1.27\n"), IsExecutable: false)],
        []);

    private sealed record ProblemView(int Status, string Title, string Detail);

    private sealed record RepositoryView(Guid Id, bool HasWebhook);

    private sealed record SecretView(RepositoryView Repository, string Secret);

    private sealed record StackView(Guid Id, bool DeployOnPush);

    private sealed record IdView(Guid Id);

    private sealed record ConnectionSecretView(string Secret);

    private sealed record WebhookView(string Provider, string Outcome, string? Detail, IReadOnlyList<WebhookStackView> Stacks);

    private sealed record WebhookStackView(Guid StackId, string Name, string Outcome, Guid? DeploymentId);

    private sealed record DeploymentView(Guid Id, string Status, string Trigger, string? Commit);
}
