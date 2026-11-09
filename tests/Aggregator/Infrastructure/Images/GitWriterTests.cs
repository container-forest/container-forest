using System.Text;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Infrastructure.Deployments;
using ContainerForest.Aggregator.Infrastructure.Images;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Images;

/// <summary>Update commits in the aggregator's bare clone, signed with the real ssh-keygen and pushed to a local bare repository.</summary>
public sealed class GitWriterTests : IDisposable
{
    private static readonly BranchName Main = BranchName.Parse("main");

    private const string Compose = "# the bot\nservices:\n  bot:\n    image: app:a1 # pinned\n";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-gitw-{Guid.NewGuid():N}");
    private readonly string origin;
    private readonly string work;
    private readonly LibGit2SharpGitSource source;

    public GitWriterTests()
    {
        origin = Path.Combine(root, "origin.git");
        work = Path.Combine(root, "work");
        Repository.Init(origin, isBare: true);
        Repository.Init(work);
        using (var repository = new Repository(work))
        {
            repository.Network.Remotes.Add("origin", origin);
        }

        source = new LibGit2SharpGitSource(
            Options.Create(new GitOptions { CacheDirectory = Path.Combine(root, "cache") }),
            new GitHostGuard(),
            NullLogger<LibGit2SharpGitSource>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Signed_update_commit_changes_one_file_and_is_pushed()
    {
        Deployments.SshKeygen.Require();
        Push(new() { ["deploy/compose.yaml"] = Compose, ["deploy/app.env"] = "A=1\n" });
        var head = await source.FetchAsync("repo", origin, null, Main, Ct);
        var key = await new SshKeygenKeyGenerator(Options.Create(new SignatureOptions())).GenerateAsync("container-forest", Ct);
        var signer = new SshKeygenCommitSigner(Options.Create(new SignatureOptions()));
        var updated = Compose.Replace("app:a1", "app:b2", StringComparison.Ordinal);

        var commit = await source.CreateCommitAsync(
            "repo",
            new GitFileCommit(head, "deploy/compose.yaml", Encoding.UTF8.GetBytes(updated), "deploy: app:b2 (policy)\n", "container-forest", "forest@example.com", DateTimeOffset.UtcNow),
            data => signer.SignAsync(key.PrivateKey, data, Ct),
            Ct);
        await source.PushAsync("repo", null, commit, Main, Ct);

        using (var bare = new Repository(origin))
        {
            Assert.Equal(commit.Value, bare.Branches["main"].Tip.Sha);
            Assert.Equal(head.Value, Assert.Single(bare.Branches["main"].Tip.Parents).Sha);
            Assert.Equal(updated, ((Blob)bare.Branches["main"].Tip["deploy/compose.yaml"].Target).GetContentText());
            Assert.Equal("A=1\n", ((Blob)bare.Branches["main"].Tip["deploy/app.env"].Target).GetContentText());
        }

        var diff = await source.DiffAsync("repo", commit, "deploy/compose.yaml", Ct);
        Assert.NotNull(diff);
        Assert.Equal(head, diff.Parent);
        Assert.Equal(["deploy/compose.yaml"], diff.ChangedPaths);
        Assert.Equal(Compose, Encoding.UTF8.GetString(diff.Before!));
        Assert.Equal(updated, Encoding.UTF8.GetString(diff.After!));
        Assert.False(diff.ModeChanged);

        var data = await source.ReadSignatureAsync("repo", commit, Ct);
        Assert.NotNull(data);
        Assert.True(SshSignature.TryParse(data.Signature, out var signature));
        var publicKey = SshPublicKey.Parse(key.PublicKey);
        Assert.Equal(publicKey.Fingerprint, signature.PublicKey.Fingerprint);
        var verifier = new SshKeygenSignatureVerifier(Options.Create(new SignatureOptions()), NullLogger<SshKeygenSignatureVerifier>.Instance);
        Assert.True(await verifier.VerifyAsync(publicKey, signature, data.SignedData, Ct));
    }

    [Fact]
    public async Task Push_after_the_branch_moved_is_refused_as_such()
    {
        Push(new() { ["compose.yaml"] = Compose });
        var head = await source.FetchAsync("repo", origin, null, Main, Ct);
        Push(new() { ["compose.yaml"] = Compose + "# someone else\n" });

        var commit = await source.CreateCommitAsync(
            "repo",
            new GitFileCommit(head, "compose.yaml", Encoding.UTF8.GetBytes("services: {}\n"), "deploy\n", "forest", "forest@example.com", DateTimeOffset.UtcNow),
            _ => Task.FromResult(""),
            Ct);

        await Assert.ThrowsAsync<GitPushRejectedException>(() => source.PushAsync("repo", null, commit, Main, Ct));
        using var bare = new Repository(origin);
        Assert.Contains("someone else", ((Blob)bare.Branches["main"].Tip["compose.yaml"].Target).GetContentText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_lists_every_changed_path()
    {
        Push(new() { ["compose.yaml"] = Compose });
        Push(new() { ["compose.yaml"] = Compose.Replace("a1", "b2", StringComparison.Ordinal), ["extra.sh"] = "rm -rf /\n" });
        var head = await source.FetchAsync("repo", origin, null, Main, Ct);

        var diff = await source.DiffAsync("repo", head, "compose.yaml", Ct);

        Assert.Equal(["compose.yaml", "extra.sh"], diff!.ChangedPaths.Order());
    }

    /// <summary>Commits the files in the work tree and pushes main to the origin.</summary>
    private void Push(Dictionary<string, string> files)
    {
        using var repository = new Repository(work);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(work, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Commands.Stage(repository, "*");
        var who = new Signature("alice", "alice@example.com", DateTimeOffset.UtcNow);
        var commit = repository.Commit("change", who, who);
        var main = repository.Branches["main"] ?? repository.Branches.Add("main", commit);
        repository.Refs.UpdateTarget(main.Reference, commit.Id, logMessage: null);
        repository.Network.Push(repository.Network.Remotes["origin"], "refs/heads/main:refs/heads/main");
    }
}
