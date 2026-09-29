using System.Runtime.Versioning;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Enrollment;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Enrollment;

public sealed class EnrollmentTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"cf-agent-test-{Guid.NewGuid():N}");
    private readonly AgentOptions options = new() { ControllerUrl = new Uri("http://localhost:5081") };
    private readonly FakeGatewayClient gateway = new();

    public EnrollmentTests() => options.DataDirectory = dataDirectory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CredentialStore Store => new(Options.Create(options));

    [Fact]
    public async Task Saved_credentials_round_trip()
    {
        await Store.SaveAsync(new AgentCredentials("id-1", "secret-1"), Ct);

        Assert.Equal(new AgentCredentials("id-1", "secret-1"), await Store.LoadAsync(Ct));
        Assert.False(File.Exists(Store.FilePath + ".tmp"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Credentials_file_is_readable_only_by_the_owner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows.");

        await Store.SaveAsync(new AgentCredentials("id-1", "secret-1"), Ct);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Store.FilePath));
    }

    [Fact]
    public async Task Missing_file_means_not_enrolled()
    {
        Assert.Null(await Store.LoadAsync(Ct));
    }

    [Fact]
    public async Task Corrupted_file_is_fatal()
    {
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Store.FilePath, "{\"agentId\":\"id-1\"}", Ct);

        await Assert.ThrowsAsync<FatalAgentException>(() => Store.LoadAsync(Ct));
    }

    [Fact]
    public void Credentials_never_print_the_secret()
    {
        Assert.DoesNotContain("secret-1", new AgentCredentials("id-1", "secret-1").ToString());
    }

    [Fact]
    public async Task First_start_enrolls_with_the_token_and_saves_the_credentials()
    {
        options.EnrollmentToken = " token-1 ";

        var credentials = await Enroller().GetCredentialsAsync(gateway, Ct);

        var request = Assert.Single(gateway.EnrollRequests);
        Assert.Equal("token-1", request.EnrollmentToken);
        Assert.Equal(Environment.MachineName, request.Hostname);
        Assert.Equal(credentials, await Store.LoadAsync(Ct));
    }

    [Fact]
    public async Task Saved_credentials_are_used_without_enrolling_again()
    {
        options.EnrollmentToken = "token-1";
        await Store.SaveAsync(new AgentCredentials("id-1", "secret-1"), Ct);

        var credentials = await Enroller().GetCredentialsAsync(gateway, Ct);

        Assert.Equal("id-1", credentials.AgentId);
        Assert.Empty(gateway.EnrollRequests);
    }

    [Fact]
    public async Task No_credentials_and_no_token_is_fatal()
    {
        await Assert.ThrowsAsync<FatalAgentException>(() => Enroller().GetCredentialsAsync(gateway, Ct));
    }

    [Fact]
    public async Task Rejected_token_is_fatal()
    {
        options.EnrollmentToken = "used-token";
        gateway.EnrollHandler = _ => throw new RpcException(new Status(StatusCode.Unauthenticated, "Enrollment token is invalid."));

        await Assert.ThrowsAsync<FatalAgentException>(() => Enroller().GetCredentialsAsync(gateway, Ct));
    }

    [Fact]
    public async Task Unreachable_aggregator_is_left_to_retry()
    {
        options.EnrollmentToken = "token-1";
        gateway.EnrollHandler = _ => throw new RpcException(new Status(StatusCode.Unavailable, "connection refused"));

        var error = await Assert.ThrowsAsync<RpcException>(() => Enroller().GetCredentialsAsync(gateway, Ct));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private AgentEnroller Enroller() => new(Store, Options.Create(options), NullLogger<AgentEnroller>.Instance);
}
