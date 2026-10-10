using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Login;

/// <summary>
/// Gets an API token through the browser (ADR-0008): starts a login, sends the person to the panel to approve it and
/// polls the aggregator until the token is there. With <c>device</c> it only prints the address and the code, for a
/// terminal without a browser (SSH).
/// </summary>
internal sealed class BrowserLogin(ForestApi api, IBrowser browser, TimeProvider time, CliOutput output)
{
    /// <summary>How much longer to wait after the server asks the CLI to slow down.</summary>
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    /// <param name="secrets">Ask for a token that may decrypt secrets (the git filter, ADR-0010).</param>
    /// <exception cref="CliException">The person denied the login, or it expired.</exception>
    public async Task<CliTokenResponse> RunAsync(bool device, bool openBrowser, CancellationToken cancellationToken, bool secrets = false)
    {
        var started = await api.StartLoginAsync($"forest {CliVersion.Current}", Environment.MachineName, secrets, cancellationToken);
        var deviceCode = ForestApi.Required(started.DeviceCode, "device code");
        var userCode = ForestApi.Required(started.UserCode, "user code");

        using var signal = device ? null : LoopbackSignal.Start();
        if (signal is null)
        {
            var page = new Uri(api.Server, ForestApi.Required(started.VerificationPath, "verification page").TrimStart('/'));
            output.Note($"Open {page} in a browser on any device, sign in and enter the code {userCode}.");
        }
        else
        {
            var path = ForestApi.Required(started.VerificationPathComplete, "verification page").TrimStart('/');
            var page = new Uri(api.Server, $"{path}&port={signal.Port}");
            output.Note($"Confirm the code {userCode} in the browser.");
            if (!openBrowser || !browser.TryOpen(page))
            {
                output.Note($"Open this page to continue: {page}");
            }
        }

        output.Note("Waiting for approval…");
        return await PollAsync(deviceCode, started, signal, cancellationToken);
    }

    private async Task<CliTokenResponse> PollAsync(
        string deviceCode, CliAuthorizationStartedResponse started, LoopbackSignal? signal, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(started.IntervalSeconds is > 0 ? started.IntervalSeconds.Value : 5);
        var expiresAt = started.ExpiresAt ?? time.GetUtcNow().AddMinutes(10);
        var wakeUp = signal?.Decision;

        while (true)
        {
            var delay = Task.Delay(interval, time, cancellationToken);
            if (wakeUp is not null && await Task.WhenAny(delay, wakeUp) == wakeUp)
            {
                // The browser is back: poll now, and from then on at the interval again.
                wakeUp = null;
            }
            else
            {
                await delay;
            }

            var response = await api.PollLoginAsync(deviceCode, cancellationToken);
            switch (response.Status)
            {
                case CliPollResult.Approved:
                    return response;
                case CliPollResult.Denied:
                    throw new CliException("The login was denied in the browser.", ExitCodes.Denied);
                case CliPollResult.Expired:
                    throw new CliException("The login expired before it was approved; run 'forest login' again.", ExitCodes.Unauthenticated);
                case CliPollResult.SlowDown:
                    interval += SlowDownStep;
                    break;
            }

            if (time.GetUtcNow() >= expiresAt)
            {
                throw new CliException("The login expired before it was approved; run 'forest login' again.", ExitCodes.Unauthenticated);
            }
        }
    }
}
