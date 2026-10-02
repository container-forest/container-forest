using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;
using LibGit2Sharp;
using Microsoft.Extensions.Options;
using GitRepository = ContainerForest.Aggregator.Domain.Repositories.GitRepository;

namespace ContainerForest.Aggregator.Infrastructure.Deployments;

/// <summary>Configuration section "Git" (env: CF_GIT__...).</summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>Where bare clones of stack repositories are kept between deployments; relative to the working directory.</summary>
    public string CacheDirectory { get; set; } = "git-cache";
}

/// <summary>
/// Keeps a bare clone of each repository and fetches only the stack's branch into it. Files are read from
/// the commit's tree directly: there is no working copy, so nothing from the repository is ever checked out on disk.
/// </summary>
internal sealed class LibGit2SharpGitSource(IOptions<GitOptions> options) : IGitSource
{
    private const string RemoteName = "origin";

    // LibGit2Sharp must not write to one repository from two threads.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    public Task<CommitSha> FetchAsync(GitRepository repository, BranchName branch, CancellationToken cancellationToken)
    {
        EnsureSupported(repository);
        return FetchAsync(repository.Id.ToString(), repository.Url.ToString(), branch, cancellationToken);
    }

    public Task<GitSnapshot> ReadAsync(
        GitRepository repository, CommitSha commit, string directory, long maxBytes, CancellationToken cancellationToken)
    {
        EnsureSupported(repository);
        return ReadAsync(repository.Id.ToString(), commit, directory, maxBytes, cancellationToken);
    }

    /// <param name="cacheKey">Name of the bare clone's directory; one per repository.</param>
    /// <param name="url">Any URL or path libgit2 accepts; tests use local paths.</param>
    internal Task<CommitSha> FetchAsync(string cacheKey, string url, BranchName branch, CancellationToken cancellationToken) =>
        WithLockAsync(cacheKey, () => Fetch(cacheKey, url, branch, cancellationToken), cancellationToken);

    /// <param name="cacheKey">Name of the bare clone's directory, as passed to <see cref="FetchAsync(string, string, BranchName, CancellationToken)"/>.</param>
    internal Task<GitSnapshot> ReadAsync(
        string cacheKey, CommitSha commit, string directory, long maxBytes, CancellationToken cancellationToken) =>
        WithLockAsync(cacheKey, () => Read(cacheKey, commit, directory, maxBytes, cancellationToken), cancellationToken);

    private static void EnsureSupported(GitRepository repository)
    {
        if (repository.ProtectedAccessToken is not null)
        {
            throw new DeploymentFailedException(
                $"Repository '{repository.Name}' has an access token; private repositories are not supported yet.");
        }
    }

    private async Task<T> WithLockAsync<T>(string cacheKey, Func<T> work, CancellationToken cancellationToken)
    {
        var gate = locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(work, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private string CachePath(string cacheKey) => Path.GetFullPath(Path.Combine(options.Value.CacheDirectory, cacheKey));

    private CommitSha Fetch(string cacheKey, string url, BranchName branch, CancellationToken cancellationToken)
    {
        var path = CachePath(cacheKey);
        if (!Repository.IsValid(path))
        {
            Directory.CreateDirectory(path);
            Repository.Init(path, isBare: true);
        }

        using var repository = new Repository(path);
        var trackingRef = $"refs/remotes/{RemoteName}/{branch}";
        try
        {
            // The URL is set before each fetch, so a changed repository URL takes effect at once.
            var remote = repository.Network.Remotes[RemoteName];
            if (remote is null)
            {
                repository.Network.Remotes.Add(RemoteName, url);
            }
            else if (remote.Url != url)
            {
                repository.Network.Remotes.Update(RemoteName, r => r.Url = url);
            }

            // Only the stack's branch is fetched. Prune deletes its tracking ref when the branch is gone,
            // instead of deploying the last commit the cache knows.
            Commands.Fetch(
                repository,
                RemoteName,
                [$"+refs/heads/{branch}:{trackingRef}"],
                new FetchOptions
                {
                    Prune = true,
                    TagFetchMode = TagFetchMode.None,
                    OnTransferProgress = _ => !cancellationToken.IsCancellationRequested,
                },
                logMessage: null);
        }
        catch (LibGit2SharpException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DeploymentFailedException($"Fetching {url} failed: {ex.Message}");
        }

        var commit = repository.Refs[trackingRef]?.ResolveToDirectReference()?.Target as Commit
            ?? throw new DeploymentFailedException($"Branch '{branch}' does not exist in {url}.");
        return CommitSha.Parse(commit.Sha);
    }

    private GitSnapshot Read(string cacheKey, CommitSha sha, string directory, long maxBytes, CancellationToken cancellationToken)
    {
        var path = CachePath(cacheKey);
        if (!Repository.IsValid(path))
        {
            throw new DeploymentFailedException($"Commit {sha.Short} has not been fetched.");
        }

        using var repository = new Repository(path);
        var commit = repository.Lookup<Commit>(sha.Value)
            ?? throw new DeploymentFailedException($"Commit {sha.Short} has not been fetched.");

        var tree = commit.Tree;
        if (directory.Length > 0)
        {
            tree = commit.Tree[directory]?.Target as Tree
                ?? throw new DeploymentFailedException($"Directory '{directory}' does not exist at commit {commit.Sha[..7]}.");
        }

        var reader = new TreeReader(maxBytes, directory, cancellationToken);
        reader.Read(tree, prefix: "");
        return new GitSnapshot(sha, reader.Files, reader.Skipped);
    }

    private sealed class TreeReader(long maxBytes, string directory, CancellationToken cancellationToken)
    {
        private long totalBytes;

        public List<SourceFile> Files { get; } = [];

        public List<string> Skipped { get; } = [];

        public void Read(Tree tree, string prefix)
        {
            foreach (var entry in tree)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = prefix + entry.Name;
                switch (entry.TargetType)
                {
                    case TreeEntryTargetType.Tree:
                        Read((Tree)entry.Target, relative + "/");
                        break;
                    case TreeEntryTargetType.Blob when entry.Mode != Mode.SymbolicLink
                        && StackFilePath.TryParse(relative, out var filePath):
                        Files.Add(new SourceFile(filePath, ReadBlob((Blob)entry.Target), entry.Mode == Mode.ExecutableFile));
                        break;
                    default:
                        // Symbolic links could point outside the stack directory; submodules are other repositories.
                        Skipped.Add(relative);
                        break;
                }
            }
        }

        private byte[] ReadBlob(Blob blob)
        {
            // Checked before reading, so a huge file is never loaded into memory.
            totalBytes += blob.Size;
            if (totalBytes > maxBytes)
            {
                var where = directory.Length == 0 ? "the repository root" : $"'{directory}'";
                throw new DeploymentFailedException(
                    $"Files in {where} exceed the bundle limit of {maxBytes / 1024} KiB. " +
                    $"Keep only the stack's files in the directory of its compose file.");
            }

            using var content = blob.GetContentStream();
            using var buffer = new MemoryStream((int)blob.Size);
            content.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
