using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContainerForest.Aggregator.Api.Grpc;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Contracts.Bot;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>
/// The aggregator in memory with its own SQLite file, plus helpers to act as an agent.
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> acts as the administrator <see cref="AdminUserName"/>,
/// whom the aggregator creates at start-up from the Bootstrap settings, so they can also sign in with <see cref="AdminPassword"/>.
/// Git is replaced by <see cref="Git"/>, so deployments never reach the network.
/// </summary>
internal sealed class AggregatorApp : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "correct horse battery staple";

    /// <summary>Password of the users <see cref="CreateUserAsync"/> makes unless a test gives its own.</summary>
    public const string DefaultPassword = "a long enough password";
    public const string DevUserHeader = "X-Dev-User";

    /// <summary>The bot token the aggregator accepts; <see cref="BotGateway"/> sends it with every call.</summary>
    public const string BotToken = "test-bot-token";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"cf-test-{Guid.NewGuid():N}.db");
    private readonly string keyDirectory = Path.Combine(Path.GetTempPath(), $"cf-test-keys-{Guid.NewGuid():N}");
    private GrpcChannel? channel;

    /// <summary>More settings or services for one test, applied after the defaults.</summary>
    public Action<IWebHostBuilder>? Customize { get; init; }

    public StubGitSource Git { get; } = new();

    public StubSops Sops { get; } = new();

    /// <summary>Signatures of fetched commits; by default every commit is unsigned.</summary>
    public StubCommits Commits { get; } = new();

    public BotGateway.BotGatewayClient BotGateway => new(Channel);

    public AgentGateway.AgentGatewayClient Gateway => new(Channel);

    private GrpcChannel Channel =>
        channel ??= GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    /// <summary>Whether the text is anywhere in the database's files, to show that a secret is not stored in clear.</summary>
    public async Task<bool> DatabaseContainsAsync(string text)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, System.Text.Encoding.Latin1);
            if ((await reader.ReadToEndAsync()).Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A client that acts as the given user; null for an anonymous client.</summary>
    public HttpClient CreateClientAs(string? userName)
    {
        var client = Server.CreateClient();
        if (userName is not null)
        {
            client.DefaultRequestHeaders.Add(DevUserHeader, userName);
        }

        return client;
    }

    /// <summary>An anonymous client that keeps cookies, like a browser: for signing in with a password.</summary>
    public HttpClient CreateBrowserClient()
    {
        var client = CreateDefaultClient(new CookieContainerHandler());
        client.DefaultRequestHeaders.Remove(DevUserHeader);
        return client;
    }

    public async Task<CreatedEnvironment> CreateEnvironmentAsync(string name = "test")
    {
        var response = await CreateClient().PostAsJsonAsync("/api/environments", new { name, isPrivate = false });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedEnvironment>(Json))!;
    }

    /// <summary>Creates a user through the user management API, as the administrator.</summary>
    /// <returns>The new user's id.</returns>
    public async Task<Guid> CreateUserAsync(string userName, bool isAdmin, string password = DefaultPassword)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/users", new { userName, password, isAdmin });
        await HttpAssert.StatusAsync(System.Net.HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    public async Task<FakeAgent> EnrollAndConnectAsync(string bootId = "boot-1")
    {
        var environment = await CreateEnvironmentAsync();
        var credentials = await Gateway.EnrollAsync(
            new EnrollRequest { EnrollmentToken = environment.EnrollmentToken, Hostname = "test-host", AgentVersion = "0.0.1" });
        var agent = await FakeAgent.ConnectAsync(Gateway, credentials, bootId);
        await WaitUntilOnlineAsync(environment.Id);
        return agent;
    }

    public async Task WaitUntilOnlineAsync(Guid environmentId, bool online = true)
    {
        var client = CreateClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var environments = await client.GetFromJsonAsync<EnvironmentView[]>("/api/environments", Json);
            if (environments!.Single(e => e.Id == environmentId).IsOnline == online)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Environment {environmentId} did not become {(online ? "online" : "offline")}.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Aggregator", $"Data Source={databasePath}");
        builder.UseSetting("DataProtection:KeyDirectory", keyDirectory);
        builder.UseSetting("AgentGateway:CommandTimeout", "00:00:05");
        builder.UseSetting("BotGateway:TokenHashes:0", SecretToken.Hash(BotToken));
        builder.UseSetting("Bootstrap:AdminUserName", AdminUserName);
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGitSource>(Git));
            services.Replace(ServiceDescriptor.Singleton<ICommitInspector>(Commits));
            services.Replace(ServiceDescriptor.Singleton<ICommitSignatureVerifier>(Commits));
            services.Replace(ServiceDescriptor.Singleton<IAgeKeyGenerator>(Sops));
            services.Replace(ServiceDescriptor.Singleton<ISopsDecryptor>(Sops));
            services.Replace(ServiceDescriptor.Singleton<ISopsEncryptor>(Sops));
        });
        Customize?.Invoke(builder);
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(DevUserHeader, AdminUserName);
    }

    public override async ValueTask DisposeAsync()
    {
        channel?.Dispose();
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            File.Delete(file);
        }

        if (Directory.Exists(keyDirectory))
        {
            Directory.Delete(keyDirectory, recursive: true);
        }
    }

    public sealed record CreatedEnvironment(Guid Id, string Name, string EnrollmentToken, DateTimeOffset ExpiresAt);

    public sealed record EnvironmentView(Guid Id, string Name, bool IsEnrolled, bool IsOnline, string? AgentHostname, string? AgentVersion);
}

