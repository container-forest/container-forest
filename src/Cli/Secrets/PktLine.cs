using System.Globalization;
using System.Text;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// Reads git's pkt-line framing: four hex digits of length (including themselves), then the data; "0000" is a flush
/// packet that ends a list. Used by the long-running filter protocol (gitattributes, "Long Running Filter Process").
/// </summary>
internal sealed class PktLineReader(Stream stream)
{
    private readonly byte[] header = new byte[4];

    /// <summary>The next packet; an empty array for a flush packet; null at the end of the stream.</summary>
    /// <exception cref="InvalidDataException">The stream is not pkt-line.</exception>
    public async Task<byte[]?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrEndAsync(header, cancellationToken))
        {
            return null;
        }

        if (!int.TryParse(Encoding.ASCII.GetString(header), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length)
            || length is 1 or 2 or 3 || length > PktLine.MaxPacketLength)
        {
            throw new InvalidDataException("git sent something that is not a pkt-line.");
        }

        if (length == 0)
        {
            return [];
        }

        var data = new byte[length - 4];
        if (!await ReadExactlyOrEndAsync(data, cancellationToken))
        {
            throw new InvalidDataException("git closed the stream in the middle of a packet.");
        }

        return data;
    }

    /// <summary>Text packets up to the next flush, without their trailing newline; null at the end of the stream.</summary>
    public async Task<List<string>?> ReadTextListAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (true)
        {
            var packet = await ReadPacketAsync(cancellationToken);
            if (packet is null)
            {
                return lines.Count == 0 ? null : throw new InvalidDataException("git closed the stream in the middle of a list.");
            }

            if (packet.Length == 0)
            {
                return lines;
            }

            lines.Add(Encoding.UTF8.GetString(packet).TrimEnd('\n'));
        }
    }

    /// <summary>Binary packets up to the next flush, joined.</summary>
    public async Task<byte[]> ReadContentAsync(CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        while (await ReadPacketAsync(cancellationToken) is { Length: > 0 } packet)
        {
            content.Write(packet);
        }

        return content.ToArray();
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                return read == 0 ? false : throw new InvalidDataException("git closed the stream in the middle of a packet.");
            }

            read += n;
        }

        return true;
    }
}

/// <summary>Writes git's pkt-line framing; see <see cref="PktLineReader"/>.</summary>
internal sealed class PktLineWriter(Stream stream)
{
    public async Task WriteTextAsync(string line, CancellationToken cancellationToken) =>
        await WritePacketAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);

    public async Task WriteTextListAsync(IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            await WriteTextAsync(line, cancellationToken);
        }

        await WriteFlushAsync(cancellationToken);
    }

    /// <summary>The content split into packets of at most <see cref="PktLine.MaxDataLength"/> bytes, then a flush.</summary>
    public async Task WriteContentAsync(byte[] content, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < content.Length; offset += PktLine.MaxDataLength)
        {
            await WritePacketAsync(content.AsMemory(offset, Math.Min(PktLine.MaxDataLength, content.Length - offset)), cancellationToken);
        }

        await WriteFlushAsync(cancellationToken);
    }

    public async Task WriteFlushAsync(CancellationToken cancellationToken)
    {
        await stream.WriteAsync("0000"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task WritePacketAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes((data.Length + 4).ToString("x4", CultureInfo.InvariantCulture)), cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
    }
}

internal static class PktLine
{
    /// <summary>LARGE_PACKET_MAX of git: the length field counts itself.</summary>
    public const int MaxPacketLength = 65520;

    public const int MaxDataLength = MaxPacketLength - 4;
}
