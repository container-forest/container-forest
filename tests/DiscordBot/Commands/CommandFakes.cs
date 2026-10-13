extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Tests.Commands;

/// <summary>
/// The aggregator in memory for command tests: answers from its lists, records calls, and throws
/// <see cref="Failure"/> from every call when it is set.
/// </summary>
internal sealed class StubForestClient : IForestClient
{
    public List<ForestEnvironment> Environments { get; } = [];

    public Dictionary<string, List<ForestContainer>> Containers { get; } = [];

    public Dictionary<string, List<ForestStack>> Stacks { get; } = [];

    /// <summary>What a stack command answers with.</summary>
    public ForestStackResult StackResult { get; set; } = new(Error: null, []);

    public List<string> Calls { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When set, calls wait for it, honouring cancellation.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public string LinkedUserName { get; set; } = "root";

    /// <summary>What a log read answers with.</summary>
    public List<ForestLogLine> LogLines { get; } = [];

    public async Task<ForestCaller?> GetCallerAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        await Enter($"caller {discordUserId}", cancellationToken);
        return null;
    }

    public async Task<string> LinkAccountAsync(
        ulong discordUserId, string discordUserName, string code, CancellationToken cancellationToken)
    {
        await Enter($"link {discordUserId} {discordUserName} {code}", cancellationToken);
        return LinkedUserName;
    }

    public async Task<IReadOnlyList<ForestEnvironment>> ListEnvironmentsAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        await Enter($"environments {discordUserId}", cancellationToken);
        return Environments;
    }

    public async Task<IReadOnlyList<ForestContainer>> ListContainersAsync(
        ulong discordUserId, string environmentId, CancellationToken cancellationToken)
    {
        await Enter($"containers {discordUserId} {environmentId}", cancellationToken);
        return Containers.TryGetValue(environmentId, out var containers) ? containers : [];
    }

    public async Task ControlContainerAsync(
        ulong discordUserId,
        string environmentId,
        string container,
        ContainerAction action,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await Enter(
            $"{action.ToString().ToLowerInvariant()} {discordUserId} {environmentId} {container} {timeoutSeconds?.ToString() ?? "default"}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<ForestLogLine>> GetContainerLogsAsync(
        ulong discordUserId, string environmentId, string container, int tailLines, CancellationToken cancellationToken)
    {
        await Enter($"logs {discordUserId} {environmentId} {container} {tailLines}", cancellationToken);
        return LogLines;
    }

    public async Task<IReadOnlyList<ForestStack>> ListStacksAsync(
        ulong discordUserId, string environmentId, CancellationToken cancellationToken)
    {
        await Enter($"stacks {discordUserId} {environmentId}", cancellationToken);
        return Stacks.TryGetValue(environmentId, out var stacks) ? stacks : [];
    }

    public async Task<ForestStackResult> ControlStackAsync(
        ulong discordUserId,
        string environmentId,
        string stack,
        StackAction action,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await Enter(
            $"stack-{action.ToString().ToLowerInvariant()} {discordUserId} {environmentId} {stack} {timeoutSeconds?.ToString() ?? "default"}",
            cancellationToken);
        return StackResult;
    }

    /// <summary>What DeployStack answers with.</summary>
    public string DeploymentId { get; set; } = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";

    /// <summary>What GetDeployment answers with.</summary>
    public ForestDeployment Deployment { get; set; } =
        new(DeploymentStatus.Queued, null, null, DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), null);

    public async Task<string> DeployStackAsync(
        ulong discordUserId, string environmentId, string stack, bool pull, CancellationToken cancellationToken)
    {
        await Enter($"deploy {discordUserId} {environmentId} {stack} {(pull ? "pull" : "missing")}", cancellationToken);
        return DeploymentId;
    }

    /// <summary>States GetDeployment answers with one by one before falling back to <see cref="Deployment"/>.</summary>
    public Queue<ForestDeployment> DeploymentSequence { get; } = new();

    public async Task<ForestDeployment> GetDeploymentAsync(ulong discordUserId, string deploymentId, CancellationToken cancellationToken)
    {
        await Enter($"deployment {discordUserId} {deploymentId}", cancellationToken);
        return DeploymentSequence.TryDequeue(out var next) ? next : Deployment;
    }

    private async Task Enter(string call, CancellationToken cancellationToken)
    {
        Calls.Add(call);
        if (Hold is not null)
        {
            await Hold.Task.WaitAsync(cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>Records what a command did with the interaction, in order.</summary>
internal sealed class RecordingInteraction(StubForestClient? forest = null) : ICommandInteraction
{
    public static readonly BotUser Dmytro = new(238_471_902_384_719_872, "dmytro_s");

    public BotUser User => Dmytro;

    public List<string> Events { get; } = [];

    public BotReply? Reply { get; private set; }

    public Task DeferAsync(CancellationToken cancellationToken)
    {
        // Proves the deferral comes before any call to the aggregator.
        Events.Add($"defer after {forest?.Calls.Count ?? 0} calls");
        return Task.CompletedTask;
    }

    public Task RespondAsync(BotReply reply, CancellationToken cancellationToken)
    {
        Events.Add("respond");
        Reply = reply;
        return Task.CompletedTask;
    }
}
