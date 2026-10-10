namespace ContainerForest.Cli.Secrets;

/// <summary>
/// The long-running filter process of git (<c>filter.forest.process</c>): one process per git command converts all
/// its files over the pkt-line protocol, so the aggregator is not reached through a new process per file.
/// </summary>
/// <remarks>See "Long Running Filter Process" in gitattributes(5). Capabilities are clean and smudge; delay is not offered.</remarks>
internal sealed class GitFilterProcess(Stream input, Stream output, IReadOnlyDictionary<string, FilterOperation> operations)
{
    private readonly PktLineReader reader = new(input);
    private readonly PktLineWriter writer = new(output);

    /// <exception cref="InvalidDataException">git does not speak version 2 of the protocol.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await HandshakeAsync(cancellationToken);
        while (await reader.ReadTextListAsync(cancellationToken) is { } request)
        {
            var fields = request.Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            var content = await reader.ReadContentAsync(cancellationToken);
            var operation = fields.TryGetValue("command", out var command) ? operations.GetValueOrDefault(command) : null;
            var path = fields.GetValueOrDefault("pathname");
            var result = operation is null || path is null
                ? FilterResult.Failed
                : await operation.RunAsync(path, content, cancellationToken);

            if (!result.Success)
            {
                await writer.WriteTextListAsync(["status=error"], cancellationToken);
                continue;
            }

            await writer.WriteTextListAsync(["status=success"], cancellationToken);
            await writer.WriteContentAsync(result.Content, cancellationToken);
            // An empty list: the status stays "success".
            await writer.WriteFlushAsync(cancellationToken);
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var welcome = await reader.ReadTextListAsync(cancellationToken) ?? [];
        if (!welcome.Contains("git-filter-client") || !welcome.Contains("version=2"))
        {
            throw new InvalidDataException("git did not start the filter protocol version 2; run this only from git (filter.forest.process).");
        }

        await writer.WriteTextListAsync(["git-filter-server", "version=2"], cancellationToken);
        var offered = await reader.ReadTextListAsync(cancellationToken) ?? [];
        await writer.WriteTextListAsync(
            offered.Where(c => c is "capability=clean" or "capability=smudge"), cancellationToken);
    }
}
