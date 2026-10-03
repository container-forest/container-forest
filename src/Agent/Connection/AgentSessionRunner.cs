using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Docker;
using ContainerForest.Agent.Enrollment;
using ContainerForest.Agent.Platform;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Connection;

/// <summary>
/// One Connect call: Hello first, then heartbeats, container events and command execution until the stream ends.
/// Commands run concurrently, so a long stop does not delay other commands or heartbeats.
/// </summary>
public sealed class AgentSessionRunner(
    CommandDispatcher dispatcher,
    ContainerEventForwarder containerEvents,
    IDockerGateway docker,
    IBootIdProvider bootIds,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<AgentSessionRunner> logger)
{
    public const string AgentIdHeader = "x-agent-id";
    public const string AgentSecretHeader = "x-agent-secret";

    /// <summary>Returns when the aggregator ends the stream or the connection breaks.</summary>
    /// <exception cref="RpcException">The call failed, e.g. with Unauthenticated for revoked credentials.</exception>
    public async Task RunAsync(AgentGateway.AgentGatewayClient gateway, AgentCredentials credentials, CancellationToken stoppingToken)
    {
        // Prepared before connecting: the aggregator waits for Hello only a few seconds.
        var hello = await CreateHelloAsync(stoppingToken);

        using var call = gateway.Connect(
            new Metadata { { AgentIdHeader, credentials.AgentId }, { AgentSecretHeader, credentials.AgentSecret } },
            cancellationToken: stoppingToken);
        using var writer = new AgentStreamWriter(call.RequestStream);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        await writer.WriteAsync(new AgentMessage { Hello = hello }, session.Token);
        logger.LogInformation(
            "Connected to {ControllerUrl} as agent {AgentId} (boot {BootId}, Docker {DockerVersion})",
            options.Value.ControllerUrl, credentials.AgentId, hello.BootId, hello.DockerVersion is "" ? "unavailable" : hello.DockerVersion);

        var heartbeats = SendHeartbeatsAsync(writer, session);
        var events = containerEvents.RunAsync(writer, session.Token);
        var commands = new List<Task>();
        try
        {
            while (await call.ResponseStream.MoveNext(session.Token))
            {
                commands.RemoveAll(c => c.IsCompleted);
                commands.Add(RunCommandAsync(call.ResponseStream.Current, writer, session.Token));
            }
        }
        catch (Exception ex) when (IsSessionCancellation(ex, session, stoppingToken))
        {
            // The heartbeat could not be written, so the stream is broken.
        }
        finally
        {
            await session.CancelAsync();
            await heartbeats;
            await events;
            await Task.WhenAll(commands);
        }
    }

    private async Task<Hello> CreateHelloAsync(CancellationToken cancellationToken)
    {
        var dockerVersion = "";
        try
        {
            dockerVersion = await docker.GetVersionAsync(cancellationToken);
        }
        catch (AgentCommandException ex)
        {
            // The agent still connects: the environment is online, and commands report the Docker problem themselves.
            logger.LogWarning("Docker is not available: {Reason}", ex.Message);
        }

        return new Hello { AgentVersion = AgentInfo.Version, DockerVersion = dockerVersion, BootId = bootIds.GetBootId() };
    }

    private async Task SendHeartbeatsAsync(AgentStreamWriter writer, CancellationTokenSource session)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, time);
            while (await timer.WaitForNextTickAsync(session.Token))
            {
                await writer.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, session.Token);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Heartbeat failed, closing the connection: {Reason}", ex.Message);
            await session.CancelAsync();
        }
    }

    private async Task RunCommandAsync(ControllerMessage message, AgentStreamWriter writer, CancellationToken sessionToken)
    {
        try
        {
            var progress = new StreamCommandProgress(message.RequestId, writer);
            if (await dispatcher.DispatchAsync(message, progress, sessionToken) is { } result)
            {
                await writer.WriteAsync(new AgentMessage { Result = result }, sessionToken);
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            // The connection is gone, so the result has nowhere to go; the aggregator already failed the request.
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not send the result of request {RequestId}: {Reason}", message.RequestId, ex.Message);
        }
    }

    private static bool IsSessionCancellation(Exception ex, CancellationTokenSource session, CancellationToken stoppingToken) =>
        session.IsCancellationRequested
        && !stoppingToken.IsCancellationRequested
        && ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled };
}
