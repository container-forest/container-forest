using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Common;

public class EntityTests
{
    [Fact]
    public void Entities_with_different_ids_are_not_equal() => Assert.NotEqual(Environment(), Environment());

    [Fact]
    public void Entities_of_different_types_are_never_equal()
    {
        var stack = Stack(EnvironmentId.New());
        var deployment = Deployment.QueueFromWebhook(stack.Id, Now);

        Assert.False(stack.Equals(deployment));
    }
}
