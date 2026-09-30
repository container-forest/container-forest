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

    public List<string> Calls { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When set, calls wait for it, honouring cancellation.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public string LinkedUserName { get; set; } = "root";

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
