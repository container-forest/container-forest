using System.CommandLine;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// Destructive commands (down, remove, revoking an agent) do nothing without <c>--yes</c>: a script says so
/// explicitly, and a person who typed the command too quickly gets a second look.
/// </summary>
internal static class Confirmation
{
    public static Option<bool> Option() => new("--yes", "-y") { Description = "Confirm; without it the command does nothing." };

    /// <exception cref="CliException">The command was not confirmed.</exception>
    public static void Require(CommandContext context, Option<bool> yes, string what)
    {
        if (!context.Value(yes))
        {
            throw new CliException($"This would {what}. Run again with --yes to confirm.", ExitCodes.Usage);
        }
    }
}
