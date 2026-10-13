using System.Security.Claims;
using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Contracts.Bot;
using Grpc.Core;
using AgentContainerState = ContainerForest.Aggregator.Application.Agents.ContainerState;
using BotContainerAction = ContainerForest.Contracts.Bot.ContainerAction;
using BotContainerLogLine = ContainerForest.Contracts.Bot.ContainerLogLine;
using BotDeploymentStatus = ContainerForest.Contracts.Bot.DeploymentStatus;
using BotEnvironment = ContainerForest.Contracts.Bot.Environment;
using BotStack = ContainerForest.Contracts.Bot.Stack;
using BotStackAction = ContainerForest.Contracts.Bot.StackAction;
using ContainerAction = ContainerForest.Aggregator.Application.Containers.ContainerAction;
using ContainerState = ContainerForest.Contracts.Bot.ContainerState;
using DeploymentStatus = ContainerForest.Aggregator.Domain.Deployments.DeploymentStatus;
using LogStream = ContainerForest.Aggregator.Application.Agents.LogStream;
using StackAction = ContainerForest.Aggregator.Application.Stacks.StackAction;

namespace ContainerForest.Aggregator.Api.Grpc;

/// <summary>
/// gRPC endpoint of the Discord bot (src/Contracts/bot/bot.proto). The bot token is checked by
/// <see cref="BotGatewayInterceptor"/>; each call then acts as the user linked to the Discord account,
/// through the same services and permission checks as the panel.
/// </summary>
public sealed class BotGatewayService(
    DiscordLinkService discordLinks,
    EnvironmentService environments,
    ContainerQueryService containerQueries,
    ContainerCommandService containerCommands,
    ContainerLogService containerLogs,
    StackService stacks,
    StackCommandService stackCommands,
    DeploymentService deployments,
    NotificationDeliveryService notificationDelivery,
    NotificationFeed notificationFeed,
    IServiceScopeFactory scopeFactory) : BotGateway.BotGatewayBase
{
    /// <summary>How often an idle notification stream rescans anyway, e.g. to expire what nobody reported.</summary>
    private static readonly TimeSpan NotificationRescanInterval = TimeSpan.FromMinutes(1);

    public override async Task<GetCallerResponse> GetCaller(GetCallerRequest request, ServerCallContext context)
    {
        var user = await discordLinks.FindLinkedUserAsync(request.DiscordUserId, context.CancellationToken);
        return user is null
            ? new GetCallerResponse { Linked = false }
            : new GetCallerResponse
            {
                Linked = true,
                UserId = user.Id.ToString(),
                UserName = user.UserName,
                IsAdmin = user.IsAdmin,
                IsActive = user.IsActive,
            };
    }

    public override async Task<LinkAccountResponse> LinkAccount(LinkAccountRequest request, ServerCallContext context)
    {
        var user = await discordLinks.LinkAsync(
            request.DiscordUserId, request.DiscordUserName, request.Code, context.CancellationToken);
        return new LinkAccountResponse { UserName = user.UserName };
    }

    public override async Task<ListEnvironmentsResponse> ListEnvironments(ListEnvironmentsRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var response = new ListEnvironmentsResponse();
        response.Environments.AddRange((await environments.ListVisibleAsync(context.CancellationToken))
            .Select(e => new BotEnvironment { Id = e.Id.ToString(), Name = e.Name, Online = e.IsOnline }));
        return response;
    }

    public override async Task<ListContainersResponse> ListContainers(ListContainersRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var visible = await containerQueries.ListAsync(ParseEnvironmentId(request.EnvironmentId), context.CancellationToken);

        var response = new ListContainersResponse();
        response.Containers.AddRange(visible.Select(v => new Container
        {
            Id = v.Container.Id,
            Name = v.Container.Name,
            State = ToState(v.Container.State),
            Image = v.Container.Image,
            StackName = v.Container.StackName ?? "",
            ServiceName = v.Container.ServiceName ?? "",
            StatusText = v.Container.StatusText,
            CanControl = v.CanControl,
        }));
        return response;
    }

    public override async Task<ControlContainerResponse> ControlContainer(ControlContainerRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var action = request.Action switch
        {
            BotContainerAction.Start => ContainerAction.Start,
            BotContainerAction.Stop => ContainerAction.Stop,
            BotContainerAction.Restart => ContainerAction.Restart,
            _ => throw new DomainValidationException("Action must be start, stop or restart."),
        };
        var stopTimeout = request.HasTimeoutSeconds ? TimeSpan.FromSeconds(request.TimeoutSeconds) : (TimeSpan?)null;

        await containerCommands.ExecuteAsync(
            ParseEnvironmentId(request.EnvironmentId), request.Container, action, stopTimeout, context.CancellationToken);
        return new ControlContainerResponse();
    }

    public override async Task<GetContainerLogsResponse> GetContainerLogs(GetContainerLogsRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var lines = await containerLogs.ReadAsync(
            ParseEnvironmentId(request.EnvironmentId),
            request.Container,
            request.HasTailLines ? request.TailLines : null,
            since: null,
            context.CancellationToken);

        var response = new GetContainerLogsResponse();
        response.Lines.AddRange(lines.Select(line => new BotContainerLogLine
        {
            TimestampUnixMs = line.At?.ToUnixTimeMilliseconds() ?? 0,
            Stderr = line.Stream == LogStream.Stderr,
            Text = line.Text,
        }));
        return response;
    }

    public override async Task<ListStacksResponse> ListStacks(ListStacksRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var visible = await stacks.ListVisibleAsync(ParseEnvironmentId(request.EnvironmentId), context.CancellationToken);

        var response = new ListStacksResponse();
        response.Stacks.AddRange(visible.Select(s => new BotStack
        {
            Id = s.Id.ToString(),
            Name = s.Name.Value,
            IsSystem = s.IsSystem,
            DeployedCommit = s.DeployedCommit?.Value ?? "",
            DeployedUnixMs = s.DeployedAt?.ToUnixTimeMilliseconds() ?? 0,
            // A system stack refuses both; hiding the buttons saves the user a FAILED_PRECONDITION.
            CanRestart = s.CanRestart && !s.IsSystem,
            CanTakeDown = s.CanDeploy && !s.IsSystem,
            CanDeploy = s.CanDeploy && !s.IsSystem,
        }));
        return response;
    }

    public override async Task<ControlStackResponse> ControlStack(ControlStackRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var action = request.Action switch
        {
            BotStackAction.Restart => StackAction.Restart,
            BotStackAction.Down => StackAction.Down,
            _ => throw new DomainValidationException("Action must be restart or down."),
        };
        var stopTimeout = request.HasTimeoutSeconds ? TimeSpan.FromSeconds(request.TimeoutSeconds) : (TimeSpan?)null;

        var result = await stackCommands.ExecuteAsync(
            ParseEnvironmentId(request.EnvironmentId), request.Stack, action, stopTimeout, context.CancellationToken);
        return new ControlStackResponse { Error = result.Error ?? "", Output = { result.Output } };
    }

    public override async Task<DeployStackResponse> DeployStack(DeployStackRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);

        // The bot names the stack as the user sees it; only a visible stack is found, so an invisible one is the
        // same NOT_FOUND as a missing one. DeploymentService checks the role, the system flag and the agent.
        var name = request.Stack.Trim();
        var stack = (await stacks.ListVisibleAsync(ParseEnvironmentId(request.EnvironmentId), context.CancellationToken))
            .FirstOrDefault(s => s.Name.Value == name)
            ?? throw new NotFoundException("Stack", $"'{name}'");

        var deployment = await deployments.QueueAsync(
            stack.Id, request.Pull ? ImagePullPolicy.Always : ImagePullPolicy.Missing, context.CancellationToken);
        return new DeployStackResponse { DeploymentId = deployment.Id.ToString() };
    }

    public override async Task<GetDeploymentResponse> GetDeployment(GetDeploymentRequest request, ServerCallContext context)
    {
        await ActAsAsync(request.DiscordUserId, context);
        var id = Guid.TryParse(request.DeploymentId, out var value)
            ? DeploymentId.From(value)
            : throw new DomainValidationException($"'{request.DeploymentId}' is not a deployment id.");

        var deployment = await deployments.GetAsync(id, context.CancellationToken);
        return new GetDeploymentResponse
        {
            Status = deployment.Status switch
            {
                DeploymentStatus.Queued => BotDeploymentStatus.Queued,
                DeploymentStatus.Running => BotDeploymentStatus.Running,
                DeploymentStatus.Succeeded => BotDeploymentStatus.Succeeded,
                DeploymentStatus.Failed => BotDeploymentStatus.Failed,
                DeploymentStatus.Cancelled => BotDeploymentStatus.Cancelled,
                _ => BotDeploymentStatus.Unspecified,
            },
            Commit = deployment.Commit?.Value ?? "",
            Error = deployment.Error ?? "",
            QueuedUnixMs = deployment.QueuedAt.ToUnixTimeMilliseconds(),
            FinishedUnixMs = deployment.FinishedAt?.ToUnixTimeMilliseconds() ?? 0,
        };
    }

    /// <summary>
    /// Sends what is pending, then each new notification as it is created. Sent notifications are not sent
    /// again on this call; one that is never reported goes out again on the next call.
    /// </summary>
    public override async Task WatchNotifications(
        WatchNotificationsRequest request, IServerStreamWriter<Notification> responseStream, ServerCallContext context)
    {
        using var subscription = notificationFeed.Subscribe(context.CancellationToken);
        var awaitingReport = new HashSet<NotificationId>();
        try
        {
            while (true)
            {
                IReadOnlyList<OutgoingNotification> batch;

                // A scope per scan: one DbContext for a call that lasts days would keep stale entities.
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    batch = await scope.ServiceProvider.GetRequiredService<NotificationDeliveryService>()
                        .TakePendingAsync(awaitingReport, subscription.Ended);
                }

                foreach (var outgoing in batch)
                {
                    await responseStream.WriteAsync(NotificationMessages.ToMessage(outgoing), subscription.Ended);
                    awaitingReport.Add(outgoing.Notification.Id);
                }

                await subscription.WaitAsync(NotificationRescanInterval);
            }
        }
        catch (OperationCanceledException) when (subscription.WasReplaced)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "A newer WatchNotifications call replaced this one."));
        }
    }

    public override async Task<ReportDeliveryResponse> ReportDelivery(ReportDeliveryRequest request, ServerCallContext context)
    {
        var id = Guid.TryParse(request.NotificationId, out var value)
            ? NotificationId.From(value)
            : throw new DomainValidationException($"'{request.NotificationId}' is not a notification id.");
        await notificationDelivery.ReportAsync(id, request.Delivered, request.Error, context.CancellationToken);
        return new ReportDeliveryResponse();
    }

    /// <summary>
    /// Makes the rest of the call run as the linked user: puts their id into the NameIdentifier claim,
    /// the one contract between authentication and authorization (see ClaimsCurrentUser).
    /// </summary>
    private async Task ActAsAsync(ulong discordUserId, ServerCallContext context)
    {
        var user = await discordLinks.FindLinkedUserAsync(discordUserId, context.CancellationToken)
            ?? throw BotGatewayInterceptor.Unauthenticated(
                "This Discord account is not linked; get a code in the panel and use /link.",
                BotGatewayInterceptor.AccountNotLinked);

        context.GetHttpContext().User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType: HttpRequestOrigin.BotAuthenticationType));
    }

    private static EnvironmentId ParseEnvironmentId(string value) =>
        Guid.TryParse(value, out var id)
            ? EnvironmentId.From(id)
            : throw new DomainValidationException($"'{value}' is not an environment id.");

    private static ContainerState ToState(AgentContainerState state) => state switch
    {
        AgentContainerState.Created => ContainerState.Created,
        AgentContainerState.Running => ContainerState.Running,
        AgentContainerState.Paused => ContainerState.Paused,
        AgentContainerState.Restarting => ContainerState.Restarting,
        AgentContainerState.Removing => ContainerState.Removing,
        AgentContainerState.Exited => ContainerState.Exited,
        AgentContainerState.Dead => ContainerState.Dead,
        _ => ContainerState.Unspecified,
    };
}
