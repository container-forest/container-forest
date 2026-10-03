using System.ComponentModel;
using System.Diagnostics;

namespace ContainerForest.Aggregator.Infrastructure.Secrets;

/// <param name="Stdout">Raw bytes: a decrypted binary file need not be text.</param>
internal sealed record CliResult(int ExitCode, byte[] Stdout, string Stderr);

/// <summary>Thrown when the binary is not installed or cannot be started.</summary>
internal sealed class CliUnavailableException(string fileName, Exception innerException)
    : Exception($"'{fileName}' cannot be started: {innerException.Message}", innerException);

/// <summary>
/// Runs a command-line tool without a shell. The child gets only the environment passed here, not the aggregator's:
/// its CF_* settings must not reach a tool, and a secret passed in the environment reaches nothing else.
/// </summary>
internal static class CliProcess
{
    public static async Task<CliResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        byte[]? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // PATH stays: a bare tool name is resolved with it, and the tool may start helpers.
        var path = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment.Clear();
        if (path is not null)
        {
            startInfo.Environment["PATH"] = path;
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new CliUnavailableException(fileName, ex);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            // Reading both streams while writing stdin: a full pipe on either side would block the other.
            var stdout = ReadAllAsync(process.StandardOutput.BaseStream, timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin, timeoutSource.Token);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeoutSource.Token);
            return new CliResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{fileName}' did not finish in {timeout.TotalSeconds:0} s.");
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
