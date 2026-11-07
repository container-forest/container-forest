using System.Globalization;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Compose;

/// <summary>
/// One run of `docker compose`. Only the factories below build invocations, so the agent can run nothing but
/// these fixed commands (security invariant 5): stack names and values are arguments, never parsed by a shell.
/// </summary>
public sealed class ComposeInvocation
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    private ComposeInvocation(
        string operation, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        Operation = operation;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Environment = environment;
    }

    /// <summary>Readable name for messages, e.g. "docker compose up".</summary>
    public string Operation { get; }

    /// <summary>Arguments after the docker executable, starting with "compose".</summary>
    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    /// <summary>Extra variables for compose; may hold secrets, so it is never logged.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>The same command with more environment variables, e.g. DOCKER_CONFIG for registry credentials.</summary>
    public ComposeInvocation WithEnvironment(string name, string value) =>
        new(Operation, Arguments, WorkingDirectory, new Dictionary<string, string>(Environment) { [name] = value });

    /// <summary>
    /// Deploys the stack from compose.yaml in <paramref name="stackDirectory"/>: redeploy for
    /// <see cref="PullPolicy.Missing"/>, redeploy with pull for <see cref="PullPolicy.Always"/>.
    /// Only -f compose.yaml is read, so compose.override.yaml and other files in the directory are not merged in.
    /// </summary>
    /// <param name="envFile">
    /// Passed as --env-file: the only file compose reads variables from, instead of whatever .env lies in the
    /// directory. Null to keep compose's own lookup of .env.
    /// </param>
    public static ComposeInvocation Up(
        string stackName, string stackDirectory, PullPolicy pull, IReadOnlyDictionary<string, string> environment, string? envFile = null)
    {
        var pullArgument = pull switch
        {
            PullPolicy.Missing => "missing",
            PullPolicy.Always => "always",
            _ => throw new ArgumentOutOfRangeException(nameof(pull), pull, "Pull policy must be Missing or Always."),
        };

        return new(
            "docker compose up",
            [.. Global, "-p", stackName, "-f", "compose.yaml", .. EnvFile(envFile), "up", "-d", "--remove-orphans", "--pull", pullArgument, "--wait"],
            stackDirectory,
            environment);
    }

    /// <summary>
    /// Prints compose's normalized model of compose.yaml in <paramref name="directory"/> as JSON on stdout, for the
    /// aggregator's compose policy (ADR-0012). It reads files and variables exactly as <see cref="Up"/> with the same
    /// <paramref name="envFile"/> does, and changes nothing.
    /// </summary>
    public static ComposeInvocation Config(
        string stackName, string directory, IReadOnlyDictionary<string, string> environment, string envFile) =>
        new(
            "docker compose config",
            [.. Global, "-p", stackName, "-f", "compose.yaml", .. EnvFile(envFile), "config", "--format", "json"],
            directory,
            environment);

    /// <summary>The --env-file for a bundle: its own .env at the root, or an empty file when it has none.</summary>
    public static string EnvFileFor(string directory, bool bundleHasEnvFile) =>
        bundleHasEnvFile ? Path.Combine(directory, DotEnv) : OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>The file compose reads variables from by default.</summary>
    public const string DotEnv = ".env";

    /// <summary>
    /// Restarts the processes in the stack's existing containers. Without -f compose does not read the compose file,
    /// so the env values it interpolates, which exist only in the containers' configuration, are not needed.
    /// </summary>
    public static ComposeInvocation Restart(string stackName, string workingDirectory, TimeSpan stopTimeout) =>
        new(
            "docker compose restart",
            [.. Global, "-p", stackName, "restart", "--timeout", Seconds(stopTimeout)],
            workingDirectory,
            NoEnvironment);

    /// <summary>Removes the stack's containers and networks; volumes are kept. Without -f for the same reason as restart.</summary>
    public static ComposeInvocation Down(string stackName, string workingDirectory, TimeSpan stopTimeout) =>
        new(
            "docker compose down",
            [.. Global, "-p", stackName, "down", "--timeout", Seconds(stopTimeout)],
            workingDirectory,
            NoEnvironment);

    private static IEnumerable<string> EnvFile(string? envFile) => envFile is null ? [] : ["--env-file", envFile];

    // Plain progress without colours: every line goes to the deployment log as text.
    private static IEnumerable<string> Global => ["compose", "--ansi", "never", "--progress", "plain"];

    private static string Seconds(TimeSpan timeout) =>
        ((int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
