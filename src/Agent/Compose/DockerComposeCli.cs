using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Compose;

/// <summary>Runs `docker compose` and reports its output line by line.</summary>
public interface IComposeRunner
{
    /// <exception cref="ComposeFailedException">Compose exited with a non-zero code.</exception>
    /// <exception cref="ComposeTimeoutException">Compose ran longer than the configured timeout and was killed.</exception>
    /// <exception cref="ComposeUnavailableException">The docker CLI could not be started.</exception>
    Task RunAsync(ComposeInvocation invocation, ICommandProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Runs compose and returns its standard output, at most <paramref name="maxOutputBytes"/> characters; standard
    /// error is reported line by line as for <see cref="RunAsync"/>.
    /// </summary>
    /// <exception cref="ComposeOutputTooLargeException">The output is longer than the limit.</exception>
    Task<string> CaptureAsync(ComposeInvocation invocation, ICommandProgress progress, int maxOutputBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Adapter from <see cref="IComposeRunner"/> to the docker CLI. The compose plugin does the real work: reimplementing
/// compose on the Docker API would be a project of its own.
/// </summary>
public sealed class DockerComposeCli(IOptions<AgentOptions> options, ILogger<DockerComposeCli> logger) : IComposeRunner
{
    /// <summary>Longer lines are cut: one progress message must stay far below gRPC's 4 MB message limit.</summary>
    public const int MaxLineLength = 8 * 1024;

    // The agent's own settings, such as CF_AGENT__ENROLLMENTTOKEN, must not reach compose, which could interpolate
    // them into a container's environment from a compose file.
    private const string AgentVariablePrefix = "CF_";

    public Task RunAsync(ComposeInvocation invocation, ICommandProgress progress, CancellationToken cancellationToken) =>
        RunCoreAsync(invocation, (stdout, token) => PumpAsync(stdout, progress, token), progress, cancellationToken);

    public async Task<string> CaptureAsync(
        ComposeInvocation invocation, ICommandProgress progress, int maxOutputBytes, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        await RunCoreAsync(invocation, async (stdout, token) =>
        {
            var buffer = new char[8192];
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (output.Length + read > maxOutputBytes)
                {
                    throw new ComposeOutputTooLargeException(invocation.Operation, maxOutputBytes);
                }

                output.Append(buffer, 0, read);
            }
        }, progress, cancellationToken);
        return output.ToString();
    }

    /// <param name="readStdout">Consumes standard output; standard error always goes to <paramref name="progress"/>.</param>
    private async Task RunCoreAsync(
        ComposeInvocation invocation,
        Func<StreamReader, CancellationToken, Task> readStdout,
        ICommandProgress progress,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(invocation) };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ComposeUnavailableException(options.Value.DockerExecutable, ex);
        }

        process.StandardInput.Close();
        logger.LogInformation("Started {Operation} in {Directory} (pid {Pid})", invocation.Operation, invocation.WorkingDirectory, process.Id);

        var timeout = options.Value.ComposeTimeout;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        var output = Task.WhenAll(
            CancelOnFailureAsync(readStdout(process.StandardOutput, budget.Token), budget),
            PumpAsync(process.StandardError, progress, budget.Token));
        try
        {
            await output;
            await process.WaitForExitAsync(budget.Token);
        }
        catch (Exception ex)
        {
            // Cancelled, timed out, or the progress could not be sent because the connection is gone:
            // compose must not keep changing the stack when nobody waits for the result.
            Kill(process);
            await DrainAsync(output);
            await process.WaitForExitAsync(CancellationToken.None);

            if (ex is OperationCanceledException && budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ComposeTimeoutException(invocation.Operation, timeout);
            }

            throw;
        }

        logger.LogInformation("{Operation} exited with code {ExitCode}", invocation.Operation, process.ExitCode);
        if (process.ExitCode != 0)
        {
            throw new ComposeFailedException(invocation.Operation, process.ExitCode);
        }
    }

    private ProcessStartInfo CreateStartInfo(ComposeInvocation invocation)
    {
        // ArgumentList, not a command line: no shell parses the arguments, so a value cannot inject another command.
        var info = new ProcessStartInfo(options.Value.DockerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in invocation.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith(AgentVariablePrefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        // Env secrets go to the process environment, not to an .env file, so they are not written to disk.
        foreach (var (name, value) in invocation.Environment)
        {
            info.Environment[name] = value;
        }

        return info;
    }

    // A reader that gives up (too much output) must stop the other one too, or waiting for both never ends.
    private static async Task CancelOnFailureAsync(Task reading, CancellationTokenSource budget)
    {
        try
        {
            await reading;
        }
        catch
        {
            await budget.CancelAsync();
            throw;
        }
    }

    private static async Task PumpAsync(StreamReader reader, ICommandProgress progress, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await progress.ReportAsync(line.Length > MaxLineLength ? line[..MaxLineLength] + " [truncated]" : line, cancellationToken);
        }
    }

    private void Kill(Process process)
    {
        try
        {
            // docker runs the compose plugin as a child process; killing only docker would leave compose running.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited.
            logger.LogDebug("Could not kill process {Pid}: {Reason}", process.Id, ex.Message);
        }
    }

    private static async Task DrainAsync(Task output)
    {
        try
        {
            await output;
        }
        catch (Exception)
        {
            // The first failure is already being reported; the rest of the output is not needed.
        }
    }
}
