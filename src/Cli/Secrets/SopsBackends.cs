using System.ComponentModel;
using System.Diagnostics;
using ContainerForest.Cli.Api;
using Microsoft.Kiota.Abstractions;

namespace ContainerForest.Cli.Secrets;

/// <summary>Why a file could not be decrypted or encrypted; the message is for the terminal and has no content.</summary>
internal sealed class SopsBackendException(string message) : Exception(message);

/// <summary>Where SOPS files are decrypted and encrypted (Strategy): the aggregator, or sops on this machine.</summary>
internal interface ISopsBackend
{
    /// <exception cref="SopsBackendException">The file cannot be decrypted here.</exception>
    Task<byte[]> DecryptAsync(string path, byte[] content, CancellationToken cancellationToken);

    /// <summary>Encrypts for the rule of the repository's .sops.yaml that matches the path.</summary>
    /// <exception cref="SopsBackendException">The file cannot be encrypted here.</exception>
    Task<byte[]> EncryptAsync(string path, byte[] content, CancellationToken cancellationToken);
}

/// <summary>
/// The aggregator decrypts with the keys of the environments where the user is an Editor and encrypts for the age
/// recipients of .sops.yaml (ADR-0010). Nobody needs a private key on their machine.
/// </summary>
internal sealed class AggregatorSops(Func<ForestApi> connect, string root) : ISopsBackend
{
    private ForestApi? api;

    public Task<byte[]> DecryptAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        CallAsync(a => a.DecryptSopsFileAsync(path, content, cancellationToken));

    public Task<byte[]> EncryptAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(root, ".sops.yaml");
        if (!File.Exists(configPath))
        {
            throw new SopsBackendException("there is no .sops.yaml at the repository root; run 'forest secrets init'.");
        }

        var config = File.ReadAllText(configPath);
        return CallAsync(a => a.EncryptSopsFileAsync(path, config, content, cancellationToken));
    }

    private async Task<byte[]> CallAsync(Func<ForestApi, Task<byte[]>> call)
    {
        try
        {
            api ??= connect();
            return await call(api);
        }
        catch (CliException exception)
        {
            throw new SopsBackendException(exception.Message);
        }
        catch (ServerProblemException exception)
        {
            throw new SopsBackendException(ApiErrors.Translate(exception, api!.Server).Message);
        }
        catch (ApiException exception)
        {
            throw new SopsBackendException(ApiErrors.Translate(exception, api!.Server).Message);
        }
        catch (HttpRequestException exception)
        {
            throw new SopsBackendException(ApiErrors.Unreachable(exception, api!.Server).Message);
        }
    }
}

/// <summary>
/// sops on this machine, with the user's own keys (SOPS_AGE_KEY_FILE and the like) and the repository's .sops.yaml:
/// for <c>forest.localSops</c>, when the aggregator is out of reach or the rules need more than age.
/// </summary>
internal sealed class LocalSops(string sopsPath, string root) : ISopsBackend
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public Task<byte[]> DecryptAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        RunAsync("decrypt", path, content, cancellationToken);

    public Task<byte[]> EncryptAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        RunAsync("encrypt", path, content, cancellationToken);

    private async Task<byte[]> RunAsync(string verb, string path, byte[] content, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(sopsPath)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { verb, "--filename-override", path, "/dev/stdin" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new SopsBackendException("sops could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new SopsBackendException($"local sops ({sopsPath}) could not be started: {exception.Message}");
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(Timeout);
            try
            {
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                using var stdout = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(content, timeout.Token);
                process.StandardInput.Close();
                await copy;
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode == 0)
                {
                    return stdout.ToArray();
                }

                // sops never prints plain text in its errors; on encryption a parse error may quote the input, so not then.
                var reason = verb == "decrypt"
                    ? (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                    : null;
                throw new SopsBackendException($"local sops failed with exit code {process.ExitCode}{(reason is null ? "" : $": {reason}")}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new SopsBackendException($"local sops did not finish in {Timeout.TotalSeconds:0} s.");
            }
        }
    }
}
