using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Agents;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Api.Grpc;

/// <summary>gRPC endpoint the agents connect to (src/Contracts/agent/agent.proto).</summary>
public sealed class AgentGatewayService(
    IServiceScopeFactory scopeFactory,
    AgentRegistry registry,
    IOptions<AgentGatewayOptions> options,
    TimeProvider time,
    ILogger<AgentGatewayService> logger) : AgentGateway.AgentGatewayBase
{
    public const string AgentIdHeader = "x-agent-id";
    public const string AgentSecretHeader = "x-agent-secret";

    public override async Task<EnrollResponse> Enroll(EnrollRequest request, ServerCallContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var enrollment = scope.ServiceProvider.GetRequiredService<AgentEnrollmentService>();

        var credentials = await enrollment.EnrollAsync(request.EnrollmentToken, request.Hostname, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Enrollment token is invalid, expired or already used."));

        logger.LogInformation(
            "Agent on host {Hostname} enrolled into environment {EnvironmentId}", request.Hostname, credentials.EnvironmentId);
        return new EnrollResponse { AgentId = credentials.EnvironmentId.ToString(), AgentSecret = credentials.Secret };
    }

    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControllerMessage> responseStream,
        ServerCallContext context)
    {
        var callAborted = context.CancellationToken;
        var environmentId = await AuthenticateAsync(context);
        var hello = await ReadHelloAsync(requestStream, callAborted);

        bool rebooted;
        try
        {
            rebooted = await WithEnrollmentAsync(
                e => e.RecordConnectedAsync(environmentId, hello.BootId, hello.AgentVersion, callAborted));
        }
        catch (DomainValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        var session = new AgentSession(environmentId, responseStream, time.GetUtcNow(), logger);
        registry.Open(session);
        logger.LogInformation(
            "Agent {AgentVersion} of environment {EnvironmentId} connected (session {SessionId}, Docker {DockerVersion})",
            hello.AgentVersion, environmentId, session.SessionId, hello.DockerVersion);
        if (rebooted)
        {
            // File secrets live on tmpfs and are gone after a reboot. Re-sending them arrives with SOPS deployments.
            logger.LogWarning("Host of environment {EnvironmentId} rebooted; secret rehydration is not implemented yet", environmentId);
        }

        var reason = "connection closed";
        try
        {
            reason = await ReceiveAsync(requestStream, session, callAborted);
        }
        finally
        {
            // Only the current session records the disconnect; a replaced one would overwrite newer data.
            if (registry.Remove(session, reason))
            {
                await RecordDisconnectedAsync(session);
            }
        }
    }

    private async Task<EnvironmentId> AuthenticateAsync(ServerCallContext context)
    {
        var agentId = context.RequestHeaders.GetValue(AgentIdHeader);
        var secret = context.RequestHeaders.GetValue(AgentSecretHeader);
        if (!Guid.TryParse(agentId, out var id) || string.IsNullOrEmpty(secret))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent credentials are missing."));
        }

        var environmentId = await WithEnrollmentAsync(
            e => e.AuthenticateAsync(EnvironmentId.From(id), secret, context.CancellationToken));
        return environmentId ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent credentials are invalid."));
    }

    private async Task<Hello> ReadHelloAsync(IAsyncStreamReader<AgentMessage> requestStream, CancellationToken callAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callAborted);
        timeout.CancelAfter(options.Value.HelloTimeout);
        try
        {
            if (await requestStream.MoveNext(timeout.Token) && requestStream.Current.BodyCase == AgentMessage.BodyOneofCase.Hello)
            {
                return requestStream.Current.Hello;
            }
        }
        catch (OperationCanceledException) when (!callAborted.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Agent did not send Hello in time."));
        }

        throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be Hello."));
    }

    /// <summary>Reads agent messages until the call ends. Returns why it ended.</summary>
    private async Task<string> ReceiveAsync(
        IAsyncStreamReader<AgentMessage> requestStream, AgentSession session, CancellationToken callAborted)
    {
        var idleTimeout = options.Value.IdleTimeout;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(callAborted, session.Closed);
        stop.CancelAfter(idleTimeout);

        try
        {
            while (await requestStream.MoveNext(stop.Token))
            {
                stop.CancelAfter(idleTimeout);
                session.RecordActivity(time.GetUtcNow());
                Dispatch(session, requestStream.Current);
            }

            return "agent closed the stream";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return session.IsClosed ? "replaced by a new connection"
                : callAborted.IsCancellationRequested ? "connection lost"
                : $"no messages for {idleTimeout.TotalSeconds:0} s";
        }
    }

    private void Dispatch(AgentSession session, AgentMessage message)
    {
        switch (message.BodyCase)
        {
            case AgentMessage.BodyOneofCase.Result:
                session.Complete(message.Result);
                break;
            case AgentMessage.BodyOneofCase.Progress:
                session.ReportProgress(message.Progress);
                break;
            case AgentMessage.BodyOneofCase.LogBatch:
                session.ReportLogs(message.LogBatch);
                break;
            case AgentMessage.BodyOneofCase.Heartbeat:
                break;
            case AgentMessage.BodyOneofCase.Hello:
                logger.LogWarning("Environment {EnvironmentId} sent a second Hello; ignoring it", session.EnvironmentId);
                break;
            default:
                // Container events get a consumer with live updates.
                logger.LogTrace("Ignoring {MessageType} from environment {EnvironmentId}", message.BodyCase, session.EnvironmentId);
                break;
        }
    }

    private async Task RecordDisconnectedAsync(AgentSession session)
    {
        try
        {
            await WithEnrollmentAsync(e => e.RecordDisconnectedAsync(session.EnvironmentId, session.LastMessageAt, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record disconnect of environment {EnvironmentId}", session.EnvironmentId);
        }
    }

    // The Connect call lives for hours; each database operation gets its own short-lived scope and DbContext.
    private async Task<T> WithEnrollmentAsync<T>(Func<AgentEnrollmentService, Task<T>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AgentEnrollmentService>());
    }

    private async Task WithEnrollmentAsync(Func<AgentEnrollmentService, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AgentEnrollmentService>());
    }
}
