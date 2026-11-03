import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { AddSigningKeyRequest, UpdateSigningKeyRequest } from "@/features/signing-keys/model";
import { api, unwrap } from "./client";
import { signingKeysQuery, userSigningKeysQuery } from "./queries";

// Signing keys (ADR-0011): the signed-in user's own from a session, and an administrator's view of anyone's.

export function useAddSigningKeyMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: AddSigningKeyRequest) => unwrap(await api.POST("/api/me/signing-keys", { body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: signingKeysQuery.queryKey }),
  });
}

export function useUpdateSigningKeyMutation(keyId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: UpdateSigningKeyRequest) =>
      unwrap(await api.PUT("/api/me/signing-keys/{id}", { params: { path: { id: keyId } }, body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: signingKeysQuery.queryKey }),
  });
}

export function useRemoveSigningKeyMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (keyId: string) => unwrap(await api.DELETE("/api/me/signing-keys/{id}", { params: { path: { id: keyId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: signingKeysQuery.queryKey }),
  });
}

/** An administrator adds a key for a user without a session, such as a CI account. */
export function useAddUserSigningKeyMutation(userId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: AddSigningKeyRequest) =>
      unwrap(await api.POST("/api/users/{userId}/signing-keys", { params: { path: { userId } }, body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: userSigningKeysQuery(userId).queryKey }),
  });
}

export function useRemoveUserSigningKeyMutation(userId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (keyId: string) =>
      unwrap(await api.DELETE("/api/users/{userId}/signing-keys/{keyId}", { params: { path: { userId, keyId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: userSigningKeysQuery(userId).queryKey }),
  });
}
