import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import { usersQuery } from "./queries";
import type { components } from "./schema.gen";

export type User = components["schemas"]["UserResponse"];
export type UserDetails = components["schemas"]["UserDetailsResponse"];
export type RoleAssignment = components["schemas"]["RoleAssignmentResponse"];
export type Role = components["schemas"]["Role"];
export type CreateUserRequest = components["schemas"]["CreateUserRequest"];

export const roles = ["Viewer", "Operator", "Editor"] as const satisfies readonly Role[];

/** Every change to a user shows in the list and in their card, so both are refetched. */
function useInvalidateUsers() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: usersQuery.queryKey });
}

export function useCreateUserMutation() {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async (request: CreateUserRequest) => unwrap(await api.POST("/api/users", { body: request })),
    onSuccess: invalidate,
  });
}

export function useRenameUserMutation(id: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async (userName: string) =>
      unwrap(await api.PUT("/api/users/{id}/user-name", { params: { path: { id } }, body: { userName } })),
    onSuccess: invalidate,
  });
}

/** Deactivating ends the user's sessions; the last active administrator cannot be deactivated (409). */
export function useSetUserActiveMutation(id: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async (active: boolean) =>
      unwrap(
        active
          ? await api.POST("/api/users/{id}/activate", { params: { path: { id } } })
          : await api.POST("/api/users/{id}/deactivate", { params: { path: { id } } }),
      ),
    onSuccess: invalidate,
  });
}

/** Revoking ends the user's sessions; the last active administrator keeps the flag (409). */
export function useSetAdminMutation(id: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async (isAdmin: boolean) =>
      unwrap(await api.PUT("/api/users/{id}/admin", { params: { path: { id } }, body: { isAdmin } })),
    onSuccess: invalidate,
  });
}

export function useUnlockUserMutation(id: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/users/{id}/unlock", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

/** Sets a password the administrator hands over; the user's sessions end. */
export function useResetPasswordMutation(id: string) {
  return useMutation({
    mutationFn: async (newPassword: string) =>
      unwrap(await api.POST("/api/users/{id}/password", { params: { path: { id } }, body: { newPassword } })),
  });
}

export function useChangeRoleMutation(userId: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async ({ assignmentId, role }: { assignmentId: string; role: Role }) =>
      unwrap(
        await api.PUT("/api/users/{id}/role-assignments/{assignmentId}", {
          params: { path: { id: userId, assignmentId } },
          body: { role },
        }),
      ),
    onSuccess: invalidate,
  });
}

export function useRevokeRoleMutation(userId: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async (assignmentId: string) =>
      unwrap(
        await api.DELETE("/api/users/{id}/role-assignments/{assignmentId}", {
          params: { path: { id: userId, assignmentId } },
        }),
      ),
    onSuccess: invalidate,
  });
}

export type GrantRoleRequest = components["schemas"]["GrantRoleRequest"];

/** Grants a role on an environment (`stackId` null) or on one of its stacks; a scope that already has one is 409. */
export function useGrantRoleMutation(userId: string) {
  const invalidate = useInvalidateUsers();

  return useMutation({
    mutationFn: async (request: GrantRoleRequest) =>
      unwrap(await api.POST("/api/users/{id}/role-assignments", { params: { path: { id: userId } }, body: request })),
    onSuccess: invalidate,
  });
}
