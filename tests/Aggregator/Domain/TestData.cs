using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    public static DockerEnvironment Environment(bool isPrivate = false) =>
        DockerEnvironment.Create("test", isPrivate, Now);

    public static Stack Stack(EnvironmentId environmentId, string name = "web", bool isSystem = false) =>
        Domain.Stacks.Stack.Create(
            environmentId,
            StackName.Parse(name),
            RepositoryId.New(),
            BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"),
            isSystem,
            Now);

    public static User User() => Domain.Access.User.Create("alice", Now);

    public static User Admin() => Domain.Access.User.CreateAdministrator("admin", Now);
}
