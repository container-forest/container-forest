using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Dashboard;

/// <summary>One page of a list. <see cref="Number"/> is always within the list, whatever page was asked for.</summary>
public sealed record PageOf<T>(IReadOnlyList<T> Items, int Number, int Count, int Total)
{
    public bool HasPrevious => Number > 0;

    public bool HasNext => Number < Count - 1;

    /// <summary>
    /// The page <paramref name="number"/> of <paramref name="items"/>, or the last one when the list has shrunk since
    /// the button was drawn (a stack was removed, a role revoked).
    /// </summary>
    public static PageOf<T> Of(IReadOnlyList<T> items, int number, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var count = Math.Max(1, (items.Count + size - 1) / size);
        var page = Math.Clamp(number, 0, count - 1);
        return new PageOf<T>([.. items.Skip(page * size).Take(size)], page, count, items.Count);
    }
}

/// <summary>How many of a stack's containers run; unknown while the agent cannot be asked.</summary>
public sealed record ContainerCount(int Running, int Total)
{
    public static ContainerCount Of(IEnumerable<ForestContainer> containers)
    {
        var list = containers.ToList();
        return new ContainerCount(list.Count(c => c.State == ContainerState.Running), list.Count);
    }
}

public enum NoticeKind
{
    Info,
    Progress,
    Success,
    Failure,
}

/// <summary>A line about the last operation, with what compose printed when there is something to show.</summary>
/// <param name="Text">Plain text: the renderer escapes it.</param>
public sealed record Notice(NoticeKind Kind, string Text, IReadOnlyList<string>? Output = null);

/// <summary>What /dashboard shows; <see cref="DashboardRenderer"/> turns it into Discord components.</summary>
public abstract record DashboardScreen;

public sealed record EnvironmentsScreen(PageOf<ForestEnvironment> Environments) : DashboardScreen;

/// <param name="Containers">Null when the agent could not be asked.</param>
public sealed record StackRow(ForestStack Stack, ContainerCount? Containers);

public sealed record StacksScreen(ForestEnvironment Environment, PageOf<StackRow> Stacks) : DashboardScreen;

/// <param name="Containers">The stack's containers, or null when the agent could not be asked.</param>
/// <param name="StacksPage">The page of stacks "Back" returns to.</param>
/// <param name="DeploymentId">The deployment the notice follows, so "Refresh" asks about it again.</param>
/// <param name="Busy">An operation is running: every action is disabled until it ends.</param>
public sealed record StackScreen(
    ForestEnvironment Environment,
    ForestStack Stack,
    IReadOnlyList<ForestContainer>? Containers,
    int StacksPage,
    Notice? Notice = null,
    Guid? DeploymentId = null,
    bool Busy = false) : DashboardScreen
{
    public Guid EnvironmentGuid => Guid.Parse(Environment.Id);

    public Guid StackGuid => Guid.Parse(Stack.Id);

    /// <summary>Actions need the agent; offline it would refuse them anyway.</summary>
    public bool CanAct => !Busy && Environment.Online;

    /// <summary>The operations the user may run, in the order of the buttons.</summary>
    public IReadOnlyList<StackOperation> Operations
    {
        get
        {
            var operations = new List<StackOperation>();
            if (Stack.CanDeploy)
            {
                operations.AddRange([StackOperation.Redeploy, StackOperation.RedeployWithPull]);
            }

            if (Stack.CanRestart)
            {
                operations.Add(StackOperation.Restart);
            }

            if (Stack.CanTakeDown)
            {
                operations.Add(StackOperation.Down);
            }

            return operations;
        }
    }
}

public sealed record ConfirmScreen(ForestEnvironment Environment, ForestStack Stack, StackOperation Operation, int StacksPage)
    : DashboardScreen
{
    public Guid EnvironmentGuid => Guid.Parse(Environment.Id);

    public Guid StackGuid => Guid.Parse(Stack.Id);
}

/// <param name="Message">Plain text for the user; the renderer escapes it.</param>
/// <param name="Back">Where "Back" leads, or null when there is nowhere to go (e.g. the account is not linked).</param>
public sealed record ErrorScreen(string Message, DashboardRoute? Back) : DashboardScreen;

/// <summary>What the operations need: confirmation, and the words for the buttons and notices.</summary>
public static class StackOperations
{
    /// <summary>Restart only restarts processes in the same containers; the others change or remove containers.</summary>
    public static bool NeedsConfirmation(this StackOperation operation) => operation is not StackOperation.Restart;

    /// <summary>Restart and down wait for compose; a redeploy is only queued.</summary>
    public static bool IsQueued(this StackOperation operation) =>
        operation is StackOperation.Redeploy or StackOperation.RedeployWithPull;

    public static string Label(this StackOperation operation) => operation switch
    {
        StackOperation.Redeploy => "Redeploy",
        StackOperation.RedeployWithPull => "Redeploy + pull",
        StackOperation.Restart => "Restart",
        StackOperation.Down => "Down",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>What the operation does, for the confirmation.</summary>
    public static string Explanation(this StackOperation operation) => operation switch
    {
        StackOperation.Redeploy =>
            "Deploys the latest commit of the stack's branch (docker compose up); only missing images are pulled. "
            + "Containers whose configuration changed are recreated.",
        StackOperation.RedeployWithPull =>
            "Deploys the latest commit of the stack's branch and pulls every image again (docker compose up --pull always). "
            + "Containers with a newer image are recreated.",
        StackOperation.Restart => "Restarts the processes in the stack's containers (docker compose restart).",
        StackOperation.Down =>
            "Removes the stack's containers and networks (docker compose down). Volumes and files stay; "
            + "a redeploy brings the stack back.",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };
}
