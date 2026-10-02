import { queryOptions } from "@tanstack/react-query";
import { type LogLine, MAX_LINES, mergeLines, nextSince, shouldRetryLoad } from "@/features/container-logs/model";
import { api, unwrap } from "./client";

// The single source of query keys: components use these factories, never inline keys.

export const meQuery = queryOptions({
  queryKey: ["me"],
  queryFn: async ({ signal }) => unwrap(await api.GET("/api/me", { signal })),
  staleTime: 60_000,
});

/** Whether the signed-in user's account is linked to a Discord account. */
export const discordLinkQuery = queryOptions({
  queryKey: ["me", "discord"],
  queryFn: async ({ signal }) => unwrap(await api.GET("/api/me/discord", { signal })),
});

/** Environments the signed-in user may see, with their agents' status, in name order. */
export const environmentsQuery = queryOptions({
  queryKey: ["environments"],
  queryFn: async ({ signal }) => unwrap(await api.GET("/api/environments", { signal })),
});

/** Containers of one environment as its agent reports them right now; fails with 503 while the agent is offline. */
export const containersQuery = (environmentId: string) =>
  queryOptions({
    queryKey: ["environments", environmentId, "containers"],
    queryFn: async ({ signal }) =>
      unwrap(await api.GET("/api/environments/{id}/containers", { params: { path: { id: environmentId } }, signal })),
  });

/**
 * The end of a container's log, oldest first. The first load asks for the last `tail` lines; every refetch asks
 * only for lines since the newest one already loaded and appends them, so auto-refresh does not reload the tail.
 * A different `tail` is a different query and starts over.
 */
export const containerLogsQuery = (environmentId: string, container: string, tail: number) =>
  queryOptions({
    queryKey: ["environments", environmentId, "containers", container, "logs", tail],
    queryFn: async ({ client, queryKey, signal }): Promise<LogLine[]> => {
      const path = { id: environmentId, containerId: container };
      const loaded = client.getQueryData<LogLine[]>(queryKey);
      const since = loaded === undefined ? null : nextSince(loaded);
      if (loaded === undefined || since === null) {
        const response = unwrap(
          await api.GET("/api/environments/{id}/containers/{containerId}/logs", { params: { path, query: { tail } }, signal }),
        );
        return response.lines;
      }

      const response = unwrap(
        await api.GET("/api/environments/{id}/containers/{containerId}/logs", {
          params: { path, query: { tail: MAX_LINES, since } },
          signal,
        }),
      );
      return mergeLines(loaded, response.lines, since);
    },
    retry: shouldRetryLoad,
  });

/** Registry logins of one environment, by server; never the passwords. Administrators only. */
export const registryCredentialsQuery = (environmentId: string) =>
  queryOptions({
    queryKey: ["environments", environmentId, "registry-credentials"],
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET("/api/environments/{id}/registry-credentials", { params: { path: { id: environmentId } }, signal }),
      ),
  });

/** All users, by name. Administrators only. */
export const usersQuery = queryOptions({
  queryKey: ["users"],
  queryFn: async ({ signal }) => unwrap(await api.GET("/api/users", { signal })),
});

/** One user with their roles. Administrators only. */
export const userQuery = (id: string) =>
  queryOptions({
    queryKey: ["users", id],
    queryFn: async ({ signal }) => unwrap(await api.GET("/api/users/{id}", { params: { path: { id } }, signal })),
  });

/** Git repositories stacks are deployed from, by name. Administrators and editors only. */
export const repositoriesQuery = queryOptions({
  queryKey: ["repositories"],
  queryFn: async ({ signal }) => unwrap(await api.GET("/api/repositories", { signal })),
});

/** Stacks of one environment the signed-in user may see, by name. */
export const stacksQuery = (environmentId: string) =>
  queryOptions({
    queryKey: ["environments", environmentId, "stacks"],
    queryFn: async ({ signal }) =>
      unwrap(await api.GET("/api/environments/{id}/stacks", { params: { path: { id: environmentId } }, signal })),
  });

/** One stack with what the user may do with it. */
export const stackQuery = (id: string) =>
  queryOptions({
    queryKey: ["stacks", id],
    queryFn: async ({ signal }) => unwrap(await api.GET("/api/stacks/{id}", { params: { path: { id } }, signal })),
  });

/** The stack's latest deployments, newest first (at most 50). */
export const deploymentsQuery = (stackId: string) =>
  queryOptions({
    queryKey: ["stacks", stackId, "deployments"],
    queryFn: async ({ signal }) =>
      unwrap(await api.GET("/api/stacks/{id}/deployments", { params: { path: { id: stackId } }, signal })),
  });

/** One deployment with its log. */
export const deploymentQuery = (id: string) =>
  queryOptions({
    queryKey: ["deployments", id],
    queryFn: async ({ signal }) => unwrap(await api.GET("/api/deployments/{id}", { params: { path: { id } }, signal })),
  });
