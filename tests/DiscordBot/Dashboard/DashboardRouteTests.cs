extern alias bot;

using bot::ContainerForest.DiscordBot.Dashboard;

namespace ContainerForest.DiscordBot.Tests.Dashboard;

public sealed class DashboardRouteTests
{
    private static readonly Guid Environment = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid Stack = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private static readonly Guid Deployment = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static TheoryData<DashboardRoute> Routes =>
    [
        new EnvironmentsRoute(0),
        new EnvironmentsRoute(12),
        new StacksRoute(Environment, 3),
        new StackRoute(Environment, Stack, 0),
        new ConfirmRoute(StackOperation.Down, Environment, Stack, 2),
        new ConfirmRoute(StackOperation.RedeployWithPull, Environment, Stack, 2),
        new RunRoute(StackOperation.Redeploy, Environment, Stack, 1),
        new RunRoute(StackOperation.Restart, Environment, Stack, 1),
        new DeploymentRoute(Environment, Stack, Deployment, DashboardRoute.MaxPage),
    ];

    [Theory]
    [MemberData(nameof(Routes))]
    public void Route_SurvivesTheCustomId_WithinDiscordsLimit(DashboardRoute route)
    {
        var id = route.ToCustomId();

        Assert.True(id.Length <= DashboardRoute.MaxCustomIdLength, $"{id} has {id.Length} characters");
        Assert.StartsWith("dash:", id, StringComparison.Ordinal);
        Assert.True(DashboardRoute.TryParse(id, out var parsed));
        Assert.Equal(route, parsed);
    }

    [Fact]
    public void LongestRoute_LeavesRoomUnderTheLimit()
    {
        // Three GUIDs and the largest page: if this grows past 100, Discord rejects the whole message.
        var id = new DeploymentRoute(Environment, Stack, Deployment, DashboardRoute.MaxPage).ToCustomId();

        Assert.True(id.Length <= 85, $"{id} has {id.Length} characters");
    }

    [Theory]
    [InlineData("")]
    [InlineData("dash")]
    [InlineData("other:envs:0")]
    [InlineData("dash:none:previous")]
    [InlineData("dash:envs")]
    [InlineData("dash:envs:-1")]
    [InlineData("dash:envs:01")]
    [InlineData("dash:envs:1000")]
    [InlineData("dash:envs:0:extra")]
    [InlineData("dash:stacks:not-a-guid:0")]
    [InlineData("dash:stacks:AQAAAAAAAAAAAAAAAAAAAA==:0")]
    [InlineData("dash:run:xx:sqGhAdTDX36KmwwdLj9KWw:sqGhAdTDX36KmwwdLj9KWw:0")]
    public void TryParse_RejectsWhatItDidNotWrite(string id)
    {
        Assert.False(DashboardRoute.TryParse(id, out var route));
        Assert.Null(route);
    }

    [Fact]
    public void ToCustomId_KeepsGuidsCompact()
    {
        var id = new StacksRoute(Environment, 0).ToCustomId();

        // "dash:stacks:" + 22 characters of base64url + ":0", no padding and nothing URL-unsafe.
        Assert.Equal(12 + 22 + 2, id.Length);
        Assert.DoesNotContain('=', id);
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
    }
}
