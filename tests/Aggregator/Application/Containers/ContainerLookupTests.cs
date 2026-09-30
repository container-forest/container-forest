using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Tests.Containers;

public class ContainerLookupTests
{
    private static readonly ContainerSnapshot Web = Container("ef92095891a4c0ffee", "web-app-1");
    private static readonly ContainerSnapshot Db = Container("ef93aa11bb22dd33", "db-1");
    private static readonly ContainerSnapshot NamedLikeAnId = Container("0123456789abcdef", "ef92");

    private static readonly IReadOnlyList<ContainerSnapshot> Containers = [Web, Db];

    [Fact]
    public void Finds_by_full_id() => Assert.Same(Web, ContainerLookup.Find(Containers, Web.Id));

    [Fact]
    public void Finds_by_name() => Assert.Same(Db, ContainerLookup.Find(Containers, "db-1"));

    [Fact]
    public void Finds_by_the_short_id() => Assert.Same(Web, ContainerLookup.Find(Containers, "ef92095891a4"));

    [Fact]
    public void Ambiguous_prefix_is_a_conflict() =>
        Assert.Throws<DomainConflictException>(() => ContainerLookup.Find(Containers, "ef9"));

    [Fact]
    public void Exact_name_wins_over_an_id_prefix() =>
        Assert.Same(NamedLikeAnId, ContainerLookup.Find([Web, NamedLikeAnId], "ef92"));

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("web")]
    public void Unknown_reference_is_not_found(string reference) =>
        Assert.Throws<NotFoundException>(() => ContainerLookup.Find(Containers, reference));

    private static ContainerSnapshot Container(string id, string name) =>
        new(id, name, ContainerState.Running, "nginx:1.27", null, null, "", null);
}
