# Git and git connections

The decision: [ADR-0006](../adr/0006-git-connections.md). Aggregator paths are relative to `src/Aggregator`.

## Clones

- **`IGitSource`** has two calls: `FetchAsync` returns the branch's commit, `ReadAsync` reads the compose file's directory from it. `GitFetchStep` records the commit in the deployment between them, so a deployment that fails on the files (no directory, bundle limit exceeded) still shows its commit.
- **Cache:** a bare clone per repository in `Git:CacheDirectory` (`git-cache` in the working directory by default), fetching only the stack's branch with `Prune`; files are read from the commit tree without a checkout. A clone larger than `Git:MaxCacheBytes` (1 GiB) is deleted and the deployment fails.
- **Bundle:** the compose file's directory (the whole repository for a compose file at the root) within `Deployments:MaxBundleBytes` (3 MiB). Symbolic links and submodules are skipped with a warning.
- **Protocol:** LibGit2Sharp from NuGet doesn't support SSH, only HTTPS.

## Connections

`GitConnection` (tables `GitConnections`, `GitConnectionEnvironments`), `GitConnectionService`, `/api/git-connections/*`.

- **Fields:** a name (unique), a URL prefix, the allowed environments or "all", the organisation webhook secret, an optional token with a user name (`x-access-token` by default). The token is encrypted with `ISecretProtector` (`SecretPurpose.GitAccessToken`) and never returned, only `hasAccessToken`.
- **Access:** only administrators manage connections. An editor sees the connections of their environments, and only their own environments in `environmentIds`.
- **Restrictions on changes:**
  - The prefix can't leave the connection's repositories outside it (409).
  - The host can't change while a token is set (400).
  - A connection with repositories can't be deleted (409, FK `Restrict`).

## Repositories

- **Model:** `GitRepository` has a URL and an optional `ConnectionId`. Without one the repository is public and available to every environment. Names aren't unique.
- **URL:**
  - The URL must be under the prefix. `GitUrl.Covers` compares host and port and path segments, case-insensitively and ignoring `.git`.
  - `GitUrl.Parse` rejects credentials, a query, a fragment and any `%`-encoding in the path, since an encoded `..` or `/` would get around the prefix.
- **Permissions** (`RepositoryAccess`). "Editor environments" are those where the user is an Editor on the environment or on a stack (`AccessPolicy.EditorEnvironments`).
  - An administrator can add a repository, or an editor if the connection is allowed in one of their environments. Any editor can add a public one.
  - An administrator can change a repository and its webhook, or someone with `Permission.Deploy` on every stack of the repository (`RepositoryResponse.canManage`).
  - An inaccessible connection or repository is a 404.

- **Write token** (ADR-0015): the optional `GitConnection.ProtectedPushToken` (`SecretPurpose.GitPushToken`) and `PushUserName`, `PUT|DELETE /api/git-connections/{id}/push-credential`, administrators only, audited (`GitPushCredentialChanged`). The aggregator pushes image update commits with it, and never uses the read token for that (`GitAccessResolver`). Writing is `IGitWriter` in the same bare clone as the fetch ([image-updates.md](image-updates.md#check-and-commit)).

## Fetching and protecting the token

- **Check:** `GitFetchStep` checks `IsUsableIn` before the fetch and passes a `GitAccess` (the connection's token with `Scope` = the prefix; `IsTrustedHost` for repositories under a connection).
- **Token:** `LibGit2SharpGitSource.CredentialsFor` hands it out only over HTTPS, to URLs under the prefix.
- **SSRF:** for a repository without a connection, `GitHostGuard` resolves the host and refuses loopback, private, CGNAT, link-local (cloud metadata) and multicast addresses unless `Git:AllowPrivateHosts` is set. A fetch error for such a repository is generalised in the deployment log; libgit2's details go to the aggregator's log.

## Migrations

`GitConnections` (schema), `MoveRepositoryTokensToConnections` (each repository with a token gets its own connection with the same id, its URL as the prefix and its stacks' environments), `RemoveRepositoryAccessTokens`. Two of them rebuild the `Repositories` table in SQLite outside a transaction, hence EF's warning at start.
