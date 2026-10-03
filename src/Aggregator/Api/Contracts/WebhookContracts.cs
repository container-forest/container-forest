using ContainerForest.Aggregator.Application.Webhooks;

namespace ContainerForest.Aggregator.Api.Contracts;

// Push webhooks from git servers. Anonymous: the delivery is authenticated by the webhook secret of the repository,
// or of the git connection for an organization-wide webhook (ADR-0006).
//
//   POST /api/webhooks/{repositoryId}                -> 200 WebhookResponse; GitHub, Gitea/Forgejo or GitLab; JSON body only
//   POST /api/webhooks/connections/{connectionId}    -> 200 WebhookResponse; the pushed repository is found among the
//                                                       connection's repositories by the address in the payload
//
// 401 for an unknown repository or connection, one without a webhook, an unknown git server or a wrong signature
// alike; 400 for an authentic body that is not JSON; 413 for a body over 5 MiB. The response is for the git
// server's delivery log: what happened to each stack that deploys the pushed branch on push.

public sealed record WebhookResponse(string Provider, WebhookOutcome Outcome, string? Detail, IReadOnlyList<WebhookStackResponse> Stacks);

/// <param name="DeploymentId">The queued deployment, for <see cref="PushStackOutcome.Queued"/>.</param>
public sealed record WebhookStackResponse(Guid StackId, Guid EnvironmentId, string Name, PushStackOutcome Outcome, Guid? DeploymentId);
