import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { StackAction } from "@/features/stacks/model";
import { api, unwrap } from "./client";
import { deploymentsQuery, repositoriesQuery, stackQuery, stacksQuery } from "./queries";
import type { components } from "./schema.gen";

export type CreateRepositoryRequest = components["schemas"]["CreateRepositoryRequest"];
export type UpdateRepositoryRequest = components["schemas"]["UpdateRepositoryRequest"];
export type CreateStackRequest = components["schemas"]["CreateStackRequest"];
export type ChangeStackSourceRequest = components["schemas"]["ChangeStackSourceRequest"];

// Access tokens belong to git connections (./git-connections.ts); repositories carry none.

export function useCreateRepositoryMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateRepositoryRequest) => unwrap(await api.POST("/api/repositories", { body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: repositoriesQuery.queryKey }),
  });
}

export function useUpdateRepositoryMutation(repositoryId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: UpdateRepositoryRequest) =>
      unwrap(await api.PUT("/api/repositories/{id}", { params: { path: { id: repositoryId } }, body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: repositoriesQuery.queryKey }),
  });
}

/**
 * Generates a new webhook secret, replacing the old one. The answer holds the secret, which is shown once: the
 * mutation is dropped as soon as nothing observes it, and the component that shows the secret keeps it in its state.
 */
export function useCreateWebhookSecretMutation(repositoryId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async () =>
      unwrap(await api.POST("/api/repositories/{id}/webhook", { params: { path: { id: repositoryId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: repositoriesQuery.queryKey }),
  });
}

export function useRemoveWebhookSecretMutation(repositoryId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/repositories/{id}/webhook", { params: { path: { id: repositoryId } } })),
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

/** Turns deploying on push on or off; the answer is the changed stack, as for the source. */
export function useSetDeployOnPushMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (enabled: boolean) =>
      unwrap(await api.PUT("/api/stacks/{id}/deploy-on-push", { params: { path: { id: stackId } }, body: { enabled } })),
    onSuccess: (stack) => {
      queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
      return queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey });
    },
  });
}

export function useSetRestartOnFileChangeMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (enabled: boolean) =>
      unwrap(await api.PUT("/api/stacks/{id}/restart-on-file-change", { params: { path: { id: stackId } }, body: { enabled } })),
    onSuccess: (stack) => {
      queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
      return queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey });
    },
  });
}

/**
 * Removes the stack with its history. The caller leaves the stack's screen first and then drops its queries
 * (`forgetStack`): dropping them while the screen is mounted would refetch a stack that is gone.
 */
export function useRemoveStackMutation(stackId: string) {
  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/stacks/{id}", { params: { path: { id: stackId } } })),
  });
}

/** Drops the cached removed stack and its deployments, and refreshes its environment's list. */
export function useForgetStack() {
  const queryClient = useQueryClient();

  return (stack: { id: string; environmentId: string }) => {
    queryClient.removeQueries({ queryKey: stackQuery(stack.id).queryKey });
    queryClient.removeQueries({ queryKey: deploymentsQuery(stack.id).queryKey });
    return queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey });
  };
}

/** A new deploy hook token; the answer holds it, so the mutation keeps nothing after use. */
export function useCreateDeployHookMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async () => unwrap(await api.POST("/api/stacks/{id}/deploy-hook", { params: { path: { id: stackId } } })),
    onSuccess: ({ stack }) => {
      queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
      return queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey });
    },
  });
}

export function useRemoveDeployHookMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/stacks/{id}/deploy-hook", { params: { path: { id: stackId } } })),
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
