using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Agents;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>Sends the bundle to the environment's agent, which writes the files and runs docker compose up.</summary>
public sealed class SendToAgentStep(IAgentChannel agents, DeploymentOptions options) : IDeployStep
{
    public string Name => "Deploy on agent";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        // Compose output arrives on the connection's receive loop while this step waits. The deployment
        // is not thread-safe, so lines are collected here and copied into its log afterwards.
        var output = new ConcurrentQueue<string>();
        try
        {
            await agents.DeployStackAsync(
                context.Environment.Id, context.Bundle, output.Enqueue, options.AgentTimeout, cancellationToken);
        }
        finally
        {
            while (output.TryDequeue(out var line))
            {
                context.Info(line);
            }
        }
    }
}
