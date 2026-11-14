# ADR-0001. The repository layout and the aggregator's layers

- Status: accepted; point 4 superseded by [ADR-0002](0002-proto-contracts-layout.md)
- Date: 2026-09-29

## Context

The README marked the repository layout as "not settled". Before writing code we had to decide:

- how to split the aggregator into projects;
- where contracts live and whether a shared project (SharedKernel) is needed;
- how the web panel gets the HTTP API's types;
- where the frontend and the tests live.

## Decision

```
ContainerForest.slnx
Directory.Build.props         shared properties of .NET projects
Directory.Packages.props      central NuGet version management
global.json
src/
  Contracts/                  .proto only (see ADR-0002)
  Aggregator/
    Domain/                   ContainerForest.Aggregator.Domain
    Application/              ContainerForest.Aggregator.Application
    Infrastructure/           ContainerForest.Aggregator.Infrastructure
    Api/                      ContainerForest.Aggregator.Api (composition root)
  Agent/                      ContainerForest.Agent
  DiscordBot/                 ContainerForest.DiscordBot
  Web/                        Vite + React + TS
tests/                        mirrors src/; projects are created with their first test
deploy/
docs/
```

1. **One top-level folder in `src/` is one separately deployed application.** The exception is `Contracts`, with the shared `.proto` files. We don't add groups like `Clients/`, since they'd share no assembly, code or way of deploying.
2. **The aggregator is split into layers.** Dependencies point only inwards: `Domain ← Application ← Infrastructure ← Api`.
   - `Domain` has no dependencies.
   - `Application` holds use cases, commands, the authorization decorator, `DeployPipeline`, `IDeployStep` and ports, such as the channel to the agent. It doesn't reference `Contracts`.
   - `Infrastructure` holds EF Core, LibGit2Sharp, calling SOPS, `AgentRegistry` and mapping domain commands to proto messages.
   - `Api` holds REST, SignalR, gRPC services, background services and DI.
3. **No SharedKernel.** The domain exists only in the aggregator. The agent and the bot share only the contracts with it. The agent validates the stack name and file paths itself, as a second line of defence, so duplicating those few lines is deliberate.
4. **`Contracts` holds only `.proto` files.** Directories follow the package name: `Protos/container_forest/<module>/v<N>/`. Code is generated through `Grpc.Tools` with `GrpcServices="Both"`, so every consumer has one assembly.
5. **HTTP DTOs live in `Api`, and the web panel gets them through OpenAPI.**
   - `Microsoft.AspNetCore.OpenApi` describes the API.
   - `Microsoft.Extensions.ApiDescription.Server` writes the document to `src/Aggregator/Api/openapi/` during the build. The document is committed, so contract changes show in the PR diff.
   - `src/Web` generates types (`openapi-typescript`) and a client (`openapi-fetch`) from it.
   - OpenAPI doesn't describe SignalR messages. Their TS types are written by hand for now.
6. **The aggregator is called `Aggregator` in code**, as in the Docker image names and the documentation.
7. **Build.**
   - `TargetFramework`, `Nullable` and `ImplicitUsings` are set once in `Directory.Build.props`.
   - Package versions are set only in `Directory.Packages.props`.
   - Warnings become errors only in CI (`CI=true`), so they don't get in the way locally.

## Consequences

- The pipeline step implementations spread across layers. The interface and `DeployPipeline` are in `Application`, while steps with external dependencies (`GitFetchStep`, `SopsDecryptStep`) are in `Infrastructure`.
- To generate the OpenAPI document, the `Api` build runs the application. So starting it must not require external resources. If configuration with `ValidateOnStart()` or the database gets in the way, those checks have to be turned off for document generation.
- Changed a DTO: rebuild `Api`, commit the updated `openapi/*.json` and regenerate the client in `src/Web`.

## Rejected options

- **A SharedKernel.** There's no shared code besides the contracts, and an empty project "for later" ends up collecting everything.
- **HTTP DTOs in `Contracts`.** No .NET consumer other than the aggregator uses them.
- **`web/` at the repository root.** Technically equivalent. `src/Web` was chosen so all application code is in `src/`.
- **Separate Presentation and Host projects for the aggregator.** An extra level for a system of this size.
