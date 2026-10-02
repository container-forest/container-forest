import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { StackAction } from "@/features/stacks/model";
import { api, unwrap } from "./client";
import { deploymentsQuery, repositoriesQuery, stackQuery, stacksQuery } from "./queries";
import type { components } from "./schema.gen";

export type CreateRepositoryRequest = components["schemas"]["CreateRepositoryRequest"];
export type CreateStackRequest = components["schemas"]["CreateStackRequest"];
export type ChangeStackSourceRequest = components["schemas"]["ChangeStackSourceRequest"];

export function useCreateRepositoryMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateRepositoryRequest) => unwrap(await api.POST("/api/repositories", { body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: repositoriesQuery.queryKey }),
  });
}

export function useCreateStackMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateStackRequest) =>
      unwrap(await api.POST("/api/environments/{id}/stacks", { params: { path: { id: environmentId } }, body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: stacksQuery(environmentId).queryKey }),
  });
}

/** The answer is the changed stack, put straight into its query; the environment's list is refetched. */
export function useChangeStackSourceMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: ChangeStackSourceRequest) =>
      unwrap(await api.PUT("/api/stacks/{id}/source", { params: { path: { id: stackId } }, body: request })),
    onSuccess: (stack) => {
      queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
      return queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey });
    },
  });
}

/** Queues a deployment (redeploy, or redeploy with pull); the answer is the queued deployment to follow. */
export function useDeployStackMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (pull: boolean) =>
      unwrap(await api.POST("/api/stacks/{id}/deployments", { params: { path: { id: stackId } }, body: { pull } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: deploymentsQuery(stackId).queryKey }),
  });
}

export interface StackActionVariables {
  action: StackAction;
  /** Seconds Docker waits for each container; undefined leaves the aggregator's default. */
  timeoutSeconds?: number;
}

/**
 * Restarts or takes down the stack; resolves with the compose output once the agent has finished. A failed compose
 * run is still a result (succeeded = false), not an error. Down forgets the deployed commit, so the stack is reloaded.
 */
export function useStackActionMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ action, timeoutSeconds }: StackActionVariables) => {
      const params = { path: { id: stackId }, query: { timeoutSeconds } };
      return action === "restart"
        ? unwrap(await api.POST("/api/stacks/{id}/restart", { params }))
        : unwrap(await api.POST("/api/stacks/{id}/down", { params }));
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: stackQuery(stackId).queryKey }),
  });
}
