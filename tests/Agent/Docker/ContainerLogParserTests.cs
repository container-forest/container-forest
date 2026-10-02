using System.Text;
using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Tests.Docker;

public class ContainerLogParserTests
{
    private const string At = "2026-10-02T12:00:00.123456789Z";
    private static readonly long AtUnixMs = DateTimeOffset.Parse("2026-10-02T12:00:00.123Z").ToUnixTimeMilliseconds();

    private readonly ContainerLogParser parser = new();

    [Fact]
    public void Timestamp_is_cut_off_into_milliseconds()
    {
        var line = Assert.Single(parser.Append(LogStream.Stdout, Bytes($"{At} listening on :8080\n")));

        Assert.Equal(LogStream.Stdout, line.Stream);
        Assert.Equal(AtUnixMs, line.TimestampUnixMs);
        Assert.Equal("listening on :8080", line.Text);
    }

    [Theory]
    [InlineData("2026-10-02T12:00:00Z", "2026-10-02T12:00:00Z")]
    [InlineData("2026-10-02T12:00:00.5Z", "2026-10-02T12:00:00.5Z")]
    [InlineData("2026-10-02T15:00:00.123456789+03:00", "2026-10-02T12:00:00.123Z")]
    public void Timestamps_with_any_precision_and_offset_are_parsed(string timestamp, string expected)
    {
        Assert.True(ContainerLogParser.TryParseTimestamp(Bytes(timestamp), out var at));
        Assert.Equal(DateTimeOffset.Parse(expected).ToUnixTimeMilliseconds(), at.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("12:00:00")]
    public void Text_that_is_not_a_timestamp_is_rejected(string token)
    {
        Assert.False(ContainerLogParser.TryParseTimestamp(Bytes(token), out _));
    }

    [Fact]
    public void Line_without_a_timestamp_keeps_its_text()
    {
        var line = Assert.Single(parser.Append(LogStream.Stdout, Bytes("plain text here\n")));

        Assert.Equal(0, line.TimestampUnixMs);
        Assert.Equal("plain text here", line.Text);
    }

    [Fact]
    public void Stdout_and_stderr_keep_separate_unfinished_lines()
    {
        Assert.Empty(parser.Append(LogStream.Stdout, Bytes($"{At} req")));
        var error = Assert.Single(parser.Append(LogStream.Stderr, Bytes($"{At} warning\n")));
        var output = Assert.Single(parser.Append(LogStream.Stdout, Bytes("uest done\n")));

        Assert.Equal((LogStream.Stderr, "warning"), (error.Stream, error.Text));
        Assert.Equal((LogStream.Stdout, "request done"), (output.Stream, output.Text));
    }

    [Fact]
    public void One_chunk_may_hold_several_lines()
    {
        var lines = parser.Append(LogStream.Stdout, Bytes($"{At} one\n{At} two\n{At} three"));

        Assert.Equal(["one", "two"], lines.Select(l => l.Text));
        Assert.Equal("three", Assert.Single(parser.Flush()).Text);
    }

    [Fact]
    public void Character_split_between_chunks_is_decoded_whole()
    {
        var bytes = Bytes($"{At} привіт\n");
        var middle = bytes.Length - 3; // inside the two bytes of the last letter

        Assert.Empty(parser.Append(LogStream.Stdout, bytes.AsSpan(0, middle)));
        Assert.Equal("привіт", Assert.Single(parser.Append(LogStream.Stdout, bytes.AsSpan(middle))).Text);
    }

    [Fact]
    public void Invalid_utf8_is_replaced()
    {
        byte[] bytes = [.. Bytes($"{At} a"), 0xFF, .. Bytes("b\n")];

        Assert.Equal("a�b", Assert.Single(parser.Append(LogStream.Stdout, bytes)).Text);
    }

    [Fact]
    public void Tty_line_endings_are_removed()
    {
        Assert.Equal("prompt", Assert.Single(parser.Append(LogStream.Stdout, Bytes($"{At} prompt\r\n"))).Text);
    }

    [Fact]
    public void Empty_line_stays_a_line()
    {
        var line = Assert.Single(parser.Append(LogStream.Stdout, Bytes($"{At} \n")));

        Assert.Equal("", line.Text);
        Assert.Equal(AtUnixMs, line.TimestampUnixMs);
    }

    [Fact]
    public void Long_line_is_cut_and_marked()
    {
        Assert.Empty(parser.Append(LogStream.Stdout, Bytes($"{At} ")));
        for (var i = 0; i < 10; i++)
        {
            Assert.Empty(parser.Append(LogStream.Stdout, Bytes(new string('x', 4096))));
        }

        var line = Assert.Single(parser.Append(LogStream.Stdout, Bytes("tail\n")));

        Assert.EndsWith(ContainerLogParser.TruncationMark, line.Text);
        Assert.True(Encoding.UTF8.GetByteCount(line.Text) <= ContainerLogParser.MaxLineBytes + 3);
        Assert.Equal(AtUnixMs, line.TimestampUnixMs);

        // The next line starts fresh.
        Assert.Equal("next", Assert.Single(parser.Append(LogStream.Stdout, Bytes($"{At} next\n"))).Text);
    }

    [Fact]
    public void Cut_does_not_leave_half_a_character()
    {
        var prefix = $"{At} ";
        var filler = new string('x', ContainerLogParser.MaxLineBytes - Encoding.UTF8.GetByteCount(prefix) - 1);

        // The two-byte letter starts at the last byte the buffer keeps.
        var line = Assert.Single(parser.Append(LogStream.Stdout, Bytes($"{prefix}{filler}ї more\n")));

        Assert.Equal(filler + ContainerLogParser.TruncationMark, line.Text);
    }

    [Fact]
    public void Flush_returns_nothing_after_complete_lines()
    {
        parser.Append(LogStream.Stdout, Bytes($"{At} done\n"));

        Assert.Empty(parser.Flush());
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
