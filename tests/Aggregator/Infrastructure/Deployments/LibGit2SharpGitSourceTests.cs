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
        source = new LibGit2SharpGitSource(Options.Create(new GitOptions { CacheDirectory = Path.Combine(root, "cache") }));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            source.FetchAsync("repo", origin, BranchName.Parse("release"), "", 1024 * 1024, Ct));
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

        await source.FetchAsync("repo", origin, BranchName.Parse("feature"), "", 1024 * 1024, Ct);
        using (var repository = new Repository(origin))
        {
            repository.Branches.Remove("feature");
        }

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            source.FetchAsync("repo", origin, BranchName.Parse("feature"), "", 1024 * 1024, Ct));
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

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            source.FetchAsync("repo", origin, Main, "", maxBytes: 1024, Ct));
    }

    [Fact]
    public async Task Unreachable_repository_is_an_error()
    {
        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            source.FetchAsync("repo", Path.Combine(root, "missing"), Main, "", 1024, Ct));
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

    private Task<GitSnapshot> Fetch(string directory) =>
        source.FetchAsync("repo", origin, Main, directory, 1024 * 1024, Ct);

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
