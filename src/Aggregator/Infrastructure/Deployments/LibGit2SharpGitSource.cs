using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;
using LibGit2Sharp;
using ContainerForest.Aggregator.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GitRepository = ContainerForest.Aggregator.Domain.Repositories.GitRepository;

namespace ContainerForest.Aggregator.Infrastructure.Deployments;

/// <summary>Configuration section "Git" (env: CF_GIT__...).</summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>Where bare clones of stack repositories are kept between deployments; relative to the working directory.</summary>
    public string CacheDirectory { get; set; } = "git-cache";

    /// <summary>
    /// Largest bare clone of one repository. A larger one is deleted and the deployment fails: editors can add public
    /// repositories, and the aggregator's disk holds its database too.
    /// </summary>
    public long MaxCacheBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Lets public repositories (without a connection) be fetched from private, loopback and link-local addresses.
    /// Off by default: anyone with the Editor role can add such a repository (ADR-0006).
    /// </summary>
    public bool AllowPrivateHosts { get; set; }

    internal bool IsValid() => MaxCacheBytes > 0 && !string.IsNullOrWhiteSpace(CacheDirectory);
}

/// <summary>
/// Keeps a bare clone of each repository and fetches only the stack's branch into it. Files are read from
/// the commit's tree directly: there is no working copy, so nothing from the repository is ever checked out on disk.
/// </summary>
internal sealed partial class LibGit2SharpGitSource(
    IOptions<GitOptions> options, GitHostGuard hostGuard, ILogger<LibGit2SharpGitSource> logger) : IGitSource
{
    private const string RemoteName = "origin";

    // LibGit2Sharp must not write to one repository from two threads.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    public async Task<CommitSha> FetchAsync(
        GitRepository repository, GitAccess access, BranchName branch, CancellationToken cancellationToken)
    {
        if (!access.IsTrustedHost && !options.Value.AllowPrivateHosts)
        {
            await hostGuard.EnsurePublicAsync(repository.Url, cancellationToken);
        }

        return await FetchAsync(
            repository.Id.ToString(), repository.Url.ToString(), access.Credentials, branch, cancellationToken, detailedErrors: access.IsTrustedHost);
    }

    public Task<GitSnapshot> ReadAsync(
        GitRepository repository, CommitSha commit, string directory, long maxBytes, CancellationToken cancellationToken) =>
        ReadAsync(repository.Id.ToString(), commit, directory, maxBytes, cancellationToken);

    /// <param name="cacheKey">Name of the bare clone's directory; one per repository.</param>
    /// <param name="url">Any URL or path libgit2 accepts; tests use local paths.</param>
    /// <param name="detailedErrors">
    /// Show libgit2's error in the deployment log. Only for hosts an administrator chose: for any other host the
    /// details would tell an editor what answers on the network.
    /// </param>
    internal Task<CommitSha> FetchAsync(
        string cacheKey,
        string url,
        GitCredentials? credentials,
        BranchName branch,
        CancellationToken cancellationToken,
        bool detailedErrors = true) =>
        WithLockAsync(cacheKey, () => Fetch(cacheKey, url, credentials, branch, detailedErrors, cancellationToken), cancellationToken);

    /// <param name="cacheKey">Name of the bare clone's directory, as passed to <see cref="FetchAsync(string, string, GitCredentials?, BranchName, CancellationToken, bool)"/>.</param>
    internal Task<GitSnapshot> ReadAsync(
        string cacheKey, CommitSha commit, string directory, long maxBytes, CancellationToken cancellationToken) =>
        WithLockAsync(cacheKey, () => Read(cacheKey, commit, directory, maxBytes, cancellationToken), cancellationToken);

    /// <summary>
    /// Answers libgit2's request for credentials only under the connection's URL prefix (ADR-0006): a server that
    /// redirects the fetch elsewhere, even on the same host, must not receive the token.
    /// </summary>
    internal static Credentials CredentialsFor(string requestUrl, GitCredentials credentials)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)
            || request.Scheme != Uri.UriSchemeHttps
            || request.AbsolutePath.Contains('%', StringComparison.Ordinal)
            || !GitUrl.Covers(credentials.Scope, request))
        {
            // LibGit2Sharp turns an exception from the callback into a failed fetch with this message.
            throw new InvalidOperationException($"The access token is only sent under {credentials.Scope} over HTTPS.");
        }

        return new UsernamePasswordCredentials { Username = credentials.UserName, Password = credentials.Token };
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

    private CommitSha Fetch(
        string cacheKey, string url, GitCredentials? credentials, BranchName branch, bool detailedErrors, CancellationToken cancellationToken)
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
                    CredentialsProvider = credentials is null
                        ? null
                        : (requestUrl, _, _) => CredentialsFor(requestUrl, credentials),
                },
                logMessage: null);
        }
        catch (LibGit2SharpException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogFetchFailed(url, ex.Message);

            // libgit2 retries a rejected token several times and then reports the replays, not the rejection.
            var authentication = ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase);
            var hint = !authentication ? ""
                : credentials is null ? " The repository may be private: add it under a git connection with a token."
                : " Check the access token of the repository's connection and its permissions.";
            var reason = detailedErrors ? ex.Message
                : authentication ? "authentication required"
                : "the server could not be reached or did not answer as a git server";
            throw new DeploymentFailedException($"Fetching {url} failed: {reason}.{hint}");
        }

        var commit = repository.Refs[trackingRef]?.ResolveToDirectReference()?.Target as Commit
            ?? throw new DeploymentFailedException($"Branch '{branch}' does not exist in {url}.");
        var sha = CommitSha.Parse(commit.Sha);
        repository.Dispose();
        EnsureCacheSize(path, url);
        return sha;
    }

    /// <summary>Deletes a clone over <see cref="GitOptions.MaxCacheBytes"/>; it would be fetched whole again next time.</summary>
    private void EnsureCacheSize(string path, string url)
    {
        var limit = options.Value.MaxCacheBytes;
        var size = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        if (size <= limit)
        {
            return;
        }

        LogCacheTooLarge(url, size, limit);
        DeleteClone(path);
        throw new DeploymentFailedException(
            $"The repository {url} takes {size / (1024 * 1024)} MiB, more than the aggregator keeps for one repository " +
            $"({limit / (1024 * 1024)} MiB). Keep large files out of it, or raise Git:MaxCacheBytes.");
    }

    private static void DeleteClone(string path)
    {
        // Git writes object files read-only, which Directory.Delete refuses on Windows.
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(path, recursive: true);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Fetching {Url} failed: {Reason}")]
    private partial void LogFetchFailed(string url, string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "The clone of {Url} takes {Size} bytes, over the limit of {Limit}; deleted it")]
    private partial void LogCacheTooLarge(string url, long size, long limit);

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
