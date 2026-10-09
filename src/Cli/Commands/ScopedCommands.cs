using System.CommandLine;
using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// A command inside one environment: takes <c>--env</c>, falls back to the context's default environment or to the
/// only one the user can see, and hands the environment to <see cref="ExecuteAsync(CommandContext, EnvironmentResponse)"/>.
/// </summary>
internal abstract class EnvironmentCommand(string name, string description) : ForestCommand(name, description)
{
    public static readonly Option<string?> EnvironmentOption = new("--env", "-e")
    {
        Description = "Environment name or id; defaults to the context's environment or the only one you can see.",
    };

    protected override void Configure(Command command) => command.Add(EnvironmentOption);

    protected sealed override async Task<int> ExecuteAsync(CommandContext context)
    {
        var environment = await context.Api.ResolveEnvironmentAsync(
            context.Value(EnvironmentOption), context.Connection.DefaultEnvironment, context.CancellationToken);
        return await ExecuteAsync(context, environment);
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment);
}

/// <summary>A command about one stack, named in the environment or given by id: <c>forest stack deploy web</c>.</summary>
internal abstract class StackCommand(string name, string description) : EnvironmentCommand(name, description)
{
    private readonly Argument<string> stackArgument = new("stack") { Description = "Stack name in the environment, or stack id." };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(stackArgument);
        ConfigureStack(command);
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var stack = await context.Api.FindStackAsync(environment, context.Value(stackArgument), context.CancellationToken);
        return await ExecuteAsync(context, environment, stack);
    }

    /// <summary>Adds options of the command beyond the environment and the stack.</summary>
    protected virtual void ConfigureStack(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack);
}
