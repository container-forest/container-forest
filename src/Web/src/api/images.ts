import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import {
  aggregatorSigningKeysQuery,
  deploymentsQuery,
  gitConnectionsQuery,
  imagePoliciesQuery,
  imageUpdateSettingsQuery,
  stackQuery,
  stacksQuery,
} from "./queries";
import type { components } from "./schema.gen";

export type CreateImagePolicyRequest = components["schemas"]["CreateImagePolicyRequest"];
export type ImagePolicySettingsRequest = components["schemas"]["ImagePolicySettingsRequest"];
export type GitAccessTokenRequest = components["schemas"]["GitAccessTokenRequest"];

// Image updates by the aggregator (ADR-0015). A check or a pin may commit and queue a deployment, so the stack's
// deployments are read again after them. The image hook's token and the push token are secrets: their mutations keep
// nothing after use (gcTime 0), as for the deploy hook.

function useInvalidatePolicies(stackId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: imagePoliciesQuery(stackId).queryKey }),
      queryClient.invalidateQueries({ queryKey: deploymentsQuery(stackId).queryKey }),
    ]);
}

export function useAddImagePolicyMutation(stackId: string) {
  const invalidate = useInvalidatePolicies(stackId);
  return useMutation({
    mutationFn: async (request: CreateImagePolicyRequest) =>
      unwrap(await api.POST("/api/stacks/{id}/image-policies", { params: { path: { id: stackId } }, body: request })),
    onSuccess: invalidate,
  });
}

export function useChangeImagePolicyMutation(stackId: string, policyId: string) {
  const invalidate = useInvalidatePolicies(stackId);
  return useMutation({
    mutationFn: async (request: ImagePolicySettingsRequest) =>
      unwrap(await api.PUT("/api/image-policies/{policyId}", { params: { path: { policyId } }, body: request })),
    onSuccess: invalidate,
  });
}

export function useRemoveImagePolicyMutation(stackId: string, policyId: string) {
  const invalidate = useInvalidatePolicies(stackId);
  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/image-policies/{policyId}", { params: { path: { policyId } } })),
    onSuccess: invalidate,
  });
}

/** Checks the registry now; with dryRun, only says what would change. */
export function useCheckImagePolicyMutation(stackId: string, policyId: string) {
  const invalidate = useInvalidatePolicies(stackId);
  return useMutation({
    mutationFn: async (dryRun: boolean) =>
      unwrap(await api.POST("/api/image-policies/{policyId}/check", { params: { path: { policyId }, query: { dryRun } } })),
    onSuccess: invalidate,
  });
}

export function usePinImageMutation(stackId: string, policyId: string) {
  const invalidate = useInvalidatePolicies(stackId);
  return useMutation({
    mutationFn: async (tag: string) =>
      unwrap(await api.POST("/api/image-policies/{policyId}/pin", { params: { path: { policyId } }, body: { tag } })),
    onSuccess: invalidate,
  });
}

export function useUnpinImageMutation(stackId: string, policyId: string) {
  const invalidate = useInvalidatePolicies(stackId);
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/image-policies/{policyId}/unpin", { params: { path: { policyId } } })),
    onSuccess: invalidate,
  });
}

function useSetStack(stackId: string) {
  const queryClient = useQueryClient();
  return (stack: components["schemas"]["StackResponse"]) => {
    queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
    return queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey });
  };
}

/** A new image hook token; the answer holds it, so the mutation keeps nothing after use. */
export function useCreateImageHookMutation(stackId: string) {
  const setStack = useSetStack(stackId);
  return useMutation({
    gcTime: 0,
    mutationFn: async (allowDowngrade: boolean) =>
      unwrap(await api.POST("/api/stacks/{id}/image-hook", { params: { path: { id: stackId } }, body: { allowDowngrade } })),
    onSuccess: ({ stack }) => setStack(stack),
  });
}

export function useSetImageHookDowngradeMutation(stackId: string) {
  const setStack = useSetStack(stackId);
  return useMutation({
    mutationFn: async (allowDowngrade: boolean) =>
      unwrap(await api.PUT("/api/stacks/{id}/image-hook", { params: { path: { id: stackId } }, body: { allowDowngrade } })),
    onSuccess: setStack,
  });
}

export function useRemoveImageHookMutation(stackId: string) {
  const setStack = useSetStack(stackId);
  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/stacks/{id}/image-hook", { params: { path: { id: stackId } } })),
    onSuccess: setStack,
  });
}

export function useSetPushTokenMutation(connectionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    gcTime: 0,
    mutationFn: async (request: GitAccessTokenRequest) =>
      unwrap(await api.PUT("/api/git-connections/{id}/push-credential", { params: { path: { id: connectionId } }, body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: gitConnectionsQuery.queryKey }),
  });
}

export function useRemovePushTokenMutation(connectionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () =>
      unwrap(await api.DELETE("/api/git-connections/{id}/push-credential", { params: { path: { id: connectionId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: gitConnectionsQuery.queryKey }),
  });
}

export function useRotateSigningKeyMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/system/signing-key/rotate")),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: aggregatorSigningKeysQuery.queryKey }),
  });
}

export function useUpdateImageUpdateSettingsMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: components["schemas"]["UpdateImageUpdateSettingsRequest"]) =>
      unwrap(await api.PUT("/api/settings/image-updates", { body: request })),
    onSuccess: (settings) => queryClient.setQueryData(imageUpdateSettingsQuery.queryKey, settings),
  });
}
