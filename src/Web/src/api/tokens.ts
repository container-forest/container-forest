import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { apiTokensQuery, userApiTokensQuery } from "./queries";
import type { components } from "./schema.gen";

/**
 * Creates a personal API token. The answer holds the token itself, so the mutation keeps nothing (`gcTime: 0`) and
 * the caller resets it as soon as the token is in its own state.
 */
export function useCreateApiTokenMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async (request: components["schemas"]["CreateApiTokenRequest"]) =>
      unwrap(await api.POST("/api/me/tokens", { body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: apiTokensQuery.queryKey }),
  });
}

/** Revokes one of the signed-in user's tokens: requests with it fail from now on. */
export function useRevokeApiTokenMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/me/tokens/{id}", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: apiTokensQuery.queryKey }),
  });
}

/** An administrator revokes another user's token. */
export function useRevokeUserApiTokenMutation(userId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (tokenId: string) =>
      unwrap(await api.DELETE("/api/users/{userId}/tokens/{tokenId}", { params: { path: { userId, tokenId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: userApiTokensQuery(userId).queryKey }),
  });
}
