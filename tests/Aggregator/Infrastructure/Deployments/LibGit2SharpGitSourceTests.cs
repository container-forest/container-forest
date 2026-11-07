using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Infrastructure.Deployments;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Deployments;

/// <summary>Fetches from a real repository in a temporary directory; libgit2 accepts a local path as the URL.</summary>
public sealed class LibGit2SharpGitSourceTests : IDisposable
{
    private static readonly BranchName Main = BranchName.Parse("main");

    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-git-{Guid.NewGuid():N}");
    private readonly string origin;
    private readonly LibGit2SharpGitSource source;

    public LibGit2SharpGitSourceTests()
    {
        origin = Path.Combine(root, "origin");
        Repository.Init(origin);
        source = Source(new GitOptions { CacheDirectory = Path.Combine(root, "cache") });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GitCredentials Credentials = new("deploy", "secret", new Uri("https://git.example.com/team/"));

    [Theory]
    [InlineData("https://git.example.com/team/infra.git")]
    [InlineData("https://git.example.com/team/infra.git/info/refs")]
    [InlineData("https://git.example.com/Team/other.git/info/refs?service=git-upload-pack")]
    public void Gives_the_token_under_the_connection_s_prefix(string requestUrl)
    {
        var credentials = LibGit2SharpGitSource.CredentialsFor(requestUrl, Credentials);

        var basic = Assert.IsType<UsernamePasswordCredentials>(credentials);
        Assert.Equal(("deploy", "secret"), (basic.Username, basic.Password));
    }

    [Theory]
    [InlineData("https://evil.example.com/team/infra.git/info/refs")]
    [InlineData("https://git.example.com:8443/team/infra.git/info/refs")]
    [InlineData("http://git.example.com/team/infra.git/info/refs")]
    [InlineData("https://git.example.com/billing/ledger.git/info/refs")]
    [InlineData("https://git.example.com/team-b/infra.git/info/refs")]
    [InlineData("https://git.example.com/team/%2e%2e/billing/ledger.git/info/refs")]
    public void Does_not_give_the_token_outside_the_prefix_after_a_redirect(string requestUrl) =>
        Assert.Throws<InvalidOperationException>(() => LibGit2SharpGitSource.CredentialsFor(requestUrl, Credentials));

    [Fact]
    public async Task Public_repository_on_a_private_address_is_not_fetched()
    {
        var guarded = Source(
            new GitOptions { CacheDirectory = Path.Combine(root, "cache") },
            (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("10.0.0.5") }));
        var repository = Domain.Repositories.GitRepository.Create("internal", "https://git.internal/team/app.git", null, DateTimeOffset.UnixEpoch);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => guarded.FetchAsync(repository, GitAccess.Public, Main, Ct));

        Assert.Contains("private network", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }

    [Fact]
    public async Task Clone_over_the_cache_limit_is_deleted()
    {
        Commit(new() { ["compose.yaml"] = new string('x', 64 * 1024) });
        var small = Source(new GitOptions { CacheDirectory = Path.Combine(root, "cache"), MaxCacheBytes = 1024 });

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => small.FetchAsync("repo", origin, null, Main, Ct));