/// <summary>Returns <see cref="Snapshot"/> for every fetch; without one, the branch does not exist.</summary>
internal sealed class StubGitSource : IGitSource
{
    public GitSnapshot? Snapshot { get; set; }

    public Task<Domain.Stacks.CommitSha> FetchAsync(
        Domain.Repositories.GitRepository repository,
        GitAccess access,
        Domain.Stacks.BranchName branch,
        CancellationToken cancellationToken) =>
        Snapshot is { } snapshot
            ? Task.FromResult(snapshot.Commit)
            : throw new DeploymentFailedException($"Branch '{branch}' does not exist.");

    /// <summary>The commits files were read at, in order.</summary>
    public List<string> ReadCommits { get; } = [];

    public Task ForgetAsync(Domain.Common.RepositoryId repositoryId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<GitSnapshot> ReadAsync(
        Domain.Repositories.GitRepository repository,
        Domain.Stacks.CommitSha commit,
        string directory,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ReadCommits.Add(commit.Value);
        return Task.FromResult(Snapshot!);
    }
}

/// <summary>
/// Commit signatures without git or ssh-keygen: <see cref="Signature"/> is what every commit carries, and it verifies
/// when it names the key (the real check is tested in the infrastructure tests).
/// </summary>
internal sealed class StubCommits : ICommitInspector, ICommitSignatureVerifier
{
    public CommitSignatureData? Signature { get; set; }

    public bool Descends { get; set; } = true;

    public Task<CommitSignatureData?> ReadSignatureAsync(
        Domain.Repositories.GitRepository repository, Domain.Stacks.CommitSha commit, CancellationToken cancellationToken) =>
        Task.FromResult(Signature);

    public Task<bool> DescendsFromAsync(
        Domain.Repositories.GitRepository repository, Domain.Stacks.CommitSha commit, Domain.Stacks.CommitSha ancestor, CancellationToken cancellationToken) =>
        Task.FromResult(Descends);

    public Task<bool> VerifyAsync(SshPublicKey key, SshSignature signature, byte[] signedData, CancellationToken cancellationToken) =>
        Task.FromResult(key.Fingerprint == signature.PublicKey.Fingerprint);

    /// <summary>What update commits change, by commit; set by <see cref="StubGitWriter"/>.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, CommitDiff> Diffs { get; } = new();

