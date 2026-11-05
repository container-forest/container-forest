using System.Net;
using System.Net.Http.Json;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>The environment administrator and the compose policy end to end (ADR-0012).</summary>
public class ComposePolicyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Environment_administrator_manages_the_environment_but_not_its_administrators()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();
        await app.CreateUserAsync("owner", isAdmin: false);
        await app.CreateUserAsync("alice", isAdmin: false);
        var admin = app.CreateClient();
        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            admin.PostAsJsonAsync($"/api/environments/{environment.Id}/role-assignments", new { userName = "owner", stackId = (Guid?)null, role = "EnvironmentAdmin" }, Ct));
        var owner = app.CreateClientAs("owner");

        var listed = await owner.GetFromJsonAsync<EnvironmentView[]>("/api/environments", AggregatorApp.Json, Ct);
        Assert.True(Assert.Single(listed!).CanAdminister);
        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            owner.PostAsJsonAsync($"/api/environments/{environment.Id}/role-assignments", new { userName = "alice", stackId = (Guid?)null, role = "Editor" }, Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            owner.PostAsJsonAsync($"/api/environments/{environment.Id}/role-assignments", new { userName = "alice", stackId = (Guid?)null, role = "EnvironmentAdmin" }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, owner.PostAsync($"/api/environments/{environment.Id}/enrollment-token", null, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, owner.DeleteAsync($"/api/environments/{environment.Id}", Ct));

        var alice = app.CreateClientAs("alice");
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.GetAsync($"/api/environments/{environment.Id}/role-assignments", Ct));
    }

    [Fact]
    public async Task Enforced_policy_stops_a_privileged_stack_until_an_allowance_lets_it_through()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        app.Git.Snapshot = new GitSnapshot(
            CommitSha.Parse(Sha),
            [new SourceFile(StackFilePath.Parse("compose.yaml"), Encoding.UTF8.GetBytes("services:\n  web:\n    image: nginx:1.27\n    privileged: true\n"), IsExecutable: false)],
            []);
        var client = app.CreateClient();
        var stackId = await CreateStackAsync(client, agent.EnvironmentId);

        var policy = await client.PutAsJsonAsync(
            $"/api/environments/{agent.EnvironmentId}/compose-policy",
            new { mode = "Enforce", dataRoot = "/var/lib/container-forest/data", systemReadOnlyPaths = new[] { "/etc/localtime" }, allowances = Array.Empty<string>() },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, policy);

        // Rejected: the agent renders, but nothing is deployed.
        var rejecting = DeployAsync(client, stackId);
        await RenderAsync(agent, privileged: true);
        var rejected = await rejecting;
        Assert.Equal("Failed", rejected.Status);
        Assert.Contains("allowance 'privileged'", rejected.Error, StringComparison.Ordinal);

        // With the stack's allowance it goes through, compose reads only the bundle's .env and the agent checks the mounts.
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK, client.PutAsJsonAsync($"/api/stacks/{stackId}/policy-allowances", new { allowances = new[] { "privileged" } }, Ct));
        var deploying = DeployAsync(client, stackId);
        await RenderAsync(agent, privileged: true);
        var deploy = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.DeployStack, deploy.BodyCase);
        Assert.True(deploy.DeployStack.ExplicitEnvFile);
        Assert.Equal(["/var/lib/container-forest/data/web"], deploy.DeployStack.VerifyPaths);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = deploy.RequestId } });
        Assert.Equal("Succeeded", (await deploying).Status);

        // The report checks the deployed version against the policy as it is now.
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK, client.PutAsJsonAsync($"/api/stacks/{stackId}/policy-allowances", new { allowances = Array.Empty<string>() }, Ct));
        var report = await client.GetFromJsonAsync<ReportView[]>($"/api/environments/{agent.EnvironmentId}/compose-policy/report", AggregatorApp.Json, Ct);
        var row = Assert.Single(report!);
        Assert.True(row.Checked);
        Assert.Equal("privileged", Assert.Single(row.Violations).Allowance);

        var audit = await client.GetStringAsync("/api/audit?category=Stacks", Ct);
        Assert.Contains("StackPolicyAllowancesChanged", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editors_read_the_policy_but_only_administrators_change_it()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync();
        var editor = await app.CreateUserAsync("editor", isAdmin: false);
        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            app.CreateClient().PostAsJsonAsync($"/api/users/{editor}/role-assignments", new { environmentId = environment.Id, stackId = (Guid?)null, role = "Editor" }, Ct));
        var client = app.CreateClientAs("editor");

        var read = await client.GetFromJsonAsync<PolicyView>($"/api/environments/{environment.Id}/compose-policy", AggregatorApp.Json, Ct);
        Assert.Equal(("Off", false), (read!.Mode, read.CanChange));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            client.PutAsJsonAsync(
                $"/api/environments/{environment.Id}/compose-policy",
                new { mode = "Off", dataRoot = "/var/lib/container-forest/data", systemReadOnlyPaths = Array.Empty<string>(), allowances = Array.Empty<string>() },
                Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest,
            app.CreateClient().PutAsJsonAsync(
                $"/api/environments/{environment.Id}/compose-policy",
                new { mode = "Audit", dataRoot = "/etc/data", systemReadOnlyPaths = Array.Empty<string>(), allowances = Array.Empty<string>() },
                Ct));
    }

    private static async Task RenderAsync(FakeAgent agent, bool privileged)
    {
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.RenderStack, command.BodyCase);
        var model = """
            {"services":{"web":{"image":"nginx:1.27","privileged":PRIVILEGED,
              "volumes":[{"type":"bind","source":"/var/lib/container-forest/data/web","target":"/data"}]}}}
            """.Replace("PRIVILEGED", privileged ? "true" : "false", StringComparison.Ordinal);
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                RenderedStack = new RenderedStack
                {
                    ModelJson = model,
                    StackDirectory = "/opt/container-forest/stacks/web",
                    AgentDirectories = { "/opt/container-forest", "/run/container-forest" },
                },
            },
        });
    }

    private static async Task<Guid> CreateStackAsync(HttpClient client, Guid environmentId)
    {
        var repository = await client.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        var stack = await client.PostAsJsonAsync(
            $"/api/environments/{environmentId}/stacks", new { name = "web", repositoryId, branch = "main", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        return (await stack.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
    }

    private static async Task<DeploymentView> DeployAsync(HttpClient client, Guid stackId)
    {
        var response = await client.PostAsJsonAsync($"/api/stacks/{stackId}/deployments", new { pull = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);
        var id = (await response.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var deployment = await client.GetFromJsonAsync<DeploymentView>($"/api/deployments/{id}", AggregatorApp.Json, Ct);
            if (deployment!.Status is "Succeeded" or "Failed" or "Cancelled")
            {
                return deployment;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Deployment {id} did not finish.");
    }

    private sealed record IdView(Guid Id);

    private sealed record EnvironmentView(Guid Id, bool CanAdminister);

    private sealed record DeploymentView(Guid Id, string Status, string? Error);

    private sealed record PolicyView(string Mode, bool CanChange);

    private sealed record ViolationView(string? Service, string Message, string? Allowance);

    private sealed record ReportView(Guid StackId, bool Checked, ViolationView[] Violations);
}
