using System.Text;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Tests.Secrets;

public class PktLineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Text_lists_and_content_survive_the_framing()
    {
        var stream = new MemoryStream();
        var writer = new PktLineWriter(stream);
        var large = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        await writer.WriteTextListAsync(["command=smudge", "pathname=a b/.env"], Ct);
        await writer.WriteContentAsync(large, Ct);
        await writer.WriteContentAsync([], Ct);

        Assert.StartsWith("0013command=smudge\n", Encoding.ASCII.GetString(stream.ToArray()), StringComparison.Ordinal);
        stream.Position = 0;
        var reader = new PktLineReader(stream);
        Assert.Equal(["command=smudge", "pathname=a b/.env"], await reader.ReadTextListAsync(Ct));
        Assert.Equal(large, await reader.ReadContentAsync(Ct));
        Assert.Empty(await reader.ReadContentAsync(Ct));
        Assert.Null(await reader.ReadTextListAsync(Ct));
    }

    [Theory]
    [InlineData("zzzz")]
    [InlineData("0002")]
    [InlineData("0010abc")]
    public async Task Anything_but_pkt_lines_is_refused(string input)
    {
        var reader = new PktLineReader(new MemoryStream(Encoding.ASCII.GetBytes(input)));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPacketAsync(Ct));
    }
}
