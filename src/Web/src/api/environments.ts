import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { environmentRolesQuery, environmentsQuery, registryCredentialsQuery, usersQuery } from "./queries";
import type { components } from "./schema.gen";

export type CreateEnvironmentRequest = components["schemas"]["CreateEnvironmentRequest"];
export type UpdateEnvironmentRequest = components["schemas"]["UpdateEnvironmentRequest"];
export type SetRegistryCredentialRequest = components["schemas"]["SetRegistryCredentialRequest"];
export type EnvironmentRole = components["schemas"]["EnvironmentRoleResponse"];
export type GrantEnvironmentRoleRequest = components["schemas"]["GrantEnvironmentRoleRequest"];

// Enrollment tokens and registry passwords are secrets. A mutation keeps its variables and answer in the mutation
// cache until it is garbage-collected, so these mutations are dropped as soon as nothing observes them
// (gcTime 0), and their screens move the token into component state and reset the mutation.

/** Creates an environment; the answer carries its first enrollment token, shown once. */
export function useCreateEnvironmentMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async (request: CreateEnvironmentRequest) => unwrap(await api.POST("/api/environments", { body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: environmentsQuery.queryKey }),
  });
}

/** Renames the environment or changes its privacy. */
export function useUpdateEnvironmentMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: UpdateEnvironmentRequest) =>
      unwrap(await api.PUT("/api/environments/{id}", { params: { path: { id: environmentId } }, body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: environmentsQuery.queryKey }),
  });
}

/** Issues a new enrollment token; the previous one stops working. The list is refetched for the new expiry. */
export function useIssueEnrollmentTokenMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async () =>
      unwrap(await api.POST("/api/environments/{id}/enrollment-token", { params: { path: { id: environmentId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: environmentsQuery.queryKey }),
  });
}

/** Forgets the agent's credentials and drops its connection; containers on the host keep running. */
export function useRevokeAgentMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => {
      unwrap(await api.POST("/api/environments/{id}/revoke-agent", { params: { path: { id: environmentId } } }));
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: environmentsQuery.queryKey }),
  });
}

/**
 * Removes an environment without stacks. The caller leaves the environment's screen first and then refreshes the
 * list: refreshing while the screen is mounted would show it as missing for a moment.
 */
export function useRemoveEnvironmentMutation(environmentId: string) {
  return useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/environments/{id}", { params: { path: { id: environmentId } } })),
  });
}

/** Adds a registry login, or replaces the login for the same server. The variables hold the password. */
export function useSetRegistryCredentialMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    gcTime: 0,
    mutationFn: async (request: SetRegistryCredentialRequest) =>
      unwrap(
        await api.PUT("/api/environments/{id}/registry-credentials", { params: { path: { id: environmentId } }, body: request }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: registryCredentialsQuery(environmentId).queryKey }),
  });
}

export function useDeleteRegistryCredentialMutation(environmentId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (credentialId: string) => {
      unwrap(
        await api.DELETE("/api/environments/{id}/registry-credentials/{credentialId}", {
          params: { path: { id: environmentId, credentialId } },
        }),
      );
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: registryCredentialsQuery(environmentId).queryKey }),
  });
}

/** A role change shows on the environment and in the user's card (for administrators). */
function useInvalidateEnvironmentRoles(environmentId: string) {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: environmentRolesQuery(environmentId).queryKey }),
      queryClient.invalidateQueries({ queryKey: usersQuery.queryKey }),
    ]);
}

/** Gives an existing user a role on the environment or one of its stacks. */
export function useGrantEnvironmentRoleMutation(environmentId: string) {
  const invalidate = useInvalidateEnvironmentRoles(environmentId);

  return useMutation({
    mutationFn: async (request: GrantEnvironmentRoleRequest) =>
      unwrap(await api.POST("/api/environments/{id}/role-assignments", { params: { path: { id: environmentId } }, body: request })),
    onSuccess: invalidate,
  });
}

export function useChangeEnvironmentRoleMutation(environmentId: string) {
  const invalidate = useInvalidateEnvironmentRoles(environmentId);

  return useMutation({
    mutationFn: async ({ assignmentId, role }: { assignmentId: string; role: EnvironmentRole["role"] }) =>
      unwrap(
        await api.PUT("/api/environments/{id}/role-assignments/{assignmentId}", {
          params: { path: { id: environmentId, assignmentId } },
          body: { role },
        }),
      ),
    onSettled: invalidate,
  });
}

export function useRevokeEnvironmentRoleMutation(environmentId: string) {
  const invalidate = useInvalidateEnvironmentRoles(environmentId);

  return useMutation({
    mutationFn: async (assignmentId: string) => {
      unwrap(
        await api.DELETE("/api/environments/{id}/role-assignments/{assignmentId}", {
          params: { path: { id: environmentId, assignmentId } },
        }),
      );
    },
    onSettled: invalidate,
  });
}
