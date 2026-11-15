using System.CommandLine;
using System.Globalization;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;
using Microsoft.Kiota.Abstractions.Serialization;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// <c>forest container stats [--stack S] [--watch]</c>: running containers' usage from the agent's latest sample, like
/// docker stats but for any environment. CPU is a percentage of one core, as docker shows it.
/// </summary>
internal sealed class ContainerStatsCommand() : EnvironmentCommand("stats", "Show the resource usage of running containers.")
{
    private readonly Option<string?> stack = new("--stack") { Description = "Only containers of this stack." };
    private readonly Option<bool> watch = Stats.WatchOption();

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(stack);
        command.Add(watch);
    }

    protected override Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment) =>
        Stats.RunAsync(context, environment, watch, async metrics =>
        {
            var containers = (metrics.Containers ?? [])
                .Where(c => context.Value(stack) is not { } name || c.Stack == name)
                .OrderBy(c => c.Stack ?? "￿", StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            await context.Output.ListAsync(
                containers,
                items =>
                {
                    var table = new Table("NAME", "STACK", "CPU", "MEMORY", "MEM %", "NET RX/TX", "BLOCK R/W", "PIDS");
                    foreach (var c in items)
                    {
                        var rates = c.HasRates == true;
                        table.AddRow(
                            c.Name,
                            c.Stack,
                            rates ? Stats.Percent(c.CpuCores) : null,
                            $"{Stats.Bytes(c.MemoryBytes)} / {(c.MemoryLimitBytes is > 0 ? Stats.Bytes(c.MemoryLimitBytes) : "no limit")}",
                            c.MemoryLimitBytes is > 0 ? Stats.Percent((double)c.MemoryBytes!.Value / c.MemoryLimitBytes.Value) : null,
                            c.HostNetwork == true ? "host" : rates ? $"{Stats.Rate(c.NetworkRxBytesPerSecond)} / {Stats.Rate(c.NetworkTxBytesPerSecond)}" : null,
                            rates ? $"{Stats.Rate(c.BlockReadBytesPerSecond)} / {Stats.Rate(c.BlockWriteBytesPerSecond)}" : null,
                            c.Pids?.ToString(CultureInfo.InvariantCulture));
                    }

                    return table;
                },
                "No running containers match.");
        });
}

/// <summary><c>forest env stats [--watch]</c>: the host's CPU, memory, disks and network from the agent's latest sample.</summary>
internal sealed class EnvStatsCommand() : EnvironmentCommand("stats", "Show the resource usage of the environment's host.")
{
    private readonly Option<bool> watch = Stats.WatchOption();

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(watch);
    }

    protected override Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment) =>
        Stats.RunAsync(context, environment, watch, async metrics =>
        {
            if (context.Output.IsJson)
            {
                await context.Output.Out.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(metrics));
                return;
            }

            if (metrics.Host is not { } host)
            {
                context.Output.Note("No host numbers: you need a role on the whole environment, or its agent cannot read the host.");
                return;
            }

            var rates = host.HasRates == true;
            await context.Output.ItemAsync(host, h =>
            [
                ("CPU", rates ? $"{Stats.Percent(h.CpuBusy)} of {h.CpuCount} cores (steal {Stats.Percent(h.CpuSteal)})" : null),
                ("Load", string.Create(CultureInfo.InvariantCulture, $"{h.Load1:0.00} {h.Load5:0.00} {h.Load15:0.00}")),
                ("Memory", $"{Stats.Bytes(h.MemoryUsedBytes)} / {Stats.Bytes(h.MemoryTotalBytes)}"),
                ("Swap", h.SwapTotalBytes is > 0 ? $"{Stats.Bytes(h.SwapUsedBytes)} / {Stats.Bytes(h.SwapTotalBytes)}" : "none"),
                ("Network", rates ? $"{Stats.Rate(h.NetworkRxBytesPerSecond)} in, {Stats.Rate(h.NetworkTxBytesPerSecond)} out" : null),
                ("Disk I/O", rates ? $"{Stats.Rate(h.DiskReadBytesPerSecond)} read, {Stats.Rate(h.DiskWriteBytesPerSecond)} written" : null),
                ("Uptime", TimeSpan.FromSeconds(h.UptimeSeconds ?? 0).ToString(@"d\d\ hh\:mm", CultureInfo.InvariantCulture)),
            ]);

            if (host.Filesystems is { Count: > 0 } filesystems)
            {
                await context.Output.Out.WriteLineAsync();
                var table = new Table("MOUNT", "DEVICE", "TYPE", "USED", "SIZE", "USE %");
                foreach (var f in filesystems)
                {
                    table.AddRow(f.MountPoint, f.Device, f.Type, Stats.Bytes(f.UsedBytes), Stats.Bytes(f.TotalBytes), Stats.Percent(f.UsedRatio));
                }

                table.Render(context.Output.Out);
            }
        });
}

internal static class Stats
{
    public static Option<bool> WatchOption() => new("--watch", "-w") { Description = "Refresh with every new sample until Ctrl+C." };

    /// <summary>Shows the latest sample once, or again after each agent interval with --watch.</summary>
    public static async Task<int> RunAsync(
        CommandContext context, EnvironmentResponse environment, Option<bool> watch, Func<CurrentMetricsResponse, Task> show)
    {
        var environmentId = environment.Id!.Value;
        var watching = context.Value(watch);
        if (watching && context.Output.IsJson)
        {
            throw new CliException("--watch shows tables; use it without -o json.", ExitCodes.Usage);
        }

        while (true)
        {
            var metrics = await context.Api.GetCurrentMetricsAsync(environmentId, context.CancellationToken);
            if (metrics.At is null)
            {
                throw new CliException(metrics.Source switch
                {
                    MetricsSourceKind.Disabled => $"The agent of {environment.Name} has metrics turned off (CF_AGENT__METRICSINTERVAL=0).",
                    MetricsSourceKind.Unsupported => $"The agent of {environment.Name} sends no resource usage: it is offline, or older than metrics.",
                    _ => $"No resource usage from {environment.Name} in the last hour: its agent is offline.",
                });
            }

            if (watching && context.Host.InteractiveTerminal)
            {
                // Home and clear, as watch(1) does, so the table stays in place.
                await context.Output.Out.WriteAsync("\u001b[H\u001b[2J");
            }

            if (metrics.At is { } at && !context.Output.IsJson)
            {
                context.Output.Note($"{environment.Name}, {CliOutput.Time(at)}");
            }

            await show(metrics);
            if (!watching)
            {
                return ExitCodes.Ok;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, metrics.IntervalSeconds ?? 10)), context.CancellationToken);
        }
    }

    public static string Percent(double? ratio) =>
        ratio is { } r ? (r * 100).ToString(r < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%" : "-";

    public static string Rate(double? bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    /// <summary>Binary units, as docker stats shows memory: 1.5GiB, 300MiB, 12KiB.</summary>
    public static string Bytes(double? bytes)
    {
        var value = bytes ?? 0;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 || value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + units[unit];
    }

    public static string Bytes(long? bytes) => Bytes((double?)bytes);
}
