using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;
using Docker.DotNet.Models;

namespace ContainerForest.Agent.Tests.Docker;

public class ContainerInfoMapperTests
{
    [Fact]
    public void Compose_labels_become_stack_and_service()
    {
        var info = ContainerInfoMapper.ToContainerInfo(new ContainerListResponse
        {
            ID = "3f4a9c1b2d7e",
            Names = ["/web-app-1"],
            Image = "nginx:1.27",
            State = "running",
            Status = "Up 3 minutes (healthy)",
            Created = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            Labels = new Dictionary<string, string>
            {
                [ContainerInfoMapper.ProjectLabel] = "web",
                [ContainerInfoMapper.ServiceLabel] = "app",
            },
        });

        Assert.Equal("3f4a9c1b2d7e", info.Id);
        Assert.Equal("web-app-1", info.Name);
        Assert.Equal(ContainerState.Running, info.State);
        Assert.Equal("nginx:1.27", info.Image);
        Assert.Equal("web", info.StackName);
        Assert.Equal("app", info.ServiceName);
        Assert.Equal("Up 3 minutes (healthy)", info.StatusText);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), info.CreatedUnixMs);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void System_label_marks_containers_of_container_forest(string? value, bool system)
    {
        var labels = new Dictionary<string, string>();
        if (value is not null)
        {
            labels[ContainerInfoMapper.SystemLabel] = value;
        }

        var info = ContainerInfoMapper.ToContainerInfo(new ContainerListResponse { ID = "c1", Names = ["/agent"], Labels = labels });

        Assert.Equal(system, info.System);
    }

    [Fact]
    public void Container_outside_compose_has_no_stack()
    {
        var info = ContainerInfoMapper.ToContainerInfo(new ContainerListResponse { ID = "c1", Names = ["/lonely"], State = "exited" });

        Assert.Equal("", info.StackName);
        Assert.Equal("", info.ServiceName);
        Assert.Equal(0, info.CreatedUnixMs);
    }

    [Fact]
    public void Missing_fields_do_not_throw()
    {
        var info = ContainerInfoMapper.ToContainerInfo(new ContainerListResponse());

        Assert.Equal("", info.Name);
        Assert.Equal(ContainerState.Unspecified, info.State);
    }

    [Theory]
    [InlineData("created", ContainerState.Created)]
    [InlineData("running", ContainerState.Running)]
    [InlineData("paused", ContainerState.Paused)]
    [InlineData("restarting", ContainerState.Restarting)]
    [InlineData("removing", ContainerState.Removing)]
    [InlineData("exited", ContainerState.Exited)]
    [InlineData("dead", ContainerState.Dead)]
    [InlineData("Running", ContainerState.Running)]
    [InlineData("something-new", ContainerState.Unspecified)]
    [InlineData(null, ContainerState.Unspecified)]
    public void Docker_states_map_to_protocol_states(string? state, ContainerState expected) =>
        Assert.Equal(expected, ContainerInfoMapper.ToState(state));
}
