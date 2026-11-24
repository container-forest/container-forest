using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;
using RegistryAuth = ContainerForest.Aggregator.Application.Images.RegistryAuth;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Image updates by the aggregator (ADR-0015) through the API, with the registry and git writes stubbed.</summary>
public class ImageUpdateTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string Image = "gitea.example.com/team/app/app";
    private const string Compose = "services:\n  bot:\n    image: gitea.example.com/team/app/app:main-1111111 # CI\n  db:\n    image: postgres:17\n";

    /// <summary>A real public key: the aggregator computes its fingerprint.</summary>
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINX3MltKY5wuuud0d6OKbOIYmZfhVtfR7HGKjMX7b8BU container-forest";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Image_hook_commits_the_newest_tag_and_deploys_exactly_that_commit()
    {
        var (app, registry, writer) = Create();
        await using var _ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent);
        registry.Tags["main-1111111"] = DateTimeOffset.UnixEpoch.AddDays(1);
        registry.Tags["main-2222222"] = DateTimeOffset.UnixEpoch.AddDays(2);
        registry.Tags["feature-3333333"] = DateTimeOffset.UnixEpoch.AddDays(3);
        var token = await CreateHookAsync(app, stack.Id, allowDowngrade: false);

        var response = await Hook(app, stack.Id, token, new { image = Image });

        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<ResultView[]>(AggregatorApp.Json, Ct))!);
        Assert.Equal(("Updated", "main-1111111", "main-2222222"), (result.Outcome, result.CurrentTag, result.TargetTag));
        var commit = Assert.Single(writer.Commits);
        Assert.Equal(Compose.Replace("main-1111111", "main-2222222", StringComparison.Ordinal), commit.Content);
        Assert.StartsWith("deploy: app:main-2222222 (policy)", commit.Message, StringComparison.Ordinal);
        Assert.Equal(commit.Sha, result.Commit);
        Assert.Equal(commit.Sha, Assert.Single(writer.Pushed));

        var command = await agent.ReceiveAsync();
        Assert.Equal(commit.Sha, command.DeployStack.CommitSha);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
        var deployment = await app.CreateClient().GetFromJsonAsync<DeploymentView>($"/api/deployments/{result.DeploymentId}", AggregatorApp.Json, Ct);
        Assert.Equal("ImageUpdate", deployment!.Trigger);

        var policies = await app.CreateClient().GetFromJsonAsync<PolicyListView>($"/api/stacks/{stack.Id}/image-policies", AggregatorApp.Json, Ct);
        Assert.Equal("main-2222222", policies!.Policies.Single().LatestTag);
        var update = Assert.Single(policies.RecentUpdates);
        Assert.Equal((commit.Sha, Head, "Policy"), (update.Commit, update.Parent, update.Reason));
    }

    [Fact]
    public async Task Image_hook_needs_its_token_and_a_policy_that_accepts_requests()
    {
        var (app, registry, _) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent, acceptRequests: false);
        registry.Tags["main-1111111"] = DateTimeOffset.UnixEpoch;
        var token = await CreateHookAsync(app, stack.Id, allowDowngrade: false);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, Hook(app, stack.Id, "wrong", new { image = Image }));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, Hook(app, Guid.NewGuid(), token, new { image = Image }));
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, Hook(app, stack.Id, token, new { image = Image }));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, Hook(app, stack.Id, token, new { tag = "main-1111111" }));
        Assert.False(await app.DatabaseContainsAsync(token));
    }

    [Fact]
    public async Task Rollback_through_the_hook_needs_permission_and_pins_the_image()
    {
        var (app, registry, writer) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent);
        registry.Tags["main-0000000"] = DateTimeOffset.UnixEpoch;
        registry.Tags["main-1111111"] = DateTimeOffset.UnixEpoch.AddDays(1);
        var token = await CreateHookAsync(app, stack.Id, allowDowngrade: false);

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, Hook(app, stack.Id, token, new { image = Image, tag = "main-0000000" }));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, Hook(app, stack.Id, token, new { image = Image, tag = "latest" }));
        Assert.Empty(writer.Commits);

        var allowed = await app.CreateClient().PutAsJsonAsync($"/api/stacks/{stack.Id}/image-hook", new { allowDowngrade = true }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, allowed);
        var response = await Hook(app, stack.Id, token, new { image = Image, tag = "main-0000000" });
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);
        Assert.Equal("Updated", Assert.Single((await response.Content.ReadFromJsonAsync<ResultView[]>(AggregatorApp.Json, Ct))!).Outcome);
        await (await agent.ReceiveAsync()).RespondAsync(agent);

        var policy = (await app.CreateClient().GetFromJsonAsync<PolicyListView>($"/api/stacks/{stack.Id}/image-policies", AggregatorApp.Json, Ct))!.Policies.Single();
        Assert.Equal(("main-0000000", "image hook"), (policy.PinnedTag, policy.PinnedBy));

        // Pinned: the hook and polling leave it alone until someone unpins it.
        var again = await Hook(app, stack.Id, token, new { image = Image });
        Assert.Equal("Pinned", Assert.Single((await again.Content.ReadFromJsonAsync<ResultView[]>(AggregatorApp.Json, Ct))!).Outcome);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, app.CreateClient().PostAsync($"/api/image-policies/{policy.Id}/unpin", null, Ct));
        var audit = await app.CreateClient().GetStringAsync("/api/audit?category=Stacks", Ct);
        Assert.Contains("ImageUnpinned", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Held_stack_is_left_alone_until_the_hold_is_released()
    {
        var (app, registry, writer) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent);
        registry.Tags["main-2222222"] = DateTimeOffset.UnixEpoch.AddDays(2);
        var token = await CreateHookAsync(app, stack.Id, allowDowngrade: false);
        var client = app.CreateClient();
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, client.PostAsJsonAsync($"/api/stacks/{stack.Id}/deployments", new { pull = false }, Ct));
        await (await agent.ReceiveAsync()).RespondAsync(agent);
        await WaitUntilDeployedAsync(app, stack.Id);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, client.PostAsJsonAsync($"/api/stacks/{stack.Id}/rollback", new { commit = Head }, Ct));
        await (await agent.ReceiveAsync()).RespondAsync(agent);
        await WaitUntilHeldAsync(app, stack.Id, held: true);
        var policy = (await client.GetFromJsonAsync<PolicyListView>($"/api/stacks/{stack.Id}/image-policies", AggregatorApp.Json, Ct))!.Policies.Single();

        var hooked = await Hook(app, stack.Id, token, new { image = Image });
        Assert.Equal("Held", Assert.Single((await hooked.Content.ReadFromJsonAsync<ResultView[]>(AggregatorApp.Json, Ct))!).Outcome);
        var checkedNow = await client.PostAsync($"/api/image-policies/{policy.Id}/check", null, Ct);
        Assert.Equal("Held", (await checkedNow.Content.ReadFromJsonAsync<ResultView>(AggregatorApp.Json, Ct))!.Outcome);
        await HttpAssert.StatusAsync(
            HttpStatusCode.Conflict, client.PostAsJsonAsync($"/api/image-policies/{policy.Id}/pin", new { tag = "main-2222222" }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, client.PostAsync($"/api/image-policies/{policy.Id}/check?dryRun=true", null, Ct));
        Assert.Empty(writer.Commits);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, client.DeleteAsync($"/api/stacks/{stack.Id}/hold", Ct));
        var updated = await Hook(app, stack.Id, token, new { image = Image });
        Assert.Equal("Updated", Assert.Single((await updated.Content.ReadFromJsonAsync<ResultView[]>(AggregatorApp.Json, Ct))!).Outcome);
        await (await agent.ReceiveAsync()).RespondAsync(agent);
    }

    private static async Task WaitUntilDeployedAsync(AggregatorApp app, Guid stackId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var view = await app.CreateClient().GetFromJsonAsync<HoldView>($"/api/stacks/{stackId}", AggregatorApp.Json, Ct);
            if (view!.DeployedCommit is not null)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The stack was not deployed.");
    }

    private static async Task WaitUntilHeldAsync(AggregatorApp app, Guid stackId, bool held)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var view = await app.CreateClient().GetFromJsonAsync<HoldView>($"/api/stacks/{stackId}", AggregatorApp.Json, Ct);
            if ((view!.HeldCommit is not null) == held)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"The stack's hold is not {held}.");
    }

    private sealed record HoldView(string? DeployedCommit, string? HeldCommit);

    [Fact]
    public async Task Dry_run_says_what_would_change_and_commits_nothing()
    {
        var (app, registry, writer) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent);
        registry.Tags["main-2222222"] = DateTimeOffset.UnixEpoch.AddDays(2);
        var policy = (await app.CreateClient().GetFromJsonAsync<PolicyListView>($"/api/stacks/{stack.Id}/image-policies", AggregatorApp.Json, Ct))!.Policies.Single();

        var response = await app.CreateClient().PostAsync($"/api/image-policies/{policy.Id}/check?dryRun=true", null, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var result = (await response.Content.ReadFromJsonAsync<ResultView>(AggregatorApp.Json, Ct))!;
        Assert.Equal(("WouldUpdate", "main-2222222"), (result.Outcome, result.TargetTag));
        Assert.Empty(writer.Commits);
    }

    [Fact]
    public async Task Refused_push_is_retried_on_the_new_head()
    {
        var (app, registry, writer) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent);
        registry.Tags["main-2222222"] = DateTimeOffset.UnixEpoch.AddDays(2);
        writer.RejectPushes = 1;
        var token = await CreateHookAsync(app, stack.Id, allowDowngrade: false);

        var response = await Hook(app, stack.Id, token, new { image = Image });

        Assert.Equal("Updated", Assert.Single((await response.Content.ReadFromJsonAsync<ResultView[]>(AggregatorApp.Json, Ct))!).Outcome);
        Assert.Equal(2, writer.Commits.Count);
        Assert.Single(writer.Pushed);
        await (await agent.ReceiveAsync()).RespondAsync(agent);
    }

    [Fact]
    public async Task Policy_needs_a_push_token_and_a_service_with_the_image()
    {
        var (app, _, _) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent, withPolicy: false, withPushToken: false);
        var client = app.CreateClient();

        var noToken = await client.PostAsJsonAsync($"/api/stacks/{stack.Id}/image-policies", Policy(Image), Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, noToken);
        Assert.Contains("push token", await noToken.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await SetPushTokenAsync(app);
        var otherImage = await client.PostAsJsonAsync($"/api/stacks/{stack.Id}/image-policies", Policy("gitea.example.com/team/app/worker"), Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, otherImage);
        Assert.Contains("No service", await otherImage.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await HttpAssert.StatusAsync(HttpStatusCode.Created, client.PostAsJsonAsync($"/api/stacks/{stack.Id}/image-policies", Policy(Image), Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, client.PostAsJsonAsync($"/api/stacks/{stack.Id}/image-policies", Policy(Image), Ct));
        Assert.False(await app.DatabaseContainsAsync("gitea_push_secret"));
    }

    [Fact]
    public async Task Aggregator_signing_key_is_created_once_rotated_and_never_a_user_key()
    {
        var (app, registry, writer) = Create();
        await using var __ = app;
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await SetUpAsync(app, agent);
        registry.Tags["main-2222222"] = DateTimeOffset.UnixEpoch.AddDays(2);
        var token = await CreateHookAsync(app, stack.Id, allowDowngrade: false);
        await Hook(app, stack.Id, token, new { image = Image });
        await (await agent.ReceiveAsync()).RespondAsync(agent);
        var client = app.CreateClient();

        var keys = await client.GetFromJsonAsync<KeyView[]>("/api/system/signing-key", AggregatorApp.Json, Ct);
        Assert.Equal(PublicKey, Assert.Single(keys!).PublicKey);
        var added = await client.PostAsJsonAsync("/api/me/signing-keys", new { name = "sneaky", publicKey = PublicKey, environmentIds = (Guid[]?)null }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, added);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, client.PostAsync("/api/system/signing-key/rotate", null, Ct));
        keys = await client.GetFromJsonAsync<KeyView[]>("/api/system/signing-key", AggregatorApp.Json, Ct);
        Assert.Equal(2, keys!.Length);
        Assert.Single(keys, k => k.RetiredAt is null);
        Assert.False(await app.DatabaseContainsAsync(StubKeys.PrivateKey));
        await app.CreateUserAsync("bob", isAdmin: false);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, app.CreateClientAs("bob").GetAsync("/api/system/signing-key", Ct));
    }

    private static (AggregatorApp App, StubRegistry Registry, StubGitWriter Writer) Create()
    {
        var registry = new StubRegistry();
        StubGitWriter? writer = null;
        var app = new AggregatorApp
        {
            Customize = builder => builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IRegistryClient>(registry));
                services.Replace(ServiceDescriptor.Singleton<ICommitSigner>(new StubSigner()));
                services.Replace(ServiceDescriptor.Singleton<ISshKeyGenerator>(new StubKeys()));
                services.Replace(ServiceDescriptor.Singleton<IGitWriter>(sp => writer!));
            }),
        };
        writer = new StubGitWriter(app);
        return (app, registry, writer);
    }

    private static object Policy(string image, bool acceptRequests = true) =>
        new { image, tagPattern = @"^main-[0-9a-f]{7}\z", order = "ImageCreated", services = (string[]?)null, poll = false, acceptRequests };

    private static async Task<StackView> SetUpAsync(
        AggregatorApp app, FakeAgent agent, bool acceptRequests = true, bool withPolicy = true, bool withPushToken = true)
    {
        var client = app.CreateClient();
        var connection = await client.PostAsJsonAsync(
            "/api/git-connections",
            new { name = "team", urlPrefix = "https://gitea.example.com/team/", accessToken = new { token = "gitea_read" }, allEnvironments = true },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, connection);
        var connectionId = (await connection.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        if (withPushToken)
        {
            await SetPushTokenAsync(app);
        }

        var repository = await client.PostAsJsonAsync(
            "/api/repositories", new { name = "app-deploy", url = "https://gitea.example.com/team/app-deploy.git", connectionId }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        app.Git.Snapshot = Snapshot(Head, Compose);

        var created = await client.PostAsJsonAsync(
            $"/api/environments/{agent.EnvironmentId}/stacks", new { name = "app", repositoryId, branch = "main", composePath = "l1/compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        var stack = (await created.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!;
        if (withPolicy)
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Created, client.PostAsJsonAsync($"/api/stacks/{stack.Id}/image-policies", Policy(Image, acceptRequests), Ct));
        }

        return stack;
    }

    private static async Task SetPushTokenAsync(AggregatorApp app)
    {
        var client = app.CreateClient();
        var connection = (await client.GetFromJsonAsync<IdView[]>("/api/git-connections", AggregatorApp.Json, Ct))!.Single();
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK,
            client.PutAsJsonAsync($"/api/git-connections/{connection.Id}/push-credential", new { userName = "forest-bot", token = "gitea_push_secret" }, Ct));
    }

    private static async Task<string> CreateHookAsync(AggregatorApp app, Guid stackId, bool allowDowngrade)
    {
        var response = await app.CreateClient().PostAsJsonAsync($"/api/stacks/{stackId}/image-hook", new { allowDowngrade }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return (await response.Content.ReadFromJsonAsync<HookView>(AggregatorApp.Json, Ct))!.Token;
    }

    private static Task<HttpResponseMessage> Hook(AggregatorApp app, Guid stackId, string token, object body)
    {
        var client = app.CreateClientAs(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.PostAsJsonAsync($"/api/image-hooks/{stackId}", body, Ct);
    }

    private static GitSnapshot Snapshot(string sha, string compose) =>
        new(CommitSha.Parse(sha), [new SourceFile(StackFilePath.Parse("compose.yaml"), Encoding.UTF8.GetBytes(compose), IsExecutable: false)], []);

    private sealed record IdView(Guid Id);

    private sealed record StackView(Guid Id);

    private sealed record HookView(string Token);

    private sealed record ResultView(string Outcome, string? CurrentTag, string? TargetTag, string? Detail, string? Commit, Guid? DeploymentId);

    private sealed record DeploymentView(string Trigger, string Status);

    private sealed record PolicyView(Guid Id, string? LatestTag, string? PinnedTag, string? PinnedBy);

    private sealed record UpdateView(string Commit, string Parent, string Reason);

    private sealed record PolicyListView(PolicyView[] Policies, UpdateView[] RecentUpdates);

    private sealed record KeyView(string PublicKey, DateTimeOffset? RetiredAt);

    private sealed class StubRegistry : IRegistryClient
    {
        public ConcurrentDictionary<string, DateTimeOffset> Tags { get; } = new();

        public Task<IReadOnlyList<string>> ListTagsAsync(ImageName image, RegistryAuth? auth, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Tags.Keys.Order(StringComparer.Ordinal).ToList());

        public Task<RegistryImage> GetImageAsync(ImageName image, string tag, RegistryAuth? auth, bool readCreated, CancellationToken cancellationToken) =>
            Tags.TryGetValue(tag, out var created)
                ? Task.FromResult(new RegistryImage("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tag))), created))
                : throw new RegistryNotFoundException($"no {tag}");
    }

    private sealed class StubSigner : ICommitSigner
    {
        public Task<string> SignAsync(string privateKey, byte[] data, CancellationToken cancellationToken) =>
            Task.FromResult("-----BEGIN SSH SIGNATURE-----\nstub\n-----END SSH SIGNATURE-----\n");
    }

    /// <summary>Hands out two real public keys in turn, so a rotation gets a key of its own.</summary>
    private sealed class StubKeys : ISshKeyGenerator
    {
        public const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----stub-private-key";

        private const string SecondKey =
            "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBF8MPijiji7Wc21jZbdVfgdYSm72ow5oL724OekIOV7/XMe4q9r76yf131Iaz3pVDg5hVM4sMOfOQEajlPurZIo= container-forest";

        private int generated;

        public Task<GeneratedSshKey> GenerateAsync(string comment, CancellationToken cancellationToken) =>
            Task.FromResult(new GeneratedSshKey(PrivateKey, Interlocked.Increment(ref generated) == 1 ? PublicKey : SecondKey));
    }

    /// <summary>
    /// Makes commits without git: each gets a new SHA, the stub git source then serves it as the branch head, and the
    /// stub inspector knows its diff, so the pipeline sees what a real clone would.
    /// </summary>
    private sealed class StubGitWriter(AggregatorApp app) : IGitWriter
    {
        public List<(string Sha, string Content, string Message)> Commits { get; } = [];

        public List<string> Pushed { get; } = [];

        public int RejectPushes { get; set; }

        public Task<CommitSha> CreateCommitAsync(
            GitRepository repository, GitFileCommit commit, Func<byte[], Task<string>> sign, CancellationToken cancellationToken)
        {
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Guid.NewGuid().ToByteArray()));
            var before = app.Git.Snapshot!.Files.Single().Content;
            Commits.Add((sha, Encoding.UTF8.GetString(commit.Content), commit.Message));
            app.Commits.Diffs[sha] = new CommitDiff(commit.Parent, [commit.Path], before, commit.Content);
            return Task.FromResult(CommitSha.Parse(sha));
        }

        public Task PushAsync(GitRepository repository, GitAccess access, CommitSha commit, BranchName branch, CancellationToken cancellationToken)
        {
            Assert.Equal("gitea_push_secret", access.Credentials!.Token);
            if (RejectPushes-- > 0)
            {
                throw new GitPushRejectedException("fetch first");
            }

            Pushed.Add(commit.Value);
            var content = Encoding.UTF8.GetBytes(Commits.Single(c => c.Sha == commit.Value).Content);
            app.Git.Snapshot = new GitSnapshot(commit, [new SourceFile(StackFilePath.Parse("compose.yaml"), content, IsExecutable: false)], []);
            return Task.CompletedTask;
        }
    }
}

internal static class DeployCommandExtensions
{
    /// <summary>Answers a deployment command as a successful compose run.</summary>
    public static Task RespondAsync(this ControllerMessage command, FakeAgent agent) =>
        agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
}
