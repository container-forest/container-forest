using System.CommandLine;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>How a finished deployment ends a command: its error on standard error, and exit code 1 unless it succeeded.</summary>
internal static class DeploymentResult
{
    public static async Task<int> ReportAsync(CommandContext context, DeploymentDetailsResponse deployment)
    {
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(deployment, _ => []);
        }

        var commit = CliOutput.ShortCommit(deployment.Commit);
        switch (deployment.Status)
        {
            case DeploymentStatus.Succeeded:
                context.Output.Note($"Deployed {commit}.");
                return ExitCodes.Ok;
            case DeploymentStatus.Queued or DeploymentStatus.Running:
                context.Output.Note($"The deployment is {deployment.Status.ToString()!.ToLowerInvariant()}.");
                return ExitCodes.Ok;
            default:
                throw new CliException($"The deployment {deployment.Status.ToString()?.ToLowerInvariant()}: {deployment.Error ?? "see its log"}");
        }
    }
}

/// <summary>Who endorsed a deployment's commit, in a word (ADR-0011).</summary>
internal static class Endorsement
{
    public static string? Describe(CommitEndorsement? status, string? signedBy) => status switch
    {
        CommitEndorsement.Signed => $"signed by {signedBy}",
        CommitEndorsement.ApprovedUnsigned => "approved unsigned",
        CommitEndorsement.AlreadyDeployed => "already deployed",
        CommitEndorsement.Rejected => "rejected",
        _ => null,
    };
}

internal sealed class DeploymentListCommand() : StackCommand("list", "List the stack's deployments, newest first.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var deployments = await context.Api.ListDeploymentsAsync(stack.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            deployments,
            items =>
            {
                var table = new Table("ID", "STATUS", "TRIGGER", "PULL", "COMMIT", "ENDORSED", "QUEUED", "ERROR");
                foreach (var d in items)
                {
                    table.AddRow(d.Id?.ToString(), d.Status?.ToString().ToLowerInvariant(), d.Trigger?.ToString(), d.PullPolicy?.ToString().ToLowerInvariant(), CliOutput.ShortCommit(d.Commit), Endorsement.Describe(d.Endorsement, d.SignedBy), CliOutput.Time(d.QueuedAt), d.Error);
                }

                return table;
            },
            $"{stack.Name} has never been deployed.");
        return ExitCodes.Ok;
    }
}

internal sealed class DeploymentShowCommand() : ForestCommand("show", "Show a deployment.")
{
    private readonly Argument<Guid> id = new("deployment") { Description = "Deployment id." };

    protected override void Configure(Command command) => command.Add(id);

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var deployment = await context.Api.GetDeploymentAsync(context.Value(id), context.CancellationToken);
        await context.Output.ItemAsync(deployment, d =>
        [
            ("Id", d.Id?.ToString()),
            ("Stack", d.StackId?.ToString()),
            ("Status", d.Status?.ToString().ToLowerInvariant()),
            ("Trigger", d.Trigger?.ToString()),
            ("Pull", d.PullPolicy?.ToString().ToLowerInvariant()),
            ("Commit", d.Commit),
            ("Queued", CliOutput.Time(d.QueuedAt)),
            ("Started", CliOutput.Time(d.StartedAt)),
            ("Finished", CliOutput.Time(d.FinishedAt)),
            ("Endorsed", Endorsement.Describe(d.Endorsement?.Status, d.Endorsement?.SignedBy)),
            ("Signing key", d.Endorsement?.KeyFingerprint),
            ("Not endorsed because", d.Endorsement?.Problem?.ToString()),
            ("Error", d.Error),
        ]);
        return ExitCodes.Ok;
    }
}

internal sealed class DeploymentLogsCommand() : ForestCommand("logs", "Show a deployment's log; with -f until it finishes.")
{
    private readonly Argument<Guid> id = new("deployment") { Description = "Deployment id." };
    private readonly Option<bool> follow = new("--follow", "-f") { Description = "Keep showing new lines until the deployment finishes." };

    protected override void Configure(Command command)
    {
        command.Add(id);
        command.Add(follow);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var watcher = new DeploymentWatcher(context.Api, context.Output, context.Host.Time);
        var deployment = await watcher.WatchAsync(context.Value(id), context.Value(follow), context.CancellationToken);
        return context.Value(follow) ? await DeploymentResult.ReportAsync(context, deployment) : ExitCodes.Ok;
    }
}
