import { queryOptions } from "@tanstack/react-query";
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