        Assert.Contains("Git:MaxCacheBytes", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "cache", "repo")));
    }

    private static LibGit2SharpGitSource Source(
        GitOptions options, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? resolve = null) =>
        new(
            Options.Create(options),
            resolve is null ? new GitHostGuard() : new GitHostGuard(resolve),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibGit2SharpGitSource>.Instance);

    [Fact]
    public async Task Reads_the_directory_at_the_branch_head()
    {
        var sha = Commit(new() { ["apps/web/compose.yaml"] = "services: {}", ["apps/web/conf/nginx.conf"] = "x", ["other/a.txt"] = "a" });

        var snapshot = await Fetch("apps/web");

        Assert.Equal(sha, snapshot.Commit.Value);
        Assert.Equal(["compose.yaml", "conf/nginx.conf"], snapshot.Files.Select(f => f.Path.Value).Order());
        Assert.Equal("services: {}", Encoding.UTF8.GetString(snapshot.Files.Single(f => f.Path.Value == "compose.yaml").Content));
    }

    [Fact]
    public async Task Reads_the_gitattributes_of_the_directories_above()
    {
        Commit(new()
        {
            [".gitattributes"] = "*.env filter=forest\n",
            ["apps/.gitattributes"] = "local.env -filter\n",
            ["apps/web/.gitattributes"] = "inside\n",
            ["apps/web/compose.yaml"] = "services: {}",
            ["other/.gitattributes"] = "elsewhere\n",
        });

        var snapshot = await Fetch("apps/web");

        Assert.Equal(
            [("", "*.env filter=forest\n"), ("apps", "local.env -filter\n")],
            snapshot.OuterAttributes!.Select(f => (f.Directory, f.Content)));
        Assert.Contains(snapshot.Files, f => f.Path.Value == ".gitattributes");
    }

    [Fact]
    public async Task Second_fetch_sees_new_commits()
    {
        Commit(new() { ["compose.yaml"] = "v1" });
        await Fetch("");
        var second = Commit(new() { ["compose.yaml"] = "v2" });

        var snapshot = await Fetch("");

        Assert.Equal(second, snapshot.Commit.Value);
        Assert.Equal("v2", Encoding.UTF8.GetString(snapshot.Files.Single().Content));
    }

    [Fact]
    public async Task Keeps_the_executable_bit_and_skips_symbolic_links()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs Unix file modes and symbolic links.");
        }

        Commit(
            new() { ["compose.yaml"] = "services: {}", ["init.sh"] = "#!/bin/sh" },
            workTree =>
            {
                // Repeated inside the lambda: the platform analyzer (CA1416) does not see the check above.
                if (OperatingSystem.IsWindows())
                {
                    return;
                }

                File.SetUnixFileMode(Path.Combine(workTree, "init.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.CreateSymbolicLink(Path.Combine(workTree, "secrets"), "../outside-the-stack");
            });

        var snapshot = await Fetch("");

        Assert.True(snapshot.Files.Single(f => f.Path.Value == "init.sh").IsExecutable);
        Assert.False(snapshot.Files.Single(f => f.Path.Value == "compose.yaml").IsExecutable);
        Assert.Equal(["secrets"], snapshot.SkippedPaths);
    }

    [Fact]
    public async Task Missing_branch_is_an_error()
    {
        Commit(new() { ["compose.yaml"] = "" });

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            source.FetchAsync("repo", origin, null, BranchName.Parse("release"), Ct));
        Assert.Contains("release", error.Message);
    }

    [Fact]
    public async Task Deleted_branch_is_not_deployed_from_the_cache()
    {
        Commit(new() { ["compose.yaml"] = "" });
        using (var repository = new Repository(origin))
        {
            repository.Branches.Add("feature", repository.Head.Tip);
        }

        await source.FetchAsync("repo", origin, null, BranchName.Parse("feature"), Ct);
        using (var repository = new Repository(origin))
        {
            repository.Branches.Remove("feature");
        }

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            source.FetchAsync("repo", origin, null, BranchName.Parse("feature"), Ct));
    }

    [Fact]
    public async Task Missing_directory_is_an_error()
    {
        Commit(new() { ["compose.yaml"] = "" });

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Fetch("apps/web"));
    }

    [Fact]
    public async Task Files_over_the_limit_are_rejected()
    {
        Commit(new() { ["compose.yaml"] = "", ["big.bin"] = new string('x', 4096) });

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Fetch("", maxBytes: 1024));
    }

    [Fact]
    public async Task Unreachable_repository_is_an_error()
    {
        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            source.FetchAsync("repo", Path.Combine(root, "missing"), null, Main, Ct));
    }

    public void Dispose()
    {
        // libgit2 marks pack files read-only, which blocks deleting them on Windows. Links are not followed.
        if (OperatingSystem.IsWindows())
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Missing_directory_names_the_fetched_commit()
    {
        var sha = Commit(new() { ["compose.yaml"] = "" });

        var commit = await source.FetchAsync("repo", origin, null, Main, Ct);
        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => source.ReadAsync("repo", commit, "apps/web", 1024, Ct));

        Assert.Equal(sha, commit.Value);
        Assert.Contains(commit.Short, error.Message);
    }

    [Fact]
    public async Task Reading_a_commit_that_was_never_fetched_is_an_error()
    {
        var sha = CommitSha.Parse(Commit(new() { ["compose.yaml"] = "" }));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => source.ReadAsync("repo", sha, "", 1024, Ct));
    }

    [Fact]
    public async Task Reads_no_signature_from_an_unsigned_commit()
    {
        Commit(new() { ["compose.yaml"] = "v1" });
        var sha = await source.FetchAsync("repo", origin, null, Main, Ct);

        Assert.Null(await source.ReadSignatureAsync("repo", sha, Ct));
    }

    [Fact]
    public async Task Signed_commit_verifies_with_its_key_and_not_with_another()
    {
        SshKeygen.Require();
        var keyFile = SshKeygen.Generate(root, "alice");
        var otherFile = SshKeygen.Generate(root, "mallory");
        Commit(new() { ["compose.yaml"] = "v1" });
        var sha = SignedCommit(keyFile, "compose.yaml", "v2");
        var fetched = await source.FetchAsync("repo", origin, null, Main, Ct);
        Assert.Equal(sha, fetched.Value);

        var data = await source.ReadSignatureAsync("repo", fetched, Ct);

        Assert.NotNull(data);
        Assert.True(Domain.Access.SshSignature.TryParse(data.Signature, out var signature));
        var key = Domain.Access.SshPublicKey.Parse(File.ReadAllText(keyFile + ".pub"));
        var other = Domain.Access.SshPublicKey.Parse(File.ReadAllText(otherFile + ".pub"));
        Assert.Equal(key.Fingerprint, signature.PublicKey.Fingerprint);
        var verifier = new SshKeygenSignatureVerifier(
            Options.Create(new SignatureOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SshKeygenSignatureVerifier>.Instance);
        Assert.True(await verifier.VerifyAsync(key, signature, data.SignedData, Ct));
        Assert.False(await verifier.VerifyAsync(other, signature, data.SignedData, Ct));
        Assert.False(await verifier.VerifyAsync(key, signature, [.. data.SignedData, (byte)'x'], Ct));
    }

    [Fact]
    public async Task Tells_whether_a_commit_descends_from_another()
    {
        var first = CommitSha.Parse(Commit(new() { ["compose.yaml"] = "v1" }));
        var second = CommitSha.Parse(Commit(new() { ["compose.yaml"] = "v2" }));
        await source.FetchAsync("repo", origin, null, Main, Ct);

        Assert.True(await source.DescendsFromAsync("repo", second, first, Ct));
        Assert.True(await source.DescendsFromAsync("repo", second, second, Ct));
        Assert.False(await source.DescendsFromAsync("repo", first, second, Ct));
        Assert.False(await source.DescendsFromAsync("repo", second, CommitSha.Parse(new string('a', 40)), Ct));
    }

    /// <summary>Commits on "main" with an SSH signature, as git does with gpg.format=ssh.</summary>
    private string SignedCommit(string keyFile, string path, string content)
    {
        using var repository = new Repository(origin);
        File.WriteAllText(Path.Combine(origin, path), content);
        Commands.Stage(repository, "*");
        var tree = repository.ObjectDatabase.CreateTree(repository.Index);
        var author = new Signature("alice", "alice@example.com", DateTimeOffset.UtcNow);
        var parent = repository.Branches["main"].Tip;
        var buffer = LibGit2Sharp.Commit.CreateBuffer(author, author, "signed\n", tree, [parent], prettifyMessage: false, commentChar: null);
        var signature = SshKeygen.Sign(keyFile, buffer);
        var id = repository.ObjectDatabase.CreateCommitWithSignature(buffer, signature);
        repository.Refs.UpdateTarget(repository.Branches["main"].Reference, id, logMessage: null);
        return id.Sha;
    }

    private async Task<GitSnapshot> Fetch(string directory, long maxBytes = 1024 * 1024)
    {
        var commit = await source.FetchAsync("repo", origin, null, Main, Ct);
        return await source.ReadAsync("repo", commit, directory, maxBytes, Ct);
    }

    /// <summary>Writes the files into the origin's work tree and commits everything on "main".</summary>
    private string Commit(Dictionary<string, string> files, Action<string>? prepare = null)
    {
        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(origin, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        prepare?.Invoke(origin);

        using var repository = new Repository(origin);
        Commands.Stage(repository, "*");
        var signature = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        var commit = repository.Commit("test", signature, signature, new CommitOptions { AllowEmptyCommit = true });

        var main = repository.Branches["main"] ?? repository.Branches.Add("main", commit);
        repository.Refs.UpdateTarget(main.Reference, commit.Id, logMessage: null);
        return commit.Sha;
    }
}

/// <summary>
/// Runs the real ssh-keygen. Without it the tests are skipped, unless CF_TEST_REQUIRE_SSH_KEYGEN=1 (as in CI),
/// where a missing tool is a failure.
/// </summary>
internal static class SshKeygen
{
    public static void Require()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ssh-keygen", "-V")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit();
            return;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            const string message = "ssh-keygen is not on PATH.";
            if (Environment.GetEnvironmentVariable("CF_TEST_REQUIRE_SSH_KEYGEN") == "1")
            {
                Assert.Fail(message);
            }

            Assert.Skip(message);
        }
    }

    public static string Generate(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        Run(["-q", "-t", "ed25519", "-N", "", "-C", name, "-f", path], stdin: null);
        return path;
    }

    public static string Sign(string keyFile, string data) =>
        Run(["-Y", "sign", "-n", "git", "-f", keyFile], stdin: data);

    private static string Run(string[] arguments, string? stdin)
    {
        var info = new System.Diagnostics.ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
