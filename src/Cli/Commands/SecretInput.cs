using System.Text;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// Passwords and tokens a command needs: never as options (the process list and the shell history would keep
/// them), but from standard input, or typed without echo when a person sits at the terminal.
/// </summary>
internal static class SecretInput
{
    /// <exception cref="CliException">Nothing was given.</exception>
    public static async Task<string> ReadAsync(CommandContext context, string what)
    {
        string secret;
        if (context.Host.InteractiveTerminal)
        {
            await context.Output.Error.WriteAsync($"{what}: ");
            secret = ReadWithoutEcho();
            await context.Output.Error.WriteLineAsync();
        }
        else
        {
            secret = (await context.Host.Input.ReadToEndAsync(context.CancellationToken)).TrimEnd('\r', '\n');
        }

        return secret.Length > 0
            ? secret
            : throw new CliException($"No {what.ToLowerInvariant()}: type it, or pass it on standard input.", ExitCodes.Usage);
    }

    private static string ReadWithoutEcho()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return text.ToString();
                case ConsoleKey.Backspace when text.Length > 0:
                    text.Length--;
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }
}
