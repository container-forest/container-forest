namespace ContainerForest.Cli;

/// <summary>Exit codes of <c>forest</c>, so scripts can tell failures apart.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The operation failed: the aggregator refused it, or an agent or compose reported an error.</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong; System.CommandLine uses it for parse errors too.</summary>
    public const int Usage = 2;

    /// <summary>No credentials, or the aggregator does not accept them.</summary>
    public const int Unauthenticated = 3;

    /// <summary>Not allowed, or not found: the aggregator answers both the same for things the user cannot see.</summary>
    public const int Denied = 4;

    /// <summary>The aggregator could not be reached.</summary>
    public const int Unreachable = 5;

    /// <summary>Interrupted with Ctrl+C.</summary>
    public const int Interrupted = 130;
}

/// <summary>A failure to report to the user as is: a readable message, no stack trace.</summary>
public sealed class CliException(string message, int exitCode = ExitCodes.Failed) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
