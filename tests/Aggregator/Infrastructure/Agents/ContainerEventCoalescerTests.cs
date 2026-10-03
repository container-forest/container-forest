using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Agents;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Agents;

public class ContainerEventCoalescerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_of_one_window_become_one_update_per_environment()
    {
        var live = new RecordingLiveUpdates();
        var coalescer = new ContainerEventCoalescer(live, TimeProvider.System);
        var prod = EnvironmentId.New();
        var staging = EnvironmentId.New();

        for (var i = 0; i < 10; i++)
        {
            coalescer.Report(prod);
        }

        coalescer.Report(staging);
        await Task.Delay(ContainerEventCoalescer.Window * 3, Ct);

        Assert.Equal(1, live.Containers.Count(e => e == prod));
        Assert.Equal(1, live.Containers.Count(e => e == staging));
    }

    [Fact]
    public async Task Event_after_the_window_is_a_new_update()
    {
        var live = new RecordingLiveUpdates();
        var coalescer = new ContainerEventCoalescer(live, TimeProvider.System);
        var prod = EnvironmentId.New();

        coalescer.Report(prod);
        await Task.Delay(ContainerEventCoalescer.Window * 3, Ct);
        coalescer.Report(prod);
        await Task.Delay(ContainerEventCoalescer.Window * 3, Ct);

        Assert.Equal(2, live.Containers.Count);
    }

    private sealed class RecordingLiveUpdates : ILiveUpdates
    {
        private readonly List<EnvironmentId> containers = [];

        public IReadOnlyList<EnvironmentId> Containers
        {
            get
            {
                lock (containers)
                {
                    return [.. containers];
                }
            }
        }

        public Task EnvironmentsChangedAsync() => Task.CompletedTask;

        public Task ContainersChangedAsync(EnvironmentId environmentId)
        {
            lock (containers)
            {
                containers.Add(environmentId);
            }

            return Task.CompletedTask;
        }

        public Task StackChangedAsync(EnvironmentId environmentId, StackId stackId) => Task.CompletedTask;

        public Task DeploymentChangedAsync(EnvironmentId environmentId, StackId stackId, DeploymentId deploymentId) =>
            Task.CompletedTask;
    }
}
