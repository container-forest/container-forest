import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { meQuery } from "./queries";
import type { components } from "./schema.gen";

export type LoginRequest = components["schemas"]["LoginRequest"];

export type ChangePasswordRequest = components["schemas"]["ChangePasswordRequest"];

/**
 * Signs in with a password; the aggregator sets the cf_session cookie. Whatever was cached belonged to whoever
 * was signed in before, so it is dropped and the new user is cached as `me`.
 */
export function useLoginMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: LoginRequest) => unwrap(await api.POST("/api/auth/login", { body: request })),
    onSuccess: (me) => {
      queryClient.removeQueries();
      queryClient.setQueryData(meQuery.queryKey, me);
    },
  });
}

/** Ends the session and drops everything cached for the signed-in user. */
export function useLogoutMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/auth/logout")),
    onSuccess: () => queryClient.removeQueries(),
  });
}

/**
 * Changes the signed-in user's own password. The aggregator ends their other sessions and renews this one's
 * cookie, so nothing cached changes.
 */
export function useChangePasswordMutation() {
  return useMutation({
    mutationFn: async (request: ChangePasswordRequest) => unwrap(await api.POST("/api/auth/password", { body: request })),
  });
}
