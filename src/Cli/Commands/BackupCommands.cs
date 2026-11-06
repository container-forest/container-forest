using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

internal sealed class BackupStatusCommand() : ForestCommand("status", "Show whether backups run and how the latest ones went (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var status = await context.Api.GetBackupStatusAsync(context.CancellationToken);
        await context.Output.ItemAsync(status, s =>
        [
            ("Enabled", CliOutput.YesNo(s.Enabled)),
            ("Running", CliOutput.YesNo(s.IsRunning)),
            ("Last success", CliOutput.Time(s.LastSucceededAt)),
            ("Next run", CliOutput.Time(s.NextRunAt)),
            ("Latest run", s.Runs is [var latest, ..] ? Describe(latest) : "-"),
            ("Problems", s.Problems is { Count: > 0 } problems ? string.Join(" ", problems) : "-"),
        ]);
        return ExitCodes.Ok;
    }

    private static string Describe(BackupRunResponse run) =>
        $"{run.Outcome?.ToString().ToLowerInvariant()} {CliOutput.Time(run.FinishedAt)}{(run.Error is null ? "" : $": {run.Error}")}";
}

internal sealed class BackupRunCommand() : ForestCommand("run", "Make a backup now and wait for it (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        context.Output.Note("Making a backup...");
        var run = await context.Api.RunBackupAsync(context.CancellationToken);
        if (run.Outcome != BackupOutcome.Succeeded)
        {
            throw new CliException($"The backup failed: {run.Error ?? "see the aggregator's log"}");
        }

        await context.Output.ItemAsync(run, r =>
        [
            ("Object", r.ObjectKey),
            ("Size", r.SizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Finished", CliOutput.Time(r.FinishedAt)),
        ]);
        return ExitCodes.Ok;
    }
}
