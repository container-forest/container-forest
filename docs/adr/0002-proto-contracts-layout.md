# ADR-0002. Contracts: only `.proto` files, packages without a prefix or version

- Status: accepted
- Date: 2026-09-29
- Supersedes point 4 of [ADR-0001](0001-solution-structure.md)

## Context

Under ADR-0001 the contracts lived in the C# project `ContainerForest.Contracts` at `Protos/container_forest/<module>/v<N>/`, generating both client and server from them. Extending `agent.proto` showed that:

- the C# project adds nothing but a place to generate code, and ties a language-independent contract to .NET;
- every consumer gets code it doesn't need: the agent a server base class, the aggregator a client;
- editors check proto files with Buf's rules (Zed does, for example), including `PACKAGE_DIRECTORY_MATCH`: a file's directory must follow the package name;
- the `container_forest.` prefix is only needed so names don't clash with other proto files in the same process. Our processes have nothing besides our own contracts and `google.protobuf.*`;
- a version in the package (`.v1`) lets the aggregator serve two incompatible versions of the protocol at once. We don't plan incompatible changes: the protocol only grows compatibly.

## Decision

1. `src/Contracts/` holds only `.proto` files, with no project. The directory follows the package name: `agent/agent.proto`, later `bot/bot.proto`. `src/Contracts` is the import root (`ProtoRoot`).
2. Each consumer generates its own code through `Grpc.Tools`, only the part it needs:
   - `Aggregator.Infrastructure`: messages, the server base class and the client (`GrpcServices="Both"`), with `Api` getting them through its reference to `Infrastructure`. The client is for the integration tests that connect to the aggregator as an agent: generating it separately in the test project doesn't work, since the same types from two assemblies clash (added 2026-09-29);
   - `Agent`: messages and the client (`GrpcServices="Client"`);
   - `DiscordBot`: messages and the `bot.proto` client (`GrpcServices="Client"`). The bot's tests generate the server themselves and reference the bot with `Aliases="bot"` (`extern alias`): that way the two copies of the generated types don't clash, and the server code doesn't end up in the bot (added 2026-09-30).
3. A package is the module's name without a prefix or version: `agent`, `bot`. The C# namespace is set with `csharp_namespace`: `ContainerForest.Contracts.<Module>`. A gRPC method's path looks like `/agent.AgentGateway/Connect`.
4. Contracts change only compatibly: new fields and new `oneof` cases, no renumbering or removal. A removed field is marked `reserved`. Until `main` has code using a contract, it's a draft and may change incompatibly.

## Consequences

- `Directory.Packages.props` aligns the versions of `Google.Protobuf`, `Grpc.Core.Api` and `Grpc.Tools` across projects.
- The generated types exist in separate assemblies of the aggregator and the agent. That's fine: they live in different processes and are never passed between them.
- If an incompatible change is ever needed, the aggregator and every agent will have to be upgraded at once, or a new package introduced then. Adding a version to the package name later costs as much as it would now.
- The Buf linter (editors such as Zed run it) is configured in `buf.yaml` at the root: the `STANDARD` set without `PACKAGE_VERSION_SUFFIX`. Other deliberate deviations are marked with a `// buf:lint:ignore <RULE>` comment with an explanation right in the `.proto`.
