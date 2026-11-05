import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { composePolicyQuery, stackQuery, stacksQuery } from "./queries";
import type { components } from "./schema.gen";

export type UpdateComposePolicyRequest = components["schemas"]["UpdateComposePolicyRequest"];

/** Changes the environment's compose policy; its report is checked again with the new rules. */
export function useUpdateComposePolicyMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: UpdateComposePolicyRequest) =>
      unwrap(await api.PUT("/api/environments/{id}/compose-policy", { params: { path: { id: environmentId } }, body: request })),
    onSuccess: (policy) => {
      queryClient.setQueryData(composePolicyQuery(environmentId).queryKey, policy);
      return queryClient.invalidateQueries({ queryKey: composePolicyQuery(environmentId).queryKey });
    },
  });
}

/** Replaces the stack's own exceptions to the compose policy. */
export function useSetStackPolicyAllowancesMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (allowances: string[]) =>
      unwrap(await api.PUT("/api/stacks/{id}/policy-allowances", { params: { path: { id: stackId } }, body: { allowances } })),
    onSuccess: (stack) => {
      queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey }),
        queryClient.invalidateQueries({ queryKey: composePolicyQuery(stack.environmentId).queryKey }),
      ]);
    },
  });
}

/** Takes the stack out of the compose policy or puts it back. */
export function useSetStackPolicyUnrestrictedMutation(stackId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (enabled: boolean) =>
      unwrap(await api.PUT("/api/stacks/{id}/policy-unrestricted", { params: { path: { id: stackId } }, body: { enabled } })),
    onSuccess: (stack) => {
      queryClient.setQueryData(stackQuery(stackId).queryKey, stack);
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: stacksQuery(stack.environmentId).queryKey }),
        queryClient.invalidateQueries({ queryKey: composePolicyQuery(stack.environmentId).queryKey }),
      ]);
    },
  });
}
