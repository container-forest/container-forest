using System.Security.Claims;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Contracts.Bot;
using Grpc.Core;
using AgentContainerState = ContainerForest.Aggregator.Application.Agents.ContainerState;
using BotContainerAction = ContainerForest.Contracts.Bot.ContainerAction;
using BotEnvironment = ContainerForest.Contracts.Bot.Environment;
using ContainerAction = ContainerForest.Aggregator.Application.Containers.ContainerAction;
using ContainerState = ContainerForest.Contracts.Bot.ContainerState;

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
    ContainerCommandService containerCommands) : BotGateway.BotGatewayBase
{
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
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType: "DiscordBot"));
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
