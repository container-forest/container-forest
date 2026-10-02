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
