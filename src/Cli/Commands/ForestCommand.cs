using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A node of the command tree: a command that does something, or a group of them (Composite).</summary>
internal interface ICliNode
{
    Command Build(CliHost host);
}

/// <summary>A command that only groups others, e.g. <c>forest stack</c>.</summary>
internal sealed class CommandGroup(string name, string description, params ICliNode[] children) : ICliNode
{
    public Command Build(CliHost host)
    {
        var command = new Command(name, description);
        foreach (var child in children)
        {
            command.Add(child.Build(host));
        }

        return command;
    }
}

/// <summary>
/// A command that does something (Template Method). The host resolves credentials and reports failures;
/// a subclass declares its arguments in <see cref="Configure"/> and does its work in <see cref="ExecuteAsync"/>.
/// </summary>
internal abstract class ForestCommand(string name, string description) : ICliNode
{
    public string Name { get; } = name;

    /// <summary>False for commands that work without a server or token: login, contexts.</summary>
    public virtual bool NeedsConnection => true;

    public Command Build(CliHost host)
    {
        var command = new Command(Name, description);
        Configure(command);
        command.SetAction((parseResult, cancellationToken) => host.RunAsync(this, parseResult, cancellationToken));
        return command;
    }

    internal Task<int> InvokeAsync(CommandContext context) => ExecuteAsync(context);

    /// <summary>Adds the command's own arguments and options.</summary>
    protected virtual void Configure(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context);
}

/// <summary>Everything a running command may use.</summary>
internal sealed class CommandContext(
    CliHost host,
    ParseResult parseResult,
    CliOutput output,
    Connection? connection,
    ForestApi? api,
    CancellationToken cancellationToken)
{
    public CliHost Host { get; } = host;

    public ParseResult ParseResult { get; } = parseResult;

    public CliOutput Output { get; } = output;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>The server and token; only for commands with <see cref="ForestCommand.NeedsConnection"/>.</summary>
    public Connection Connection => connection ?? throw new InvalidOperationException("This command runs without a connection.");

    public ForestApi Api => api ?? throw new InvalidOperationException("This command runs without a connection.");

    public T Value<T>(Argument<T> argument) => ParseResult.GetValue(argument)!;

    public T? Value<T>(Option<T> option) => ParseResult.GetValue(option);
}
