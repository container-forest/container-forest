using System.Runtime.Versioning;
using System.Text;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Stacks;

public sealed class StackFileStoreTests : IDisposable
{
    private const uint Regular = 0b110_100_100;
    private const uint Executable = 0b111_101_101;

    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-store-test-{Guid.NewGuid():N}");
    private readonly StackFileStore store;

    public StackFileStoreTests()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.Combine(root, "data"),
            StacksDirectory = Path.Combine(root, "stacks"),
            SecretsDirectory = Path.Combine(root, "run"),
        };
        store = new StackFileStore(Options.Create(options), NullLogger<StackFileStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StackDirectory => Path.Combine(root, "stacks", "web");

    private string SecretsDirectory => Path.Combine(root, "run", "web");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task First_deployment_writes_the_files_into_the_stack_directory()
    {
        Assert.False(store.Exists("web"));

        var directory = await Write(Compose("v1"), File("conf/site.conf", "site"));

        Assert.Equal(StackDirectory, directory);
        Assert.True(store.Exists("web"));
        Assert.Equal("services: v1", Read("compose.yaml"));
        Assert.Equal("site", Read("conf/site.conf"));
        Assert.False(Directory.Exists(StackDirectory + ".tmp"));
        Assert.False(Directory.Exists(StackDirectory + ".prev"));
    }

    [Fact]
    public async Task Redeploy_replaces_bundle_files_and_keeps_what_containers_created()
    {
        await Write(Compose("v1"), File("conf/site.conf", "site v1"), File("old.env", "x"));
        Directory.CreateDirectory(Path.Combine(StackDirectory, "data"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(StackDirectory, "data", "db.sqlite"), "rows", Ct);
        await System.IO.File.WriteAllTextAsync(Path.Combine(StackDirectory, "conf", "cache.tmp"), "runtime", Ct);

        await Write(Compose("v2"), File("conf/site.conf", "site v2"));

        Assert.Equal("services: v2", Read("compose.yaml"));
        Assert.Equal("site v2", Read("conf/site.conf"));
        Assert.False(System.IO.File.Exists(Path.Combine(StackDirectory, "old.env")));
        Assert.Equal("rows", Read("data/db.sqlite"));
        Assert.Equal("runtime", Read("conf/cache.tmp"));
    }

    [Fact]
    public async Task Only_a_change_to_files_other_than_the_compose_file_counts_as_changed_files()
    {
        Assert.False((await WriteResult(Compose("v1"), File("conf/site.conf", "site v1"), File("old.env", "x"))).FilesChanged);
        Assert.False((await WriteResult(Compose("v2"), File("conf/site.conf", "site v1"), File("old.env", "x"))).FilesChanged);
        Assert.True((await WriteResult(Compose("v2"), File("conf/site.conf", "site v2"), File("old.env", "x"))).FilesChanged);
        Assert.True((await WriteResult(Compose("v2"), File("conf/site.conf", "site v2"))).FilesChanged);
        Assert.True((await WriteResult(Compose("v2"), File("conf/site.conf", "site v2"), File("new.env", "y"))).FilesChanged);
        Assert.False((await WriteResult(Compose("v2"), File("conf/site.conf", "site v2"), File("new.env", "y"))).FilesChanged);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Changed_secret_counts_as_changed_files_and_an_unchanged_one_does_not()
    {
        await Write(Compose("v1"), File(".env", "TOKEN=a", secret: true));

        Assert.False((await WriteResult(Compose("v1"), File(".env", "TOKEN=a", secret: true))).FilesChanged);
        Assert.True((await WriteResult(Compose("v1"), File(".env", "TOKEN=b", secret: true))).FilesChanged);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task File_a_container_replaced_with_a_link_counts_as_changed_without_being_read()
    {
        await Write(Compose("v1"), File("conf/site.conf", "site"));
        var target = Path.Combine(StackDirectory, "conf", "site.conf");
        var planted = Path.Combine(root, "planted.conf");
        await System.IO.File.WriteAllTextAsync(planted, "site", Ct);
        System.IO.File.Delete(target);
        System.IO.File.CreateSymbolicLink(target, planted);

        Assert.True((await WriteResult(Compose("v1"), File("conf/site.conf", "site"))).FilesChanged);
    }

    [Fact]
    public async Task Previous_bundle_is_kept_for_a_rollback()
    {
        await Write(Compose("v1"), File("conf/site.conf", "site v1"));
        await Write(Compose("v2"));
        await Write(Compose("v3"));

        Assert.Equal("services: v2", await System.IO.File.ReadAllTextAsync(Path.Combine(StackDirectory + ".prev", "compose.yaml"), Ct));
        Assert.False(System.IO.File.Exists(Path.Combine(StackDirectory + ".prev", "conf", "site.conf")));
    }

    [Fact]
    public async Task Directories_left_empty_by_removed_files_are_removed()
    {
        await Write(Compose("v1"), File("conf/nginx/site.conf", "x"), File("keep/app.env", "x"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(StackDirectory, "keep", "runtime.log"), "log", Ct);

        await Write(Compose("v2"));

        Assert.False(Directory.Exists(Path.Combine(StackDirectory, "conf")));
        Assert.True(System.IO.File.Exists(Path.Combine(StackDirectory, "keep", "runtime.log")));
    }

    [Fact]
    public async Task Directory_on_the_host_where_the_bundle_has_a_file_is_a_conflict_and_nothing_changes()
    {
        await Write(Compose("v1"));
        Directory.CreateDirectory(Path.Combine(StackDirectory, "config"));

        var ex = await Assert.ThrowsAsync<StackFileConflictException>(() => Write(Compose("v2"), File("config", "x")));

        Assert.Equal(ErrorCode.CommandFailed, ex.Code);
        Assert.Equal("services: v1", Read("compose.yaml"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Files_get_the_bundle_modes_whatever_the_umask()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows.");

        await Write(Compose("v1"), File("bin/run.sh", "#!/bin/sh", Executable));

        Assert.Equal(StackLayout.RegularMode, System.IO.File.GetUnixFileMode(Path.Combine(StackDirectory, "compose.yaml")));
        Assert.Equal(StackLayout.ExecutableMode, System.IO.File.GetUnixFileMode(Path.Combine(StackDirectory, "bin", "run.sh")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Secret_content_goes_to_the_secrets_directory_and_the_stack_directory_links_to_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Secrets are written into the stack directory on Windows.");

        await Write(Compose("v1"), File("secrets/db_password", "s3cr3t", secret: true));

        var secret = Path.Combine(SecretsDirectory, "secrets", "db_password");
        Assert.Equal("s3cr3t", await System.IO.File.ReadAllTextAsync(secret, Ct));

        var link = new FileInfo(Path.Combine(StackDirectory, "secrets", "db_password"));
        Assert.Equal(secret, link.LinkTarget);
        Assert.Equal("s3cr3t", Read("secrets/db_password"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, System.IO.File.GetUnixFileMode(SecretsDirectory));
        Assert.Equal(StackLayout.RegularMode, System.IO.File.GetUnixFileMode(secret));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Secret_content_is_never_written_into_the_stack_directory_or_its_backup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Secrets are written into the stack directory on Windows.");

        await Write(Compose("v1"), File("secrets/db_password", "s3cr3t", secret: true));
        await Write(Compose("v2"), File("secrets/db_password", "n3w", secret: true));

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "stacks"), "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is null)
            {
                var content = await System.IO.File.ReadAllTextAsync(file, Ct);
                Assert.DoesNotContain("s3cr3t", content);
                Assert.DoesNotContain("n3w", content);
            }
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Previous_secrets_stay_until_they_are_discarded_after_compose_up()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Secrets are written into the stack directory on Windows.");

        await Write(Compose("v1"), File("secrets/db_password", "old", secret: true));
        await Write(Compose("v2"), File("secrets/db_password", "new", secret: true));

        Assert.Equal("new", Read("secrets/db_password"));
        Assert.Equal("old", await System.IO.File.ReadAllTextAsync(Path.Combine(SecretsDirectory + ".prev", "secrets", "db_password"), Ct));

        store.DiscardPreviousSecrets("web");

        Assert.False(Directory.Exists(SecretsDirectory + ".prev"));
        Assert.Equal("new", Read("secrets/db_password"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Removed_secret_loses_its_link_and_delete_secrets_empties_the_tmpfs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Secrets are written into the stack directory on Windows.");

        await Write(Compose("v1"), File("secrets/a", "1", secret: true), File("secrets/b", "2", secret: true));
        await Write(Compose("v2"), File("secrets/a", "1", secret: true));

        Assert.Null(new FileInfo(Path.Combine(StackDirectory, "secrets", "b")).LinkTarget);

        store.DeleteSecrets("web");

        Assert.False(Directory.Exists(SecretsDirectory));
        Assert.False(Directory.Exists(SecretsDirectory + ".prev"));
        Assert.True(store.Exists("web"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Links_after_a_reboot_are_dangling_and_a_new_deployment_replaces_them()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Secrets are written into the stack directory on Windows.");

        await Write(Compose("v1"), File("secrets/db_password", "s3cr3t", secret: true));
        Directory.Delete(SecretsDirectory, recursive: true); // tmpfs after a reboot

        await Write(Compose("v1"), File("secrets/db_password", "s3cr3t", secret: true));

        Assert.Equal("s3cr3t", Read("secrets/db_password"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Symbolic_link_planted_by_a_container_is_never_written_through()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");

        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        await Write(Compose("v1"));
        Directory.CreateSymbolicLink(Path.Combine(StackDirectory, "conf"), outside);

        var ex = await Assert.ThrowsAsync<StackFileConflictException>(() => Write(Compose("v2"), File("conf/passwd", "pwned")));

        Assert.Contains("symbolic link", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Equal("services: v1", Read("compose.yaml"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Stale_files_behind_a_planted_link_are_not_deleted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs privileges on Windows.");

        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        await System.IO.File.WriteAllTextAsync(Path.Combine(outside, "app.conf"), "precious", Ct);
        await Write(Compose("v1"), File("conf/app.conf", "x"));
        Directory.Delete(Path.Combine(StackDirectory, "conf"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(StackDirectory, "conf"), outside);

        await Write(Compose("v2"));

        Assert.Equal("precious", await System.IO.File.ReadAllTextAsync(Path.Combine(outside, "app.conf"), Ct));
    }

    [Fact]
    public async Task Unreadable_manifest_keeps_old_files_instead_of_guessing()
    {
        await Write(Compose("v1"), File("old.env", "x"));
        await System.IO.File.WriteAllTextAsync(StackDirectory + ".manifest.json", "not json", Ct);

        await Write(Compose("v2"));

        Assert.True(System.IO.File.Exists(Path.Combine(StackDirectory, "old.env")));
        Assert.Equal("services: v2", Read("compose.yaml"));
    }

    private async Task<string> Write(params StackFile[] files) => (await WriteResult(files)).Directory;

    private Task<StackWriteResult> WriteResult(params StackFile[] files) => store.WriteAsync(StackLayout.Create("web", files), Ct);

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(StackDirectory, path), Encoding.UTF8);

    private static StackFile Compose(string version) => File(StackLayout.ComposeFileName, $"services: {version}");

    private static StackFile File(string path, string content, uint mode = Regular, bool secret = false) => new()
    {
        Path = path,
        Content = ByteString.CopyFromUtf8(content),
        Mode = mode,
        Secret = secret,
    };
}
