using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Signing keys and stacks that deploy only endorsed commits (ADR-0011).</summary>
public class SignedCommitTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINX3MltKY5wuuud0d6OKbOIYmZfhVtfR7HGKjMX7b8BU alice@laptop";

    private const string OtherKey =
        "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBF8MPijiji7Wc21jZbdVfgdYSm72ow5oL724OekIOV7/XMe4q9r76yf131Iaz3pVDg5hVM4sMOfOQEajlPurZIo= ci";

    /// <summary>A git signature by <see cref="PublicKey"/>, made with ssh-keygen.</summary>
    private const string Signature = """
        -----BEGIN SSH SIGNATURE-----
        U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAg1fcyW0pjnC6653R3o4ps4hiZl+
        FW19HscYqMxftvwFQAAAADZ2l0AAAAAAAAAAZzaGE1MTIAAABTAAAAC3NzaC1lZDI1NTE5
        AAAAQNvnQrdkjPedsaxUazhjASDKX1Ih3IncXTdE8UZFBxpbesT6oDLtak9yYr4nUZLBit
        kP+Fw7yxLvHFa7iN5miAU=
        -----END SSH SIGNATURE-----
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task User_manages_own_signing_keys_from_a_session()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();

        var added = await client.PostAsJsonAsync("/api/me/signing-keys", new { name = "laptop", publicKey = PublicKey, environmentIds = (Guid[]?)null }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, added);
        var key = (await added.Content.ReadFromJsonAsync<KeyView>(AggregatorApp.Json, Ct))!;
        Assert.Equal("SHA256:TrQ9n49hqDp2MBx+ECIov6V5jfq4uTNXkf9sDWJmsyA", key.Fingerprint);
        Assert.Null(key.EnvironmentIds);
        Assert.Null(key.AddedBy);

        var duplicate = await client.PostAsJsonAsync("/api/me/signing-keys", new { name = "again", publicKey = PublicKey, environmentIds = (Guid[]?)null }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, duplicate);

        var environment = await app.CreateEnvironmentAsync();
        var updated = await client.PutAsJsonAsync($"/api/me/signing-keys/{key.Id}", new { name = "laptop", environmentIds = new[] { environment.Id } }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, updated);
        Assert.Equal([environment.Id], (await updated.Content.ReadFromJsonAsync<KeyView>(AggregatorApp.Json, Ct))!.EnvironmentIds!);

        var audit = await client.GetStringAsync("/api/audit?category=Tokens", Ct);
        Assert.Contains("SigningKeyAdded", audit, StringComparison.Ordinal);
        Assert.Contains("SigningKeyChanged", audit, StringComparison.Ordinal);

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, client.DeleteAsync($"/api/me/signing-keys/{key.Id}", Ct));
        Assert.Empty((await client.GetFromJsonAsync<KeyView[]>("/api/me/signing-keys", AggregatorApp.Json, Ct))!);
    }

    [Fact]
    public async Task Api_token_cannot_add_a_signing_key()
    {
        await using var app = new AggregatorApp();
        var created = await app.CreateClient().PostAsJsonAsync("/api/me/tokens", new { name = "cli", readOnly = false }, Ct);
        var token = (await created.Content.ReadFromJsonAsync<TokenView>(AggregatorApp.Json, Ct))!.Token;
        var client = app.CreateClientAs(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/me/signing-keys", new { name = "sneaky", publicKey = PublicKey, environmentIds = (Guid[]?)null }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task Administrator_adds_a_key_for_a_ci_account_and_the_key_says_so()
    {
        await using var app = new AggregatorApp();
        var ci = await app.CreateUserAsync("ci-bot", isAdmin: false);

        var added = await app.CreateClient().PostAsJsonAsync($"/api/users/{ci}/signing-keys", new { name = "runner", publicKey = OtherKey, environmentIds = (Guid[]?)null }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, added);
        Assert.Equal(AggregatorApp.AdminUserName, (await added.Content.ReadFromJsonAsync<KeyView>(AggregatorApp.Json, Ct))!.AddedBy);

        var notAdmin = await app.CreateClientAs("ci-bot").PostAsJsonAsync($"/api/users/{ci}/signing-keys", new { name = "x", publicKey = PublicKey, environmentIds = (Guid[]?)null }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, notAdmin);
    }

    [Fact]
    public async Task Stack_requiring_signatures_deploys_a_signed_commit_and_an_approved_one_only()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        app.Git.Snapshot = new GitSnapshot(
            CommitSha.Parse(Sha),
            [new SourceFile(StackFilePath.Parse("compose.yaml"), Encoding.UTF8.GetBytes("services:\n  web:\n    image: nginx:1.27\n"), IsExecutable: false)],
            []);
        var client = app.CreateClient();
        var stackId = await CreateStackAsync(app, agent);
        var required = await client.PutAsJsonAsync($"/api/stacks/{stackId}/require-signed-commits", new { enabled = true }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, required);
        Assert.True((await required.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.RequireSignedCommits);

        // Unsigned: the agent gets nothing.
        var rejected = await DeployAsync(app, stackId, new { pull = false });
        Assert.Equal("Failed", rejected.Status);
        Assert.Equal("Rejected", rejected.Endorsement.Status);
        Assert.Equal("Unsigned", rejected.Endorsement.Problem);

        // Approved by its SHA: deployed, and the history says so.
        var approving = DeployAsync(app, stackId, new { pull = false, approveCommit = Sha });
        await CompleteAsync(agent);
        var approved = await approving;
        Assert.Equal("Succeeded", approved.Status);
        Assert.Equal("ApprovedUnsigned", approved.Endorsement.Status);
        Assert.Equal(Sha, approved.Endorsement.ApprovedCommit);

        // Signed with the administrator's key.
        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            client.PostAsJsonAsync("/api/me/signing-keys", new { name = "laptop", publicKey = PublicKey, environmentIds = (Guid[]?)null }, Ct));
        app.Commits.Signature = new CommitSignatureData(Signature, Encoding.UTF8.GetBytes("hello"));
        var signing = DeployAsync(app, stackId, new { pull = false });
        await CompleteAsync(agent);
        var signed = await signing;
        Assert.Equal("Succeeded", signed.Status);
        Assert.Equal("Signed", signed.Endorsement.Status);
        Assert.Equal(AggregatorApp.AdminUserName, signed.Endorsement.SignedBy);

        var history = await client.GetFromJsonAsync<HistoryView[]>($"/api/stacks/{stackId}/deployments", AggregatorApp.Json, Ct);
        Assert.Equal(["Signed", "ApprovedUnsigned", "Rejected"], history!.Select(d => d.Endorsement));

        var audit = await client.GetStringAsync("/api/audit?category=Stacks", Ct);
        Assert.Contains("StackEndorsementChanged", audit, StringComparison.Ordinal);
    }

    private static async Task CompleteAsync(FakeAgent agent)
    {
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.DeployStack, command.BodyCase);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
    }

    private static async Task<Guid> CreateStackAsync(AggregatorApp app, FakeAgent agent)
    {
        var client = app.CreateClient();
        var repository = await client.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        var stack = await client.PostAsJsonAsync(
            $"/api/environments/{agent.EnvironmentId}/stacks", new { name = "web", repositoryId, branch = "main", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        return (await stack.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
    }

    private static async Task<DeploymentView> DeployAsync(AggregatorApp app, Guid stackId, object request)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/stacks/{stackId}/deployments", request, Ct);
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

    private sealed record TokenView(string Token);

    private sealed record KeyView(Guid Id, string Name, string Fingerprint, Guid[]? EnvironmentIds, string? AddedBy);

    private sealed record StackView(Guid Id, bool RequireSignedCommits, bool RequireImageDigests);

    private sealed record EndorsementView(string Status, string? Problem, string? SignedBy, string? KeyFingerprint, string? ApprovedCommit);

    private sealed record DeploymentView(Guid Id, string Status, string? Error, EndorsementView Endorsement);

    private sealed record HistoryView(Guid Id, string Endorsement, string? SignedBy);
}
