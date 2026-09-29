using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Enrollment;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Connection;

/// <summary>Enrolls once, then keeps a connection to the aggregator open, reconnecting with backoff.</summary>
public sealed class AgentWorker(
    AgentEnroller enroller,
    CredentialStore credentialStore,
    AgentSessionRunner runner,
    IOptions<AgentOptions> options,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly ReconnectBackoff backoff = new(Random.Shared);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var channel = CreateChannel(options.Value.ControllerUrl!);
        var gateway = new AgentGateway.AgentGatewayClient(channel);
        try
        {
            var credentials = await EnrollAsync(gateway, stoppingToken);
            await KeepConnectedAsync(gateway, credentials, stoppingToken);
        }
        catch (FatalAgentException ex)
        {
            logger.LogCritical(ex, "The agent cannot continue: {Reason}", ex.Message);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        catch (Exception ex) when (stoppingToken.IsCancellationRequested && IsCancellation(ex))
        {
            // Normal shutdown. gRPC reports a cancelled call as RpcException(Cancelled), not OperationCanceledException.
        }
    }

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled };

    private async Task<AgentCredentials> EnrollAsync(AgentGateway.AgentGatewayClient gateway, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                var credentials = await enroller.GetCredentialsAsync(gateway, stoppingToken);
                backoff.Reset();
                return credentials;
            }
            catch (RpcException ex) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = backoff.NextDelay();
                logger.LogWarning(
                    "Cannot enroll, the aggregator is unreachable ({Reason}); retrying in {Delay:0.0} s", Describe(ex), delay.TotalSeconds);
                await Task.Delay(delay, time, stoppingToken);
            }
        }
    }

    private async Task KeepConnectedAsync(
        AgentGateway.AgentGatewayClient gateway, AgentCredentials credentials, CancellationToken stoppingToken)
    {
        while (true)
        {
            var started = time.GetTimestamp();
            try
            {
                await runner.RunAsync(gateway, credentials, stoppingToken);
                logger.LogWarning("The connection to the aggregator ended");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated && !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(
                    "The aggregator rejected the agent credentials ({Reason}). If the agent was revoked, delete {Path} "
                    + "and start it with a new CF_AGENT__ENROLLMENTTOKEN.",
                    ex.Status.Detail, credentialStore.FilePath);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ex is not FatalAgentException)
            {
                logger.LogWarning("The connection to the aggregator failed: {Reason}", Describe(ex));
            }

            backoff.SessionEnded(time.GetElapsedTime(started));
            var delay = backoff.NextDelay();
            logger.LogInformation("Reconnecting in {Delay:0.0} s (attempt {Attempt})", delay.TotalSeconds, backoff.Attempts);
            await Task.Delay(delay, time, stoppingToken);
        }
    }

    private static GrpcChannel CreateChannel(Uri controllerUrl) => GrpcChannel.ForAddress(controllerUrl, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // HTTP/2 pings notice a dead connection (e.g. a dropped NAT mapping) even when writes still fit in buffers.
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
        },
    });

    private static string Describe(Exception ex) =>
        ex is RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : ex.Message;
}
