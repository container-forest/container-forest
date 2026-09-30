using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Compose;

public sealed class RegistryConfigWriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-registry-test-{Guid.NewGuid():N}");
    private readonly RegistryConfigWriter writer;

    public RegistryConfigWriterTests()
    {
        var options = new AgentOptions { DataDirectory = Path.Combine(root, "data"), SecretsDirectory = Path.Combine(root, "secrets") };
        writer = new RegistryConfigWriter(Options.Create(options), NullLogger<RegistryConfigWriter>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task No_credentials_means_no_config()
    {
        Assert.Null(await writer.WriteAsync([], Ct));
    }

    [Fact]
    public async Task Config_has_the_auths_in_the_docker_login_format_and_is_removed_on_dispose()
    {
        var config = await writer.WriteAsync(
            [new RegistryAuth { Server = "ghcr.io", Username = "bot", Password = "p:ss" }, new RegistryAuth { Server = "registry.example.com:5000", Username = "ci", Password = "x" }],
            Ct);

        Assert.NotNull(config);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(config.Directory, "config.json"), Ct)))
        {
            var auths = json.RootElement.GetProperty("auths");
            Assert.Equal("bot:p:ss", Decode(auths.GetProperty("ghcr.io").GetProperty("auth").GetString()!));
            Assert.Equal("ci:x", Decode(auths.GetProperty("registry.example.com:5000").GetProperty("auth").GetString()!));
        }

        config.Dispose();
        Assert.False(Directory.Exists(config.Directory));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task On_linux_the_config_is_on_the_secrets_tmpfs_and_readable_only_by_the_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows.");

        using var config = await writer.WriteAsync([new RegistryAuth { Server = "ghcr.io", Username = "bot", Password = "token" }], Ct);

        Assert.StartsWith(Path.Combine(root, "secrets"), config!.Directory);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(config.Directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(config.Directory, "config.json")));
    }

    [Theory]
    [InlineData("", "bot")]
    [InlineData("ghcr .io", "bot")]
    [InlineData("ghcr.io", "")]
    [InlineData("ghcr.io", "a:b")]
    public void Unusable_server_or_user_names_are_rejected(string server, string username)
    {
        Assert.Throws<InvalidCommandArgumentException>(() =>
            RegistryConfigWriter.Validate([new RegistryAuth { Server = server, Username = username, Password = "token" }]));
    }

    [Fact]
    public void Two_credentials_for_one_registry_are_rejected()
    {
        Assert.Throws<InvalidCommandArgumentException>(() => RegistryConfigWriter.Validate(
            [new RegistryAuth { Server = "ghcr.io", Username = "a", Password = "1" }, new RegistryAuth { Server = "GHCR.io", Username = "b", Password = "2" }]));
    }

    [Fact]
    public void Error_messages_never_contain_the_password()
    {
        var ex = Assert.Throws<InvalidCommandArgumentException>(() =>
            RegistryConfigWriter.Validate([new RegistryAuth { Server = "ghcr.io", Username = "", Password = "hunter2" }]));

        Assert.DoesNotContain("hunter2", ex.Message);
    }

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));
}
