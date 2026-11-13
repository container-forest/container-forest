import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ContainerAction } from "@/features/containers/model";
import { api, unwrap } from "./client";
import { containersQuery } from "./queries";

export interface ContainerActionVariables {
  /** Full id, exact name or unique id prefix. */
  containerId: string;
  action: ContainerAction;
  /** For stop and restart; undefined leaves the aggregator's default. */
  timeoutSeconds?: number;
}

/**
 * Starts, stops, restarts or removes a container; resolves once the agent has done it. The list is reloaded either way:
 * after a failure (e.g. the container changed state meanwhile) the shown state may be stale too.
 */
export function useContainerActionMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ containerId, action, timeoutSeconds }: ContainerActionVariables) => {
      const path = { id: environmentId, containerId };
      switch (action) {
        case "start":
          return unwrap(await api.POST("/api/environments/{id}/containers/{containerId}/start", { params: { path } }));
        case "stop":
          return unwrap(
            await api.POST("/api/environments/{id}/containers/{containerId}/stop", {
              params: { path, query: { timeoutSeconds } },
            }),
          );
        case "restart":
          return unwrap(
            await api.POST("/api/environments/{id}/containers/{containerId}/restart", {
              params: { path, query: { timeoutSeconds } },
            }),
          );
        case "remove":
          return unwrap(await api.POST("/api/environments/{id}/containers/{containerId}/remove", { params: { path } }));
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: containersQuery(environmentId).queryKey }),
  });
}
