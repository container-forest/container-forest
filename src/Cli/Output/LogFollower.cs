using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Output;

/// <summary>
/// Keeps track of a container log being followed. The aggregator's <c>since</c> includes its bound, so a poll
/// repeats the lines of the newest second seen; those are recognized by their time, stream and text and skipped.
/// </summary>
public sealed class LogFollower
{
    private readonly HashSet<(LogStream?, string?)> atNewest = [];

    /// <summary>The time to ask from next; null until a line with a time has been seen.</summary>
    public DateTimeOffset? Since { get; private set; }

    /// <summary>The lines not shown yet, in order; remembers them as shown.</summary>
    public IReadOnlyList<ContainerLogLineResponse> Accept(IEnumerable<ContainerLogLineResponse> lines)
    {
        var fresh = new List<ContainerLogLineResponse>();
        foreach (var line in lines)
        {
            if (line.At is not { } at)
            {
                fresh.Add(line);
                continue;
            }

            if (Since is { } newest && at < newest)
            {
                continue;
            }

            if (Since == at && !atNewest.Add((line.Stream, line.Text)))
            {
                continue;
            }

            if (Since != at)
            {
                Since = at;
                atNewest.Clear();
                atNewest.Add((line.Stream, line.Text));
            }

            fresh.Add(line);
        }

        return fresh;
    }
}
