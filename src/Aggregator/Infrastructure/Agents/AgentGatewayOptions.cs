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

    internal bool IsValid() =>
        CommandTimeout > TimeSpan.Zero && HelloTimeout > TimeSpan.Zero && IdleTimeout > TimeSpan.Zero;
}
