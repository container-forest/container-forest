using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContainerForest.Aggregator.Api.Grpc;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Contracts.Bot;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>
/// The aggregator in memory with its own SQLite file, plus helpers to act as an agent.
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> acts as the administrator <see cref="AdminUserName"/>.
/// Git is replaced by <see cref="Git"/>, so deployments never reach the network.
/// </summary>
internal sealed class AggregatorApp : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";
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

    public StubGitSource Git { get; } = new();

    public BotGateway.BotGatewayClient BotGateway => new(Channel);

    public AgentGateway.AgentGatewayClient Gateway => new(Channel);

    private GrpcChannel Channel =>
        channel ??= GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

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

    public async Task<CreatedEnvironment> CreateEnvironmentAsync(string name = "test")
    {
        var response = await CreateClient().PostAsJsonAsync("/api/dev/environments", new { name, isPrivate = false });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedEnvironment>(Json))!;
    }

    public async Task CreateUserAsync(string userName, bool isAdmin)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/dev/users", new { userName, isAdmin });
        response.EnsureSuccessStatusCode();
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
            var environments = await client.GetFromJsonAsync<EnvironmentView[]>("/api/dev/environments", Json);
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
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IGitSource>(Git)));
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(DevUserHeader, AdminUserName);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // The host has started, so migrations are applied.
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        users.Add(User.CreateAdministrator(AdminUserName, DateTimeOffset.UtcNow));
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync().GetAwaiter().GetResult();

        return host;
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

/// <summary>Returns <see cref="Snapshot"/> for every fetch.</summary>
internal sealed class StubGitSource : IGitSource
{
    public GitSnapshot? Snapshot { get; set; }

    public Task<GitSnapshot> FetchAsync(
        Domain.Repositories.GitRepository repository,
        Domain.Stacks.BranchName branch,
        string directory,
        long maxBytes,
        CancellationToken cancellationToken) =>
        Snapshot is { } snapshot
            ? Task.FromResult(snapshot)
            : throw new DeploymentFailedException($"Branch '{branch}' does not exist.");
}

/// <summary>Plays the agent's side of the Connect stream.</summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly AsyncDuplexStreamingCall<AgentMessage, ControllerMessage> call;

    private FakeAgent(Guid environmentId, AsyncDuplexStreamingCall<AgentMessage, ControllerMessage> call)
    {
        EnvironmentId = environmentId;
        this.call = call;
    }

    public Guid EnvironmentId { get; }

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
        return new FakeAgent(Guid.Parse(credentials.AgentId), call);
    }

    public async Task<ControllerMessage> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token), "The aggregator closed the stream.");
        return call.ResponseStream.Current;
    }

    public Task SendAsync(AgentMessage message) => call.RequestStream.WriteAsync(message);

    public ValueTask DisposeAsync()
    {
        call.Dispose();
        return ValueTask.CompletedTask;
    }
}
