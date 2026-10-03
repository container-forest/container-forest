import type { QueryKey } from "@tanstack/react-query";

/**
 * Events of /hubs/live, written by hand to match ILiveClient in the aggregator (src/Aggregator/Api/Live/LiveHub.cs).
 * They carry ids only: the panel reloads what changed through the API, which checks the user's rights.
 */
export type LiveEvent =
  | { name: "EnvironmentsChanged" }
  | { name: "ContainersChanged"; environmentId: string }
  | { name: "StackChanged"; environmentId: string; stackId: string }
  | { name: "DeploymentChanged"; environmentId: string; stackId: string; deploymentId: string };

/** "connecting" until the first connection, "reconnecting" after one was lost, "offline" when the aggregator refuses. */
export type LiveStatus = "connecting" | "live" | "reconnecting" | "offline";

export type Invalidation = { queryKey: QueryKey; exact: boolean };

/**
 * The queries an event makes stale (keys as in src/api/queries.ts). Lists are matched exactly, so that, for example,
 * a container change does not reload every open log; a stack's key also covers its deployment history.
 */
export function invalidationsFor(event: LiveEvent): Invalidation[] {
  switch (event.name) {
    case "EnvironmentsChanged":
      return [{ queryKey: ["environments"], exact: true }];
    case "ContainersChanged":
      return [{ queryKey: ["environments", event.environmentId, "containers"], exact: true }];
    case "StackChanged":
      return [
        { queryKey: ["environments", event.environmentId, "stacks"], exact: true },
        { queryKey: ["stacks", event.stackId], exact: true },
      ];
    case "DeploymentChanged":
      return [
        { queryKey: ["deployments", event.deploymentId], exact: true },
        { queryKey: ["stacks", event.stackId], exact: false },
        { queryKey: ["environments", event.environmentId, "stacks"], exact: true },
      ];
  }
}

/** Waits between attempts to reach the aggregator: 1 s, doubling up to 30 s, and never giving up. */
export function retryDelay(attempt: number): number {
  return Math.min(30_000, 1_000 * 2 ** Math.min(attempt, 5));
}

/**
 * While the connection is live, a screen needs no polling (or only a slow safety net, `whileLiveMs`):
 * the events tell it when to reload. Without the connection it polls every `intervalMs`.
 */
export function pollingInterval(
  status: LiveStatus,
  intervalMs: number,
  whileLiveMs: number | false = false,
): number | false {
  return status === "live" ? whileLiveMs : intervalMs;
}
