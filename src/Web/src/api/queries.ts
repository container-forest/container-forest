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
