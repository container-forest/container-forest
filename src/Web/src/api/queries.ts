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
