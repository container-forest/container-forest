using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ContainerForest.Cli.Login;

/// <summary>
/// Listens on 127.0.0.1 for the browser coming back from the approval page (ADR-0008), so the CLI asks for its token
/// at once instead of on its next poll. The request carries no secret, only the decision; whoever else on the machine
/// calls the port at most makes the CLI poll early.
/// </summary>
internal sealed class LoopbackSignal : IDisposable
{
    private const string DonePath = "/done";

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<string> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LoopbackSignal(TcpListener listener)
    {
        this.listener = listener;
        _ = AcceptAsync();
    }

    /// <summary>The port the panel sends the browser to.</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Completes with "approved" or "denied" when the browser comes back.</summary>
    public Task<string> Decision => decision.Task;

    public static LoopbackSignal Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackSignal(listener);
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                await AnswerAsync(client);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            catch (IOException)
            {
                // A broken connection from the browser; keep listening.
            }
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        // "GET /done?result=approved HTTP/1.1"; the headers that follow do not matter.
        var requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
        var target = requestLine.Split(' ') is [_, var path, ..] ? path : "";
        var isDone = target == DonePath || target.StartsWith(DonePath + "?", StringComparison.Ordinal);

        var body = isDone
            ? "<!doctype html><meta charset=utf-8><title>forest</title><p>Done. You can close this tab and return to the terminal."
            : "<!doctype html><meta charset=utf-8><title>forest</title><p>Not found.";
        var response =
            $"HTTP/1.1 {(isDone ? "200 OK" : "404 Not Found")}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n" + body;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), timeout.Token);

        if (isDone)
        {
            decision.TrySetResult(target.Contains("result=denied", StringComparison.Ordinal) ? "denied" : "approved");
        }
    }
}
