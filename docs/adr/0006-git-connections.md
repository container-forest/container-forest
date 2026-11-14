# ADR-0006. Git connections: credentials and the environment scope belong to the connection, not the repository

- Status: accepted
- Date: 2026-10-03
- Changed: [ADR-0015](0015-image-updates.md) added an optional write token on the connection for image update commits.

## Context

Before this decision a repository was a resource shared by the whole system. Only an administrator added it and set its access token, and an Editor of any environment or stack could pick a repository for their stack. That caused two problems:

1. **Leaks between environments.** An Editor of environment A could bind their stack to a private repository an administrator had added for environment B. The aggregator fetched it with the administrator's token and deployed it on host A, where the Editor is root and reads everything. B's SOPS files weren't decrypted (each environment has its own keys), but the rest of the content leaked: configs, addresses, unencrypted `.env` files.
2. **Every new repository went through an administrator.** A developer with the Editor role on their application's environment couldn't connect it themselves.

The token was also tied to the repository's host rather than to the repository itself: changing the URL within the host kept the token, so whoever could change the URL could read other repositories with it.

Options considered:

1. **A list of environments on the repository, the token on the repository, changing the URL resets the token.** Closes the leak, but every new repository needs a new token. Gitea has no per-repository tokens at all: a token belongs to a user and is scoped to every repository that user sees. Requiring a token every time would mean pasting the same token again with no security gain.
2. **A git connection** (chosen): credentials, a URL prefix and a list of environments on a connection an administrator sets up. Editors add repositories under the connection without tokens.
3. **Listing repositories through the git server's API** (GitHub, Gitea, GitLab), so a developer picks from a list instead of typing a URL. Convenient, but every provider's API is different; deferred, and option 2 doesn't get in its way.

## Decision

1. **A connection** (`GitConnection`, table `GitConnections`) has:
   - a name;
   - a **URL prefix**: `https://gitea.example.com/team-a/` (an organisation), `https://gitea.example.com/` (a whole server) or one repository's URL;
   - optional **credentials**: a user name and a token encrypted with Data Protection (`SecretPurpose.GitAccessToken`, ADR-0003), never returned;
   - the **environments** where the connection is allowed (table `GitConnectionEnvironments`), or "all environments";
   - an optional **webhook secret** for deliveries from an organisation (point 7).

   Only an administrator creates, changes and deletes connections. A connection without a token makes sense too: it limits environments and marks the host as trusted (point 6).

2. **A repository** has a URL and an optional connection; the URL lies under the connection's prefix. Repositories no longer have tokens. The prefix is compared by path segments, case-insensitively (as Gitea and GitHub do) and ignoring a trailing `.git`: `team-a/` doesn't cover `team-ab/`. Repository URLs and prefixes are `https://` only, with no credentials, query, fragment or encoded `/` or `\` in the path.

3. **Where a repository can be used.** A repository without a connection (public) can be used in any environment. One with a connection only in environments where the connection is allowed. The check is in three places:
   - creating a stack and changing its source (`StackService`): an unavailable repository is a 404, like a nonexistent one;
   - `GitFetchStep` before the fetch: for a stack created before its environment was removed from the connection, the deployment fails with an explanation and the token never goes to the git server; the same applies to webhooks and rehydrate;
   - webhooks: such a stack gets the result `NotAllowed`.

4. **Who manages repositories:**
   - an administrator, or a user with the Editor role (on the environment or on a stack) in an environment where the connection is allowed, can **add** a repository; one without a connection, in any environment;
   - an administrator, or a user with the deploy permission on **every** stack of the repository who could add the repository with both the current and the new connection, can **change** it (name, URL, connection) and **create or disable its webhook**. Changing a repository changes what its stacks deploy, so whoever is responsible for them decides; otherwise it's simpler to create a new repository;
   - the **list** of repositories: an administrator sees all of them, others those available in environments where they're an Editor (on the environment or a stack);
   - repository names aren't unique: a system-wide uniqueness check would reveal the names of other people's repositories.

5. **Credentials go only under the prefix.** libgit2 gets the token through a callback (`CredentialsFor`), only for an HTTPS request whose host, port and path lie under the connection's prefix. A server that redirects a fetch outside the prefix doesn't get the token.

6. **SSRF protection.** Any Editor can add a repository without a connection, so before fetching it the aggregator resolves the host and refuses if any address is loopback, private (RFC 1918, ULA, CGNAT), link-local (including the cloud metadata `169.254.169.254`), multicast or unspecified. A connection's host is trusted: an administrator set it. For an internal git server without a connection, an administrator turns on `Git:AllowPrivateHosts`. The check doesn't fully protect against DNS rebinding (resolving and libgit2's connection are separate steps), but it cuts off probing the internal network. A fetch error for such a repository is generalised in the deployment log; libgit2's details go to the aggregator's log.

7. **Organisation webhooks.** A webhook secret can be created on a connection: the git server sends pushes from any repository of the organisation to `POST /api/webhooks/connections/{connectionId}`. The aggregator checks the signature with the connection's secret, takes the repository's URL from the body (`repository.clone_url` and `html_url` on GitHub and Gitea, `project.git_http_url` and `web_url` on GitLab; the body is authenticated by the signature) and deploys the stacks of the connection's repositories with that URL, branch and `DeployOnPush` turned on. The per-repository webhook (`/api/webhooks/{repositoryId}`) stays, including for repositories without a connection.

8. **A git cache limit.** A repository's bare clone larger than `Git:MaxCacheBytes` (1 GiB by default) is deleted and the deployment fails with an explanation: any Editor can add a repository without a connection, and the aggregator's disk is shared.

9. **Migration.** Every repository with a token gets its own connection with the same id and name, its URL as the prefix, its token and user name, and the environments where it already has stacks. The "one token per repository" behaviour is kept and nothing breaks. An administrator can merge them into a connection per organisation later: create a connection and move the repositories to it. Repositories without a token stay without a connection, i.e. available in every environment, as before.

## Setting up Gitea

1. A service user (e.g. `forest-team-a`) without write access, added to a team with read access to the needed repositories or the whole organisation.
2. That user's token with the `read:repository` scope.
3. A connection with the prefix `https://gitea.example.com/team-a/`, this token and the team's environments.
4. An organisation webhook with the connection's secret (type `application/json`, the push event).

Different levels of trust mean different connections with different service users. Membership in Gitea decides what the token reads; the connection records which environments may use it.

## Consequences

- The trust boundary is explicit: by creating a connection, an administrator allows the listed environments to deploy everything its token reads under the prefix. An environment's Editor is root on the host anyway, so a repository allowed in their environment gives them nothing beyond that; the boundary between environments isn't crossed.
- A developer with the Editor role connects a new application themselves: they add the repository under the connection, create a stack and turn on deploy on push. An administrator is needed only once per connection.
- The API changes incompatibly: `/api/repositories/{id}/access-token` is removed, and `RepositoryResponse` has `connectionId`, `connectionName` and `canManage` instead of `hasAccessToken` and `accessUserName`. The panel is updated in the same PR; the bot doesn't use repositories.
- An internal git server that worked so far as a public repository without a token needs a connection after the upgrade (or `Git:AllowPrivateHosts=true`): the deployment error says so.
- Changing a connection's prefix can't leave its repositories outside it (409), and changing the host while a token is set requires deleting the token first, as it did for repositories.
- Deleting a connection that repositories use is a 409.
