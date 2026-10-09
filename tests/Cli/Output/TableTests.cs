using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Tests.Output;

public class TableTests
{
    [Fact]
    public void Columns_are_padded_to_the_widest_cell()
    {
        var table = new Table("NAME", "STATE").AddRow("web-app-1", "running").AddRow("db", "exited");

        Assert.Equal(
            "NAME       STATE\nweb-app-1  running\ndb         exited\n",
            Render(table));
    }

    [Fact]
    public void Missing_cells_show_a_dash()
    {
        var table = new Table("A", "B", "C").AddRow("x", null);

        Assert.Equal("A  B  C\nx  -  -\n", Render(table));
    }

    [Fact]
    public void Control_characters_from_the_server_cannot_break_the_table()
    {
        var table = new Table("NAME").AddRow("evil\nname\u001b[31m");

        Assert.Equal("NAME\nevil name [31m\n", Render(table));
    }

    [Fact]
    public void Too_many_cells_are_an_error() =>
        Assert.Throws<ArgumentException>(() => new Table("A").AddRow("x", "y"));

    private static string Render(Table table)
    {
        var writer = new StringWriter { NewLine = "\n" };
        table.Render(writer);
        return writer.ToString();
    }
}
