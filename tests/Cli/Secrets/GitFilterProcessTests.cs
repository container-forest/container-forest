using System.Text;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Tests.Secrets;

/// <summary>Plays git's side of the long-running filter protocol against the filter.</summary>
public class GitFilterProcessTests
{
    private readonly FakeSops sops = new();
    private readonly StringWriter error = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Handshake_then_smudge_and_clean_of_each_file()
    {
        var (requests, git) = Git();
        await git.WriteTextListAsync(["git-filter-client", "version=2"], Ct);
        await git.WriteTextListAsync(["capability=clean", "capability=smudge", "capability=delay"], Ct);
        await Request(git, "smudge", "app/.env", FakeSops.Wrap("TOKEN=s3cret"));
        await Request(git, "clean", "app/.env", "TOKEN=new"u8.ToArray());
        await Request(git, "clean", "other/.env", FakeSops.Wrap("A=1", 7));
        requests.Position = 0;

        var responses = new MemoryStream();
        await new GitFilterProcess(requests, responses, Operations()).RunAsync(Ct);

        responses.Position = 0;
        var reader = new PktLineReader(responses);
        Assert.Equal(["git-filter-server", "version=2"], await reader.ReadTextListAsync(Ct));
        Assert.Equal(["capability=clean", "capability=smudge"], await reader.ReadTextListAsync(Ct));
        Assert.Equal("TOKEN=s3cret", await Response(reader));
        Assert.Equal(Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=new", 1)), await Response(reader));
        Assert.Equal(Encoding.UTF8.GetString(FakeSops.Wrap("A=1", 7)), await Response(reader));
        Assert.Null(await reader.ReadTextListAsync(Ct));
    }

    [Fact]
    public async Task Failed_clean_is_an_error_for_that_file_only()
    {
        sops.Refusal = "no .sops.yaml";
        var (requests, git) = Git();
        await git.WriteTextListAsync(["git-filter-client", "version=2"], Ct);
        await git.WriteTextListAsync(["capability=clean"], Ct);
        await Request(git, "clean", ".env", "TOKEN=x"u8.ToArray());
        await Request(git, "unknown", ".env", "TOKEN=x"u8.ToArray());
        requests.Position = 0;

        var responses = new MemoryStream();
        await new GitFilterProcess(requests, responses, Operations()).RunAsync(Ct);

        responses.Position = 0;
        var reader = new PktLineReader(responses);
        await reader.ReadTextListAsync(Ct);
        await reader.ReadTextListAsync(Ct);
        Assert.Equal(["status=error"], await reader.ReadTextListAsync(Ct));
        Assert.Equal(["status=error"], await reader.ReadTextListAsync(Ct));
        Assert.Contains("cannot encrypt .env", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN=x", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_protocols_are_refused()
    {
        var (requests, git) = Git();
        await git.WriteTextListAsync(["git-filter-client", "version=3"], Ct);
        requests.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => new GitFilterProcess(requests, new MemoryStream(), Operations()).RunAsync(Ct));
    }

    private Dictionary<string, FilterOperation> Operations()
    {
        var services = new FilterServices([sops], sops, null, error);
        return new() { ["clean"] = new CleanOperation(services), ["smudge"] = new SmudgeOperation(services) };
    }

    private static (MemoryStream Stream, PktLineWriter Writer) Git()
    {
        var stream = new MemoryStream();
        return (stream, new PktLineWriter(stream));
    }

    private static async Task Request(PktLineWriter git, string command, string path, byte[] content)
    {
        await git.WriteTextListAsync([$"command={command}", $"pathname={path}"], Ct);
        await git.WriteContentAsync(content, Ct);
    }

    private static async Task<string> Response(PktLineReader reader)
    {
        Assert.Equal(["status=success"], await reader.ReadTextListAsync(Ct));
        var content = Encoding.UTF8.GetString(await reader.ReadContentAsync(Ct));
        Assert.Empty((await reader.ReadTextListAsync(Ct))!);
        return content;
    }
}
