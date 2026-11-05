import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { ageKeysQuery } from "./queries";

/** Generates a key pair on the aggregator; the answer carries only the public key. */
export function useCreateAgeKeyMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    // A stack's own key (ADR-0012) with a stack id, the environment's key without one.
    mutationFn: async (stackId: string | null = null) =>
      unwrap(
        await api.POST("/api/environments/{id}/age-keys", {
          params: { path: { id: environmentId }, query: stackId ? { stackId } : {} },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ageKeysQuery(environmentId).queryKey }),
  });
}

export function useDeleteAgeKeyMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (keyId: string) =>
      unwrap(
        await api.DELETE("/api/environments/{id}/age-keys/{keyId}", { params: { path: { id: environmentId, keyId } } }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ageKeysQuery(environmentId).queryKey }),
  });
}
