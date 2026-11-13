import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { UnmanagedStackAction } from "@/features/unmanaged-stacks/model";
import { api, unwrap } from "./client";
import { containersQuery, unmanagedStacksQuery } from "./queries";

export interface UnmanagedStackActionVariables {
  stack: string;
  action: UnmanagedStackAction;
  /** For restart and down; undefined leaves the aggregator's default. */
  timeoutSeconds?: number;
}

/**
 * Runs docker compose for an unmanaged stack; resolves with its output once compose has finished. A failed compose
 * run is a result with an error, not a rejection. The stacks, containers and audit log are reloaded either way.
 */
export function useUnmanagedStackActionMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ stack, action, timeoutSeconds }: UnmanagedStackActionVariables) => {
      const path = { id: environmentId, stackName: stack };
      switch (action) {
        case "up":
          return unwrap(await api.POST("/api/environments/{id}/unmanaged-stacks/{stackName}/up", { params: { path } }));
        case "pull":
          return unwrap(await api.POST("/api/environments/{id}/unmanaged-stacks/{stackName}/pull", { params: { path } }));
        case "restart":
          return unwrap(
            await api.POST("/api/environments/{id}/unmanaged-stacks/{stackName}/restart", { params: { path, query: { timeoutSeconds } } }),
          );
        case "down":
          return unwrap(
            await api.POST("/api/environments/{id}/unmanaged-stacks/{stackName}/down", { params: { path, query: { timeoutSeconds } } }),
          );
      }
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: unmanagedStacksQuery(environmentId).queryKey, exact: true }),
        queryClient.invalidateQueries({ queryKey: containersQuery(environmentId).queryKey, exact: true }),
        // The stack's page shows its history from the audit log.
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]),
  });
}
