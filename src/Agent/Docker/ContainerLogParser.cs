using System.Globalization;
using System.Text;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Docker;

/// <summary>Splits the output of Docker's logs endpoint, read with timestamps, into <see cref="LogLine"/>s.</summary>
/// <remarks>
/// Bytes arrive in chunks that may end in the middle of a line, and stdout and stderr chunks interleave,
/// so each stream keeps its own unfinished line. Not thread-safe: one parser per read.
/// </remarks>
public sealed class ContainerLogParser
{
    /// <summary>Longer lines are cut, so that a batch of them fits in one gRPC message.</summary>
    public const int MaxLineBytes = 16 * 1024;

    /// <summary>Appended to a line that was cut at <see cref="MaxLineBytes"/>.</summary>
    public const string TruncationMark = "…";

    // "2026-10-02T12:00:00.123456789Z" is 30 characters, with an offset instead of Z 35.
    private const int MaxTimestampLength = 40;

    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    // Invalid bytes become U+FFFD instead of failing the read.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly LineBuffer stdout = new();
    private readonly LineBuffer stderr = new();

    /// <summary>Adds a chunk of one stream and returns the lines it completed, oldest first.</summary>
    public IReadOnlyList<LogLine> Append(LogStream stream, ReadOnlySpan<byte> chunk)
    {
        var buffer = stream == LogStream.Stderr ? stderr : stdout;
        List<LogLine>? lines = null;
        while (true)
        {
            var newline = chunk.IndexOf((byte)'\n');
            if (newline < 0)
            {
                buffer.Write(chunk);
                return lines ?? [];
            }

            buffer.Write(chunk[..newline]);
            (lines ??= []).Add(buffer.TakeLine(stream));
            chunk = chunk[(newline + 1)..];
        }
    }

    /// <summary>Returns the lines the output ended without a newline: stdout first, then stderr.</summary>
    public IReadOnlyList<LogLine> Flush()
    {
        var lines = new List<LogLine>(2);
        if (stdout.HasContent)
        {
            lines.Add(stdout.TakeLine(LogStream.Stdout));
        }

        if (stderr.HasContent)
        {
            lines.Add(stderr.TakeLine(LogStream.Stderr));
        }

        return lines;
    }

    /// <summary>Parses one line without its newline: a leading RFC 3339 timestamp and a space, then the text.</summary>
    /// <param name="truncated">The line was cut at <see cref="MaxLineBytes"/>; the text gets <see cref="TruncationMark"/>.</param>
    /// <remarks>A line without a timestamp keeps all of its text and gets timestamp 0, which means "unknown".</remarks>
    public static LogLine ParseLine(LogStream stream, ReadOnlySpan<byte> line, bool truncated = false)
    {
        if (!truncated && line.EndsWith("\r"u8))
        {
            // A container with a TTY ends its lines with \r\n.
            line = line[..^1];
        }

        long timestampUnixMs = 0;
        var space = line[..Math.Min(line.Length, MaxTimestampLength + 1)].IndexOf((byte)' ');
        if (space > 0 && TryParseTimestamp(line[..space], out var at))
        {
            timestampUnixMs = at.ToUnixTimeMilliseconds();
            line = line[(space + 1)..];
        }

        var text = truncated ? Utf8.GetString(WholeCharacters(line)) + TruncationMark : Utf8.GetString(line);
        return new LogLine { Stream = stream, TimestampUnixMs = timestampUnixMs, Text = text };
    }

    /// <summary>Parses Docker's timestamp, e.g. 2026-10-02T12:00:00.123456789Z.</summary>
    public static bool TryParseTimestamp(ReadOnlySpan<byte> token, out DateTimeOffset at)
    {
        at = default;
        if (token.Length is 0 or > MaxTimestampLength || !Ascii.IsValid(token))
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(token);

        // Docker writes nanoseconds; .NET parses at most seven fractional digits.
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }

        return DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    // A line cut at a byte limit may end in the middle of a character; drop that part instead of showing U+FFFD.
    private static ReadOnlySpan<byte> WholeCharacters(ReadOnlySpan<byte> bytes)
    {
        var lead = bytes.Length - 1;
        while (lead >= 0 && lead > bytes.Length - 4 && (bytes[lead] & 0b1100_0000) == 0b1000_0000)
        {
            lead--;
        }

        if (lead < 0)
        {
            return bytes;
        }

        var length = bytes[lead] switch
        {
            >= 0b1111_0000 => 4,
            >= 0b1110_0000 => 3,
            >= 0b1100_0000 => 2,
            _ => 1,
        };
        return lead + length > bytes.Length ? bytes[..lead] : bytes;
    }

    /// <summary>The unfinished line of one stream; keeps at most <see cref="MaxLineBytes"/> and drops the rest.</summary>
    private sealed class LineBuffer
    {
        private readonly byte[] bytes = new byte[MaxLineBytes];
        private int length;
        private bool truncated;

        public bool HasContent => length > 0 || truncated;

        public void Write(ReadOnlySpan<byte> chunk)
        {
            var room = MaxLineBytes - length;
            if (chunk.Length > room)
            {
                chunk = chunk[..room];
                truncated = true;
            }

            chunk.CopyTo(bytes.AsSpan(length));
            length += chunk.Length;
        }

        public LogLine TakeLine(LogStream stream)
        {
            var line = ParseLine(stream, bytes.AsSpan(0, length), truncated);
            length = 0;
            truncated = false;
            return line;
        }
    }
}
