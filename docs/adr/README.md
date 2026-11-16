# Architecture decision records (ADR)

Short records of decisions that were argued over, with their context and the options rejected. A new decision that changes the [list of decisions](../design/decisions.md) goes here under the next number.

| ADR | Decision |
|---|---|
| [0001](0001-solution-structure.md) | The repository layout and the aggregator's layers |
| [0002](0002-proto-contracts-layout.md) | Contracts: only `.proto` files, packages without a prefix or version |
| [0003](0003-secrets-at-rest.md) | Secrets in the aggregator's database are encrypted with ASP.NET Core Data Protection |
| [0004](0004-stack-directory-in-place.md) | The stack directory isn't replaced: bundle files are updated in place using a manifest |
| [0005](0005-caddy-entry-point.md) | The single entry point is Caddy in the panel's image: the SPA, `/api`, gRPC for agents and the bot, automatic TLS |
| [0006](0006-git-connections.md) | Git connections: credentials and allowed environments belong to the connection; Editors add repositories under it |
| [0007](0007-api-tokens.md) | Personal API tokens with restrictions (read-only, environments, expiry); permissions checked through an `Actor` with the credentials' scope |
| [0008](0008-cli.md) | The `forest` CLI: browser sign-in by a device flow with a loopback signal, the token created only when issued; .NET + System.CommandLine + Kiota |
| [0009](0009-passwordless-sign-in.md) | Passwordless sign-in: ASP.NET Core Identity passkeys in .NET 10; sign-in and linking through Discord OAuth2 with `DiscordLink` as the single identity |
| [0010](0010-git-secrets-filter.md) | A git filter for SOPS secrets: plain text in an Editor's clone, standard sops files in git; the aggregator decrypts only with the keys of environments where the person is an Editor, and the token needs the `secrets` flag |
| [0011](0011-signed-commits.md) | Endorsing stack content with SSH commit signatures: users' keys with a limited scope, verification before decrypting secrets, descending from the deployed commit for automatic deployments, an image digest mode |
| [0012](0012-stack-isolation.md) | Stack isolation on the host: the environment administrator role, a compose policy checked against the agent's normalised model with allowances and an exemption for endorsed content, SOPS keys per stack |
| [0013](0013-system-settings.md) | System settings: typed sections, environment variables take precedence, write-only secrets, fresh identity confirmation for sensitive changes |
| [0014](0014-aggregator-backup.md) | An optional aggregator backup: an archive of the database and the key ring encrypted to an offline key in an S3-compatible bucket, refining invariant 1 |
| [0015](0015-image-updates.md) | Image updates by the aggregator from a stack's policy, committed to the stack's repository, endorsed through an endorsed parent commit, with explicit tag requests that pin |
| [0016](0016-unmanaged-stacks.md) | Unmanaged stacks: compose projects started by hand in directories the host's administrator explicitly opened to the agent; the aggregator names a stack only by name, permissions come from the environment role; removing a stopped container |
| [0017](0017-metrics.md) | Resource usage metrics: the agent reads cgroup v2 and /proc itself (Docker stats as the fallback), the aggregator keeps an hour in memory and minutes and hours in a separate metrics.db outside the backup, alerts through notification rules |