    public Task<CommitDiff?> DiffAsync(
        Domain.Repositories.GitRepository repository, Domain.Stacks.CommitSha commit, string path, CancellationToken cancellationToken) =>
        Task.FromResult(Diffs.GetValueOrDefault(commit.Value));
}

/// <summary>
/// Stands in for age-keygen and sops: keys are "AGE-SECRET-KEY-1TEST&lt;n&gt;", and a file decrypts to the text after
/// "encrypted for &lt;identity&gt;: " up to the end of the line. Encryption writes that form for the first recipient,
/// with the recipients in dotenv metadata. The real tools are tested in the infrastructure tests.
/// </summary>
internal sealed class StubSops : IAgeKeyGenerator, ISopsDecryptor, ISopsEncryptor
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> identities = new();
    private int generated;

    public Task<GeneratedAgeKey> GenerateAsync(CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref generated);
        var key = new GeneratedAgeKey($"age1{new string('q', 57)}{"023456789acdefghjklmnpqrstuvwxyz"[n % 32]}", $"AGE-SECRET-KEY-1TEST{n}");
        identities[key.Recipient] = key.Identity;
        return Task.FromResult(key);
    }

    public Task<byte[]> EncryptAsync(
        Domain.Stacks.StackFilePath path, byte[] content, SopsEncryptionRule rule, CancellationToken cancellationToken)
    {
        var identity = identities.GetValueOrDefault(rule.AgeRecipients[0], "unknown");
        var text = $"encrypted for {identity}: {System.Text.Encoding.UTF8.GetString(content).TrimEnd('\n')}\n"
            + string.Concat(rule.AgeRecipients.Select((r, i) => $"sops_age__list_{i}__map_recipient={r}\n"))
            + "sops_version=3.13.3\n";
        return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(text));
    }

    public Task<byte[]> DecryptAsync(
        Domain.Stacks.StackFilePath path, byte[] content, IReadOnlyList<string> identities, CancellationToken cancellationToken)
    {
        var text = System.Text.Encoding.UTF8.GetString(content);
        foreach (var identity in identities)
        {
            var prefix = $"encrypted for {identity}: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(text[prefix.Length..text.IndexOf('\n', StringComparison.Ordinal)]));
            }
        }

        throw new SopsDecryptionException("none of this environment's age keys is a recipient of the file.");
    }
}

/// <summary>Plays the agent's side of the Connect stream.</summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly AsyncDuplexStreamingCall<AgentMessage, ControllerMessage> call;

    private FakeAgent(EnrollResponse enrollment, AsyncDuplexStreamingCall<AgentMessage, ControllerMessage> call)
    {
        Enrollment = enrollment;
        EnvironmentId = Guid.Parse(enrollment.AgentId);
        this.call = call;
    }

    public Guid EnvironmentId { get; }

    /// <summary>The agent's saved credentials, to connect again as the same agent (e.g. after a "reboot").</summary>
    public EnrollResponse Enrollment { get; }

    public static Metadata Credentials(string agentId, string secret) => new()
    {
        { AgentGatewayService.AgentIdHeader, agentId },
        { AgentGatewayService.AgentSecretHeader, secret },
    };

    public static async Task<FakeAgent> ConnectAsync(AgentGateway.AgentGatewayClient gateway, EnrollResponse credentials, string bootId)
    {
        var call = gateway.Connect(Credentials(credentials.AgentId, credentials.AgentSecret));
        await call.RequestStream.WriteAsync(new AgentMessage
        {
            Hello = new Hello { AgentVersion = "0.0.1", DockerVersion = "29.8.0", BootId = bootId },
        });
        return new FakeAgent(credentials, call);
    }

    public async Task<ControllerMessage> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token), "The aggregator closed the stream.");
        return call.ResponseStream.Current;
    }

    public Task SendAsync(AgentMessage message) => call.RequestStream.WriteAsync(message);

    /// <summary>Receives the next command, which must be ListUnmanagedStacks, and answers it with <paramref name="stacks"/>.</summary>
    public async Task AnswerUnmanagedStacksAsync(params UnmanagedStack[] stacks)
    {
        var command = await ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.ListUnmanagedStacks, command.BodyCase);
        await SendAsync(new AgentMessage
        {
            Result = new CommandResult { RequestId = command.RequestId, UnmanagedStacks = new UnmanagedStackList { Items = { stacks } } },
        });
    }

    public ValueTask DisposeAsync()
    {
        call.Dispose();
        return ValueTask.CompletedTask;
    }
}
