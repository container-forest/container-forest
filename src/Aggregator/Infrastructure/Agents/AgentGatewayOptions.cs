namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>Configuration section "AgentGateway" (env: CF_AGENTGATEWAY__...).</summary>
public sealed class AgentGatewayOptions
{
    public const string SectionName = "AgentGateway";

    /// <summary>How long a command waits for the agent's CommandResult.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a new connection may take to send its Hello.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A connection with no messages for this long is considered dead. The agent sends a heartbeat
    /// every 15 s, so this allows two missed heartbeats.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long restart and down of a stack wait for the agent. Compose stops containers in dependency order,
    /// each with its own stop timeout, so this is not derived from it. The agent itself ends compose after its
    /// Agent:ComposeTimeout (15 min); the extra minute lets that answer arrive first.
    /// </summary>
    public TimeSpan StackCommandTimeout { get; set; } = TimeSpan.FromMinutes(16);

    internal bool IsValid() =>
        CommandTimeout > TimeSpan.Zero && HelloTimeout > TimeSpan.Zero && IdleTimeout > TimeSpan.Zero
        && StackCommandTimeout > TimeSpan.Zero;
}
